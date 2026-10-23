using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class IdentifyTests
    {
        private sealed class NoHttp : IRevnixHttp
        {
            public Task<RevnixHttpResponse> Send(
                string method, string url, string jsonBody,
                IReadOnlyDictionary<string, string> headers, int timeoutMs)
            {
                return Task.FromResult(new RevnixHttpResponse(200, "{}"));
            }
        }

        private static RevnixClient MakeClient(IRevnixStorage storage)
        {
            return new RevnixClient(new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = new NoHttp(),
                Storage = storage,
                Device = new DeviceFacts { Platform = "ios" },
            });
        }

        [Test]
        public void IdentifyTrimsAdoptsAndPersistsAcrossANewClient()
        {
            var storage = new MemoryStorage();
            var client = MakeClient(storage);

            Assert.AreEqual("user_42", client.Identify("  user_42 "));
            Assert.AreEqual("user_42", client.CustomerId());
            Assert.AreEqual("user_42", MakeClient(storage).CustomerId());
        }

        [Test]
        public void EmptyBlankNullAndOverlongIdsAreIgnored()
        {
            var client = MakeClient(new MemoryStorage());
            var anon = client.CustomerId();

            Assert.IsNotEmpty(anon);
            Assert.AreEqual(anon, client.Identify(""));
            Assert.AreEqual(anon, client.Identify("   "));
            Assert.AreEqual(anon, client.Identify(null));
            Assert.AreEqual(anon, client.Identify(new string('x', 201)));
            Assert.AreEqual(anon, client.CustomerId());
            Assert.AreEqual(new string('x', 200), client.Identify(new string('x', 200)));
        }
    }
}
