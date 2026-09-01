// The screen background: the wire contract, the layer stack and the CSS
// gradient parser.
//
// These are the first tests in this package. They exist because five SDKs —
// this one included — decoded the ground colour under the key `ground`, which
// is the name of the RESOLVED layer and a key the dashboard has never written.
// Every paywall whose background had been edited rendered pure black, and
// nothing caught it.
//
// Everything asserted here is in Runtime/Core, which is deliberately free of
// UnityEngine, so the whole suite is plain C# with no scene and no play mode.

using System.Collections.Generic;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class PaywallBackgroundTests
    {
        /// <summary>
        /// A real document the dashboard published from its background library
        /// — the same bytes the other five renderers decode in their own
        /// suites. This is the closest thing the six have to a shared contract
        /// test.
        /// </summary>
        private const string GoldenDoc = @"{
          ""version"": 1,
          ""layout"": ""flow"",
          ""background"": {
            ""color"": ""radial-gradient(120% 85% at 50% 0%, #D6FF3F38 0%, #D6FF3F00 58%), linear-gradient(180deg, #111820 0%, #07090C 100%)"",
            ""presetId"": ""neon-track-spotlight""
          },
          ""textColor"": ""#FFFFFF"",
          ""accent"": ""#D6FF3F"",
          ""accentInk"": ""#14160A"",
          ""blocks"": [{ ""id"": ""t1"", ""type"": ""text"", ""text"": ""Go Pro"" }]
        }";

        private const string GoldenGround =
            "radial-gradient(120% 85% at 50% 0%, #D6FF3F38 0%, #D6FF3F00 58%), " +
            "linear-gradient(180deg, #111820 0%, #07090C 100%)";

        private static object Json(string json) => RevnixJson.Parse(json);

        private static List<RevnixGradient> Parse(string css)
            => RevnixBackground.ParseGradients(css, v => RevnixBlockColor.ParseColor(v).HasValue);

        // ── The wire contract ────────────────────────────────────────────────

        [Test]
        public void ARealPublishedDocumentResolvesToItsGradientGroundNotBlack()
        {
            var doc = PaywallBlockDoc.Parse(Json(GoldenDoc));
            Assert.IsNotNull(doc);
            Assert.AreEqual(GoldenGround, doc.Background);
            Assert.AreNotEqual("#000000", doc.Background);
        }

        [Test]
        public void TheGroundKeyIsColor()
        {
            Assert.AreEqual("#0B0D10", RevnixBackground.Ground(Json(@"{""color"":""#0B0D10""}")));
        }

        [Test]
        public void TheLegacyGroundKeyIsStillAccepted()
        {
            Assert.AreEqual("#0B0D10", RevnixBackground.Ground(Json(@"{""ground"":""#0B0D10""}")));
        }

        [Test]
        public void ColorWinsOverGroundWhenBothArePresent()
        {
            Assert.AreEqual(
                "#111820",
                RevnixBackground.Ground(Json(@"{""color"":""#111820"",""ground"":""#FF0000""}")));
        }

        [Test]
        public void APlainStringIsTheLegacyGround()
        {
            Assert.AreEqual("#0B0D10", RevnixBackground.Ground("#0B0D10"));
        }

        [Test]
        public void AnEmptyOrAbsentGroundIsNullRatherThanAnEmptyPaint()
        {
            Assert.IsNull(RevnixBackground.Ground(Json(@"{""color"":""""}")));
            Assert.IsNull(RevnixBackground.Ground(Json("{}")));
            Assert.IsNull(RevnixBackground.Ground(null));
        }

        // ── The layer stack ──────────────────────────────────────────────────

        [Test]
        public void ALegacyStringResolvesToExactlyOneGroundLayer()
        {
            var layers = RevnixBackground.Resolve("#0A0B0D");
            Assert.AreEqual("#0A0B0D", layers.Ground);
            Assert.IsNull(layers.Image);
            Assert.IsNull(layers.Overlay);
            Assert.IsTrue(layers.IsGroundOnly);
        }

        [Test]
        public void APhotoCarriesItsFitFocalPointOpacityAndBlur()
        {
            var layers = RevnixBackground.Resolve(Json(@"{""color"":""#000"",""image"":{
                ""url"":""https://x/y.jpg"",""fit"":""contain"",
                ""focalX"":20,""focalY"":35,""opacity"":70,""blur"":8}}"));
            Assert.IsNotNull(layers.Image);
            Assert.AreEqual("https://x/y.jpg", layers.Image.Url);
            Assert.AreEqual(RevnixBackgroundFit.Contain, layers.Image.Fit);
            Assert.AreEqual(20, layers.Image.FocalX);
            Assert.AreEqual(35, layers.Image.FocalY);
            Assert.AreEqual(0.7, layers.Image.Opacity, 0.0001);
            Assert.AreEqual(8, layers.Image.Blur);
            Assert.IsFalse(layers.IsGroundOnly);
        }

        [Test]
        public void PhotoDefaultsAreCoverCentredOpaqueAndUnblurred()
        {
            var layers = RevnixBackground.Resolve(Json(@"{""image"":{""url"":""https://x/y.jpg""}}"));
            Assert.AreEqual(RevnixBackgroundFit.Cover, layers.Image.Fit);
            Assert.AreEqual(50, layers.Image.FocalX);
            Assert.AreEqual(50, layers.Image.FocalY);
            Assert.AreEqual(1, layers.Image.Opacity);
            Assert.AreEqual(0, layers.Image.Blur);
        }

        [Test]
        public void LayersThatWouldDrawNothingAreDroppedRatherThanEmitted()
        {
            // A zero-opacity photo and a urlless one are both no-ops; emitting
            // them would cost a GameObject that paints nothing.
            Assert.IsNull(RevnixBackground.Resolve(Json(@"{""image"":{""fit"":""cover""}}")).Image);
            Assert.IsNull(
                RevnixBackground.Resolve(
                    Json(@"{""image"":{""url"":""https://x/y.jpg"",""opacity"":0}}")).Image);
            Assert.IsNull(
                RevnixBackground.Resolve(Json(@"{""overlay"":{""fill"":""#000"",""opacity"":0}}")).Overlay);
        }

        [Test]
        public void FocalPointAndOpacityAreClampedToTheirRanges()
        {
            var layers = RevnixBackground.Resolve(Json(@"{""image"":{
                ""url"":""https://x/y.jpg"",""focalX"":-40,""focalY"":900,""opacity"":400}}"));
            Assert.AreEqual(0, layers.Image.FocalX);
            Assert.AreEqual(100, layers.Image.FocalY);
            Assert.AreEqual(1, layers.Image.Opacity);
        }

        [Test]
        public void AScrimCarriesItsFillAndOpacity()
        {
            var layers = RevnixBackground.Resolve(
                Json(@"{""overlay"":{""fill"":""#000000"",""opacity"":40}}"));
            Assert.AreEqual("#000000", layers.Overlay.Fill);
            Assert.AreEqual(0.4, layers.Overlay.Opacity, 0.0001);
        }

        // ── @bg base colour ──────────────────────────────────────────────────

        [Test]
        public void AStackedGradientAnswersWithTheBottomLayerNotTheGlowOnTop()
        {
            // In CSS the first-listed layer paints on top. The fixture stacks a
            // translucent lime glow over a near-black base; answering with the
            // glow would paint the whole screen lime under the gradient.
            Assert.AreEqual("#111820", RevnixBackground.BaseColor(GoldenGround));
        }

        [Test]
        public void ASingleGradientAnswersWithItsFirstStop()
        {
            Assert.AreEqual(
                "#111820",
                RevnixBackground.BaseColor("linear-gradient(180deg, #111820, #07090C)"));
            Assert.AreEqual(
                "rgba(0, 0, 0, 0.5)",
                RevnixBackground.BaseColor(
                    "linear-gradient(180deg, rgba(0, 0, 0, 0.5), rgba(0, 0, 0, 1))"));
        }

        [Test]
        public void AFullyTransparentStopIsSkippedSinceItSaysNothingAboutTheGround()
        {
            Assert.AreEqual(
                "#111820",
                RevnixBackground.BaseColor("linear-gradient(180deg, #D6FF3F00 0%, #111820 100%)"));
        }

        [Test]
        public void AFlatColourAnswersWithItselfAndNothingAnswersBlack()
        {
            Assert.AreEqual("#0A0B0D", RevnixBackground.BaseColor("#0A0B0D"));
            Assert.AreEqual("#000000", RevnixBackground.BaseColor(null));
            Assert.AreEqual("#000000", RevnixBackground.BaseColor("   "));
        }

        // ── The CSS gradient parser ──────────────────────────────────────────

        [Test]
        public void AFlatColourIsNotAGradient()
        {
            Assert.AreEqual(0, Parse("#0A0B0D").Count);
        }

        [Test]
        public void OneEightyDegRunsStraightDownWhichIsTheLibrarysMostCommonRecipe()
        {
            var gradients = Parse("linear-gradient(180deg, #111820 0%, #07090C 100%)");
            Assert.AreEqual(1, gradients.Count);
            var linear = gradients[0];
            Assert.AreEqual(RevnixGradientKind.Linear, linear.Kind);
            Assert.AreEqual(0, linear.DirX, 0.0001);
            Assert.AreEqual(1, linear.DirY, 0.0001);
            Assert.AreEqual(2, linear.Stops.Count);
            Assert.AreEqual(0, linear.Stops[0].Position, 0.0001);
            Assert.AreEqual(1, linear.Stops[1].Position, 0.0001);
        }

        [Test]
        public void OneThirtyFiveDegRunsCornerToCorner()
        {
            var linear = Parse("linear-gradient(135deg, #000 0%, #fff 100%)")[0];
            Assert.AreEqual(1, linear.DirX, 0.0001);
            Assert.AreEqual(1, linear.DirY, 0.0001);
        }

        [Test]
        public void ARadialGradientKeepsItsCentreAndItsEllipseExtents()
        {
            var radial = Parse("radial-gradient(120% 85% at 50% 0%, #D6FF3F38 0%, #D6FF3F00 58%)")[0];
            Assert.AreEqual(RevnixGradientKind.Radial, radial.Kind);
            Assert.AreEqual(0.5, radial.CenterX, 0.0001);
            Assert.AreEqual(0, radial.CenterY, 0.0001);
            // This renderer rasterises, so unlike the SDKs backed by a circular
            // platform gradient it keeps CSS's independent extents.
            Assert.AreEqual(1.2, radial.RadiusX, 0.0001);
            Assert.AreEqual(0.85, radial.RadiusY, 0.0001);
            Assert.AreEqual(0.58, radial.Stops[1].Position, 0.0001);
        }

        [Test]
        public void AStackedGradientComesBackBottomFirstReversingCssOwnOrder()
        {
            // In CSS the FIRST layer paints on top. The renderer draws in
            // sequence, so the list is reversed on the way out — getting this
            // backwards would bury the glow under its own base.
            var gradients = Parse(GoldenGround);
            Assert.AreEqual(2, gradients.Count);
            Assert.AreEqual(RevnixGradientKind.Linear, gradients[0].Kind);
            Assert.AreEqual(RevnixGradientKind.Radial, gradients[1].Kind);
        }

        [Test]
        public void CommasInsideRgbaDoNotTearAStopInHalf()
        {
            var linear = Parse(
                "linear-gradient(180deg, rgba(0, 0, 0, 0.5) 0%, rgba(255, 255, 255, 1) 100%)")[0];
            Assert.AreEqual(2, linear.Stops.Count);
        }

        [Test]
        public void StopsWithNoPositionAreInterpolatedTheWayCssSpacesThem()
        {
            var linear = Parse("linear-gradient(180deg, #000, #888, #fff)")[0];
            Assert.AreEqual(0, linear.Stops[0].Position, 0.0001);
            Assert.AreEqual(0.5, linear.Stops[1].Position, 0.0001);
            Assert.AreEqual(1, linear.Stops[2].Position, 0.0001);
        }

        [Test]
        public void AKeywordDirectionIsUnderstoodAsWellAsAnAngle()
        {
            Assert.AreEqual(1, Parse("linear-gradient(to bottom, #000, #fff)")[0].DirY, 0.0001);
        }

        [Test]
        public void AOneStopOrUnparseableGradientIsDroppedRatherThanHalfDrawn()
        {
            Assert.AreEqual(0, Parse("linear-gradient(180deg, #000)").Count);
            Assert.AreEqual(0, Parse("conic-gradient(#000, #fff)").Count);
            Assert.AreEqual(0, Parse("linear-gradient(180deg, notacolour, alsonot)").Count);
        }
    }
}
