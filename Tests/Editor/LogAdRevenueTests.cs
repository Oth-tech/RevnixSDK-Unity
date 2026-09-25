using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class LogAdRevenueTests
    {
        private sealed class FakeHttp : IRevnixHttp
        {
            public readonly List<(string method, string url, string body)> Calls =
                new List<(string, string, string)>();

            public Task<RevnixHttpResponse> Send(
                string method, string url, string jsonBody,
                IReadOnlyDictionary<string, string> headers, int timeoutMs)
            {
                Calls.Add((method, url, jsonBody));
                return Task.FromResult(new RevnixHttpResponse(200, "{}"));
            }
        }

        private static RevnixClient MakeClient(FakeHttp http)
        {
            return new RevnixClient(new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = http,
                Storage = new MemoryStorage(),
            });
        }

        [Test]
        public void ARevenueEventPostsToAdRevenueWithTheOptionalFields()
        {
            var http = new FakeHttp();
            var client = MakeClient(http);

            client.LogAdRevenue(
                0.0032, "USD", network: "admob", mediation: "applovin_max",
                adUnit: "unit1", placement: "rewarded_end", format: "rewarded",
                eventId: "evt1").GetAwaiter().GetResult();

            Assert.AreEqual(1, http.Calls.Count);
            var call = http.Calls[0];
            Assert.AreEqual("POST", call.method);
            StringAssert.EndsWith("/v1/ad-revenue", call.url);

            var body = RevnixJson.ParseObject(call.body);
            Assert.AreEqual(0.0032, RevnixJson.GetNullableDouble(body, "revenue"));
            Assert.AreEqual("USD", RevnixJson.GetString(body, "currency"));
            Assert.AreEqual("admob", RevnixJson.GetString(body, "network"));
            Assert.AreEqual("applovin_max", RevnixJson.GetString(body, "mediation"));
            Assert.AreEqual("unit1", RevnixJson.GetString(body, "adUnit"));
            Assert.AreEqual("rewarded_end", RevnixJson.GetString(body, "placement"));
            Assert.AreEqual("rewarded", RevnixJson.GetString(body, "format"));
            Assert.AreEqual("evt1", RevnixJson.GetString(body, "eventId"));
            Assert.IsTrue(body.ContainsKey("customerId"));
            Assert.IsTrue(body.ContainsKey("sdkVersion"));
        }

        [Test]
        public void ANonPositiveRevenueSendsNothing()
        {
            var http = new FakeHttp();
            var diagnostics = new List<RevnixDiagnostic>();
            var client = new RevnixClient(new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = http,
                Storage = new MemoryStorage(),
                OnDiagnostic = d => diagnostics.Add(d),
            });

            client.LogAdRevenue(0, "USD").GetAwaiter().GetResult();

            Assert.AreEqual(0, http.Calls.Count);
            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreEqual("logAdRevenue", diagnostics[0].Op);
        }
    }
}
