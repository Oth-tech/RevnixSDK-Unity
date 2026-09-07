// REV-268: the device attribute contract's wire form. Everything asserted
// here is in Runtime/Core, which is deliberately free of UnityEngine; the
// facade's UnityDeviceFacts.Detect() is exercised only in a player.

using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class DeviceFactsTests
    {
        private static Dictionary<string, object> Decode(string header)
        {
            var base64 = header.Replace('-', '+').Replace('_', '/');
            while (base64.Length % 4 != 0) base64 += "=";
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return RevnixJson.ParseObject(json);
        }

        [Test]
        public void EncodedHeaderCarriesEveryFactPlusTheSdkOwnedOnes()
        {
            var facts = new DeviceFacts
            {
                Platform = "ios", OsVersion = "18.1", AppVersion = "1.2.10", Locale = "en-US",
                Currency = "USD", Storefront = "US", Model = "iPhone15,3", Sandbox = true,
            };
            var header = facts.EncodedHeader("0.3.0", 1_700_000_000_000L, true);
            Assert.That(header, Does.Not.Contain("="));
            Assert.That(header, Does.Not.Contain("+"));
            Assert.That(header, Does.Not.Contain("/"));
            var decoded = Decode(header);
            Assert.AreEqual("ios", RevnixJson.GetString(decoded, "platform"));
            Assert.AreEqual("18.1", RevnixJson.GetString(decoded, "osVersion"));
            Assert.AreEqual("1.2.10", RevnixJson.GetString(decoded, "appVersion"));
            Assert.AreEqual("en-US", RevnixJson.GetString(decoded, "locale"));
            Assert.AreEqual("USD", RevnixJson.GetString(decoded, "currency"));
            Assert.AreEqual("US", RevnixJson.GetString(decoded, "storefront"));
            Assert.AreEqual("iPhone15,3", RevnixJson.GetString(decoded, "model"));
            Assert.IsTrue(RevnixJson.GetBool(decoded, "sandbox"));
            Assert.AreEqual("0.3.0", RevnixJson.GetString(decoded, "sdkVersion"));
            Assert.AreEqual(1_700_000_000_000L, RevnixJson.GetLong(decoded, "installedAt"));
            Assert.IsTrue(RevnixJson.GetBool(decoded, "firstOpen"));
        }

        [Test]
        public void UnknownFactsAreLeftOutRatherThanSentEmpty()
        {
            var decoded = Decode(new DeviceFacts { Platform = "android" }.EncodedHeader("0.3.0", null, null));
            Assert.AreEqual("android", RevnixJson.GetString(decoded, "platform"));
            Assert.IsFalse(decoded.ContainsKey("osVersion"));
            Assert.IsFalse(decoded.ContainsKey("sandbox"));
            Assert.IsFalse(decoded.ContainsKey("installedAt"));
            Assert.IsFalse(decoded.ContainsKey("firstOpen"));
        }

        [Test]
        public void OverriddenByKeepsWhatTheOverrideDoesNotSay()
        {
            var detected = new DeviceFacts { Platform = "android", OsVersion = "15", Model = "Pixel 8" };
            var merged = detected.OverriddenBy(new DeviceFacts { AppVersion = "2.0", Model = "Pixel 8 Pro" });
            Assert.AreEqual("android", merged.Platform);
            Assert.AreEqual("15", merged.OsVersion);
            Assert.AreEqual("2.0", merged.AppVersion);
            Assert.AreEqual("Pixel 8 Pro", merged.Model);
            Assert.AreSame(detected, detected.OverriddenBy(null));
        }

        [Test]
        public void NonAsciiSurvivesTheEncoding()
        {
            var decoded = Decode(new DeviceFacts { Model = "端末", Locale = "ja-JP" }.EncodedHeader("0.3.0", null, null));
            Assert.AreEqual("端末", RevnixJson.GetString(decoded, "model"));
        }
    }
}
