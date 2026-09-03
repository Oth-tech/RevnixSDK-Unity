// Canvas-layout maths — render contract v2 (REV-262), section 3.
//
// A `layout: "canvas"` design is authored against a 393×852 device screen and
// scaled as a whole. These are the numbers every renderer agrees on; the UGUI
// component that applies them (RevnixCanvasScaler) reads its answers from
// here, so the rule is testable without a scene.

using System;

namespace Revnix
{
    public static class RevnixPaywallCanvasLayout
    {
        /// <summary>
        /// The widest viewport a phone-authored design scales to. Beyond it the
        /// design is centred with the background filling the rest, so a tablet
        /// or a landscape phone never blows a phone screen up 2.6×.
        /// </summary>
        public const float MaxScaledWidth = 480f;

        /// <summary>Uniform scale for a viewport width: <c>min(width, 480) / 393</c>.</summary>
        public static float Scale(float viewportWidth)
        {
            if (viewportWidth <= 0f || float.IsNaN(viewportWidth)) return 1f;
            return Math.Min(viewportWidth, MaxScaledWidth) / PaywallBlockDoc.CanvasWidth;
        }

        /// <summary>
        /// The design's layout height in design units: <c>max(852,
        /// viewportHeight / scale)</c>. A taller viewport FILLS — root cards
        /// with <c>height: "100%"</c> and bottom-anchored groups follow — and
        /// never leaves a band; a shorter one scrolls.
        /// </summary>
        public static float DesignHeight(float viewportHeight, float scale)
        {
            if (scale <= 0f || float.IsNaN(scale)) return PaywallBlockDoc.CanvasHeight;
            if (viewportHeight <= 0f || float.IsNaN(viewportHeight)) return PaywallBlockDoc.CanvasHeight;
            return Math.Max(PaywallBlockDoc.CanvasHeight, viewportHeight / scale);
        }

        /// <summary>The scaled document height on screen.</summary>
        public static float ScaledHeight(float designHeight, float scale)
            => designHeight * scale;

        /// <summary>
        /// Whether content this tall must scroll in a viewport that tall.
        /// Scrolling is enabled only when the content is taller — a fit is
        /// pinned to the top with no bounce.
        /// </summary>
        public static bool Overflows(float viewportHeight, float contentHeight)
            => contentHeight > viewportHeight + 0.5f;

        /// <summary>
        /// The status-bar inset the fallback close chip moves down by, in
        /// canvas units. <c>Screen.safeArea</c> is in screen pixels with y up
        /// from the bottom, so the top inset is what the safe rect leaves
        /// above it; a canvas scale factor converts pixels to canvas units.
        /// </summary>
        public static float SafeAreaTopInset(
            float screenHeight,
            float safeAreaY,
            float safeAreaHeight,
            float canvasScaleFactor)
        {
            var pixels = screenHeight - (safeAreaY + safeAreaHeight);
            if (float.IsNaN(pixels) || pixels <= 0f) return 0f;
            var factor = canvasScaleFactor > 0.0001f && !float.IsNaN(canvasScaleFactor)
                ? canvasScaleFactor
                : 1f;
            return pixels / factor;
        }
    }
}
