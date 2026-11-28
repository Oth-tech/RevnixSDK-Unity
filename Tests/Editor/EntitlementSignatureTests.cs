using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class EntitlementSignatureTests
    {
        private const long DayMs = 24L * 3600 * 1000;

        private static readonly BigInteger N = BigInteger.Parse(
            "00ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551",
            System.Globalization.NumberStyles.HexNumber);

        private static readonly string[] PinnedKeys =
        {
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEsVQ+iVnl3H7IWe6uma8VASdqlJ09SrtOIKpxB5vF1xF7f3nD+SBzXlse1+skTi/jv/CLa4fUz9qGfZ4T8M+Nhw==",
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEzGACMgBMy29LVcpzlmUFBRi4aiCB0FSitlLcO0k8fJ9q3foy22k2twy2U/qL0yVJHKQWWIEmt96dchyGlgDZHQ==",
        };

        private static string Body(bool active, long cursor = 7)
            => "{\"customerId\":\"cus_1\",\"cursor\":" + cursor + ",\"entitlements\":[" +
               "{\"entitlementId\":\"pro\",\"isActive\":" + (active ? "true" : "false") + ",\"sources\":[]}]}";

        private static byte[] Der(BigInteger r, BigInteger s)
        {
            byte[] Integer(BigInteger value)
            {
                var content = value.ToByteArray().Reverse().ToArray();
                return new byte[] { 0x02, (byte)content.Length }.Concat(content).ToArray();
            }
            var body = Integer(r).Concat(Integer(s)).ToArray();
            return new byte[] { 0x30, (byte)body.Length }.Concat(body).ToArray();
        }

        private static BigInteger Unsigned(byte[] bigEndian)
            => new BigInteger(bigEndian.Reverse().Concat(new byte[] { 0 }).ToArray());

        [Test]
        public void TheManagedVerifierAgreesWithDotnetOnRandomKeysAndMessages()
        {
            var random = new Random(1234);
            for (var i = 0; i < 50; i++)
            {
                var signer = new TestSigningKey();
                var key = RevnixP256Key.FromSpki(signer.Spki);
                Assert.IsNotNull(key);
                var message = new byte[random.Next(0, 200)];
                random.NextBytes(message);
                var der = signer.SignDer(message);

                Assert.IsTrue(key.Verify(message, der), "valid signature " + i);

                var flippedMessage = message.Length == 0 ? new byte[] { 1 } : (byte[])message.Clone();
                if (message.Length > 0) flippedMessage[random.Next(message.Length)] ^= 0x01;
                Assert.IsFalse(key.Verify(flippedMessage, der), "flipped message " + i);

                var flippedSignature = (byte[])der.Clone();
                flippedSignature[random.Next(der.Length)] ^= (byte)(1 << random.Next(8));
                Assert.IsFalse(key.Verify(message, flippedSignature), "flipped signature " + i);

                Assert.IsFalse(RevnixP256Key.FromSpki(new TestSigningKey().Spki).Verify(message, der), "wrong key " + i);
            }
        }

        [Test]
        public void AHighSSignatureIsAcceptedBecauseTheServerDoesNotNormaliseS()
        {
            var signer = new TestSigningKey();
            var key = RevnixP256Key.FromSpki(signer.Spki);
            var message = Encoding.UTF8.GetBytes("high s");
            var raw = signer.SignRaw(message);
            var r = Unsigned(raw.Take(32).ToArray());
            var s = Unsigned(raw.Skip(32).ToArray());

            Assert.IsTrue(key.Verify(message, Der(r, s)));
            Assert.IsTrue(key.Verify(message, Der(r, N - s)));
        }

        [Test]
        public void OutOfRangeScalarsAndMalformedDerAreRejected()
        {
            var signer = new TestSigningKey();
            var key = RevnixP256Key.FromSpki(signer.Spki);
            var message = Encoding.UTF8.GetBytes("range");
            var raw = signer.SignRaw(message);
            var r = Unsigned(raw.Take(32).ToArray());
            var s = Unsigned(raw.Skip(32).ToArray());
            var der = Der(r, s);
            Assert.IsTrue(key.Verify(message, der));

            Assert.IsFalse(key.Verify(message, Der(BigInteger.Zero, s)), "r = 0");
            Assert.IsFalse(key.Verify(message, Der(r, BigInteger.Zero)), "s = 0");
            Assert.IsFalse(key.Verify(message, Der(r, N)), "s = n");
            Assert.IsFalse(key.Verify(message, Der(r, s + N)), "s > n");
            Assert.IsFalse(key.Verify(message, Der(r + N, s)), "r > n");
            Assert.IsFalse(key.Verify(message, Der(-r, s)), "negative r");

            Assert.IsFalse(key.Verify(message, null), "null");
            Assert.IsFalse(key.Verify(message, new byte[0]), "empty");
            Assert.IsFalse(key.Verify(message, raw), "raw r||s is not DER");
            Assert.IsFalse(key.Verify(message, der.Take(der.Length - 1).ToArray()), "truncated");
            Assert.IsFalse(key.Verify(message, der.Concat(new byte[] { 0 }).ToArray()), "trailing byte");
            var padded = new List<byte> { 0x30, (byte)(der[1] + 1), 0x02, (byte)(der[3] + 1), 0x00 };
            padded.AddRange(der.Skip(4));
            if ((der[4] & 0x80) == 0) Assert.IsFalse(key.Verify(message, padded.ToArray()), "non-minimal integer");
            var longForm = new List<byte> { 0x30, 0x81, der[1] };
            longForm.AddRange(der.Skip(2));
            Assert.IsFalse(key.Verify(message, longForm.ToArray()), "long-form length");
            var random = new Random(99);
            for (var i = 0; i < 20; i++)
            {
                var garbage = new byte[random.Next(1, 80)];
                random.NextBytes(garbage);
                Assert.IsFalse(key.Verify(message, garbage), "garbage " + i);
            }
        }

        [Test]
        public void OnlyUncompressedP256SpkiKeysOnTheCurveParse()
        {
            foreach (var pinned in PinnedKeys) Assert.IsNotNull(RevnixP256Key.FromSpki(pinned));

            var spki = Convert.FromBase64String(PinnedKeys[0]);
            var offCurve = (byte[])spki.Clone();
            offCurve[offCurve.Length - 1] ^= 0x01;
            var compressed = (byte[])spki.Clone();
            compressed[26] = 0x02;
            var otherCurve = (byte[])spki.Clone();
            otherCurve[22] = 0x08;

            Assert.IsNull(RevnixP256Key.FromSpki(Convert.ToBase64String(offCurve)), "off curve");
            Assert.IsNull(RevnixP256Key.FromSpki(Convert.ToBase64String(compressed)), "compressed");
            Assert.IsNull(RevnixP256Key.FromSpki(Convert.ToBase64String(otherCurve)), "other curve oid");
            Assert.IsNull(RevnixP256Key.FromSpki(Convert.ToBase64String(spki.Take(90).ToArray())), "short");
            Assert.IsNull(RevnixP256Key.FromSpki("not base64!"), "not base64");
            Assert.IsNull(RevnixP256Key.FromSpki(null), "null");
        }

        private sealed class FakeHttp : IRevnixHttp
        {
            public Func<string, RevnixHttpResponse> Entitlements;
            public int EntitlementCalls;

            public Task<RevnixHttpResponse> Send(
                string method, string url, string jsonBody,
                IReadOnlyDictionary<string, string> headers, int timeoutMs)
            {
                if (!url.Contains("/entitlements")) return Task.FromResult(new RevnixHttpResponse(200, "{}"));
                EntitlementCalls++;
                var cid = Uri.UnescapeDataString(url.Split('/').Reverse().Skip(1).First());
                return Task.FromResult(Entitlements(cid));
            }
        }

        private sealed class Harness
        {
            public readonly FakeHttp Http = new FakeHttp();
            public readonly MemoryStorage Storage = new MemoryStorage();
            public readonly TestSigningKey Key = new TestSigningKey();
            public long Now = 1_700_000_000_000L;

            public RevnixClient Client(TimeSpan? ttl = null) => new RevnixClient(new RevnixConfig
            {
                ApiKey = TestSigningKey.ApiKey,
                BaseUrl = "https://x",
                Http = Http,
                Storage = Storage,
                Now = () => Now,
                Delay = _ => Task.CompletedTask,
                EntitlementsTtl = ttl ?? TimeSpan.Zero,
                ReadYourWritesDelays = new List<TimeSpan> { TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1) },
                EntitlementSigningKeys = new[] { Key.Spki },
            });

            public void SignedLive(bool active = true, long cursor = 7)
                => Http.Entitlements = cid => Key.Response(Body(active, cursor), cid, Now);

            public void Offline()
                => Http.Entitlements = _ => throw new RevnixNetworkException("offline");

            public string Cache(string cid) => Storage.Get("revnix.ent." + cid);
        }

        private static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

        [Test]
        public void ASignedLiveResponseIsServedAndCached()
        {
            var h = new Harness();
            h.SignedLive();
            var client = h.Client();

            var fresh = Run(client.Entitlements());

            Assert.AreEqual(false, fresh.Stale);
            Assert.IsTrue(fresh.IsEntitled("pro"));
            var stored = RevnixJson.ParseObject(h.Cache(client.CustomerId()));
            Assert.AreEqual(Body(true), RevnixJson.GetString(stored, "body"));
            Assert.IsNotNull(RevnixJson.GetString(stored, "signature"));
            Assert.AreEqual(h.Now.ToString(), RevnixJson.GetString(stored, "signedAt"));
        }

        [Test]
        public void TheVerifiedCacheSurvivesANewClientAndServesOffline()
        {
            var h = new Harness();
            h.SignedLive();
            Run(h.Client().Entitlements());

            h.Offline();
            h.Now += 1000;
            var relaunched = h.Client();
            var served = Run(relaunched.Entitlements());

            Assert.AreEqual(true, served.Stale);
            Assert.IsTrue(served.IsEntitled("pro"));
            Assert.IsTrue(Run(relaunched.IsEntitled("pro")));
            Assert.IsTrue(relaunched.CachedEntitlements().IsEntitled("pro"));
        }

        [Test]
        public void AnUnsignedLiveResponseWithNoCacheThrowsARetryableError()
        {
            var h = new Harness();
            h.Http.Entitlements = _ => new RevnixHttpResponse(200, Body(true));
            var client = h.Client();

            var err = Assert.Throws<RevnixBadResponseException>(() => Run(client.Entitlements()));
            Assert.IsTrue(err.IsRetryable);
            Assert.IsFalse(Run(client.IsEntitled("pro")));
            Assert.IsNull(h.Cache(client.CustomerId()));
            Assert.IsNull(h.Storage.Get("revnix.entIndex"));
        }

        public enum Forgery
        {
            Unsigned,
            TamperedBody,
            UnpinnedKey,
            OtherCustomer,
            OtherApiKey,
        }

        private static RevnixHttpResponse ForgedLive(Harness h, Forgery forgery, string cid)
        {
            var signedAt = h.Now;
            switch (forgery)
            {
                case Forgery.Unsigned:
                    return new RevnixHttpResponse(200, Body(true));
                case Forgery.TamperedBody:
                    var genuine = h.Key.Response(Body(false), cid, signedAt);
                    return new RevnixHttpResponse(200, Body(true), null, genuine.SignedAtHeader, genuine.SignatureHeader);
                case Forgery.UnpinnedKey:
                    return new TestSigningKey().Response(Body(true), cid, signedAt);
                case Forgery.OtherCustomer:
                    return h.Key.Response(Body(true), "someone_else", signedAt);
                default:
                    return h.Key.Response(Body(true), cid, signedAt, "rvx_pk_other");
            }
        }

        private static string ForgedCache(Harness h, Forgery forgery, string cid)
        {
            var live = ForgedLive(h, forgery, cid);
            return RevnixJson.Serialize(new Dictionary<string, object>
            {
                ["body"] = live.Body,
                ["signedAt"] = live.SignedAtHeader ?? h.Now.ToString(),
                ["signature"] = live.SignatureHeader ?? "",
                ["fetchedAt"] = h.Now,
            });
        }

        [TestCase(Forgery.Unsigned)]
        [TestCase(Forgery.TamperedBody)]
        [TestCase(Forgery.UnpinnedKey)]
        [TestCase(Forgery.OtherCustomer)]
        [TestCase(Forgery.OtherApiKey)]
        public void ARejectedLiveResponseFallsBackToTheVerifiedCacheAndLeavesStorageAlone(Forgery forgery)
        {
            var h = new Harness();
            h.SignedLive(active: false);
            var client = h.Client();
            var cid = client.CustomerId();
            Run(client.Entitlements());
            var cacheBefore = h.Cache(cid);
            var indexBefore = h.Storage.Get("revnix.entIndex");

            h.Http.Entitlements = id => ForgedLive(h, forgery, id);
            var served = Run(client.Entitlements());

            Assert.AreEqual(true, served.Stale);
            Assert.IsFalse(served.IsEntitled("pro"));
            Assert.IsFalse(Run(client.IsEntitled("pro")));
            Assert.AreEqual(cacheBefore, h.Cache(cid));
            Assert.AreEqual(indexBefore, h.Storage.Get("revnix.entIndex"));
        }

        [TestCase(Forgery.TamperedBody)]
        [TestCase(Forgery.UnpinnedKey)]
        [TestCase(Forgery.OtherCustomer)]
        [TestCase(Forgery.OtherApiKey)]
        public void ARejectedLiveResponseWithNoCacheThrowsAndStoresNothing(Forgery forgery)
        {
            var h = new Harness();
            h.Http.Entitlements = id => ForgedLive(h, forgery, id);
            var client = h.Client();

            Assert.Throws<RevnixBadResponseException>(() => Run(client.Entitlements()));
            Assert.IsFalse(Run(client.IsEntitled("pro")));
            Assert.IsNull(h.Cache(client.CustomerId()));
        }

        [TestCase(Forgery.Unsigned)]
        [TestCase(Forgery.TamperedBody)]
        [TestCase(Forgery.UnpinnedKey)]
        [TestCase(Forgery.OtherCustomer)]
        [TestCase(Forgery.OtherApiKey)]
        public void AForgedCacheEntryCountsAsNoCache(Forgery forgery)
        {
            var h = new Harness();
            var client = h.Client(TimeSpan.FromSeconds(30));
            var cid = client.CustomerId();
            h.Storage.Set("revnix.ent." + cid, ForgedCache(h, forgery, cid));
            h.Offline();

            Assert.IsNull(client.CachedEntitlements());
            Assert.Throws<RevnixNetworkException>(() => Run(client.Entitlements()));
            Assert.IsFalse(Run(client.IsEntitled("pro")));
        }

        [Test]
        public void AVerifiedEntryCopiedToAnotherCustomerIsRejected()
        {
            var h = new Harness();
            h.SignedLive();
            var client = h.Client(TimeSpan.FromSeconds(30));
            Run(client.Entitlements());
            var raw = h.Cache(client.CustomerId());

            client.Identify("cus_other");
            h.Storage.Set("revnix.ent.cus_other", raw);
            h.Offline();

            Assert.IsNull(client.CachedEntitlements());
            Assert.IsFalse(Run(client.IsEntitled("pro")));
        }

        [Test]
        public void ALegacySnapshotEntryIsIgnored()
        {
            var h = new Harness();
            var client = h.Client(TimeSpan.FromSeconds(30));
            h.Storage.Set("revnix.ent." + client.CustomerId(),
                "{\"snapshot\":" + Body(true) + ",\"fetchedAt\":" + h.Now + "}");
            h.Offline();

            Assert.IsNull(client.CachedEntitlements());
            Assert.Throws<RevnixNetworkException>(() => Run(client.Entitlements()));
            Assert.IsFalse(Run(client.IsEntitled("pro")));
        }

        [Test]
        public void AFutureFetchedAtCannotExtendTheTtlOrTheOfflineWindow()
        {
            var h = new Harness();
            var client = h.Client(TimeSpan.FromSeconds(30));
            var cid = client.CustomerId();
            var signedAt = h.Now;
            h.Storage.Set("revnix.ent." + cid, h.Key.CacheEntry(Body(true), cid, signedAt, signedAt + 365 * DayMs));
            Assert.IsNotNull(client.CachedEntitlements());

            h.SignedLive(cursor: 8);
            h.Now += 31_000;
            Assert.AreEqual(8, Run(client.Entitlements()).Cursor);
            Assert.AreEqual(1, h.Http.EntitlementCalls);

            h.Storage.Set("revnix.ent." + cid, h.Key.CacheEntry(Body(true), cid, signedAt, signedAt + 365 * DayMs));
            h.Offline();
            h.Now = signedAt + 15 * DayMs;
            var served = Run(client.Entitlements());
            Assert.AreEqual(signedAt, served.FetchedAt);
            Assert.IsFalse(served.IsEntitled("pro"));
        }

        [Test]
        public void WaitForEntitlementsDoesNotResolveOnAnUnsignedResponse()
        {
            var h = new Harness();
            h.SignedLive();
            var client = h.Client(TimeSpan.FromSeconds(30));
            Run(client.Entitlements());

            h.Http.Entitlements = _ => new RevnixHttpResponse(200, Body(true, cursor: 9));
            var settled = Run(client.WaitForEntitlements(9));

            Assert.AreEqual(7, settled.Cursor);
            Assert.AreEqual(3, h.Http.EntitlementCalls);
        }
    }
}
