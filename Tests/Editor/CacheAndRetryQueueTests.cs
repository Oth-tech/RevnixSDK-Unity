using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class CacheAndRetryQueueTests
    {
        private const long DayMs = 24L * 3600 * 1000;

        private static string Placement(int revision)
            => "{\"status\":\"ok\",\"placementKey\":\"main\",\"revision\":" + revision +
               ",\"offering\":{\"offeringId\":\"off_1\"}}";

        private const string EntitlementsBody =
            "{\"customerId\":\"cus_1\",\"cursor\":7,\"entitlements\":[" +
            "{\"entitlementId\":\"pro\",\"isActive\":true,\"sources\":[]}]}";

        private sealed class FakeHttp : IRevnixHttp
        {
            public readonly List<(string url, int timeoutMs)> Calls = new List<(string, int)>();
            public readonly Dictionary<string, Func<Task<RevnixHttpResponse>>> Routes =
                new Dictionary<string, Func<Task<RevnixHttpResponse>>>();

            public Task<RevnixHttpResponse> Send(
                string method, string url, string jsonBody,
                IReadOnlyDictionary<string, string> headers, int timeoutMs)
            {
                Calls.Add((url, timeoutMs));
                foreach (var route in Routes)
                {
                    if (url.Contains(route.Key)) return route.Value();
                }
                return Task.FromResult(new RevnixHttpResponse(200, "{}"));
            }

            public void Respond(string path, int status, string body)
                => Routes[path] = () => Task.FromResult(new RevnixHttpResponse(status, body));

            public void Hang(string path)
                => Routes[path] = () => new TaskCompletionSource<RevnixHttpResponse>().Task;

            public void Offline(string path)
                => Routes[path] = () => throw new RevnixNetworkException("offline");

            public int CallsTo(string path) => Calls.FindAll(c => c.url.Contains(path)).Count;
        }

        private sealed class Harness
        {
            public readonly FakeHttp Http = new FakeHttp();
            public readonly MemoryStorage Storage = new MemoryStorage();
            public readonly List<RevnixDiagnostic> Diagnostics = new List<RevnixDiagnostic>();
            public long Now = 1_700_000_000_000L;

            public RevnixClient Client(TimeSpan? entitlementsTtl = null) => new RevnixClient(new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = Http,
                Storage = Storage,
                Now = () => Now,
                EntitlementsTtl = entitlementsTtl ?? TimeSpan.FromSeconds(30),
                OnDiagnostic = d => Diagnostics.Add(d),
            });
        }

        private static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

        [Test]
        public void AFreshCachedPlacementAnswersWithoutTheNetworkAndRefreshesInTheBackground()
        {
            var h = new Harness();
            h.Http.Respond("/placements", 200, Placement(1));
            var client = h.Client();
            Run(client.ResolvePlacement("main"));

            h.Http.Hang("/placements");
            var cached = client.ResolvePlacement("main");
            Assert.IsTrue(cached.IsCompleted);
            Assert.AreEqual(1, Run(cached).Revision);
            Assert.AreEqual(2, h.Http.CallsTo("/placements"), "the refresh went out");

            h.Http.Respond("/placements", 200, Placement(2));
            h.Now += 1;
            Assert.AreEqual(1, Run(client.ResolvePlacement("main")).Revision);
            StringAssert.Contains("\"revision\":2", h.Storage.Get("revnix.placement.main"));
            Assert.AreEqual(2, Run(client.ResolvePlacement("main")).Revision);
        }

        [Test]
        public void ARejectionSeenByTheBackgroundRefreshDropsTheCache()
        {
            var h = new Harness();
            h.Http.Respond("/placements", 200, Placement(1));
            var client = h.Client();
            Run(client.ResolvePlacement("main"));

            h.Http.Respond("/placements", 404, "{\"error\":\"unknown placement\"}");
            Assert.AreEqual(1, Run(client.ResolvePlacement("main")).Revision);
            Assert.IsNull(h.Storage.Get("revnix.placement.main"));
            Assert.IsNull(h.Storage.Get("revnix.placementAt.main"));
            Assert.Throws<RevnixNotFoundException>(() => Run(client.ResolvePlacement("main")));
        }

        [Test]
        public void AStaleCachedPlacementGoesToTheNetworkFirst()
        {
            var h = new Harness();
            h.Http.Respond("/placements", 200, Placement(1));
            var client = h.Client();
            Run(client.ResolvePlacement("main"));

            h.Http.Respond("/placements", 200, Placement(2));
            h.Now += 25 * 3600 * 1000L;
            Assert.AreEqual(2, Run(client.ResolvePlacement("main")).Revision);
            Assert.AreEqual(2, h.Http.CallsTo("/placements"));
        }

        [Test]
        public void AStaleCacheIsStillTheOfflineFallback()
        {
            var h = new Harness();
            h.Http.Respond("/placements", 200, Placement(1));
            var client = h.Client();
            Run(client.ResolvePlacement("main"));

            h.Http.Offline("/placements");
            h.Now += 25 * 3600 * 1000L;
            Assert.AreEqual(1, Run(client.ResolvePlacement("main")).Revision);
        }

        [Test]
        public void AnotherCustomersCacheIsNotServedFirst()
        {
            var h = new Harness();
            h.Http.Respond("/placements", 200, Placement(1));
            var client = h.Client();
            Run(client.ResolvePlacement("main"));

            client.Logout();
            h.Http.Respond("/placements", 200, Placement(2));
            Assert.AreEqual(2, Run(client.ResolvePlacement("main")).Revision);
        }

        [Test]
        public void SetAttributesAndPurchasesInvalidateTheCache()
        {
            var h = new Harness();
            h.Http.Respond("/placements", 200, Placement(1));
            h.Http.Respond("/purchases", 200, "{\"customerId\":\"\"}");
            var client = h.Client();
            Run(client.ResolvePlacement("main"));

            h.Now += 1;
            client.SetAttributes(new Dictionary<string, object> { ["plan"] = "pro" }).GetAwaiter().GetResult();
            h.Http.Respond("/placements", 200, Placement(2));
            Assert.AreEqual(2, Run(client.ResolvePlacement("main")).Revision);

            h.Now += 1;
            Run(client.RegisterPurchase(new RegisterPurchaseInput
            {
                Source = RevnixStore.Apple, Token = "t", ProductId = "p", TransactionId = "x",
            }));
            h.Http.Respond("/placements", 200, Placement(3));
            Assert.AreEqual(3, Run(client.ResolvePlacement("main")).Revision);
        }

        [Test]
        public void ARefreshInFlightWhenAttributesChangeIsNotServedCacheFirst()
        {
            var h = new Harness();
            h.Http.Respond("/placements", 200, Placement(1));
            var client = h.Client();
            Run(client.ResolvePlacement("main"));

            h.Now += 1;
            var inFlight = new TaskCompletionSource<RevnixHttpResponse>();
            h.Http.Routes["/placements"] = () => inFlight.Task;
            Run(client.ResolvePlacement("main"));
            h.Now += 1;
            client.SetAttributes(new Dictionary<string, object> { ["plan"] = "pro" }).GetAwaiter().GetResult();
            h.Now += 1;
            inFlight.SetResult(new RevnixHttpResponse(200, Placement(2)));
            StringAssert.Contains("\"revision\":2", h.Storage.Get("revnix.placement.main"));

            h.Http.Respond("/placements", 200, Placement(3));
            Assert.AreEqual(3, Run(client.ResolvePlacement("main")).Revision);
        }

        [Test]
        public void PreloadFillsTheCacheSoTheNextResolveSkipsTheNetwork()
        {
            var h = new Harness();
            h.Http.Respond("/placements/main", 200, Placement(1));
            h.Http.Respond("/placements/broken", 503, "{\"error\":\"down\"}");
            var client = h.Client();

            client.Preload(new[] { "main", "broken" }).GetAwaiter().GetResult();
            Assert.AreEqual(2, h.Http.CallsTo("/placements"));
            Assert.IsTrue(h.Diagnostics.Exists(d => d.Op == "resolvePlacement"));

            h.Http.Hang("/placements/main");
            var resolution = client.ResolvePlacement("main");
            Assert.IsTrue(resolution.IsCompleted);
            Assert.AreEqual("off_1", Run(resolution).Offering.OfferingId);
        }

        [Test]
        public void EntitlementsWithACachedSnapshotUseTheShortTimeout()
        {
            var h = new Harness();
            h.Http.Respond("/entitlements", 200, EntitlementsBody);
            var client = h.Client(TimeSpan.Zero);

            Run(client.Entitlements());
            Assert.AreEqual(10_000, h.Http.Calls[h.Http.Calls.Count - 1].timeoutMs);
            Run(client.Entitlements());
            Assert.AreEqual(3_000, h.Http.Calls[h.Http.Calls.Count - 1].timeoutMs);
        }

        [Test]
        public void ThePurchaseQueueKeepsTheNewest100()
        {
            var h = new Harness();
            h.Http.Offline("/purchases");
            var client = h.Client();
            for (var i = 0; i <= 100; i++)
            {
                Assert.Throws<RevnixNetworkException>(() => Run(client.RegisterPurchase(new RegisterPurchaseInput
                {
                    Source = RevnixStore.Apple, Token = "t" + i, ProductId = "p", TransactionId = "x" + i,
                })));
            }

            Assert.AreEqual(100, client.PendingPurchaseCount());
            var raw = h.Storage.Get("revnix.pendingPurchases");
            StringAssert.DoesNotContain("apple:t0:x0", raw);
            StringAssert.Contains("apple:t100:x100", raw);
            Assert.IsTrue(h.Diagnostics.Exists(d => d.Message.Contains("queue full")));
        }

        [Test]
        public void QueuedPurchasesOlderThan30DaysAreDroppedAndUnstampedOnesCountAsNew()
        {
            var h = new Harness();
            h.Http.Offline("/purchases");
            h.Storage.Set("revnix.pendingPurchases",
                "[{\"key\":\"apple:old:1\",\"source\":\"apple\",\"token\":\"old\",\"productId\":\"p\",\"transactionId\":\"1\",\"queuedAt\":" + (h.Now - 31 * DayMs) + "}," +
                "{\"key\":\"apple:recent:2\",\"source\":\"apple\",\"token\":\"recent\",\"productId\":\"p\",\"transactionId\":\"2\",\"queuedAt\":" + (h.Now - DayMs) + "}," +
                "{\"key\":\"apple:legacy:3\",\"source\":\"apple\",\"token\":\"legacy\",\"productId\":\"p\",\"transactionId\":\"3\"}]");
            var client = h.Client();

            Assert.AreEqual(0, Run(client.RetryPendingPurchases()));
            Assert.AreEqual(2, h.Http.CallsTo("/purchases"));
            Assert.AreEqual(2, client.PendingPurchaseCount());
            var raw = h.Storage.Get("revnix.pendingPurchases");
            StringAssert.DoesNotContain("apple:old:1", raw);
            StringAssert.Contains("\"queuedAt\":" + h.Now, raw);

            h.Now += 31 * DayMs;
            Run(client.RetryPendingPurchases());
            Assert.AreEqual(0, client.PendingPurchaseCount());
        }
    }
}
