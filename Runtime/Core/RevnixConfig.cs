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
    }
}
