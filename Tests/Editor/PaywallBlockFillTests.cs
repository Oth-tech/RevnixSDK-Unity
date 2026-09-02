// Block fills: the half of a paint string that is NOT a plain colour.
//
// A `fill` is handed straight to CSS `background` by the dashboard, so it may
// be a colour, a gradient, or a stack of them. This SDK parsed only the first
// form and painted nothing for the others — 113 of the 250 shipped gallery
// presets use a gradient somewhere, so "nothing" was the common case.
//
// Three separate defects are pinned here, because each of them alone was enough
// to lose a gradient:
//
//   1. the fill was never routed through the gradient parser at all;
//   2. `@bg` resolved to the RAW ground, so a `@bg` stop inside a gradient
//      failed to parse and was dropped — and a gradient left with one stop does
//      not parse either, taking the whole fill with it;
//   3. a stop may carry TWO positions (`@accent 0 22%`), and reading only the
//      last one turned every hard edge in the library into a smooth fade.
//
// Everything asserted here is in Runtime/Core, which is deliberately free of
// UnityEngine, so the whole suite is plain C# with no scene and no play mode.

using System.Collections.Generic;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class PaywallBlockFillTests
    {
        private static PaywallBlockDoc Doc(string background = "#101014")
        {
            return new PaywallBlockDoc
            {
                Background = background,
                TextColor = "#F5F7FA",
                Accent = "#6478ff",
                AccentInk = "#0B0D10",
            };
        }

        private static List<RevnixGradient> Parse(string css, PaywallBlockDoc doc)
        {
            return RevnixBackground.ParseGradients(
                css, value => RevnixBlockColor.Resolve(value, doc).HasValue);
        }

        private static string Hex(RevnixBlockColor.Rgba c)
        {
            return string.Format(
                "#{0:X2}{1:X2}{2:X2}",
                (int)System.Math.Round(c.R * 255),
                (int)System.Math.Round(c.G * 255),
                (int)System.Math.Round(c.B * 255));
        }

        // ── @bg over a gradient ground ──────────────────────────────────────

        [Test]
        public void BgResolvesToTheGroundsFlatBaseNotTheRawGradient()
        {
            // The dashboard answers `@bg` with BaseColor(...) because it feeds
            // the token into color-mix(), which cannot take a gradient.
            var doc = Doc("linear-gradient(180deg, #231646 0%, #0C0C13 100%)");
            var resolved = RevnixBlockColor.Resolve("@bg", doc);
            Assert.IsTrue(resolved.HasValue);
            Assert.AreEqual("#231646", Hex(resolved.Value));
            Assert.IsTrue(RevnixBlockColor.Resolve("@bg/50", doc).HasValue);
        }

        [Test]
        public void ABgStopInsideAGradientNoLongerTakesTheWholeFillWithIt()
        {
            // Before the fix `@bg` returned null, the stop was dropped, and a
            // gradient left under two stops does not parse — so a fill the
            // design wrote as three stops painted nothing at all.
            var doc = Doc("linear-gradient(180deg, #231646 0%, #0C0C13 100%)");
            var layers = Parse("linear-gradient(180deg, rgba(12,16,19,0.5) 0%, @bg 100%)", doc);
            Assert.AreEqual(1, layers.Count);
            Assert.AreEqual(2, layers[0].Stops.Count);
        }

        // ── stop syntax the library actually ships ──────────────────────────

        [Test]
        public void AStopMayCarryTwoPositionsWhichIsAHardEdge()
        {
            // "@accent 0 22%" is the accent at BOTH 0 and 22%, then the next
            // colour starts at 22% — the progress-bar idiom, and a hard edge
            // rather than the smooth fade that reading one position produced.
            var doc = Doc();
            var layers = Parse("linear-gradient(90deg, @accent 0 22%, #16203C 22%)", doc);
            Assert.AreEqual(1, layers.Count);
            var stops = layers[0].Stops;
            Assert.AreEqual(3, stops.Count);
            Assert.AreEqual(0.0, stops[0].Position, 0.0001);
            Assert.AreEqual(0.22, stops[1].Position, 0.0001);
            Assert.AreEqual(0.22, stops[2].Position, 0.0001);
            Assert.AreEqual("@accent", stops[0].Color);
            Assert.AreEqual("@accent", stops[1].Color);
            Assert.AreEqual("#16203C", stops[2].Color);
        }

        [Test]
        public void TransparentIsAColourTheDesignsUse()
        {
            var doc = Doc();
            var parsed = RevnixBlockColor.ParseColor("transparent");
            Assert.IsTrue(parsed.HasValue);
            Assert.AreEqual(0f, parsed.Value.A);
            var layers = Parse("linear-gradient(90deg, @accent 0 60%, transparent 60%)", doc);
            Assert.AreEqual(1, layers.Count);
            Assert.AreEqual(3, layers[0].Stops.Count);
        }

        [Test]
        public void AColourWithSpacesInsideItIsNotTornApartByTheStopParser()
        {
            var doc = Doc();
            var layers = Parse("linear-gradient(180deg, rgba(0, 0, 0, 0.5) 0%, #FFFFFF 100%)", doc);
            Assert.AreEqual(1, layers.Count);
            Assert.AreEqual("rgba(0, 0, 0, 0.5)", layers[0].Stops[0].Color);
        }

        // ── colour-only fields ──────────────────────────────────────────────

        [Test]
        public void AGradientInAColourOnlyFieldCollapsesRatherThanDisappearing()
        {
            var doc = Doc();
            var reported = new List<string>();
            var colour = RevnixBlockColor.ResolveFlat(
                "linear-gradient(90deg, #00FF00 0%, #0000FF 100%)", doc, reported.Add);
            Assert.IsTrue(colour.HasValue);
            Assert.AreEqual("#00FF00", Hex(colour.Value));
            Assert.AreEqual(1, reported.Count);
            StringAssert.Contains("flattened", reported[0]);
        }

        [Test]
        public void APlainColourInAColourOnlyFieldReportsNothing()
        {
            var doc = Doc();
            var reported = new List<string>();
            var colour = RevnixBlockColor.ResolveFlat("@accent", doc, reported.Add);
            Assert.IsTrue(colour.HasValue);
            Assert.AreEqual("#6478FF", Hex(colour.Value));
            Assert.AreEqual(0, reported.Count);
        }

        [Test]
        public void AnUnreadableColourFallsBackToADesignColourAndReports()
        {
            var doc = Doc();
            var reported = new List<string>();
            // A repeating gradient this build does not know. The design's own
            // colour is still in the string, and that is what must paint —
            // never black, which is the regression this ticket exists for.
            var colour = RevnixBlockColor.ResolveFlat(
                "repeating-linear-gradient(180deg, transparent 0 33px, #E2D2B6 33px 34px), #FBF3E4",
                doc,
                reported.Add);
            Assert.IsTrue(colour.HasValue);
            Assert.AreEqual("#FBF3E4", Hex(colour.Value));
            Assert.AreEqual(1, reported.Count);
            StringAssert.Contains("unreadable colour", reported[0]);
        }

        [Test]
        public void AnAbsentValueResolvesToNothingAndReportsNothing()
        {
            var reported = new List<string>();
            Assert.IsFalse(RevnixBlockColor.ResolveFlat(null, Doc(), reported.Add).HasValue);
            Assert.IsFalse(RevnixBlockColor.ResolveFlat("  ", Doc(), reported.Add).HasValue);
            Assert.AreEqual(0, reported.Count);
        }

        // ── the flat base a gradient stands in for ──────────────────────────

        [Test]
        public void TheGradientBaseIsTheBottomLayersFirstOpaqueStop()
        {
            var doc = Doc();
            var css =
                "radial-gradient(120% 90% at 86% 4%, #FF3D7F 0%, rgba(255,61,127,0) 48%)," +
                "linear-gradient(180deg, #1C1046 0%, #0E0722 100%)";
            var layers = Parse(css, doc);
            Assert.AreEqual(2, layers.Count);
            // CSS paints the FIRST-listed layer on top, so the list is reversed:
            // the linear base comes first, and IT is what the fill collapses to.
            Assert.AreEqual(RevnixGradientKind.Linear, layers[0].Kind);
            Assert.AreEqual(RevnixGradientKind.Radial, layers[1].Kind);
            var baseColour = RevnixBlockColor.GradientBaseColor(layers, doc);
            Assert.IsTrue(baseColour.HasValue);
            Assert.AreEqual("#1C1046", Hex(baseColour.Value));
        }

        [Test]
        public void AFullyTransparentFirstStopIsNotTheBase()
        {
            var doc = Doc();
            var layers = Parse(
                "linear-gradient(180deg, rgba(10,7,20,0) 0%, #221133 40%, #000000 100%)", doc);
            var baseColour = RevnixBlockColor.GradientBaseColor(layers, doc);
            Assert.IsTrue(baseColour.HasValue);
            Assert.AreEqual("#221133", Hex(baseColour.Value));
        }

        [Test]
        public void AMalformedPositionCostsThePositionNotTheStop()
        {
            // The token comes off the colour either way. Leaving it attached
            // made the colour unparseable, dropping the stop — and a gradient
            // left with one stop does not parse at all, so one typo lost the
            // whole fill.
            var layers = Parse("linear-gradient(180deg, #112233 1.2.3%, #445566 100%)", Doc());
            Assert.AreEqual(1, layers.Count);
            Assert.AreEqual(2, layers[0].Stops.Count);
            Assert.AreEqual("#112233", layers[0].Stops[0].Color);
        }

        [Test]
        public void ARepeatingPatternPaintsNothingRatherThanAStripeColour()
        {
            // The colours inside a pattern are STRIPE colours. The library's
            // hairline grid is `#0E1B21` once every 26px; as a solid fill it is
            // a slab, which is a wrong answer rather than a degraded one.
            var reported = new List<string>();
            var colour = RevnixBlockColor.ResolveFlat(
                "repeating-linear-gradient(180deg, #0E1B21 0 1px, @bg 1px 26px)",
                Doc(),
                reported.Add);
            Assert.IsFalse(colour.HasValue);
            Assert.AreEqual(1, reported.Count);
        }

        [Test]
        public void APatternStackedOverAGroundStillFallsBackToThatGround()
        {
            // The BOTTOM layer decides: here it is a plain colour, and a plain
            // colour is exactly the surface colour the box should take.
            var colour = RevnixBlockColor.ResolveFlat(
                "repeating-linear-gradient(180deg, transparent 0 33px, #E2D2B6 33px 34px), #FBF3E4",
                Doc());
            Assert.IsTrue(colour.HasValue);
            Assert.AreEqual("#FBF3E4", Hex(colour.Value));
        }

        [Test]
        public void AUnitlessPositionIsReadRatherThanSwallowingTheStop()
        {
            // CSS allows a unitless zero. Requiring a `%` made the whole
            // "#112233 0" argument the COLOUR, which failed to parse and
            // dropped the stop — and a gradient left with one stop does not
            // parse at all.
            var layers = Parse("linear-gradient(180deg, #112233 0, #445566 100%)", Doc());
            Assert.AreEqual(1, layers.Count);
            Assert.AreEqual(2, layers[0].Stops.Count);
            Assert.AreEqual(0.0, layers[0].Stops[0].Position, 0.0001);
        }

        [Test]
        public void AnEmptyGradientListHasNoBaseColour()
        {
            Assert.IsFalse(
                RevnixBlockColor.GradientBaseColor(new List<RevnixGradient>(), Doc()).HasValue);
        }
    }
}
