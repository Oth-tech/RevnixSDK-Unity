using System;
using System.Collections.Generic;
using System.Text;

namespace Revnix
{
    /// <summary>
    /// REV-268: the device attribute contract, SDK side. Every Revnix SDK sends
    /// the same facts about the device on each placement resolve, in the
    /// <c>X-Revnix-Device</c> header, and the server stores them as reserved
    /// <c>device.*</c> customer attributes — what a targeting rule ("US
    /// storefront", "app version at least 3", "first open") evaluates against
    /// on the very request that serves the paywall.
    ///
    /// The core is engine-free, so it detects nothing itself: the Unity facade
    /// fills this from <c>SystemInfo</c> / <c>Application</c>
    /// (<c>UnityDeviceFacts.Detect()</c>) and a test injects a fixed one. The
    /// SDK adds <c>sdkVersion</c>, <c>installedAt</c> and <c>firstOpen</c>
    /// itself. Every field is optional: the server treats a missing fact as
    /// missing, never as an error.
    /// </summary>
    public sealed class DeviceFacts
    {
        /// <summary>"ios", "android", "windows", "macos", "web", … lowercase.</summary>
        public string Platform;
        /// <summary>e.g. "18.1".</summary>
        public string OsVersion;
        /// <summary>The app's own version, e.g. "1.2.10". The server derives the
        /// zero-padded sortable form (<c>device.appVersionPadded</c>).</summary>
        public string AppVersion;
        /// <summary>BCP-47 ("en-US") or "en_US" — either separator.</summary>
        public string Locale;
        /// <summary>ISO 4217, e.g. "USD".</summary>
        public string Currency;
        /// <summary>Store country, alpha-2 ("US") or alpha-3 ("USA").</summary>
        public string Storefront;
        /// <summary>Hardware model, e.g. "iPhone15,3".</summary>
        public string Model;
        /// <summary>True for a development / debug build.</summary>
        public bool? Sandbox;
        /// <summary>Opaque per-device id sent only with install reports, not
        /// the resolve header. Survives an uninstall/reinstall on Android;
        /// on iOS it's best-effort (IDFV), resetting when no other app from
        /// the same vendor stays installed.</summary>
        public string DeviceKey;

        /// <summary>A copy of these facts with every non-null field of
        /// <paramref name="over"/> winning.</summary>
        public DeviceFacts OverriddenBy(DeviceFacts over)
        {
            if (over == null) return this;
            return new DeviceFacts
            {
                Platform = over.Platform ?? Platform,
                OsVersion = over.OsVersion ?? OsVersion,
                AppVersion = over.AppVersion ?? AppVersion,
                Locale = over.Locale ?? Locale,
                Currency = over.Currency ?? Currency,
                Storefront = over.Storefront ?? Storefront,
                Model = over.Model ?? Model,
                Sandbox = over.Sandbox ?? Sandbox,
                DeviceKey = over.DeviceKey ?? DeviceKey,
            };
        }

        /// <summary>The wire payload for one resolve: these facts plus the
        /// SDK-owned ones, base64url of the UTF-8 JSON. Null fields are left
        /// out — the server would drop them, and bytes on every resolve are
        /// bytes on every resolve.</summary>
        public string EncodedHeader(string sdkVersion, long? installedAt, bool? firstOpen)
        {
            var payload = new Dictionary<string, object> { ["sdkVersion"] = sdkVersion };
            if (Platform != null) payload["platform"] = Platform;
            if (OsVersion != null) payload["osVersion"] = OsVersion;
            if (AppVersion != null) payload["appVersion"] = AppVersion;
            if (Locale != null) payload["locale"] = Locale;
            if (Currency != null) payload["currency"] = Currency;
            if (Storefront != null) payload["storefront"] = Storefront;
            if (Model != null) payload["model"] = Model;
            if (Sandbox.HasValue) payload["sandbox"] = Sandbox.Value;
            if (installedAt.HasValue) payload["installedAt"] = installedAt.Value;
            if (firstOpen.HasValue) payload["firstOpen"] = firstOpen.Value;
            var json = RevnixJson.Serialize(payload);
            return Base64Url(Encoding.UTF8.GetBytes(json));
        }

        /// <summary>Unpadded base64url — what the server decodes.</summary>
        public static string Base64Url(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
