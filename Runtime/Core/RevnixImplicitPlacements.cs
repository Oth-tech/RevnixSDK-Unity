using System;
using System.Collections.Generic;

namespace Revnix
{
    /// <summary>
    /// REV-272: implicit placements — the six moments the SDK reports on its
    /// own.
    ///
    /// A placement is normally a location the HOST resolves by name, so every
    /// new place a paywall could appear costs a code change and a store
    /// release. Six moments are the same in every app and visible to the SDK
    /// without the host saying anything, so an operator can attach a paywall to
    /// them from the dashboard alone.
    ///
    /// Mirrors <c>convex/lib/implicitPlacements.ts</c> in revnix-app and
    /// <c>src/implicit.ts</c> in revnix-react — the backend owns the key
    /// spellings, and a mismatch here is silent (the server 400s a key it does
    /// not know), so the list is asserted against the contract in the tests.
    /// </summary>
    public enum RevnixImplicitPlacement
    {
        AppInstall,
        AppLaunch,
        SessionStart,
        DeeplinkOpen,
        PaywallDecline,
        TransactionAbandon,
    }

    /// <summary>Wire names for the two enums above. C# enum members cannot
    /// carry a string, so the mapping lives here — one place, so the key an SDK
    /// sends and the key an operator sees in the dashboard cannot drift.</summary>
    public static class RevnixImplicitPlacements
    {
        /// <summary>Lowercase throughout: a placement key must match
        /// <c>^[a-z0-9][a-z0-9._-]{0,63}$</c> server-side, which is why this is
        /// <c>deeplink_open</c> and not Superwall's <c>deepLink_open</c>.</summary>
        private static readonly Dictionary<RevnixImplicitPlacement, string> Keys =
            new Dictionary<RevnixImplicitPlacement, string>
            {
                [RevnixImplicitPlacement.AppInstall] = "app_install",
                [RevnixImplicitPlacement.AppLaunch] = "app_launch",
                [RevnixImplicitPlacement.SessionStart] = "session_start",
                [RevnixImplicitPlacement.DeeplinkOpen] = "deeplink_open",
                [RevnixImplicitPlacement.PaywallDecline] = "paywall_decline",
                [RevnixImplicitPlacement.TransactionAbandon] = "transaction_abandon",
            };

        public static string KeyOf(RevnixImplicitPlacement placement)
        {
            return Keys[placement];
        }

        /// <summary>The placementKey <see cref="RevnixClient.HandleDeepLink"/>
        /// reports for a dashboard QR/link preview
        /// (<c>&lt;scheme&gt;://revnix-preview?revnix_preview=&lt;token&gt;</c>) —
        /// not one of the six above, and never sent to
        /// <c>/v1/placements/triggered</c>.</summary>
        public const string PreviewPlacementKey = "revnix_preview";

        /// <summary>The placement for a wire key, or null when the key is not
        /// one of the six.</summary>
        public static RevnixImplicitPlacement? FromKey(string key)
        {
            if (key == null) return null;
            foreach (var pair in Keys)
            {
                if (pair.Value == key) return pair.Key;
            }
            return null;
        }

        /// <summary>How long the app must have been backgrounded for the return
        /// to count as a new session rather than an app switch. Matches
        /// Superwall's own definition so a team moving over gets the same
        /// numbers.</summary>
        public static readonly TimeSpan DefaultSessionTimeout = TimeSpan.FromMinutes(30);
    }

    /// <summary>One implicit moment that resolved to a paywall. Only ever
    /// delivered WITH a paywall — a moment the server answered with none
    /// (nothing attached, or the same paywall the player is leaving) is
    /// reported and then dropped, since there is nothing to present.</summary>
    public sealed class RevnixImplicitTrigger
    {
        /// <summary>Which of the six fired.</summary>
        public RevnixImplicitPlacement Placement;

        /// <summary>The resolution, exactly as
        /// <see cref="RevnixClient.ResolvePlacement"/> would have returned it.</summary>
        public PlacementResolution Resolution;
    }

    /// <summary>Wire parsing for the two implicit-placement responses, kept
    /// out of RevnixClient so the client stays about policy and this stays
    /// about JSON.</summary>
    public static class RevnixImplicitConfig
    {
        /// <summary>The configured keys from a <c>GET /v1/config</c> body. An
        /// unparseable body answers "none" rather than throwing: the caller
        /// treats a failure as "nothing configured", and a malformed 200 (a
        /// captive portal) must land in the same place as a 500.</summary>
        public static HashSet<string> Parse(string raw)
        {
            var keys = new HashSet<string>();
            try
            {
                var map = RevnixJson.ParseObject(raw);
                var list = RevnixJson.GetList(map, "implicitPlacements");
                if (list == null) return keys;
                foreach (var entry in list)
                {
                    var key = entry as string;
                    if (key != null) keys.Add(key);
                }
            }
            catch (Exception)
            {
                // Answering "none" is the safe direction: the app simply does
                // not fire, rather than firing for moments nobody configured.
            }
            return keys;
        }

        /// <summary>True when a trigger response actually RESOLVED — status
        /// "ok" with a paywall attached. An unconfigured moment answers 200
        /// with <c>paywall: null</c> and no <c>status</c> at all, which is a
        /// normal state and must not be read as a malformed body.</summary>
        public static bool Resolved(string raw)
        {
            try
            {
                var map = RevnixJson.ParseObject(raw);
                return RevnixJson.GetString(map, "status") == "ok"
                    && RevnixJson.GetObject(map, "paywall") != null;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Where the app is. Both transitions matter: a session is
    /// defined by how long the app was in the BACKGROUND, which cannot be known
    /// from foreground events alone — "time since the last return" would mint a
    /// session after 35 minutes of continuous play plus a three-second app
    /// switch.</summary>
    public enum RevnixAppState
    {
        Foreground,
        Background,
    }

    /// <summary>
    /// How the SDK learns the app's foreground/background transitions, which is
    /// what <c>session_start</c> is built on.
    ///
    /// <c>Runtime/Core</c> has no UnityEngine dependency (it is plain C# so the
    /// suite can run under dotnet without an Editor), so the platform
    /// implementation lives in <c>Runtime/Unity</c> as a MonoBehaviour over
    /// <c>OnApplicationPause</c> / <c>OnApplicationFocus</c>.
    /// </summary>
    public interface IRevnixLifecycle
    {
        /// <summary>Subscribe to app state transitions. Returns a cancel
        /// action; the client calls it from
        /// <see cref="RevnixClient.StopImplicitPlacements"/>. Repeated reports
        /// of the same state are harmless.</summary>
        Action OnStateChange(Action<RevnixAppState> handler);
    }
}
