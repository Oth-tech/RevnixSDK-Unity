using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class SetAttributionTests
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
        public void AnAttributionPostsToAttributionWithOnlyTheSetOptionalFields()
        {
            var http = new FakeHttp();
            var client = MakeClient(http);

            client.SetAttribution(
                "adjust", "Facebook Installs", campaign: "summer_sale").GetAwaiter().GetResult();

            Assert.AreEqual(1, http.Calls.Count);
            var call = http.Calls[0];
            Assert.AreEqual("POST", call.method);
            StringAssert.EndsWith("/v1/attribution", call.url);

            var body = RevnixJson.ParseObject(call.body);
            Assert.AreEqual("adjust", RevnixJson.GetString(body, "provider"));
            Assert.AreEqual("Facebook Installs", RevnixJson.GetString(body, "network"));
            Assert.AreEqual("summer_sale", RevnixJson.GetString(body, "campaign"));
            Assert.IsFalse(body.ContainsKey("adGroup"));
            Assert.IsFalse(body.ContainsKey("creative"));
            Assert.IsTrue(body.ContainsKey("customerId"));
            Assert.IsTrue(body.ContainsKey("sdkVersion"));
        }

        [Test]
        public void AnIdenticalRepeatSendsNothingButAChangedPayloadSendsAgain()
        {
            var http = new FakeHttp();
            var client = MakeClient(http);

            client.SetAttribution("adjust", "Facebook Installs").GetAwaiter().GetResult();
            Assert.AreEqual(1, http.Calls.Count);

            client.SetAttribution("adjust", "Facebook Installs").GetAwaiter().GetResult();
            Assert.AreEqual(1, http.Calls.Count);

            client.SetAttribution("adjust", "Google Installs").GetAwaiter().GetResult();
            Assert.AreEqual(2, http.Calls.Count);
        }
    }
}
