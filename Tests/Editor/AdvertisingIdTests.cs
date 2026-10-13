using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class AdvertisingIdTests
    {
        private const string Gaid = "38400000-8cf0-11bd-b23e-10b96e40000d";

        private sealed class FakeHttp : IRevnixHttp
        {
            public readonly List<string> Attributes = new List<string>();

            public Task<RevnixHttpResponse> Send(
                string method, string url, string jsonBody,
                IReadOnlyDictionary<string, string> headers, int timeoutMs)
            {
                if (url.EndsWith("/attributes")) Attributes.Add(jsonBody);
                return Task.FromResult(new RevnixHttpResponse(200, "{}"));
            }
        }

        private static void Launch(FakeHttp http, MemoryStorage storage, Func<Task<string>> source)
        {
            var client = new RevnixClient(new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = http,
                Storage = storage,
                AdvertisingId = source,
            });
            client.RegisterInstall().GetAwaiter().GetResult();
        }

        [Test]
        public void StoresTheIdOncePerValueAndDeletesItAfterAnOptOut()
        {
            var http = new FakeHttp();
            var storage = new MemoryStorage();

            Launch(http, storage, () => Task.FromResult<string>(null));
            Assert.AreEqual(0, http.Attributes.Count);

            Launch(http, storage, () => Task.FromResult(Gaid));
            Launch(http, storage, () => Task.FromResult(Gaid));
            Assert.AreEqual(1, http.Attributes.Count);
            StringAssert.Contains(Gaid, http.Attributes[0]);

            Launch(http, storage, () => Task.FromResult<string>(null));
            Assert.AreEqual(2, http.Attributes.Count);
            StringAssert.Contains("\"gps_adid\":null", http.Attributes[1]);
        }

        [Test]
        public void AFailedReadKeepsTheStoredId()
        {
            var http = new FakeHttp();
            var storage = new MemoryStorage();
            Launch(http, storage, () => Task.FromResult(Gaid));
            Launch(http, storage, () => throw new InvalidOperationException("play services updating"));
            Launch(http, storage, () => new TaskCompletionSource<string>().Task);
            Assert.AreEqual(1, http.Attributes.Count);
        }
    }
}
