using System;
using System.Collections.Generic;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class PaywallBlockTypesTests
    {
        private const string Json = @"{
          ""version"": 1,
          ""defaultLocale"": ""en"",
          ""locales"": {
            ""es"": {
              ""t1.format"": ""Termina en {mm}:{ss}"",
              ""t1.expiredText"": ""Oferta terminada"",
              ""g1.label"": ""Prueba gratis""
            }
          },
          ""blocks"": [
            { ""id"": ""t1"", ""type"": ""timer"", ""duration"": 600, ""format"": ""Offer ends in {mm}:{ss}"", ""expiredText"": ""Offer expired"" },
            { ""id"": ""t2"", ""type"": ""timer"", ""endsAt"": ""2026-10-31T23:59:59Z"" },
            { ""id"": ""v1"", ""type"": ""video"", ""url"": ""https://cdn.example.com/intro.mp4"", ""poster"": ""https://cdn.example.com/intro.jpg"", ""fit"": ""contain"" },
            { ""id"": ""g1"", ""type"": ""toggle"", ""label"": ""Free trial enabled"", ""onPackageIndex"": 0, ""offPackageIndex"": 1 },
            { ""id"": ""c1"", ""type"": ""card"", ""layout"": ""carousel"", ""autoAdvance"": 4, ""style"": { ""height"": 240 },
              ""children"": [ { ""id"": ""p1"", ""type"": ""image"" }, { ""id"": ""p2"", ""type"": ""sparkle"" } ] }
          ]
        }";

        private static PaywallBlockDoc Doc()
        {
            var doc = PaywallBlockDoc.Parse(RevnixJson.Parse(Json));
            Assert.IsNotNull(doc);
            return doc;
        }

        private static List<BlockPackage> Packages()
        {
            return new List<BlockPackage>
            {
                new BlockPackage { PackageId = "trial" },
                new BlockPackage { PackageId = "plain" },
            };
        }

        [Test]
        public void ParsesTimerFields()
        {
            var doc = Doc();
            var duration = doc.Blocks[0];
            Assert.AreEqual(PaywallBlockKind.Timer, duration.Kind);
            Assert.AreEqual(600, duration.Duration);
            Assert.IsNull(duration.EndsAt);
            Assert.AreEqual("Offer ends in {mm}:{ss}", duration.Format);
            Assert.AreEqual("Offer expired", duration.ExpiredText);

            var deadline = doc.Blocks[1];
            Assert.AreEqual(PaywallBlockKind.Timer, deadline.Kind);
            Assert.AreEqual("2026-10-31T23:59:59Z", deadline.EndsAt);
            Assert.IsNull(deadline.Duration);
            Assert.IsNull(deadline.Format);
            Assert.IsNull(deadline.ExpiredText);
        }

        [Test]
        public void ParsesVideoFields()
        {
            var video = Doc().Blocks[2];
            Assert.AreEqual(PaywallBlockKind.Video, video.Kind);
            Assert.AreEqual("https://cdn.example.com/intro.mp4", video.Url);
            Assert.AreEqual("https://cdn.example.com/intro.jpg", video.Poster);
            Assert.AreEqual("contain", video.Fit);
        }

        [Test]
        public void ParsesToggleFields()
        {
            var toggle = Doc().Blocks[3];
            Assert.AreEqual(PaywallBlockKind.Toggle, toggle.Kind);
            Assert.AreEqual("Free trial enabled", toggle.Label);
            Assert.AreEqual(0, toggle.OnPackageIndex);
            Assert.AreEqual(1, toggle.OffPackageIndex);
        }

        [Test]
        public void ParsesCarouselAndStillSkipsUnknownTypes()
        {
            var card = Doc().Blocks[4];
            Assert.AreEqual(PaywallBlockKind.Card, card.Kind);
            Assert.AreEqual("carousel", card.Layout);
            Assert.AreEqual(4.0, card.AutoAdvance);
            Assert.AreEqual(240.0, card.Style.Height.Value.Px);
            Assert.AreEqual(2, card.Children.Count);
            Assert.AreEqual(PaywallBlockKind.Image, card.Children[0].Kind);
            Assert.AreEqual(PaywallBlockKind.Unknown, card.Children[1].Kind);
        }

        [Test]
        public void FormatsTheContractExamples()
        {
            Assert.AreEqual("01:02:05", RevnixPaywallTimer.Format("{hh}:{mm}:{ss}", 3725));
            Assert.AreEqual("1d 01:01:01", RevnixPaywallTimer.Format("{d}d {hh}:{mm}:{ss}", 90061));
            Assert.AreEqual("25:01", RevnixPaywallTimer.Format("{hh}:{mm}", 90061));
            Assert.AreEqual("01:02:05", RevnixPaywallTimer.Format(null, 3725));
            Assert.AreEqual("05 05", RevnixPaywallTimer.Format("{ss} {ss}", 5));
            Assert.AreEqual("90:00", RevnixPaywallTimer.Format("{mm}:{ss}", 5400));
            Assert.AreEqual("120:00", RevnixPaywallTimer.Format("{mm}:{ss}", 7200));
            Assert.AreEqual("125s", RevnixPaywallTimer.Format("{ss}s", 125));
        }

        [Test]
        public void DurationCountsFromFirstDisplay()
        {
            var timer = Doc().Blocks[0];
            var shown = new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(600, RevnixPaywallTimer.Remaining(timer, shown, shown));
            Assert.AreEqual(509, RevnixPaywallTimer.Remaining(timer, shown, shown.AddSeconds(90.4)));
            Assert.AreEqual(0, RevnixPaywallTimer.Remaining(timer, shown, shown.AddHours(1)));
            Assert.AreEqual("Offer ends in 08:29", RevnixPaywallTimer.Text(timer, 509));
            Assert.AreEqual("Offer expired", RevnixPaywallTimer.Text(timer, 0));
        }

        [Test]
        public void EndsAtIsAFixedDeadlineAndUnparseableIsExpired()
        {
            var timer = Doc().Blocks[1];
            var now = new DateTime(2026, 10, 31, 23, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(3599, RevnixPaywallTimer.Remaining(timer, now.AddDays(-5), now));
            Assert.IsNull(RevnixPaywallTimer.Text(timer, 0));

            var broken = new PaywallBlock { Kind = PaywallBlockKind.Timer, EndsAt = "not a date" };
            Assert.AreEqual(0, RevnixPaywallTimer.Remaining(broken, now, now));
        }

        [Test]
        public void ToggleIsOnWhileItsTrialPackageIsSelectedAndTapsFlip()
        {
            var toggle = Doc().Blocks[3];
            var packages = Packages();

            Assert.IsTrue(RevnixPaywallSelection.ResolveToggle(toggle, packages, "trial", out var on, out var tap));
            Assert.IsTrue(on);
            Assert.AreEqual("plain", tap);

            Assert.IsTrue(RevnixPaywallSelection.ResolveToggle(toggle, packages, "plain", out on, out tap));
            Assert.IsFalse(on);
            Assert.AreEqual("trial", tap);

            Assert.IsTrue(RevnixPaywallSelection.ResolveToggle(toggle, packages, null, out on, out tap));
            Assert.IsTrue(on);
        }

        [Test]
        public void ToggleIsNotDrawnWhenAnIndexIsOutsideTheOffering()
        {
            var toggle = Doc().Blocks[3];
            var one = new List<BlockPackage> { new BlockPackage { PackageId = "trial" } };
            Assert.IsFalse(RevnixPaywallSelection.ResolveToggle(toggle, one, "trial", out _, out var tap));
            Assert.IsNull(tap);
            Assert.IsFalse(RevnixPaywallSelection.ResolveToggle(
                new PaywallBlock { Kind = PaywallBlockKind.Toggle }, Packages(), "trial", out _, out _));
        }

        [Test]
        public void ACloseInsideACarouselDoesNotCountAsCertain()
        {
            var carousel = PaywallBlockDoc.Parse(RevnixJson.Parse(@"{ ""blocks"": [
              { ""id"": ""c1"", ""type"": ""card"", ""layout"": ""carousel"", ""children"": [
                { ""id"": ""x"", ""type"": ""button"", ""label"": ""Close"", ""action"": ""close"" } ] } ] }"));
            Assert.IsFalse(RevnixPaywallClose.HasCloseAction(carousel.Blocks));

            var column = PaywallBlockDoc.Parse(RevnixJson.Parse(@"{ ""blocks"": [
              { ""id"": ""c1"", ""type"": ""card"", ""layout"": ""column"", ""children"": [
                { ""id"": ""x"", ""type"": ""button"", ""label"": ""Close"", ""action"": ""close"" } ] } ] }"));
            Assert.IsTrue(RevnixPaywallClose.HasCloseAction(column.Blocks));
        }

        [Test]
        public void LocalizesTimerAndToggleKeys()
        {
            var doc = RevnixLocale.Localize(Doc(), "es-MX");
            Assert.AreEqual("Termina en {mm}:{ss}", doc.Blocks[0].Format);
            Assert.AreEqual("Oferta terminada", doc.Blocks[0].ExpiredText);
            Assert.AreEqual("Prueba gratis", doc.Blocks[3].Label);
            Assert.IsNull(doc.Blocks[1].Format);

            var source = Doc();
            Assert.AreEqual("Offer ends in {mm}:{ss}", source.Blocks[0].Format);
        }
    }
}
