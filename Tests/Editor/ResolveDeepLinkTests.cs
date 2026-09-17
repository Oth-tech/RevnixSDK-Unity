using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class ResolveDeepLinkTests
    {
        private sealed class FakeHttp : IRevnixHttp
        {
            public readonly List<(string method, string url, string body)> Calls =
                new List<(string, string, string)>();
            public string ResponseBody = "{}";
            public Exception ThrowOnSend;

            public Task<RevnixHttpResponse> Send(
                string method, string url, string jsonBody,
                IReadOnlyDictionary<string, string> headers, int timeoutMs)
            {
                if (ThrowOnSend != null) throw ThrowOnSend;
                Calls.Add((method, url, jsonBody));
                return Task.FromResult(new RevnixHttpResponse(200, ResponseBody));
            }
        }

        private static RevnixClient MakeClient(
            FakeHttp http, out List<RevnixDiagnostic> diagnostics)
        {
            var captured = new List<RevnixDiagnostic>();
            diagnostics = captured;
            var config = new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = http,
                OnDiagnostic = captured.Add,
            };
            return new RevnixClient(config);
        }

        [Test]
        public void ResolveDeepLinkReturnsTheResolvedUrlAndRequestsItByEscapedQuery()
        {
            var http = new FakeHttp
            {
                ResponseBody = "{\"url\":\"com.voigu.app://promo?utm_source=x\",\"hops\":2}",
            };
            var client = MakeClient(http, out _);

            var result = client.ResolveDeepLink("https://click.example.com/abc").GetAwaiter().GetResult();

            Assert.AreEqual("com.voigu.app://promo?utm_source=x", result);
            Assert.AreEqual(1, http.Calls.Count);
            Assert.AreEqual("GET", http.Calls[0].method);
            StringAssert.Contains(
                "/v1/links/resolve?url=" + Uri.EscapeDataString("https://click.example.com/abc"),
                http.Calls[0].url);
        }

        [Test]
        public void AThrowingTransportReturnsTheInputAndReportsADiagnostic()
        {
            var http = new FakeHttp { ThrowOnSend = new InvalidOperationException("boom") };
            var client = MakeClient(http, out var diagnostics);

            var result = client.ResolveDeepLink("https://click.example.com/abc").GetAwaiter().GetResult();

            Assert.AreEqual("https://click.example.com/abc", result);
            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreEqual("resolveDeepLink", diagnostics[0].Op);
        }

        [Test]
        public void AnAppSchemeUrlIsReturnedUnchangedWithNoRequest()
        {
            var http = new FakeHttp();
            var client = MakeClient(http, out _);

            var result = client.ResolveDeepLink("com.voigu.app://promo?utm_source=x")
                .GetAwaiter().GetResult();

            Assert.AreEqual("com.voigu.app://promo?utm_source=x", result);
            Assert.AreEqual(0, http.Calls.Count);
        }

        [Test]
        public void AnOverlongHttpsUrlIsReturnedUnchangedWithNoRequest()
        {
            var http = new FakeHttp();
            var client = MakeClient(http, out _);
            var longUrl = "https://click.example.com/" + new string('a', 1000);
            Assert.Greater(longUrl.Length, 1024);

            var result = client.ResolveDeepLink(longUrl).GetAwaiter().GetResult();

            Assert.AreEqual(longUrl, result);
            Assert.AreEqual(0, http.Calls.Count);
        }

        [Test]
        public void NullEmptyOrWhitespaceInputIsReturnedUnchangedWithNoRequest()
        {
            var http = new FakeHttp();
            var client = MakeClient(http, out _);

            Assert.IsNull(client.ResolveDeepLink(null).GetAwaiter().GetResult());
            Assert.AreEqual("", client.ResolveDeepLink("").GetAwaiter().GetResult());
            Assert.AreEqual("   ", client.ResolveDeepLink("   ").GetAwaiter().GetResult());
            Assert.AreEqual(0, http.Calls.Count);
        }
    }
}
