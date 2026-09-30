using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class TrackTests
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
        public void ACustomEventPostsToEventsWithProperties()
        {
            var http = new FakeHttp();
            var client = MakeClient(http);

            client.Track(
                "level_up",
                new Dictionary<string, object> { ["level"] = 5 }).GetAwaiter().GetResult();

            Assert.AreEqual(1, http.Calls.Count);
            var call = http.Calls[0];
            Assert.AreEqual("POST", call.method);
            StringAssert.EndsWith("/v1/events", call.url);

            var body = RevnixJson.ParseObject(call.body);
            Assert.AreEqual("level_up", RevnixJson.GetString(body, "event"));
            var properties = RevnixJson.GetObject(body, "properties");
            Assert.AreEqual(5, RevnixJson.GetNullableLong(properties, "level"));
            Assert.IsTrue(body.ContainsKey("customerId"));
            Assert.IsTrue(body.ContainsKey("occurredAt"));
            Assert.IsFalse(string.IsNullOrEmpty(RevnixJson.GetString(body, "eventId")));
        }

        [Test]
        public void AnInvalidEventNameSendsNothing()
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

            client.Track("Level Up").GetAwaiter().GetResult();

            Assert.AreEqual(0, http.Calls.Count);
            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreEqual("track", diagnostics[0].Op);
        }

        [Test]
        public void UnsupportedPropertyValuesAreDropped()
        {
            var http = new FakeHttp();
            var client = MakeClient(http);

            client.Track(
                "level_up",
                new Dictionary<string, object>
                {
                    ["level"] = 5,
                    ["score"] = double.NaN,
                    ["price"] = 1.5m,
                }).GetAwaiter().GetResult();

            var properties = RevnixJson.GetObject(RevnixJson.ParseObject(http.Calls[0].body), "properties");
            Assert.AreEqual(5, RevnixJson.GetNullableLong(properties, "level"));
            Assert.IsFalse(properties.ContainsKey("score"));
            Assert.IsFalse(properties.ContainsKey("price"));
        }
    }
}
