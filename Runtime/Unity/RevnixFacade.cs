using System;
using UnityEngine;

namespace Revnix.Unity
{
    /// <summary>
    /// The one-line entry point for games:
    ///
    ///     var client = RevnixSdk.Configure("rvx_pk_live_…", "https://….convex.site");
    ///     if (await client.IsEntitled("pro")) { … }
    ///
    /// Configure wires the Unity transport + PlayerPrefs storage, then
    /// fire-and-forgets the launch chores (drain the purchase retry queue,
    /// report the install) — both are idempotent server-side.
    /// </summary>
    public static class RevnixSdk
    {
        private static RevnixClient _client;

        /// <summary>The configured client. Throws until Configure() ran.</summary>
        public static RevnixClient Client
        {
            get
            {
                if (_client == null)
                {
                    throw new InvalidOperationException(
                        "RevnixSdk.Configure() must be called before RevnixSdk.Client");
                }
                return _client;
            }
        }

        public static bool IsConfigured => _client != null;

        /// <summary>Configure once at startup, from the main thread. Use a
        /// publishable key (`rvx_pk_…`) — secret keys must never ship in a
        /// build. Reconfiguring replaces the client (same replace-on-
        /// reconfigure semantics as the other Revnix SDKs).</summary>
        public static RevnixClient Configure(
            string apiKey,
            string baseUrl,
            Action<RevnixDiagnostic> onDiagnostic = null)
        {
            var config = new RevnixConfig
            {
                ApiKey = apiKey,
                BaseUrl = baseUrl,
                Http = new UnityWebRequestHttp(),
                Storage = new PlayerPrefsStorage(),
                OnDiagnostic = onDiagnostic ?? (d => Debug.Log("[Revnix] " + d.Op + ": " + d.Message)),
            };
            return Configure(config);
        }

        /// <summary>Full-control overload — bring your own RevnixConfig (custom
        /// storage, transport, TTLs, clock). Http/Storage default to the Unity
        /// implementations when left null.</summary>
        public static RevnixClient Configure(RevnixConfig config)
        {
            if (config.Http == null) config.Http = new UnityWebRequestHttp();
            if (config.Storage == null || config.Storage is MemoryStorage)
            {
                config.Storage = new PlayerPrefsStorage();
            }
            _client = new RevnixClient(config);

            // Launch chores, deliberately not awaited: neither may delay the
            // first frame, both are idempotent, and failures land in
            // OnDiagnostic rather than on the caller.
            var client = _client;
            RunLaunchChores(client);
            return client;
        }

        private static async void RunLaunchChores(RevnixClient client)
        {
            try
            {
                await client.RetryPendingPurchases();
                await client.RegisterInstall(
                    platform: Application.platform.ToString(),
                    appVersion: Application.version);
            }
            catch (Exception)
            {
                // RetryPendingPurchases/RegisterInstall already route failures
                // to OnDiagnostic; this guard only keeps an unexpected bug from
                // surfacing as an unobserved async-void crash.
            }
        }
    }
}
