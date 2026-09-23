using System;
using System.Threading.Tasks;
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
            Action<RevnixDiagnostic> onDiagnostic = null,
            Action<RevnixImplicitTrigger> onImplicitPaywall = null,
            Action<string, DeferredDeepLinkMatch> onDeferredDeepLink = null,
            Action<RevnixAttribution> onAttribution = null)
        {
            var config = new RevnixConfig
            {
                ApiKey = apiKey,
                BaseUrl = baseUrl,
                Http = new UnityWebRequestHttp(),
                Storage = new PlayerPrefsStorage(),
                Device = UnityDeviceFacts.Detect(),
                OnDiagnostic = onDiagnostic ?? (d => Debug.Log("[Revnix] " + d.Op + ": " + d.Message)),
                // REV-272: passing a handler is what turns implicit placements
                // on — see RevnixConfig.OnImplicitPaywall.
                OnImplicitPaywall = onImplicitPaywall,
                OnDeferredDeepLink = onDeferredDeepLink,
                OnAttribution = onAttribution,
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
            // REV-268: an app that brings its own config still gets the device
            // facts unless it said not to; a partial Device it set keeps its
            // own fields and takes the detected ones for the rest.
            if (config.SendDeviceFacts)
            {
                config.Device = UnityDeviceFacts.Detect().OverriddenBy(config.Device);
            }
            // REV-272: foreground detection needs a MonoBehaviour, and only
            // the facade is allowed to touch UnityEngine — created lazily so a
            // game that never turns implicit placements on never gets a hidden
            // GameObject it did not ask for.
            if (config.Lifecycle == null && config.ImplicitPlacementsEnabled)
            {
                config.Lifecycle = RevnixLifecycleBehaviour.Create();
            }
            // A re-configure retires the previous client's foreground observer,
            // or two would mint a session apiece on every return.
            if (_client != null) _client.StopImplicitPlacements();
            _client = new RevnixClient(config);

            // Launch chores, deliberately not awaited: neither may delay the
            // first frame, both are idempotent, and failures land in
            // OnDiagnostic rather than on the caller.
            var client = _client;
            RunLaunchChores(client);
            return client;
        }

        /// <summary>REV-299: hand over the raw Android Play Install Referrer
        /// string, read yourself via the Play Install Referrer library.
        /// Fills platform/appVersion the same way <c>Configure</c> fills them
        /// for <see cref="RevnixClient.RegisterInstall"/>, so whichever of the
        /// two calls reaches the server first still carries them.</summary>
        public static void HandleInstallReferrer(string referrer)
        {
            _ = Client.HandleInstallReferrer(
                referrer,
                platform: UnityDeviceFacts.PlatformName(Application.platform),
                appVersion: Application.version);
        }

        /// <summary>AT9: hand over the Apple AdServices attribution token,
        /// minted yourself via your own native iOS plugin — this package
        /// ships no <c>ios/</c> layer, so there is nothing here to call
        /// <c>AAAttribution</c> from. Fills platform/appVersion the same way
        /// <see cref="HandleInstallReferrer"/> does. Fire-and-forget; if you
        /// need the server's <c>appleAttribution</c> verdict to decide
        /// whether to retry a <c>"pending"</c> token, call
        /// <see cref="RevnixClient.HandleAttributionToken"/> on <see cref="Client"/>
        /// directly instead.</summary>
        public static void HandleAttributionToken(string attributionToken)
        {
            _ = Client.HandleAttributionToken(
                attributionToken,
                platform: UnityDeviceFacts.PlatformName(Application.platform),
                appVersion: Application.version);
        }

        /// <summary>Unwraps a link an email service provider (Mailchimp,
        /// SendGrid…) rewrote through its own click-tracking domain. Route the
        /// result yourself and hand it to <see cref="RevnixClient.HandleDeepLink"/>;
        /// the result may still be an http(s) URL if the chain could not be
        /// unwrapped, so check its scheme before routing. A null, empty, or
        /// whitespace URL, or any failure, returns the input unchanged instead
        /// of throwing.</summary>
        public static Task<string> ResolveDeepLink(string url) => Client.ResolveDeepLink(url);

        private static async void RunLaunchChores(RevnixClient client)
        {
            try
            {
                await client.RetryPendingPurchases();
                await client.RegisterInstall(
                    platform: UnityDeviceFacts.PlatformName(Application.platform),
                    appVersion: Application.version);
                // REV-272: last of the launch chores — a no-op unless the game
                // opted in, and it must not delay the two above.
                await client.StartImplicitPlacements();
            }
            catch (Exception)
            {
                // RetryPendingPurchases/RegisterInstall/StartImplicitPlacements
                // already route failures to OnDiagnostic; this guard only keeps
                // an unexpected bug from surfacing as an unobserved async-void
                // crash.
            }
        }
    }
}
