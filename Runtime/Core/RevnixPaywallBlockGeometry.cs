// Geometry paint strings — the block style fields whose value is a small piece
// of CSS geometry rather than a number.
//
// `clipPath`, `translate`, `fillSize` and `filter` were named by revnix-app's
// BlockStyle and by NONE of the native renderers, so BlockStyle.FromJson
// dropped them and the renderer never saw them. 13 of the 25 shipped template
// categories set at least one, which meant a pinned "MOST POPULAR" chip sat
// half a width off centre — silently, and only on device, never in the
// dashboard preview the design was approved in.
//
// Lives in Core for the same reason the rest of the block model does: it is
// ordinary C# with no UnityEngine dependency, so it compiles and tests without
// the editor, and that is where the parsing bugs live.
//
// Parsing is total: an unreadable value yields null and the block draws
// unmoved — never wrong, never thrown. Kept in lockstep with the Swift
// PaywallBlockGeometry.swift and the Kotlin PaywallBlockGeometry.kt; the three
// are interpreters of the same document and must agree on every form below.

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Revnix
{
    /// <summary>
    /// A CSS <c>&lt;length-percentage&gt;</c>, kept as both halves so one type
    /// covers every form the designs use: <c>16px</c> is <see cref="Px"/>
    /// alone, <c>50%</c> is <see cref="Fraction"/> alone, and
    /// <c>calc(100% - 16px)</c> — which the ticket-notch clips need — is the
    /// two together. Resolving is then a single multiply-add against the box.
    /// </summary>
    public struct RevnixLength
    {
        /// <summary>Fraction of the reference box on this axis: 50% → 0.5.</summary>
        public double Fraction;

        /// <summary>Fixed px added after the fraction: calc(100% - 16px) → -16.</summary>
        public double Px;

        public RevnixLength(double fraction, double px)
        {
            Fraction = fraction;
            Px = px;
        }

        /// <summary>This length against a box edge of <paramref name="extent"/> px.</summary>
        public double ResolvedAgainst(double extent)
        {
            return Fraction * extent + Px;
        }

        /// <summary>
        /// True when the value needs no measuring — a pure-px length applies
        /// without laying the block out first.
        /// </summary>
        public bool IsAbsolute
        {
            get { return Fraction == 0.0; }
        }
    }

    /// <summary>
    /// A parsed CSS <c>translate</c>: an x and a y, each of which may be a
    /// percentage OF THE BLOCK'S OWN SIZE. That "own size" is the whole point —
    /// the designs centre a pinned badge with <c>left: 50%</c> plus
    /// <c>translate: "-50% 0"</c>, and resolving the -50% against anything but
    /// the badge's own width puts it somewhere else entirely.
    /// </summary>
    public struct RevnixTranslate
    {
        public RevnixLength X;
        public RevnixLength Y;

        public RevnixTranslate(RevnixLength x, RevnixLength y)
        {
            X = x;
            Y = y;
        }

        /// <summary>True when neither axis needs the block measured.</summary>
        public bool IsAbsolute
        {
            get { return X.IsAbsolute && Y.IsAbsolute; }
        }
    }

    /// <summary>One polygon vertex, each axis resolved against its own edge.</summary>
    public struct RevnixPolygonPoint
    {
        public RevnixLength X;
        public RevnixLength Y;

        public RevnixPolygonPoint(RevnixLength x, RevnixLength y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>The geometry parsers. Pure, total, and shared with the UI layer.</summary>
    public static class RevnixBlockGeometry
    {
        /// <summary>
        /// One <c>&lt;length-percentage&gt;</c> token: <c>50%</c>, <c>-8px</c>,
        /// a bare <c>0</c>, or the <c>calc(&lt;pct&gt; ± &lt;px&gt;)</c> form.
        /// Returns null for anything else — a <c>var()</c>, an unsupported unit
        /// — so the caller declines the whole value rather than moving a block
        /// by a number it guessed.
        /// </summary>
        public static RevnixLength? ParseLength(string token)
        {
            if (token == null) return null;
            var raw = token.Trim();
            if (raw.Length == 0) return null;

            // calc(100% - 16px) / calc(50% + 4px). Only the single-operator
            // form the designs write; nested arithmetic is declined.
            if (raw.ToLowerInvariant().StartsWith("calc(") && raw.EndsWith(")"))
            {
                var inner = raw.Substring(5, raw.Length - 6);
                for (var i = 1; i < inner.Length; i++)
                {
                    var c = inner[i];
                    if (c != '+' && c != '-') continue;
                    // An operator in CSS calc must be surrounded by whitespace,
                    // which is also what stops `1e-3` from splitting here.
                    if (inner[i - 1] != ' ') continue;
                    var a = ParseLength(inner.Substring(0, i));
                    var b = ParseLength(inner.Substring(i + 1));
                    if (a == null || b == null) return null;
                    var sign = c == '-' ? -1.0 : 1.0;
                    return new RevnixLength(
                        a.Value.Fraction + sign * b.Value.Fraction,
                        a.Value.Px + sign * b.Value.Px);
                }

                return ParseLength(inner);
            }

            if (raw.EndsWith("%"))
            {
                double pct;
                if (!double.TryParse(raw.Substring(0, raw.Length - 1),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out pct)) return null;
                return new RevnixLength(pct / 100.0, 0.0);
            }

            if (raw.ToLowerInvariant().EndsWith("px"))
            {
                double px;
                if (!double.TryParse(raw.Substring(0, raw.Length - 2),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out px)) return null;
                return new RevnixLength(0.0, px);
            }

            // A bare number is px, which is how the designs write `0`.
            double n;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out n))
                return null;
            return new RevnixLength(0.0, n);
        }

        /// <summary>
        /// Splits on <paramref name="separator"/> at PAREN DEPTH ZERO, so the
        /// spaces and commas inside <c>calc(100% - 16px)</c> stay part of their
        /// token. Splitting naively is exactly how a calc-bearing polygon turns
        /// into garbage points.
        /// </summary>
        public static List<string> SplitTopLevel(string value, char separator)
        {
            var outList = new List<string>();
            var current = new StringBuilder();
            var depth = 0;
            foreach (var ch in value)
            {
                if (ch == '(')
                {
                    depth++;
                    current.Append(ch);
                }
                else if (ch == ')')
                {
                    if (depth > 0) depth--;
                    current.Append(ch);
                }
                else if (ch == separator && depth == 0)
                {
                    outList.Add(current.ToString());
                    current.Length = 0;
                }
                else
                {
                    current.Append(ch);
                }
            }

            outList.Add(current.ToString());
            var trimmed = new List<string>();
            foreach (var part in outList)
            {
                var t = part.Trim();
                if (t.Length > 0) trimmed.Add(t);
            }

            return trimmed;
        }

        /// <summary>
        /// <c>"-50% 0"</c> / <c>"0 -8px"</c> / <c>"12px"</c>. A single component
        /// sets x and leaves y at zero, as CSS does.
        /// </summary>
        public static RevnixTranslate? ParseTranslate(string css)
        {
            if (string.IsNullOrEmpty(css) || css.Trim().Length == 0) return null;
            var parts = SplitTopLevel(css, ' ');
            if (parts.Count == 0 || parts.Count > 3) return null;
            var x = ParseLength(parts[0]);
            if (x == null) return null;
            // A third component is the z axis, meaningless in this 2D renderer;
            // the x/y prefix is still honoured rather than dropped.
            var y = parts.Count > 1 ? ParseLength(parts[1]) : new RevnixLength(0.0, 0.0);
            if (y == null) return null;
            return new RevnixTranslate(x.Value, y.Value);
        }

        /// <summary>
        /// <c>polygon(50% 0,100% 100%,0 100%)</c> → its vertices.
        ///
        /// Only the <c>polygon()</c> form is read; it is the only one the 250
        /// shipped presets use. An optional leading fill rule is accepted and
        /// ignored. Parsed even though this platform cannot clip to it, so the
        /// renderer can REPORT a clip it is dropping instead of dropping it in
        /// silence — a diagnostic is what turns a bug into a known limitation.
        /// </summary>
        public static List<RevnixPolygonPoint> ParsePolygon(string css)
        {
            if (css == null) return null;
            var trimmed = css.Trim();
            if (!trimmed.ToLowerInvariant().StartsWith("polygon(") || !trimmed.EndsWith(")"))
                return null;
            var inner = trimmed.Substring("polygon(".Length,
                trimmed.Length - "polygon(".Length - 1);

            var groups = SplitTopLevel(inner, ',');
            if (groups.Count > 0)
            {
                var rule = groups[0].ToLowerInvariant();
                if (rule == "nonzero" || rule == "evenodd") groups.RemoveAt(0);
            }

            // Two points describe a line, which would clip a block away to
            // nothing. Decline rather than erase it.
            if (groups.Count < 3) return null;

            var points = new List<RevnixPolygonPoint>(groups.Count);
            foreach (var group in groups)
            {
                var axes = SplitTopLevel(group, ' ');
                if (axes.Count != 2) return null;
                var x = ParseLength(axes[0]);
                var y = ParseLength(axes[1]);
                if (x == null || y == null) return null;
                points.Add(new RevnixPolygonPoint(x.Value, y.Value));
            }

            return points;
        }
    }
}
