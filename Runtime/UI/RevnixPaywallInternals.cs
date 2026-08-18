using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Revnix.Unity.UI
{
    /// <summary>
    /// Procedural sprites for the runtime-generated paywall — no bundled
    /// assets, no prefabs. Rounded rects are 9-sliced so one texture serves
    /// every card size; textures are generated once and cached for the life
    /// of the process (HideAndDontSave — they survive scene loads).
    /// </summary>
    internal static class RevnixPaywallSprites
    {
        private static readonly Dictionary<int, Sprite> Cache = new Dictionary<int, Sprite>();
        private static Sprite _circle;
        private static Sprite _arc;

        /// <summary>Filled rounded rect, 9-sliced at the corner radius.</summary>
        public static Sprite Rounded(int radius) => Get(radius, 0);

        /// <summary>Border-only rounded rect (package rows, plan columns,
        /// spotlight). The transparent interior still receives raycasts —
        /// Image raycasting is rect-based unless an alpha threshold is set.</summary>
        public static Sprite RoundedOutline(int radius, int thickness) => Get(radius, thickness);

        /// <summary>64px filled circle; scale via the Image rect (dots, chips,
        /// timeline dots).</summary>
        public static Sprite Circle()
        {
            if (_circle == null) _circle = Build(30, 0, circle: true);
            return _circle;
        }

        /// <summary>270° ring segment — the CTA spinner arc (rotated by
        /// RevnixPaywallSpinner, standing in for RN's ActivityIndicator).</summary>
        public static Sprite Arc()
        {
            if (_arc == null) _arc = BuildArc();
            return _arc;
        }

        private static Sprite Get(int radius, int thickness)
        {
            var key = radius * 100 + thickness;
            Sprite sprite;
            if (Cache.TryGetValue(key, out sprite) && sprite != null) return sprite;
            sprite = Build(radius, thickness, circle: false);
            Cache[key] = sprite;
            return sprite;
        }

        private static Sprite Build(int radius, int thickness, bool circle)
        {
            const int pad = 2;
            var body = circle ? 0 : 4;
            var size = radius * 2 + pad * 2 + body;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
            };
            var half = size * 0.5f;
            var straight = half - pad - radius; // half-extent of the flat edge
            var pixels = new Color[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    // Signed distance to the rounded-rect boundary; ±0.5px
                    // smoothing anti-aliases the edge.
                    var px = x + 0.5f - half;
                    var py = y + 0.5f - half;
                    var qx = Mathf.Abs(px) - straight;
                    var qy = Mathf.Abs(py) - straight;
                    var ox = Mathf.Max(qx, 0f);
                    var oy = Mathf.Max(qy, 0f);
                    var d = Mathf.Sqrt(ox * ox + oy * oy)
                        + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                    var alpha = thickness <= 0
                        ? Mathf.Clamp01(0.5f - d)
                        : Mathf.Clamp01(0.5f - d) * Mathf.Clamp01(d + thickness + 0.5f);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply(false, true);
            var border = circle
                ? Vector4.zero
                : new Vector4(radius + pad, radius + pad, radius + pad, radius + pad);
            var sprite = Sprite.Create(
                tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f),
                100f, 0, SpriteMeshType.FullRect, border);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        private static Sprite BuildArc()
        {
            const int size = 32;
            const float mid = 12.5f;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
            };
            var half = size * 0.5f;
            var pixels = new Color[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var px = x + 0.5f - half;
                    var py = y + 0.5f - half;
                    var r = Mathf.Sqrt(px * px + py * py);
                    var ring = Mathf.Clamp01(2f - Mathf.Abs(r - mid));
                    var angle = Mathf.Atan2(py, px) * Mathf.Rad2Deg;
                    if (angle < 0f) angle += 360f;
                    var alpha = angle <= 270f ? ring : 0f;
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply(false, true);
            var sprite = Sprite.Create(
                tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
    }

    /// <summary>Rotates the CTA spinner arc (RN's ActivityIndicator).</summary>
    internal sealed class RevnixPaywallSpinner : MonoBehaviour
    {
        private void Update()
        {
            transform.Rotate(0f, 0f, -360f * Time.unscaledDeltaTime);
        }
    }

    /// <summary>Mirrors the TSX scrollContent flexGrow+center rule: content
    /// sits vertically centered on tall screens (no dead bottom half) and
    /// scrolls normally when it overflows.</summary>
    internal sealed class RevnixPaywallCenterOnShort : MonoBehaviour
    {
        public ScrollRect Scroll;

        private void LateUpdate()
        {
            if (Scroll == null || Scroll.content == null || Scroll.viewport == null) return;
            var viewportH = Scroll.viewport.rect.height;
            var contentH = Scroll.content.rect.height;
            if (contentH <= viewportH)
            {
                Scroll.vertical = false;
                Scroll.content.anchoredPosition = new Vector2(
                    Scroll.content.anchoredPosition.x, -(viewportH - contentH) * 0.5f);
            }
            else if (!Scroll.vertical)
            {
                Scroll.vertical = true;
                Scroll.content.anchoredPosition = new Vector2(
                    Scroll.content.anchoredPosition.x, 0f);
            }
        }
    }

    /// <summary>Mirrors the TSX content maxWidth rule: on tablets/wide screens
    /// the column stays a readable width (440), horizontally centered, instead
    /// of stretching edge to edge.</summary>
    internal sealed class RevnixPaywallColumnWidth : MonoBehaviour
    {
        public LayoutElement Element;
        public RectTransform Reference;
        public float MaxWidth = 440f;
        public float HorizontalPadding = 48f;

        private void LateUpdate()
        {
            if (Element == null || Reference == null) return;
            var available = Reference.rect.width - HorizontalPadding;
            var target = Mathf.Min(MaxWidth, Mathf.Max(0f, available));
            if (!Mathf.Approximately(Element.preferredWidth, target))
            {
                Element.preferredWidth = target;
            }
        }
    }

    /// <summary>Sizes a badge/offer pill to its label, clamped to a fraction
    /// of its card's width. Absolute views take no width constraint from the
    /// card — a long badge string used to grow past the card edge (same clamp
    /// as the TSX renderer; the single-line label truncates via the pill's
    /// RectMask2D). When `Element` is set the pill is layout-driven (plan
    /// badge, offer pill) and the clamp writes preferred sizes instead of the
    /// rect.</summary>
    internal sealed class RevnixPaywallPillSize : MonoBehaviour
    {
        public Text Label;
        public RectTransform Reference;
        public LayoutElement Element;
        public float MaxFraction = 0.8f;
        public float PadX = 10f;
        public float PadY = 3f;

        private void LateUpdate()
        {
            if (Label == null || Reference == null) return;
            var width = Mathf.Min(
                Label.preferredWidth + PadX * 2f, Reference.rect.width * MaxFraction);
            var height = Label.preferredHeight + PadY * 2f;
            if (Element != null)
            {
                Element.preferredWidth = width;
                Element.preferredHeight = height;
            }
            else
            {
                var rt = (RectTransform)transform;
                rt.sizeDelta = new Vector2(width, height);
            }
        }
    }

    /// <summary>RN `resizeMode: "cover"` for a remote texture: crops via
    /// uvRect so the image fills its container without distortion.</summary>
    internal sealed class RevnixPaywallCoverImage : MonoBehaviour
    {
        public RawImage Target;

        private void LateUpdate()
        {
            if (Target == null || Target.texture == null) return;
            var rt = (RectTransform)Target.transform;
            float rectW = rt.rect.width, rectH = rt.rect.height;
            float texW = Target.texture.width, texH = Target.texture.height;
            if (rectW <= 0f || rectH <= 0f || texW <= 0f || texH <= 0f) return;
            var scale = Mathf.Max(rectW / texW, rectH / texH);
            var u = rectW / (texW * scale);
            var v = rectH / (texH * scale);
            Target.uvRect = new Rect((1f - u) * 0.5f, (1f - v) * 0.5f, u, v);
        }
    }
}
