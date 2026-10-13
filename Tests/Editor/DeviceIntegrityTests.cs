using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class DeviceIntegrityTests
    {
        private sealed class FakeHttp : IRevnixHttp
        {
            public readonly List<(string url, string body)> Calls = new List<(string, string)>();

            public Task<RevnixHttpResponse> Send(
                string method, string url, string jsonBody,
                IReadOnlyDictionary<string, string> headers, int timeoutMs)
            {
                Calls.Add((url, jsonBody));
                return Task.FromResult(new RevnixHttpResponse(200, "{}"));
            }
        }

        private static Dictionary<string, object> Evidence() => new Dictionary<string, object>
        {
            ["platform"] = "android",
            ["token"] = "tok",
        };

        private static RevnixConfig MakeConfig(
            FakeHttp http, Func<string, Task<Dictionary<string, object>>> hook, List<RevnixDiagnostic> diagnostics = null)
        {
            return new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = http,
                Storage = new MemoryStorage(),
                DeviceIntegrity = hook,
                OnDiagnostic = d => diagnostics?.Add(d),
            };
        }

        private static Dictionary<string, object> Body((string url, string body) call) =>
            RevnixJson.ParseObject(call.body);

        private static void RunAll(RevnixClient client)
        {
            client.RegisterInstall().GetAwaiter().GetResult();
            client.HandleInstallReferrer("utm_source=x").GetAwaiter().GetResult();
            client.HandleAttributionToken("abc").GetAwaiter().GetResult();
            client.SetAttribution("adjust", "Facebook").GetAwaiter().GetResult();
        }

        [Test]
        public void EvidenceIsAttachedToEveryInstallAndAttributionReport()
        {
            var http = new FakeHttp();
            var client = new RevnixClient(MakeConfig(http, cid => Task.FromResult(Evidence())));

            RunAll(client);

            Assert.AreEqual(4, http.Calls.Count);
            StringAssert.EndsWith("/v1/attribution", http.Calls[3].url);
            foreach (var call in http.Calls)
            {
                var integrity = RevnixJson.GetObject(Body(call), "integrity");
                Assert.AreEqual("android", RevnixJson.GetString(integrity, "platform"));
                Assert.AreEqual("tok", RevnixJson.GetString(integrity, "token"));
            }
        }

        [Test]
        public void TheHookRunsOncePerCustomerId()
        {
            var http = new FakeHttp();
            var seen = new List<string>();
            var client = new RevnixClient(MakeConfig(http, cid =>
            {
                seen.Add(cid);
                return Task.FromResult(Evidence());
            }));

            RunAll(client);
            Assert.AreEqual(new[] { client.CustomerId() }, seen.ToArray());

            client.Logout();
            client.HandleInstallReferrer("utm_source=y").GetAwaiter().GetResult();
            Assert.AreEqual(2, seen.Count);
            Assert.AreEqual(client.CustomerId(), seen[1]);
        }

        [Test]
        public void AThrowingHookSendsTheReportWithoutEvidence()
        {
            var http = new FakeHttp();
            var diagnostics = new List<RevnixDiagnostic>();
            var client = new RevnixClient(MakeConfig(
                http, cid => throw new InvalidOperationException("attest broke"), diagnostics));

            client.RegisterInstall().GetAwaiter().GetResult();

            Assert.AreEqual(1, http.Calls.Count);
            Assert.IsFalse(Body(http.Calls[0]).ContainsKey("integrity"));
            Assert.AreEqual("deviceIntegrity", diagnostics[0].Op);
            Assert.AreEqual("attest broke", diagnostics[0].Message);
        }

        [Test]
        public void AFaultedHookSendsTheReportWithoutEvidence()
        {
            var http = new FakeHttp();
            var diagnostics = new List<RevnixDiagnostic>();
            var client = new RevnixClient(MakeConfig(
                http, cid => Task.FromException<Dictionary<string, object>>(new Exception("denied")), diagnostics));

            client.SetAttribution("adjust", "Facebook").GetAwaiter().GetResult();

            Assert.AreEqual(1, http.Calls.Count);
            Assert.IsFalse(Body(http.Calls[0]).ContainsKey("integrity"));
            Assert.AreEqual("deviceIntegrity", diagnostics[0].Op);
        }

        [Test]
        public void ANullAnswerSendsTheReportWithoutEvidence()
        {
            var http = new FakeHttp();
            var diagnostics = new List<RevnixDiagnostic>();
            var client = new RevnixClient(MakeConfig(
                http, cid => Task.FromResult<Dictionary<string, object>>(null), diagnostics));

            client.HandleAttributionToken("abc").GetAwaiter().GetResult();

            Assert.AreEqual(1, http.Calls.Count);
            Assert.IsFalse(Body(http.Calls[0]).ContainsKey("integrity"));
            Assert.AreEqual("deviceIntegrity", diagnostics[0].Op);
        }

        [Test]
        public void AHookThatNeverAnswersTimesOutAfterTenSeconds()
        {
            var http = new FakeHttp();
            var diagnostics = new List<RevnixDiagnostic>();
            var delays = new List<int>();
            var config = MakeConfig(
                http, cid => new TaskCompletionSource<Dictionary<string, object>>().Task, diagnostics);
            config.Delay = ms =>
            {
                delays.Add(ms);
                return Task.CompletedTask;
            };
            var client = new RevnixClient(config);

            client.RegisterInstall().GetAwaiter().GetResult();

            Assert.AreEqual(new[] { 10_000 }, delays.ToArray());
            Assert.AreEqual(1, http.Calls.Count);
            Assert.IsFalse(Body(http.Calls[0]).ContainsKey("integrity"));
            Assert.AreEqual("deviceIntegrity", diagnostics[0].Op);
            Assert.AreEqual("attestation timed out", diagnostics[0].Message);
        }

        [Test]
        public void NoHookSendsNoIntegrityKey()
        {
            var http = new FakeHttp();
            var client = new RevnixClient(MakeConfig(http, null));

            RunAll(client);

            Assert.AreEqual(4, http.Calls.Count);
            foreach (var call in http.Calls)
            {
                Assert.IsFalse(Body(call).ContainsKey("integrity"));
            }
        }
    }
}
