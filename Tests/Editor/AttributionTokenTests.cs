using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class AttributionTokenTests
    {
        private sealed class FakeHttp : IRevnixHttp
        {
            public readonly List<(string method, string url, string body)> Calls =
                new List<(string, string, string)>();
            public string InstallsResponseBody = "{}";

            public Task<RevnixHttpResponse> Send(
                string method, string url, string jsonBody,
                IReadOnlyDictionary<string, string> headers, int timeoutMs)
            {
                Calls.Add((method, url, jsonBody));
                if (url.Contains("/v1/installs"))
                {
                    return Task.FromResult(new RevnixHttpResponse(200, InstallsResponseBody));
                }
                return Task.FromResult(new RevnixHttpResponse(200, "{}"));
            }
        }

        private static RevnixClient MakeClient(FakeHttp http, DeviceFacts device = null)
        {
            var config = new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = http,
                Device = device,
            };
            return new RevnixClient(config);
        }

        [Test]
        public void HandleAttributionTokenPostsTheTokenVerbatim()
        {
            var http = new FakeHttp();
            var client = MakeClient(http);

            client.HandleAttributionToken("abc123").GetAwaiter().GetResult();

            var call = http.Calls.Find(c => c.url.Contains("/v1/installs"));
            StringAssert.Contains("\"attributionToken\":\"abc123\"", call.body);
        }

        [Test]
        public void HandleAttributionTokenTruncatesAt2048()
        {
            var http = new FakeHttp();
            var client = MakeClient(http);
            var longToken = new string('a', 3000);

            client.HandleAttributionToken(longToken).GetAwaiter().GetResult();

            var call = http.Calls.Find(c => c.url.Contains("/v1/installs"));
            StringAssert.Contains("\"attributionToken\":\"" + new string('a', 2048) + "\"", call.body);
            StringAssert.DoesNotContain(new string('a', 2049), call.body);
        }

        [Test]
        public void HandleAttributionTokenRefusesBlank()
        {
            var http = new FakeHttp();
            var client = MakeClient(http);

            client.HandleAttributionToken("").GetAwaiter().GetResult();
            client.HandleAttributionToken("   ").GetAwaiter().GetResult();
            client.HandleAttributionToken(null).GetAwaiter().GetResult();

            Assert.AreEqual(0, http.Calls.Count);
        }

        [Test]
        public void HandleAttributionTokenReturnsNullForBlank()
        {
            var client = MakeClient(new FakeHttp());

            var result = client.HandleAttributionToken(" ").GetAwaiter().GetResult();

            Assert.IsNull(result);
        }

        [TestCase("resolved")]
        [TestCase("organic")]
        [TestCase("pending")]
        public void HandleAttributionTokenReturnsTheServerVerdict(string verdict)
        {
            var http = new FakeHttp
            {
                InstallsResponseBody = "{\"appleAttribution\":\"" + verdict + "\"}",
            };
            var client = MakeClient(http);

            var result = client.HandleAttributionToken("abc123").GetAwaiter().GetResult();

            Assert.AreEqual(verdict, result);
        }

        [Test]
        public void HandleAttributionTokenSendsPlatformAndAppVersionWhenGiven()
        {
            var http = new FakeHttp();
            var client = MakeClient(http);

            client.HandleAttributionToken("abc123", platform: "ios", appVersion: "1.2.3")
                .GetAwaiter().GetResult();

            var call = http.Calls.Find(c => c.url.Contains("/v1/installs"));
            StringAssert.Contains("\"platform\":\"ios\"", call.body);
            StringAssert.Contains("\"appVersion\":\"1.2.3\"", call.body);
        }

        [Test]
        public void HandleAttributionTokenIncludesDeviceKeyWhenSet()
        {
            var http = new FakeHttp();
            var client = MakeClient(http, new DeviceFacts { DeviceKey = "dev-key-1" });

            client.HandleAttributionToken("abc123").GetAwaiter().GetResult();

            var call = http.Calls.Find(c => c.url.Contains("/v1/installs"));
            StringAssert.Contains("\"deviceKey\":\"dev-key-1\"", call.body);
        }

        [Test]
        public void HandleAttributionTokenOmitsDeviceKeyWhenNull()
        {
            var http = new FakeHttp();
            var client = MakeClient(http);

            client.HandleAttributionToken("abc123").GetAwaiter().GetResult();

            var call = http.Calls.Find(c => c.url.Contains("/v1/installs"));
            StringAssert.DoesNotContain("deviceKey", call.body);
        }
    }
}
