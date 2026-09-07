using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Revnix.Unity
{
    /// <summary>
    /// REV-268: the device facts the engine can report. The store country is
    /// not among them — Unity IAP exposes no storefront, so the server stores
    /// no storefront for Unity customers rather than a guessed one; set
    /// <c>Storefront</c> on the config's <c>Device</c> yourself if your app
    /// knows it.
    /// </summary>
    public static class UnityDeviceFacts
    {
        public static DeviceFacts Detect()
        {
            string currency = null;
            try
            {
                currency = RegionInfo.CurrentRegion.ISOCurrencySymbol;
            }
            catch (System.Exception)
            {
                // Invariant / unknown region on some players — no currency.
            }
            var locale = CultureInfo.CurrentCulture.Name;
            return new DeviceFacts
            {
                Platform = PlatformName(Application.platform),
                OsVersion = OsVersionFrom(SystemInfo.operatingSystem),
                AppVersion = Blank(Application.version) ? null : Application.version,
                Locale = Blank(locale) ? null : locale,
                Currency = Blank(currency) ? null : currency,
                Model = Blank(SystemInfo.deviceModel) ? null : SystemInfo.deviceModel,
                Sandbox = Debug.isDebugBuild,
            };
        }

        /// <summary>The same family names the other SDKs use, so one rule
        /// ("platform is ios") covers a Unity build and a native one.</summary>
        public static string PlatformName(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.IPhonePlayer:
                case RuntimePlatform.tvOS:
                    return platform == RuntimePlatform.tvOS ? "tvos" : "ios";
                case RuntimePlatform.Android:
                    return "android";
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                    return "macos";
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                    return "windows";
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor:
                    return "linux";
                case RuntimePlatform.WebGLPlayer:
                    return "web";
                default:
                    return platform.ToString().ToLowerInvariant();
            }
        }

        /// <summary>"iOS 18.1" → "18.1"; "Android OS 15 / API-35 (…)" → "15";
        /// "Mac OS X 15.1.0" → "15.1.0". The first dotted number in the string,
        /// which is the version on every platform Unity names this way.</summary>
        public static string OsVersionFrom(string operatingSystem)
        {
            if (Blank(operatingSystem)) return null;
            var match = Regex.Match(operatingSystem, @"\d+(?:\.\d+)*");
            return match.Success ? match.Value : null;
        }

        private static bool Blank(string value) => string.IsNullOrWhiteSpace(value);
    }
}
