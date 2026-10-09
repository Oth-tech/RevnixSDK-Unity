using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class DeferredDeepLinkTests
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

        private static RevnixClient MakeClient(
            FakeHttp http, out List<(string url, DeferredDeepLinkMatch match)> seen, DeviceFacts device = null)
        {
            var captured = new List<(string, DeferredDeepLinkMatch)>();
            seen = captured;
            var config = new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = http,
                Device = device,
                OnDeferredDeepLink = (url, match) => captured.Add((url, match)),
            };
            return new RevnixClient(config);
        }

        [Test]
        public void HandleInstallReferrerPostsTheReferrerVerbatim()
        {
            var http = new FakeHttp();
            var client = MakeClient(http, out _);

            client.HandleInstallReferrer("utm_source=instagram&utm_medium=cpc").GetAwaiter().GetResult();

            var call = http.Calls.Find(c => c.url.Contains("/v1/installs"));
            StringAssert.Contains("\"installReferrer\":\"utm_source=instagram&utm_medium=cpc\"", call.body);
        }

        [Test]
        public void HandleInstallReferrerSendsPlatformAndAppVersionWhenGiven()
        {
            var http = new FakeHttp();
            var client = MakeClient(http, out _);

            client.HandleInstallReferrer("utm_source=instagram", platform: "android", appVersion: "1.2.3")
                .GetAwaiter().GetResult();

            var call = http.Calls.Find(c => c.url.Contains("/v1/installs"));
            StringAssert.Contains("\"platform\":\"android\"", call.body);
            StringAssert.Contains("\"appVersion\":\"1.2.3\"", call.body);
        }

        [Test]
        public void HandleInstallReferrerIncludesDeviceKeyWhenSet()
        {
            var http = new FakeHttp();
            var client = MakeClient(http, out _, new DeviceFacts { DeviceKey = "dev-key-1" });

            client.HandleInstallReferrer("utm_source=instagram").GetAwaiter().GetResult();

            var call = http.Calls.Find(c => c.url.Contains("/v1/installs"));
            StringAssert.Contains("\"deviceKey\":\"dev-key-1\"", call.body);
        }

        [Test]
        public void HandleInstallReferrerOmitsDeviceKeyWhenNull()
        {
            var http = new FakeHttp();
            var client = MakeClient(http, out _);

            client.HandleInstallReferrer("utm_source=instagram").GetAwaiter().GetResult();

            var call = http.Calls.Find(c => c.url.Contains("/v1/installs"));
            StringAssert.DoesNotContain("deviceKey", call.body);
        }

        [Test]
        public void HandleInstallReferrerTruncatesAt1024()
        {
            var http = new FakeHttp();
            var client = MakeClient(http, out _);
            var longReferrer = new string('a', 2000);

            client.HandleInstallReferrer(longReferrer).GetAwaiter().GetResult();

            var call = http.Calls.Find(c => c.url.Contains("/v1/installs"));
            StringAssert.Contains("\"installReferrer\":\"" + new string('a', 1024) + "\"", call.body);
            StringAssert.DoesNotContain(new string('a', 1025), call.body);
        }

        [Test]
        public void HandleInstallReferrerRefusesBlank()
        {
            var http = new FakeHttp();
            var client = MakeClient(http, out _);

            client.HandleInstallReferrer("").GetAwaiter().GetResult();
            client.HandleInstallReferrer("   ").GetAwaiter().GetResult();
            client.HandleInstallReferrer(null).GetAwaiter().GetResult();

            Assert.AreEqual(0, http.Calls.Count);
        }

        [Test]
        public void ADeferredDeepLinkFiresOnceWithExactMatch()
        {
            var http = new FakeHttp
            {
                InstallsResponseBody =
                    "{\"deferredDeepLink\":{\"url\":\"https://x.app/promo\",\"match\":\"exact\"}}",
            };
            var client = MakeClient(http, out var seen);

            client.HandleInstallReferrer("utm_source=instagram").GetAwaiter().GetResult();

            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual("https://x.app/promo", seen[0].url);
            Assert.AreEqual(DeferredDeepLinkMatch.Exact, seen[0].match);
        }

        [Test]
        public void ASecondResponseCarryingTheSameLinkDoesNotFireAgain()
        {
            var http = new FakeHttp
            {
                InstallsResponseBody =
                    "{\"deferredDeepLink\":{\"url\":\"https://x.app/promo\",\"match\":\"exact\"}}",
            };
            var client = MakeClient(http, out var seen);

            client.HandleInstallReferrer("utm_source=instagram").GetAwaiter().GetResult();
            client.HandleInstallReferrer("utm_source=instagram").GetAwaiter().GetResult();

            Assert.AreEqual(1, seen.Count);
        }

        [Test]
        public void NoDeferredDeepLinkInTheResponseFiresNothing()
        {
            var http = new FakeHttp { InstallsResponseBody = "{}" };
            var client = MakeClient(http, out var seen);

            client.HandleInstallReferrer("utm_source=instagram").GetAwaiter().GetResult();

            Assert.AreEqual(0, seen.Count);
        }

        [Test]
        public void AThrowingHandlerIsSwallowed()
        {
            var http = new FakeHttp
            {
                InstallsResponseBody =
                    "{\"deferredDeepLink\":{\"url\":\"https://x.app/promo\",\"match\":\"exact\"}}",
            };
            var config = new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = http,
                OnDeferredDeepLink = (url, match) => throw new InvalidOperationException("boom"),
            };
            var client = new RevnixClient(config);

            Assert.DoesNotThrow(() =>
                client.HandleInstallReferrer("utm_source=instagram").GetAwaiter().GetResult());
        }

        [Test]
        public void AnUnknownMatchIsIgnored()
        {
            var http = new FakeHttp
            {
                InstallsResponseBody =
                    "{\"deferredDeepLink\":{\"url\":\"https://x.app/promo\",\"match\":\"quantum\"}}",
            };
            var client = MakeClient(http, out var seen);

            client.HandleInstallReferrer("utm_source=instagram").GetAwaiter().GetResult();

            Assert.AreEqual(0, seen.Count);
        }

        [Test]
        public void RegisterInstallDeliversAProbabilisticMatch()
        {
            var http = new FakeHttp
            {
                InstallsResponseBody =
                    "{\"deferredDeepLink\":{\"url\":\"https://x.app/promo\",\"match\":\"probabilistic\"}}",
            };
            var client = MakeClient(http, out var seen);

            client.RegisterInstall().GetAwaiter().GetResult();

            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual("https://x.app/promo", seen[0].url);
            Assert.AreEqual(DeferredDeepLinkMatch.Probabilistic, seen[0].match);
        }

        [Test]
        public void RegisterInstallIncludesDeviceKeyWhenSet()
        {
            var http = new FakeHttp();
            var client = MakeClient(http, out _, new DeviceFacts { DeviceKey = "dev-key-1" });

            client.RegisterInstall().GetAwaiter().GetResult();

            var call = http.Calls.Find(c => c.url.Contains("/v1/installs"));
            StringAssert.Contains("\"deviceKey\":\"dev-key-1\"", call.body);
        }

        [Test]
        public void RegisterInstallOmitsDeviceKeyWhenNull()
        {
            var http = new FakeHttp();
            var client = MakeClient(http, out _);

            client.RegisterInstall().GetAwaiter().GetResult();

            var call = http.Calls.Find(c => c.url.Contains("/v1/installs"));
            StringAssert.DoesNotContain("deviceKey", call.body);
        }

        [Test]
        public void NoCallbackLeavesTheFlagUntouched()
        {
            var http = new FakeHttp
            {
                InstallsResponseBody =
                    "{\"deferredDeepLink\":{\"url\":\"https://x.app/promo\",\"match\":\"exact\"}}",
            };
            var storage = new MemoryStorage();
            var config = new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = http,
                Storage = storage,
            };
            var client = new RevnixClient(config);

            client.HandleInstallReferrer("utm_source=instagram").GetAwaiter().GetResult();

            Assert.IsNull(storage.Get("revnix.deferredDeepLinkDelivered"));
        }

        [Test]
        public void GetLastDeepLinkIsNullBeforeAnyLink()
        {
            var client = MakeClient(new FakeHttp(), out _);

            Assert.IsNull(client.GetLastDeepLink());
        }

        [Test]
        public void HandleDeepLinkStoresTheUrlAndReceivedAt()
        {
            var client = MakeClient(new FakeHttp(), out _);

            client.HandleDeepLink("com.voigu.app://promo?utm_source=email").GetAwaiter().GetResult();

            var last = client.GetLastDeepLink();
            Assert.IsNotNull(last);
            Assert.AreEqual("com.voigu.app://promo?utm_source=email", last.Url);
            Assert.Greater(last.ReceivedAt, 0);
        }

        [Test]
        public void ALaterLinkOverwritesTheEarlierOne()
        {
            var client = MakeClient(new FakeHttp(), out _);

            client.HandleDeepLink("com.voigu.app://a").GetAwaiter().GetResult();
            client.HandleDeepLink("com.voigu.app://b").GetAwaiter().GetResult();

            Assert.AreEqual("com.voigu.app://b", client.GetLastDeepLink().Url);
        }

        [Test]
        public void APreviewLinkIsNotStored()
        {
            var client = MakeClient(new FakeHttp(), out _);

            client.HandleDeepLink(
                "com.voigu.app://revnix-preview?revnix_preview=" + new string('a', 64))
                .GetAwaiter().GetResult();

            Assert.IsNull(client.GetLastDeepLink());
        }

        [Test]
        public void ADeliveredDeferredDeepLinkIsStored()
        {
            var http = new FakeHttp
            {
                InstallsResponseBody =
                    "{\"deferredDeepLink\":{\"url\":\"https://x.app/promo\",\"match\":\"exact\"}}",
            };
            var client = MakeClient(http, out _);

            client.HandleInstallReferrer("utm_source=instagram").GetAwaiter().GetResult();

            Assert.AreEqual("https://x.app/promo", client.GetLastDeepLink().Url);
        }

        [Test]
        public void AMalformedStoredValueAnswersNull()
        {
            var storage = new MemoryStorage();
            storage.Set("revnix.lastDeepLink", "{not json");
            var config = new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = new FakeHttp(),
                Storage = storage,
            };
            var client = new RevnixClient(config);

            Assert.IsNull(client.GetLastDeepLink());
        }
    }
}
