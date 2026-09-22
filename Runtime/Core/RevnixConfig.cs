using System;
using System.Collections.Generic;

namespace Revnix
{
    public sealed class RevnixConfig
    {
        /// <summary>Publishable key (`rvx_pk_live_…` / `rvx_pk_test_…`). The key
        /// fixes app + environment server-side. Secret keys never ship in a
        /// binary — identify / alias are server-proxied by design.</summary>
        public string ApiKey;

        /// <summary>e.g. `https://your-deployment.convex.site`</summary>
        public string BaseUrl;

        /// <summary>Durable key-value store. Unity's facade defaults this to
        /// PlayerPrefs; the core default is in-memory (tests).</summary>
        public IRevnixStorage Storage = new MemoryStorage();

        /// <summary>Transport. Unity's facade defaults this to UnityWebRequest.</summary>
        public IRevnixHttp Http;

        /// <summary>Per-request timeout. Default 10 s (matches revnix-react).</summary>
        public TimeSpan Timeout = TimeSpan.FromSeconds(10);

        /// <summary>Snapshots older than this serve as all-inactive. Default 14 days.</summary>
        public TimeSpan OfflineMaxCacheAge = TimeSpan.FromDays(14);

        /// <summary>Soft TTL on entitlement reads: a snapshot this fresh answers
        /// without a network round trip, so a screen full of gates costs one
        /// fetch. Zero = always fetch.</summary>
        public TimeSpan EntitlementsTtl = TimeSpan.FromSeconds(30);

        /// <summary>Read-your-writes poll schedule after a purchase. Each delay
        /// is jittered ±20% so a promo push does not put a fleet's polls in
        /// lockstep against our own rate limiter. Empty disables polling.</summary>
        public List<TimeSpan> ReadYourWritesDelays = new List<TimeSpan>
        {
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
        };

        /// <summary>Swallowed background failures report here (queue drains,
        /// telemetry beacons).</summary>
        public Action<RevnixDiagnostic> OnDiagnostic;

        /// <summary>Injectable clock for tests (unix ms).</summary>
        public Func<long> Now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>Injectable async delay for tests (ms).</summary>
        public Func<int, System.Threading.Tasks.Task> Delay =
            ms => System.Threading.Tasks.Task.Delay(ms);

        /// <summary>REV-268: facts about the device, sent with every placement
        /// resolve so targeting rules can be evaluated on the request that
        /// serves the paywall, and stored on the customer as reserved
        /// <c>device.*</c> attributes. Unity's facade fills this from
        /// <c>SystemInfo</c> / <c>Application</c> when left null; set
        /// <see cref="SendDeviceFacts"/> false to send nothing.</summary>
        public DeviceFacts Device;

        /// <summary>False disables the device attribute header entirely.</summary>
        public bool SendDeviceFacts = true;

        /// <summary>REV-272: called when one of the six implicit moments
        /// resolved to a paywall — an app launch, a session start, a deep link,
        /// a dismissed paywall, an abandoned checkout, or the install itself.
        /// Present it however your game presents paywalls; the SDK deliberately
        /// does not present for you, because it does not own your scene stack
        /// and a paywall over a loading screen is worse than no paywall.
        ///
        /// Providing this handler is what TURNS IMPLICIT PLACEMENTS ON. Without
        /// it the SDK makes no extra requests at all, except that
        /// <see cref="RevnixClient.HandleDeepLink"/> always reports the link
        /// it is handed — for its attribution facts, not for a paywall. With
        /// this handler set, the SDK asks <c>GET /v1/config</c> once and then
        /// fires only for the moments this app has actually configured in the
        /// dashboard.
        ///
        /// Called from a background continuation, not necessarily Unity's main
        /// thread — hop to the main thread before touching the scene.
        ///
        /// Pass <c>trigger.Resolution.PlacementKey</c> to
        /// <see cref="RevnixClient.LogPaywallDisplay"/> for the display you
        /// present. That is what tells the SDK this display came FROM an
        /// implicit trigger, and it is the only thing that stops a
        /// <c>paywall_decline</c> paywall from firing <c>paywall_decline</c>
        /// again when the player dismisses it — a loop with no way out but
        /// quitting. The server refuses to serve back the very same paywall as
        /// a backstop, but it cannot see a rule pointing at a DIFFERENT paywall
        /// that points back.</summary>
        public Action<RevnixImplicitTrigger> OnImplicitPaywall;

        /// <summary>REV-272: explicit off switch, even when
        /// <see cref="OnImplicitPaywall"/> is set. Null means "on when a
        /// handler is present". With this false,
        /// <see cref="RevnixClient.HandleDeepLink"/> still reports the link it
        /// is handed for attribution and never presents a deep-link paywall —
        /// except a dashboard preview link, which always presents.</summary>
        public bool? ImplicitPlacements;

        /// <summary>REV-272: the rule the client reads —
        /// <see cref="ImplicitPlacements"/> when set, else whether a handler is
        /// present.</summary>
        public bool ImplicitPlacementsEnabled => ImplicitPlacements ?? (OnImplicitPaywall != null);

        /// <summary>REV-272: how the SDK learns the app came to the foreground
        /// — <c>session_start</c> is built on it. Unity's facade fills this with
        /// a MonoBehaviour over <c>OnApplicationPause</c>; null means
        /// launch-time moments only.</summary>
        public IRevnixLifecycle Lifecycle;

        /// <summary>REV-272: how long the app must have been backgrounded for
        /// the return to count as a new session rather than an app switch.
        /// Default 30 minutes.</summary>
        public TimeSpan SessionTimeout = RevnixImplicitPlacements.DefaultSessionTimeout;

        /// <summary>Called with the link a customer clicked before
        /// installing. Fires at most once per install, from a background
        /// continuation like <see cref="OnImplicitPaywall"/>; route it
        /// however you route <see cref="RevnixClient.HandleDeepLink"/>. Left
        /// null, the SDK does nothing with the link and does not mark it
        /// delivered — set it before the app ever calls
        /// <see cref="RevnixClient.RegisterInstall"/>,
        /// <see cref="RevnixClient.HandleInstallReferrer"/>, or
        /// <see cref="RevnixClient.HandleAttributionToken"/>.</summary>
        public Action<string, DeferredDeepLinkMatch> OnDeferredDeepLink;
    }
}
