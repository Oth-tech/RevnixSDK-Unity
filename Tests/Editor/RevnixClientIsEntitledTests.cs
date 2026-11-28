using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class RevnixClientIsEntitledTests
    {
        private const string EntitlementsBody =
            "{\"customerId\":\"cus_1\",\"cursor\":7,\"entitlements\":[" +
            "{\"entitlementId\":\"pro\",\"isActive\":true,\"sources\":[]}]}";

        private const string CustomerId = "cus_1";
        private const long Now = 1_700_000_000_000L;
        private static readonly TestSigningKey Key = new TestSigningKey();

        private static RevnixHttpResponse Signed(string body) => Key.Response(body, CustomerId, Now);

        private class FakeHttp : IRevnixHttp
        {
            private readonly Queue<RevnixHttpResponse> _responses;

            public FakeHttp(params RevnixHttpResponse[] responses)
            {
                _responses = new Queue<RevnixHttpResponse>(responses);
            }

            public Task<RevnixHttpResponse> Send(
                string method, string url, string jsonBody,
                IReadOnlyDictionary<string, string> headers, int timeoutMs)
            {
                return Task.FromResult(_responses.Count > 1 ? _responses.Dequeue() : _responses.Peek());
            }
        }

        private static RevnixClient MakeClient(IRevnixHttp http)
        {
            var storage = new MemoryStorage();
            storage.Set("revnix.customerId", CustomerId);
            var config = new RevnixConfig
            {
                ApiKey = TestSigningKey.ApiKey,
                BaseUrl = "https://x",
                Http = http,
                Storage = storage,
                Now = () => Now,
                EntitlementsTtl = System.TimeSpan.Zero,
                EntitlementSigningKeys = new[] { Key.Spki },
            };
            return new RevnixClient(config);
        }

        [Test]
        public void IsEntitledAnswersFalseForARevokedKeyEvenWithAnActiveEntitlementCached()
        {
            var http = new FakeHttp(
                Signed(EntitlementsBody),
                new RevnixHttpResponse(401, "{\"error\":\"revoked\"}"));
            var client = MakeClient(http);

            client.Entitlements().GetAwaiter().GetResult();
            var entitled = client.IsEntitled("pro").GetAwaiter().GetResult();

            Assert.IsFalse(entitled);
        }

        [Test]
        public void IsEntitledStillServesTheCacheOnATransientFailure()
        {
            var http = new FakeHttp(
                Signed(EntitlementsBody),
                new RevnixHttpResponse(503, "{\"error\":\"unavailable\"}"));
            var client = MakeClient(http);

            client.Entitlements().GetAwaiter().GetResult();
            var entitled = client.IsEntitled("pro").GetAwaiter().GetResult();

            Assert.IsTrue(entitled);
        }
    }
}
