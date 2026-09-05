// The geometry style fields — `clipPath`, `translate`, `fillSize`, `filter`,
// `textWrap`.
//
// All five were named by revnix-app's BlockStyle and by NONE of the native
// renderers, so BlockStyle.FromJson dropped them and the renderer never saw
// them. 13 of the 25 shipped template categories set at least one.
//
// `translate` is the one this platform DRAWS: a pinned badge is centred with
// `left: 50%` plus `translate: "-50% 0"`, and a percentage there is a fraction
// of the badge's own width — which is exactly what a UGUI pivot is, so the two
// compose without measuring anything. The parser is pinned here; the pivot
// arithmetic that consumes it lives in Runtime/UI and needs the editor.
//
// The rest are pinned as CARRIED. UGUI cannot clip to an arbitrary polygon,
// stack filters, or tile a gradient, so those stay undrawn — but they now
// DECODE, which is what lets the renderer report them instead of dropping them
// in silence. A field that decodes is a documented limitation; a field that
// does not is an invisible one.
//
// Everything asserted here is in Runtime/Core, which is deliberately free of
// UnityEngine, so the whole suite is plain C# with no scene and no play mode.

using System.Collections.Generic;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class PaywallBlockGeometryTests
    {
        private const string Starburst =
            "polygon(50% 0%,57% 9%,68% 4%,72% 15%,84% 13%,84% 25%,96% 27%," +
            "92% 38%,100% 45%,93% 54%,98% 65%,88% 69%,89% 81%,77% 80%,73% 92%," +
            "62% 87%,54% 97%,46% 88%,35% 94%,30% 83%,18% 84%,20% 72%,8% 68%," +
            "14% 58%,5% 50%,13% 42%,7% 31%,18% 28%,17% 16%,29% 17%,32% 5%,43% 9%)";

        // ——— lengths ———

        [Test]
        public void LengthReadsEveryFormTheDesignsWrite()
        {
            Assert.AreEqual(16.0, RevnixBlockGeometry.ParseLength("16px").Value.Px);
            Assert.AreEqual(-8.0, RevnixBlockGeometry.ParseLength("-8px").Value.Px);
            // A bare number is px — how the designs write a zero.
            Assert.AreEqual(0.0, RevnixBlockGeometry.ParseLength("0").Value.Px);
            Assert.AreEqual(0.5, RevnixBlockGeometry.ParseLength("50%").Value.Fraction);
            Assert.AreEqual(-0.5, RevnixBlockGeometry.ParseLength("-50%").Value.Fraction);
            Assert.AreEqual(1.0, RevnixBlockGeometry.ParseLength(" 100% ").Value.Fraction);
        }

        [Test]
        public void CalcCarriesBothHalves()
        {
            // The ticket-notch clips are authored as calc(100% - 16px); reading
            // only one half puts the notch at the wrong edge.
            var notch = RevnixBlockGeometry.ParseLength("calc(100% - 16px)").Value;
            Assert.AreEqual(1.0, notch.Fraction);
            Assert.AreEqual(-16.0, notch.Px);
            Assert.AreEqual(184.0, notch.ResolvedAgainst(200.0));
            Assert.IsFalse(notch.IsAbsolute);
        }

        [Test]
        public void UnreadableLengthIsDeclinedRatherThanGuessed()
        {
            Assert.IsNull(RevnixBlockGeometry.ParseLength("var(--x)"));
            Assert.IsNull(RevnixBlockGeometry.ParseLength("4rem"));
            Assert.IsNull(RevnixBlockGeometry.ParseLength(""));
            Assert.IsNull(RevnixBlockGeometry.ParseLength(null));
        }

        // ——— translate ———

        [Test]
        public void TranslateReadsTheBadgeCentringIdiom()
        {
            var t = RevnixBlockGeometry.ParseTranslate("-50% 0").Value;
            Assert.AreEqual(-0.5, t.X.Fraction);
            Assert.AreEqual(0.0, t.Y.Fraction);
            Assert.IsFalse(t.IsAbsolute);
            // The badge is pulled back by half its OWN width, not the parent's.
            Assert.AreEqual(-60.0, t.X.ResolvedAgainst(120.0));
        }

        [Test]
        public void TranslateReadsThePxFormAndNeedsNoMeasuring()
        {
            var t = RevnixBlockGeometry.ParseTranslate("0 -8px").Value;
            Assert.AreEqual(-8.0, t.Y.Px);
            Assert.IsTrue(t.IsAbsolute);
        }

        [Test]
        public void SingleComponentTranslateLeavesYAtZero()
        {
            var t = RevnixBlockGeometry.ParseTranslate("12px").Value;
            Assert.AreEqual(12.0, t.X.Px);
            Assert.AreEqual(0.0, t.Y.Px);
            Assert.AreEqual(0.0, t.Y.Fraction);
        }

        [Test]
        public void UnreadableTranslateMovesNothing()
        {
            Assert.IsNull(RevnixBlockGeometry.ParseTranslate("nonsense"));
            Assert.IsNull(RevnixBlockGeometry.ParseTranslate(""));
            Assert.IsNull(RevnixBlockGeometry.ParseTranslate(null));
        }

        // ——— clip-path ———

        [Test]
        public void PolygonReadsATriangle()
        {
            var points = RevnixBlockGeometry.ParsePolygon("polygon(50% 0,100% 100%,0 100%)");
            Assert.AreEqual(3, points.Count);
            Assert.AreEqual(0.5, points[0].X.Fraction);
            Assert.AreEqual(1.0, points[1].Y.Fraction);
        }

        [Test]
        public void PolygonKeepsCalcPointsTogether()
        {
            // The space inside calc() must NOT split the point in two — this is
            // the ticket notch, and splitting naively yields garbage vertices.
            var points = RevnixBlockGeometry.ParsePolygon(
                "polygon(0 0,100% 0,100% calc(100% - 16px),50% 100%,0 calc(100% - 16px))");
            Assert.AreEqual(5, points.Count);
            Assert.AreEqual(1.0, points[2].Y.Fraction);
            Assert.AreEqual(-16.0, points[2].Y.Px);
        }

        [Test]
        public void PolygonReadsThe32PointStarburst()
        {
            Assert.AreEqual(32, RevnixBlockGeometry.ParsePolygon(Starburst).Count);
        }

        [Test]
        public void LeadingFillRuleIsAcceptedAndIgnored()
        {
            var points = RevnixBlockGeometry.ParsePolygon("polygon(evenodd, 0 0, 100% 0, 50% 100%)");
            Assert.AreEqual(3, points.Count);
        }

        [Test]
        public void NonPolygonClipIsDeclined()
        {
            Assert.IsNull(RevnixBlockGeometry.ParsePolygon("inset(10px)"));
            Assert.IsNull(RevnixBlockGeometry.ParsePolygon("circle(50%)"));
            Assert.IsNull(RevnixBlockGeometry.ParsePolygon(null));
        }

        [Test]
        public void DegeneratePolygonIsDeclined()
        {
            // Two points describe a line, which would erase the block.
            Assert.IsNull(RevnixBlockGeometry.ParsePolygon("polygon(0 0,100% 100%)"));
            Assert.IsNull(RevnixBlockGeometry.ParsePolygon("polygon(0 0,100% 0,50%)"));
        }

        // ——— the model carries all five ———

        private static BlockStyle Style(string json)
        {
            var map = RevnixJson.Parse(json) as Dictionary<string, object>;
            return BlockStyle.FromJson(map);
        }

        [Test]
        public void AllFiveFieldsSurviveDecoding()
        {
            var s = Style(
                "{\"clipPath\":\"polygon(50% 0,100% 100%,0 100%)\",\"translate\":\"-50% 0\"," +
                "\"fillSize\":\"18px 18px\",\"textWrap\":\"pretty\",\"filter\":\"blur(6px)\"}");
            Assert.AreEqual("polygon(50% 0,100% 100%,0 100%)", s.ClipPath);
            Assert.AreEqual("-50% 0", s.Translate);
            Assert.AreEqual("18px 18px", s.FillSize);
            Assert.AreEqual("pretty", s.TextWrap);
            Assert.AreEqual("blur(6px)", s.Filter);
        }

        [Test]
        public void AllFiveFieldsMergeLikeSelectedStyle()
        {
            // A plan card's selectedStyle merges over its base style. A field
            // the merge forgets is a field that cannot change on selection.
            var basis = Style(
                "{\"clipPath\":\"polygon(0 0,100% 0,50% 100%)\",\"translate\":\"0 0\"," +
                "\"fillSize\":\"6px 6px\",\"textWrap\":\"pretty\",\"filter\":\"blur(2px)\"}");
            var selected = Style(
                "{\"clipPath\":\"polygon(0 0,100% 0,100% 100%)\",\"translate\":\"-50% 0\"," +
                "\"fillSize\":\"18px 18px\",\"textWrap\":\"balance\",\"filter\":\"blur(6px)\"}");
            var merged = basis.Merging(selected);
            Assert.AreEqual("polygon(0 0,100% 0,100% 100%)", merged.ClipPath);
            Assert.AreEqual("-50% 0", merged.Translate);
            Assert.AreEqual("18px 18px", merged.FillSize);
            Assert.AreEqual("balance", merged.TextWrap);
            Assert.AreEqual("blur(6px)", merged.Filter);
        }

        [Test]
        public void MergeLeavesUnsetGeometryAlone()
        {
            var basis = Style(
                "{\"clipPath\":\"polygon(0 0,100% 0,50% 100%)\",\"translate\":\"-50% 0\"," +
                "\"fillSize\":\"6px 6px\",\"textWrap\":\"pretty\",\"filter\":\"blur(2px)\"}");
            var merged = basis.Merging(Style("{\"fill\":\"#ffffff\"}"));
            Assert.AreEqual("polygon(0 0,100% 0,50% 100%)", merged.ClipPath);
            Assert.AreEqual("-50% 0", merged.Translate);
            Assert.AreEqual("6px 6px", merged.FillSize);
            Assert.AreEqual("pretty", merged.TextWrap);
            Assert.AreEqual("blur(2px)", merged.Filter);
        }
    }
}
