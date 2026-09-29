using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class SetPushTokenTests
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
                Device = new DeviceFacts { Platform = "ios" },
            });
        }

        [Test]
        public void APushTokenPostsToPushTokenThenAnIdenticalRepeatSendsNothing()
        {
            var http = new FakeHttp();
            var client = MakeClient(http);

            client.SetPushToken("abc123").GetAwaiter().GetResult();

            Assert.AreEqual(1, http.Calls.Count);
            var call = http.Calls[0];
            Assert.AreEqual("POST", call.method);
            StringAssert.EndsWith("/v1/push-token", call.url);

            var body = RevnixJson.ParseObject(call.body);
            Assert.AreEqual("ios", RevnixJson.GetString(body, "platform"));
            Assert.AreEqual("abc123", RevnixJson.GetString(body, "token"));
            Assert.IsTrue(body.ContainsKey("customerId"));
            Assert.IsTrue(body.ContainsKey("sdkVersion"));

            client.SetPushToken("abc123").GetAwaiter().GetResult();
            Assert.AreEqual(1, http.Calls.Count);

            client.SetPushToken("def456").GetAwaiter().GetResult();
            Assert.AreEqual(2, http.Calls.Count);

            client.Logout();
            client.SetPushToken("def456").GetAwaiter().GetResult();
            Assert.AreEqual(3, http.Calls.Count);
        }

        [Test]
        public void AnUnsupportedPlatformIsNeverSent()
        {
            var http = new FakeHttp();
            var client = new RevnixClient(new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = http,
                Storage = new MemoryStorage(),
                Device = new DeviceFacts { Platform = "windows" },
            });

            client.SetPushToken("abc123").GetAwaiter().GetResult();

            Assert.AreEqual(0, http.Calls.Count);
        }
    }
}
