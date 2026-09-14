using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class RevnixPreviewTests
    {
        private const string Token =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private const string PreviewBody =
            "{\"placementKey\":\"revnix_preview\",\"revision\":null,\"offering\":null," +
            "\"paywall\":{\"paywallId\":\"pw_preview\",\"name\":\"Preview\",\"config\":{}}," +
            "\"experiment\":null,\"targeting\":null,\"preview\":true,\"expiresAt\":1800000000000}";

        private const string ConfigWithDeeplinkOpen =
            "{\"revision\":1,\"implicitPlacements\":[\"deeplink_open\"]}";

        private const string TriggeredResolution =
            "{\"status\":\"ok\",\"placementKey\":\"deeplink_open\"," +
            "\"paywall\":{\"paywallId\":\"pw_live\",\"name\":\"Live\",\"config\":{}}}";

        private sealed class FakeHttp : IRevnixHttp
        {
            public readonly List<string> Calls = new List<string>();
            public int PreviewStatus = 200;
            public string PreviewBodyOverride = PreviewBody;

            public Task<RevnixHttpResponse> Send(
                string method, string url, string jsonBody,
                IReadOnlyDictionary<string, string> headers, int timeoutMs)
            {
                Calls.Add(method + " " + url);
                if (url.Contains("/v1/paywalls/preview/"))
                {
                    return Task.FromResult(new RevnixHttpResponse(PreviewStatus, PreviewBodyOverride));
                }
                if (url.Contains("/v1/config"))
                {
                    return Task.FromResult(new RevnixHttpResponse(200, ConfigWithDeeplinkOpen));
                }
                if (url.Contains("/v1/placements/triggered"))
                {
                    return Task.FromResult(new RevnixHttpResponse(200, TriggeredResolution));
                }
                return Task.FromResult(new RevnixHttpResponse(200, "{}"));
            }
        }

        private static RevnixClient MakeClient(FakeHttp http, out List<RevnixImplicitTrigger> seen)
        {
            var captured = new List<RevnixImplicitTrigger>();
            seen = captured;
            var config = new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = http,
                OnImplicitPaywall = t => captured.Add(t),
            };
            return new RevnixClient(config);
        }

        [Test]
        public void APreviewLinkFetchesThePreviewAndNeverTriggersDeeplinkOpen()
        {
            var http = new FakeHttp();
            var client = MakeClient(http, out var seen);

            client.HandleDeepLink("voigu://revnix-preview?revnix_preview=" + Token).GetAwaiter().GetResult();

            Assert.IsTrue(http.Calls.Exists(c => c.Contains("/v1/paywalls/preview/" + Token)));
            Assert.IsFalse(http.Calls.Exists(c => c.Contains("/v1/placements/triggered")));
            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual(RevnixImplicitPlacement.DeeplinkOpen, seen[0].Placement);
            Assert.AreEqual("revnix_preview", seen[0].Resolution.PlacementKey);
            Assert.IsTrue(seen[0].Resolution.Preview);
        }

        [Test]
        public void APreviewBodyParsesTolerantly()
        {
            var http = new FakeHttp();
            var client = MakeClient(http, out var seen);

            client.HandleDeepLink("voigu://revnix-preview?revnix_preview=" + Token).GetAwaiter().GetResult();

            var resolution = seen[0].Resolution;
            Assert.AreEqual(0, resolution.Revision);
            Assert.IsNotNull(resolution.Offering);
            Assert.AreEqual("pw_preview", resolution.Paywall.PaywallId);
        }

        [Test]
        public void AMalformedTokenFallsThroughToTheOrdinaryDeepLinkPath()
        {
            var http = new FakeHttp();
            var client = MakeClient(http, out var seen);

            client.HandleDeepLink("voigu://revnix-preview?revnix_preview=not-hex").GetAwaiter().GetResult();

            Assert.IsFalse(http.Calls.Exists(c => c.Contains("/v1/paywalls/preview/")));
            Assert.IsTrue(http.Calls.Exists(c => c.Contains("/v1/placements/triggered")));
            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual("deeplink_open", seen[0].Resolution.PlacementKey);
        }

        [Test]
        public void AnOrdinaryUrlIsUnaffected()
        {
            var http = new FakeHttp();
            var client = MakeClient(http, out var seen);

            client.HandleDeepLink("https://example.com/promo").GetAwaiter().GetResult();

            Assert.IsFalse(http.Calls.Exists(c => c.Contains("/v1/paywalls/preview/")));
            Assert.IsTrue(http.Calls.Exists(c => c.Contains("/v1/placements/triggered")));
            Assert.AreEqual(1, seen.Count);
        }

        [Test]
        public void A404PreviewNeverThrowsAndNeverPresents()
        {
            var http = new FakeHttp { PreviewStatus = 404, PreviewBodyOverride = "{\"error\":\"not found\"}" };
            var client = MakeClient(http, out var seen);

            client.HandleDeepLink("voigu://revnix-preview?revnix_preview=" + Token).GetAwaiter().GetResult();

            Assert.AreEqual(0, seen.Count);
        }

        [Test]
        public void LogPaywallShownForAPreviewSendsNoRequest()
        {
            var http = new FakeHttp();
            var client = MakeClient(http, out _);

            client.LogPaywallShown("revnix_preview", "pw_preview").GetAwaiter().GetResult();
            client.LogPaywallClosed("v", "revnix_preview", "pw_preview").GetAwaiter().GetResult();

            Assert.AreEqual(0, http.Calls.Count);
        }
    }
}
