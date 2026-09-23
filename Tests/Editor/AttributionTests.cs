using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class AttributionTests
    {
        private const string ClickVerdict =
            "{\"installMatch\":\"click\",\"attributedAt\":1700000000000," +
            "\"linkToken\":\"abc\",\"matchSignals\":[\"ip\",\"os\"]," +
            "\"source\":\"instagram\",\"campaign\":\"summer50\"}";

        private sealed class FakeHttp : IRevnixHttp
        {
            public readonly List<(string method, string url)> Calls =
                new List<(string, string)>();
            public string AttributionBody = "{\"installMatch\":\"unknown\"}";
            public int AttributionStatus = 200;

            public Task<RevnixHttpResponse> Send(
                string method, string url, string jsonBody,
                IReadOnlyDictionary<string, string> headers, int timeoutMs)
            {
                Calls.Add((method, url));
                if (url.Contains("/attribution"))
                {
                    return Task.FromResult(new RevnixHttpResponse(AttributionStatus, AttributionBody));
                }
                return Task.FromResult(new RevnixHttpResponse(200, "{}"));
            }
        }

        private static RevnixConfig MakeConfig(FakeHttp http)
        {
            return new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = http,
                Storage = new MemoryStorage(),
            };
        }

        private static int AttributionCalls(FakeHttp http)
            => http.Calls.FindAll(c => c.url.Contains("/attribution")).Count;

        [Test]
        public void AVerdictIsParsedAndReturned()
        {
            var http = new FakeHttp { AttributionBody = ClickVerdict };
            var client = new RevnixClient(MakeConfig(http));

            var verdict = client.GetAttribution().GetAwaiter().GetResult();

            Assert.IsNotNull(verdict);
            Assert.AreEqual("click", verdict.InstallMatch);
            Assert.AreEqual(1700000000000L, verdict.AttributedAt);
            Assert.AreEqual(0L, verdict.ReattributedAt);
            Assert.AreEqual("abc", verdict.LinkToken);
            Assert.AreEqual(new[] { "ip", "os" }, verdict.MatchSignals);
            Assert.AreEqual("instagram", verdict.Source);
            Assert.AreEqual("summer50", verdict.Campaign);
            Assert.IsNull(verdict.Medium);
            Assert.IsNull(verdict.ReferrerSource);
        }

        [Test]
        public void TheVerdictIsReadFromTheCustomersAttributionEndpoint()
        {
            var http = new FakeHttp { AttributionBody = ClickVerdict };
            var client = new RevnixClient(MakeConfig(http));

            client.GetAttribution().GetAwaiter().GetResult();

            var call = http.Calls.Find(c => c.url.Contains("/attribution"));
            Assert.AreEqual("GET", call.method);
            StringAssert.StartsWith("https://x/v1/customers/rvx_anon_", call.url);
            StringAssert.EndsWith("/attribution", call.url);
        }

        [Test]
        public void UnknownAnswersNull()
        {
            var http = new FakeHttp { AttributionBody = "{\"installMatch\":\"unknown\"}" };
            var client = new RevnixClient(MakeConfig(http));

            Assert.IsNull(client.GetAttribution().GetAwaiter().GetResult());
        }

        [Test]
        public void AFailedReadAnswersNullAndReports()
        {
            var http = new FakeHttp { AttributionStatus = 500, AttributionBody = "{}" };
            var config = MakeConfig(http);
            var diagnostics = new List<RevnixDiagnostic>();
            config.OnDiagnostic = d => diagnostics.Add(d);
            var client = new RevnixClient(config);

            Assert.IsNull(client.GetAttribution().GetAwaiter().GetResult());
            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreEqual("getAttribution", diagnostics[0].Op);
        }

        [Test]
        public void TheFirstVerdictIsDeliveredOnceAndAnIdenticalOneIsNot()
        {
            var http = new FakeHttp { AttributionBody = ClickVerdict };
            var config = MakeConfig(http);
            var seen = new List<RevnixAttribution>();
            config.OnAttribution = a => seen.Add(a);
            var client = new RevnixClient(config);

            client.GetAttribution().GetAwaiter().GetResult();
            client.GetAttribution().GetAwaiter().GetResult();

            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual("click", seen[0].InstallMatch);
        }

        [Test]
        public void AChangedVerdictIsDeliveredAgain()
        {
            var http = new FakeHttp { AttributionBody = ClickVerdict };
            var config = MakeConfig(http);
            var seen = new List<RevnixAttribution>();
            config.OnAttribution = a => seen.Add(a);
            var client = new RevnixClient(config);

            client.GetAttribution().GetAwaiter().GetResult();
            http.AttributionBody =
                "{\"installMatch\":\"referrer\",\"attributedAt\":1700000000000," +
                "\"reattributedAt\":1800000000000,\"referrerSource\":\"play\"}";
            client.GetAttribution().GetAwaiter().GetResult();

            Assert.AreEqual(2, seen.Count);
            Assert.AreEqual("referrer", seen[1].InstallMatch);
            Assert.AreEqual(1800000000000L, seen[1].ReattributedAt);
            Assert.AreEqual("play", seen[1].ReferrerSource);
        }

        [Test]
        public void AThrowingHandlerStillLeavesTheVerdictCachedAndReturned()
        {
            var http = new FakeHttp { AttributionBody = ClickVerdict };
            var config = MakeConfig(http);
            var diagnostics = new List<RevnixDiagnostic>();
            config.OnDiagnostic = d => diagnostics.Add(d);
            config.OnAttribution = a => throw new InvalidOperationException("boom");
            var client = new RevnixClient(config);

            RevnixAttribution verdict = null;
            Assert.DoesNotThrowAsync(async () => verdict = await client.GetAttribution());

            Assert.IsNotNull(verdict);
            Assert.IsNotNull(config.Storage.Get("revnix.attribution"));
            Assert.AreEqual("onAttribution", diagnostics[0].Op);
        }

        [Test]
        public void AnInstallReportDoesNotAskWithoutAHandler()
        {
            var http = new FakeHttp { AttributionBody = ClickVerdict };
            var client = new RevnixClient(MakeConfig(http));

            client.RegisterInstall().GetAwaiter().GetResult();

            Assert.AreEqual(0, AttributionCalls(http));
        }

        [Test]
        public void AnInstallReportAsksExactlyOnceWithAHandler()
        {
            var http = new FakeHttp { AttributionBody = ClickVerdict };
            var config = MakeConfig(http);
            var seen = new List<RevnixAttribution>();
            config.OnAttribution = a => seen.Add(a);
            var client = new RevnixClient(config);

            client.RegisterInstall().GetAwaiter().GetResult();

            Assert.AreEqual(1, AttributionCalls(http));
            Assert.AreEqual(1, seen.Count);
        }

        [Test]
        public void TheReferrerAndAppleTokenReportsAskToo()
        {
            var http = new FakeHttp { AttributionBody = ClickVerdict };
            var config = MakeConfig(http);
            config.OnAttribution = a => { };
            var client = new RevnixClient(config);

            client.HandleInstallReferrer("utm_source=instagram").GetAwaiter().GetResult();
            client.HandleAttributionToken("token").GetAwaiter().GetResult();

            Assert.AreEqual(2, AttributionCalls(http));
        }
    }
}
