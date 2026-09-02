// The paywall screen background — the C# half of the dashboard's model.
//
// A background is either the original CSS string (a colour or a gradient) or a
// layered spec: a ground colour/gradient, then a photo (fit, focal point,
// opacity, blur), then a scrim. RevnixBackground.Resolve turns either form into
// one paint list, mirroring revnix-app/src/lib/paywall-blocks/background.ts and
// the React SDK's blocks/background.ts — the same names, the same defaults, the
// same clamping.
//
// Two things this file exists to get right:
//
//  1. The ground field on the wire is `color`. It is NOT `ground` — that is the
//     name of the RESOLVED layer, and decoding it off the wire is what rendered
//     every edited paywall pure black. `ground` stays accepted so a document
//     published by a build that wrote it still opens.
//
//  2. Most shipped library backgrounds are CSS gradients, which ParseColor
//     rejects by design. They are parsed here into descriptors the renderer
//     bakes into a texture, so the ground paints as designed rather than
//     falling back to black.
//
// Like the rest of Runtime/Core this file stays free of UnityEngine: it is
// plain C# that can be exercised without an editor.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Revnix
{
    /// <summary>How a background photo fills the screen.</summary>
    public enum RevnixBackgroundFit
    {
        Cover,
        Contain,
    }

    /// <summary>The photo layer.</summary>
    public sealed class RevnixBackgroundImage
    {
        /// <summary>An https URL the device can load.</summary>
        public string Url;
        public RevnixBackgroundFit Fit = RevnixBackgroundFit.Cover;

        /// <summary>
        /// Focal point in percent (0-100, 50/50 = centred): the part of the
        /// photo that must survive a cover-crop.
        /// </summary>
        public double FocalX = 50;
        public double FocalY = 50;

        /// <summary>0-1.</summary>
        public double Opacity = 1;

        /// <summary>Blur radius in px; 0 when no blur was asked for.</summary>
        public double Blur;
    }

    /// <summary>The scrim painted over the photo.</summary>
    public sealed class RevnixBackgroundOverlay
    {
        /// <summary>Any colour or gradient string.</summary>
        public string Fill;

        /// <summary>0-1.</summary>
        public double Opacity = 1;
    }

    /// <summary>The resolved paint list, bottom layer first.</summary>
    public sealed class RevnixBackgroundLayers
    {
        /// <summary>The ground fill (a colour or gradient string), or null.</summary>
        public string Ground;
        public RevnixBackgroundImage Image;
        public RevnixBackgroundOverlay Overlay;

        /// <summary>
        /// True when the background paints nothing but a ground — the shape
        /// every unedited paywall has, and the one that needs no extra layers.
        /// </summary>
        public bool IsGroundOnly => Image == null && Overlay == null;
    }

    /// <summary>One colour stop: the colour string and its position in 0-1.</summary>
    public struct RevnixGradientStop
    {
        public string Color;
        public double Position;
    }

    /// <summary>The two gradient shapes the dashboard emits.</summary>
    public enum RevnixGradientKind
    {
        Linear,
        Radial,
    }

    /// <summary>A parsed CSS gradient, in terms the renderer can rasterise.</summary>
    public sealed class RevnixGradient
    {
        public RevnixGradientKind Kind;

        /// <summary>
        /// Linear only: the unit direction in screen coordinates (x right,
        /// y down), scaled so its largest component is 1 — which is what makes
        /// 135deg run corner to corner as CSS draws it.
        /// </summary>
        public double DirX;
        public double DirY;

        /// <summary>Radial only: 0-1 fractions of the box.</summary>
        public double CenterX;
        public double CenterY;

        /// <summary>
        /// Radial only: the ellipse extents as fractions of the box's width and
        /// height. This renderer rasterises the gradient into a texture that is
        /// then stretched to the screen, so it can draw CSS's ellipse exactly
        /// rather than approximating it with a circle the way the SDKs backed
        /// by a platform gradient API have to.
        /// </summary>
        public double RadiusX;
        public double RadiusY;

        public List<RevnixGradientStop> Stops = new List<RevnixGradientStop>();
    }

    /// <summary>Background decoding: the wire form to a paint list.</summary>
    public static class RevnixBackground
    {
        private const string DefaultGround = "#000000";

        /// <summary>0-100 (or absent) to 0-1, clamped.</summary>
        private static double Pct(double? value, double fallback)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            {
                return fallback;
            }
            return Math.Max(0, Math.Min(1, value.Value / 100));
        }

        /// <summary>0-100 (or absent) to a clamped percentage.</summary>
        private static double Coord(double? value)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            {
                return 50;
            }
            return Math.Max(0, Math.Min(100, value.Value));
        }

        private static string NonEmpty(string value)
            => string.IsNullOrEmpty(value) ? null : value;

        /// <summary>
        /// The ground paint of a background: `color`, or the legacy `ground`.
        /// Reading `color` FIRST is the fix — it is the only key the dashboard
        /// writes.
        /// </summary>
        public static string Ground(object background)
        {
            if (background is string s) return NonEmpty(s);
            if (background is Dictionary<string, object> map)
            {
                return NonEmpty(RevnixJson.GetString(map, "color"))
                    ?? NonEmpty(RevnixJson.GetString(map, "ground"));
            }
            return null;
        }

        /// <summary>
        /// The paint list for a background: ground, then photo, then scrim.
        /// Layers that would draw nothing (no url, zero opacity) are dropped,
        /// so a legacy string resolves to exactly one ground layer.
        /// </summary>
        public static RevnixBackgroundLayers Resolve(object background, Func<string, string> resolve = null)
        {
            var identity = resolve ?? (v => v);
            var ground = Ground(background);
            var layers = new RevnixBackgroundLayers
            {
                Ground = ground == null ? null : identity(ground),
            };

            if (!(background is Dictionary<string, object> map)) return layers;

            var image = RevnixJson.GetObject(map, "image");
            if (image != null)
            {
                var url = NonEmpty(RevnixJson.GetString(image, "url"));
                var opacity = Pct(RevnixJson.GetNullableDouble(image, "opacity"), 1);
                if (url != null && opacity > 0)
                {
                    var blur = RevnixJson.GetNullableDouble(image, "blur") ?? 0;
                    layers.Image = new RevnixBackgroundImage
                    {
                        Url = url,
                        Fit = RevnixJson.GetString(image, "fit") == "contain"
                            ? RevnixBackgroundFit.Contain
                            : RevnixBackgroundFit.Cover,
                        FocalX = Coord(RevnixJson.GetNullableDouble(image, "focalX")),
                        FocalY = Coord(RevnixJson.GetNullableDouble(image, "focalY")),
                        Opacity = opacity,
                        Blur = blur > 0 ? blur : 0,
                    };
                }
            }

            var overlay = RevnixJson.GetObject(map, "overlay");
            if (overlay != null)
            {
                var fill = NonEmpty(RevnixJson.GetString(overlay, "fill"));
                var opacity = Pct(RevnixJson.GetNullableDouble(overlay, "opacity"), 1);
                if (fill != null && opacity > 0)
                {
                    layers.Overlay = new RevnixBackgroundOverlay
                    {
                        Fill = identity(fill),
                        Opacity = opacity,
                    };
                }
            }

            return layers;
        }

        /// <summary>
        /// The flat colour a gradient ground stands in for — what paints UNDER
        /// the gradient, so a form this parser does not understand still shows
        /// a colour from the design rather than black.
        /// <para>
        /// It reads the LAST comma-separated layer, because in CSS the
        /// first-listed layer paints on TOP: taking the first colour would
        /// answer with the translucent accent glow the library's Spotlight and
        /// Corner halo presets stack over their base, not with the base
        /// itself. Fully transparent stops are skipped for the same reason.
        /// </para>
        /// <para>
        /// This is ALSO what `@bg` resolves to: the dashboard feeds that token
        /// into color-mix(), which cannot take a gradient, so it collapses a
        /// gradient ground to one colour exactly as this does.
        /// </para>
        /// </summary>
        public static string BaseColor(string ground)
        {
            var s = (ground ?? string.Empty).Trim();
            if (s.Length == 0) return DefaultGround;
            var layers = SplitTopLevel(s);
            var bottom = layers.Count == 0 ? s : layers[layers.Count - 1];
            var matches = Regex.Matches(
                bottom, @"#[0-9a-fA-F]{3,8}\b|rgba?\([^)]*\)", RegexOptions.IgnoreCase);
            if (matches.Count == 0) return s;
            foreach (Match match in matches)
            {
                var parsed = RevnixBlockColor.ParseColor(match.Value);
                if (parsed.HasValue && parsed.Value.A > 0) return match.Value;
            }
            return matches[0].Value;
        }

        /// <summary>
        /// Whether a paint string's BOTTOM layer is a repeating pattern.
        /// <para>
        /// A <c>repeating-*</c> gradient is a TEXTURE, and the colours inside it
        /// are stripe colours rather than the surface's. When a build cannot
        /// draw one, painting a colour lifted out of its arguments across the
        /// whole box is a WRONG answer rather than a degraded one — the
        /// library's hairline grid is one colour every 26px, and as a solid fill
        /// it is a slab. Such a fill paints nothing instead.
        /// </para>
        /// <para>
        /// The bottom layer decides, so a pattern stacked over a real ground
        /// still falls back to that ground.
        /// </para>
        /// </summary>
        public static bool IsRepeatingPattern(string css)
        {
            var s = (css ?? string.Empty).Trim();
            if (s.Length == 0) return false;
            var layers = SplitTopLevel(s);
            var bottom = layers.Count == 0 ? s : layers[layers.Count - 1];
            return bottom.StartsWith("repeating-", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Splits on top-level commas only, so the commas inside `rgba(...)`
        /// and inside a nested gradient's argument list do not tear an argument
        /// in half.
        /// </summary>
        internal static List<string> SplitTopLevel(string input)
        {
            var out_ = new List<string>();
            if (string.IsNullOrEmpty(input)) return out_;
            var depth = 0;
            var start = 0;
            for (var i = 0; i < input.Length; i++)
            {
                var c = input[i];
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    if (depth > 0) depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    var piece = input.Substring(start, i - start).Trim();
                    if (piece.Length > 0) out_.Add(piece);
                    start = i + 1;
                }
            }
            var tail = input.Substring(start).Trim();
            if (tail.Length > 0) out_.Add(tail);
            return out_;
        }

        /// <summary>
        /// Parses the gradient forms the dashboard emits —
        /// `linear-gradient(Ndeg, ...)` and
        /// `radial-gradient(RX% RY% at X% Y%, ...)` — plus the comma-separated
        /// stacks of them the library's Spotlight and Corner halo presets use.
        ///
        /// Returns the layers BOTTOM FIRST, the reverse of CSS's own order (in
        /// CSS the first-listed layer paints on top), so the result can be
        /// drawn in sequence.
        /// </summary>
        public static List<RevnixGradient> ParseGradients(string css, Func<string, bool> isColor)
        {
            var out_ = new List<RevnixGradient>();
            if (string.IsNullOrEmpty(css)) return out_;
            foreach (var part in SplitTopLevel(css))
            {
                var gradient = ParseOne(part, isColor);
                if (gradient != null) out_.Add(gradient);
            }
            out_.Reverse();
            return out_;
        }

        private struct RawStop
        {
            public string Color;
            public double? Position;
        }

        /// <summary>
        /// One argument of a gradient's stop list, as the stop(s) it stands for.
        /// <para>
        /// The positions are the trailing <c>&lt;n&gt;%</c> (or a unitless
        /// <c>0</c>); everything before them is the colour, which may itself
        /// contain spaces (<c>rgba(0, 0, 0, 0.5)</c>). CSS allows TWO positions
        /// on one stop — <c>@accent 0 22%</c> is the same colour at both, the
        /// hard edge the library's progress bars and split panels are drawn
        /// with — so this answers with a list rather than a single stop.
        /// </para>
        /// </summary>
        private static List<RawStop> ParseStops(string raw, Func<string, bool> isColor)
        {
            var out_ = new List<RawStop>();
            var body = (raw ?? string.Empty).Trim();
            if (body.Length == 0) return out_;
            var positions = new List<double>();
            while (positions.Count < 2)
            {
                var match = Regex.Match(body, @"\s(-?[0-9.]+%|0)\s*$");
                if (!match.Success) break;
                var text = match.Groups[1].Value;
                var isPercent = text.EndsWith("%", StringComparison.Ordinal);
                var parsed = double.TryParse(
                    isPercent ? text.Substring(0, text.Length - 1) : text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value);
                // The token comes off `body` either way. Leaving a position
                // this build could not read attached to the colour made the
                // colour unparseable too, which dropped the whole stop — and a
                // gradient left with one stop does not parse at all. A
                // malformed position is worth losing; the stop is not.
                body = body.Substring(0, match.Index).Trim();
                if (!parsed) break;
                positions.Insert(0, Math.Max(0, Math.Min(1, isPercent ? value / 100 : value)));
            }
            if (body.Length == 0 || !isColor(body)) return out_;
            if (positions.Count == 0)
            {
                out_.Add(new RawStop { Color = body, Position = null });
                return out_;
            }
            foreach (var position in positions)
            {
                out_.Add(new RawStop { Color = body, Position = position });
            }
            return out_;
        }

        /// <summary>Fills in the positions CSS would interpolate.</summary>
        private static List<double> StopPositions(List<RawStop> stops)
        {
            var out_ = new List<double?>();
            foreach (var stop in stops) out_.Add(stop.Position);
            if (!out_[0].HasValue) out_[0] = 0;
            if (!out_[out_.Count - 1].HasValue) out_[out_.Count - 1] = 1;
            for (var i = 1; i < out_.Count - 1; i++)
            {
                if (out_[i].HasValue) continue;
                var next = i + 1;
                while (next < out_.Count && !out_[next].HasValue) next++;
                var from = out_[i - 1].Value;
                var to = out_[next].Value;
                var span = (double)(next - (i - 1));
                for (var k = i; k < next; k++)
                {
                    out_[k] = from + (to - from) * ((k - (i - 1)) / span);
                }
                i = next - 1;
            }
            // Gradient stops must be non-decreasing.
            var result = new List<double>();
            var previous = 0.0;
            foreach (var value in out_)
            {
                var v = Math.Max(0, Math.Min(1, value ?? previous));
                if (v < previous) v = previous;
                previous = v;
                result.Add(v);
            }
            return result;
        }

        private static RevnixGradient ParseOne(string raw, Func<string, bool> isColor)
        {
            var s = (raw ?? string.Empty).Trim();
            var open = s.IndexOf('(');
            if (open < 0 || !s.EndsWith(")", StringComparison.Ordinal)) return null;
            var name = s.Substring(0, open).Trim().ToLowerInvariant();
            var args = SplitTopLevel(s.Substring(open + 1, s.Length - open - 2));
            if (args.Count == 0) return null;

            if (name == "linear-gradient")
            {
                var angle = 180.0;
                var first = 0;
                var head = args[0].Trim().ToLowerInvariant();
                var deg = Regex.Match(head, @"^(-?[0-9.]+)deg$");
                if (deg.Success)
                {
                    if (double.TryParse(
                            deg.Groups[1].Value,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var value))
                    {
                        angle = value;
                    }
                    first = 1;
                }
                else if (head.StartsWith("to ", StringComparison.Ordinal))
                {
                    var keyword = AngleForKeyword(head.Substring(3).Trim());
                    if (keyword.HasValue) angle = keyword.Value;
                    first = 1;
                }
                var stops = CollectStops(args, first, isColor);
                if (stops.Count < 2) return null;
                // CSS measures clockwise from "to top", so the gradient runs
                // along (sin a, -cos a) in screen coordinates.
                var radians = angle * Math.PI / 180;
                var dx = Math.Sin(radians);
                var dy = -Math.Cos(radians);
                var longest = Math.Max(Math.Abs(dx), Math.Abs(dy));
                if (longest > 0.0001)
                {
                    dx /= longest;
                    dy /= longest;
                }
                // Math.Sin(PI) is 1.2e-16, not 0, so an axis-aligned gradient
                // carries a hair of the other axis. Harmless when sampled, but
                // snapping it keeps the descriptor exact and comparable.
                if (Math.Abs(dx) < 1e-9) dx = 0;
                if (Math.Abs(dy) < 1e-9) dy = 0;
                return Build(RevnixGradientKind.Linear, stops, g =>
                {
                    g.DirX = dx;
                    g.DirY = dy;
                });
            }

            if (name == "radial-gradient")
            {
                var centerX = 0.5;
                var centerY = 0.5;
                var radiusX = 0.5;
                var radiusY = 0.5;
                var first = 0;
                var head = args[0].Trim();
                var percents = Percents(head);
                if (Regex.IsMatch(head, @"^[0-9.]+%\s+[0-9.]+%") && percents.Count >= 2)
                {
                    radiusX = Math.Max(0.05, Math.Min(4, percents[0] / 100));
                    radiusY = Math.Max(0.05, Math.Min(4, percents[1] / 100));
                    if (percents.Count >= 4)
                    {
                        centerX = percents[2] / 100;
                        centerY = percents[3] / 100;
                    }
                    first = 1;
                }
                else if (head.StartsWith("at ", StringComparison.OrdinalIgnoreCase) && percents.Count >= 2)
                {
                    centerX = percents[0] / 100;
                    centerY = percents[1] / 100;
                    first = 1;
                }
                var stops = CollectStops(args, first, isColor);
                if (stops.Count < 2) return null;
                return Build(RevnixGradientKind.Radial, stops, g =>
                {
                    g.CenterX = centerX;
                    g.CenterY = centerY;
                    g.RadiusX = radiusX;
                    g.RadiusY = radiusY;
                });
            }

            return null;
        }

        private static List<RawStop> CollectStops(List<string> args, int first, Func<string, bool> isColor)
        {
            var stops = new List<RawStop>();
            for (var i = first; i < args.Count; i++)
            {
                stops.AddRange(ParseStops(args[i], isColor));
            }
            return stops;
        }

        private static RevnixGradient Build(
            RevnixGradientKind kind,
            List<RawStop> stops,
            Action<RevnixGradient> configure)
        {
            var gradient = new RevnixGradient { Kind = kind };
            configure(gradient);
            var positions = StopPositions(stops);
            for (var i = 0; i < stops.Count; i++)
            {
                gradient.Stops.Add(new RevnixGradientStop
                {
                    Color = stops[i].Color,
                    Position = positions[i],
                });
            }
            return gradient;
        }

        /// <summary>Every `&lt;n&gt;%` in a string, in order.</summary>
        private static List<double> Percents(string text)
        {
            var out_ = new List<double>();
            foreach (Match match in Regex.Matches(text ?? string.Empty, "[0-9.]+%"))
            {
                var body = match.Value.Substring(0, match.Value.Length - 1);
                if (double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    out_.Add(value);
                }
            }
            return out_;
        }

        private static double? AngleForKeyword(string keyword)
        {
            var normalised = Regex.Replace((keyword ?? string.Empty).Trim(), @"\s+", " ");
            switch (normalised)
            {
                case "top": return 0;
                case "right": return 90;
                case "bottom": return 180;
                case "left": return 270;
                case "top right":
                case "right top": return 45;
                case "bottom right":
                case "right bottom": return 135;
                case "bottom left":
                case "left bottom": return 225;
                case "top left":
                case "left top": return 315;
                default: return null;
            }
        }
    }
}
