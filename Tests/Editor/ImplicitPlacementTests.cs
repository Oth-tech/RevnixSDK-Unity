// REV-272: the implicit placement contract. Everything asserted here is in
// Runtime/Core, which is deliberately free of UnityEngine; the facade's
// RevnixLifecycleBehaviour is exercised only in a player.
//
// The server 400s a key it does not know, and the failure is SILENT from the
// game's side — the moment simply never fires and nobody sees a paywall that
// was configured. That makes the key spellings the one thing worth asserting
// hard, alongside the loop guard's shape.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class ImplicitPlacementTests
    {
        private sealed class FakeLifecycle : IRevnixLifecycle
        {
            public Action<RevnixAppState> Handler;

            public Action OnStateChange(Action<RevnixAppState> handler)
            {
                Handler = handler;
                return () => Handler = null;
            }
        }

        private sealed class FakeHttp : IRevnixHttp
        {
            public readonly List<Dictionary<string, object>> Triggered =
                new List<Dictionary<string, object>>();

            public Task<RevnixHttpResponse> Send(
                string method, string url, string jsonBody,
                IReadOnlyDictionary<string, string> headers, int timeoutMs)
            {
                if (url.Contains("/v1/config"))
                {
                    return Task.FromResult(new RevnixHttpResponse(
                        200, "{\"revision\":1,\"implicitPlacements\":[\"session_start\"]}"));
                }
                if (url.Contains("/placements/triggered") && jsonBody != null)
                {
                    Triggered.Add(RevnixJson.ParseObject(jsonBody));
                }
                return Task.FromResult(new RevnixHttpResponse(
                    200, "{\"paywall\":null,\"skipReason\":\"not_configured\",\"recorded\":false}"));
            }
        }

        private static List<Dictionary<string, object>> SessionStarts(FakeHttp http)
            => http.Triggered.FindAll(b => RevnixJson.GetString(b, "placement") == "session_start");

        private static readonly RevnixImplicitPlacement[] All =
        {
            RevnixImplicitPlacement.AppInstall,
            RevnixImplicitPlacement.AppLaunch,
            RevnixImplicitPlacement.SessionStart,
            RevnixImplicitPlacement.DeeplinkOpen,
            RevnixImplicitPlacement.PaywallDecline,
            RevnixImplicitPlacement.TransactionAbandon,
        };

        [Test]
        public void SixKeysMatchTheServerContract()
        {
            var keys = new List<string>();
            foreach (var placement in All) keys.Add(RevnixImplicitPlacements.KeyOf(placement));
            CollectionAssert.AreEqual(
                new[]
                {
                    "app_install",
                    "app_launch",
                    "session_start",
                    "deeplink_open",
                    "paywall_decline",
                    "transaction_abandon",
                },
                keys);
        }

        [Test]
        public void EveryKeyIsOneTheCatalogWouldAccept()
        {
            // A placement key must match ^[a-z0-9][a-z0-9._-]{0,63}$ server-side.
            // This is why the deep link moment is `deeplink_open` and not
            // Superwall's `deepLink_open` — the capital L could never be stored.
            var pattern = new Regex("^[a-z0-9][a-z0-9._-]{0,63}$");
            foreach (var placement in All)
            {
                var key = RevnixImplicitPlacements.KeyOf(placement);
                Assert.IsTrue(pattern.IsMatch(key), key + " would be refused by the catalog");
            }
            Assert.IsFalse(pattern.IsMatch("deepLink_open"));
        }

        [Test]
        public void KeysRoundTrip()
        {
            foreach (var placement in All)
            {
                var key = RevnixImplicitPlacements.KeyOf(placement);
                Assert.AreEqual(placement, RevnixImplicitPlacements.FromKey(key));
            }
            // An ordinary placement must not be mistaken for one of the six —
            // that is exactly what the loop guard keys off.
            Assert.IsNull(RevnixImplicitPlacements.FromKey("premium_button"));
            Assert.IsNull(RevnixImplicitPlacements.FromKey(null));
        }

        [Test]
        public void ResolvedTellsAnAnswerFromAnUnconfiguredMoment()
        {
            // The not_configured body: 200, paywall null, no status. A normal
            // state, never a decode failure to count against the server.
            Assert.IsFalse(RevnixImplicitConfig.Resolved(
                "{\"placementKey\":\"app_launch\",\"revision\":null,\"offering\":null,\"paywall\":null,\"skipReason\":\"not_configured\",\"recorded\":false}"));
            // The same_paywall bounce: resolved, but the paywall was withheld.
            Assert.IsFalse(RevnixImplicitConfig.Resolved(
                "{\"status\":\"ok\",\"paywall\":null,\"skipReason\":\"same_paywall\"}"));
            Assert.IsTrue(RevnixImplicitConfig.Resolved(
                "{\"status\":\"ok\",\"paywall\":{\"paywallId\":\"pw\"},\"skipReason\":null}"));
            Assert.IsFalse(RevnixImplicitConfig.Resolved("not json"));
        }

        [Test]
        public void ConfigParseNamesOnlyWhatTheServerSent()
        {
            var keys = RevnixImplicitConfig.Parse(
                "{\"revision\":3,\"implicitPlacements\":[\"app_launch\",\"paywall_decline\"]}");
            Assert.IsTrue(keys.Contains("app_launch"));
            Assert.IsTrue(keys.Contains("paywall_decline"));
            Assert.IsFalse(keys.Contains("session_start"));
            Assert.AreEqual(2, keys.Count);
        }

        [Test]
        public void ConfigParseAnswersNoneRatherThanThrowing()
        {
            // Answering "none" is the safe direction: the game simply does not
            // fire, rather than firing for moments nobody configured. A
            // captive-portal 200 must land in the same place as a 500.
            Assert.AreEqual(0, RevnixImplicitConfig.Parse("<html>not json</html>").Count);
            Assert.AreEqual(0, RevnixImplicitConfig.Parse("{\"revision\":null}").Count);
            Assert.AreEqual(
                0,
                RevnixImplicitConfig.Parse("{\"implicitPlacements\":[]}").Count);
        }

        [Test]
        public void ImplicitPlacementsAreOffUntilAHandlerIsGiven()
        {
            // The default has to be free: without a handler there is nothing to
            // do with a resolved paywall, and asking /v1/config for the whole
            // fleet is exactly the cost this feature is designed to avoid.
            var config = new RevnixConfig { ApiKey = "rvx_pk_test", BaseUrl = "https://x" };
            Assert.IsFalse(config.ImplicitPlacementsEnabled);
            Assert.IsNull(config.Lifecycle);
            Assert.AreEqual(30 * 60 * 1000, (int)config.SessionTimeout.TotalMilliseconds);

            config.OnImplicitPaywall = _ => { };
            Assert.IsTrue(config.ImplicitPlacementsEnabled);
            // The explicit switch wins over a handler, in both directions.
            config.ImplicitPlacements = false;
            Assert.IsFalse(config.ImplicitPlacementsEnabled);
            config.OnImplicitPaywall = null;
            config.ImplicitPlacements = true;
            Assert.IsTrue(config.ImplicitPlacementsEnabled);
        }

        [Test]
        public void PreviousSessionMsIsReportedOnTheNextSessionStart()
        {
            var http = new FakeHttp();
            var lifecycle = new FakeLifecycle();
            var now = 1_700_000_000_000L;
            var config = new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = http,
                Storage = new MemoryStorage(),
                Lifecycle = lifecycle,
                Now = () => now,
                OnImplicitPaywall = _ => { },
            };
            var client = new RevnixClient(config);

            client.StartImplicitPlacements().GetAwaiter().GetResult();
            var coldStart = SessionStarts(http);
            Assert.AreEqual(1, coldStart.Count);
            Assert.IsFalse(coldStart[0].ContainsKey("previousSessionMs"));

            now += 10 * 60 * 1000;
            lifecycle.Handler(RevnixAppState.Background);
            now += 31 * 60 * 1000;
            lifecycle.Handler(RevnixAppState.Foreground);

            var afterForeground = SessionStarts(http);
            Assert.AreEqual(2, afterForeground.Count);
            Assert.AreEqual(600000L, RevnixJson.GetLong(afterForeground[1], "previousSessionMs"));
        }

        [Test]
        public void PreviousSessionMsSurvivesAColdStartAcrossClients()
        {
            var storage = new MemoryStorage();
            var t0 = 1_700_000_000_000L;

            var lifecycleA = new FakeLifecycle();
            var nowA = t0;
            var clientA = new RevnixClient(new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = new FakeHttp(),
                Storage = storage,
                Lifecycle = lifecycleA,
                Now = () => nowA,
                OnImplicitPaywall = _ => { },
            });
            clientA.StartImplicitPlacements().GetAwaiter().GetResult();
            nowA = t0 + 5 * 60 * 1000;
            lifecycleA.Handler(RevnixAppState.Background);

            var httpB = new FakeHttp();
            var nowB = t0 + 2 * 3600 * 1000;
            var clientB = new RevnixClient(new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = httpB,
                Storage = storage,
                Now = () => nowB,
                OnImplicitPaywall = _ => { },
            });
            clientB.StartImplicitPlacements().GetAwaiter().GetResult();

            var coldStart = SessionStarts(httpB);
            Assert.AreEqual(1, coldStart.Count);
            Assert.AreEqual(300000L, RevnixJson.GetLong(coldStart[0], "previousSessionMs"));
        }
    }
}
