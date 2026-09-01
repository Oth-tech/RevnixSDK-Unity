// The screen background's paint layers, as UGUI objects.
//
// Runtime/Core parses a CSS background into framework-free descriptors (see
// RevnixPaywallBackground.cs); this file is the half that needs UnityEngine.
// Keeping the split means the parsing — where the bugs live — is exercisable
// without an editor.
//
// UGUI has no gradient component and `Image` takes one flat colour, so a
// gradient is RASTERISED into a Texture2D once and shown in a RawImage that
// stretches it to the screen. That is also what lets a radial gradient be the
// ellipse CSS asks for rather than the circle a platform gradient API would
// force: the square texture stretches to the box along with everything else.

using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace Revnix.Unity.UI
{
    /// <summary>Bakes parsed CSS gradients into textures.</summary>
    public static class RevnixGradientTexture
    {
        /// <summary>
        /// The baked size. Gradients are smooth by nature and the texture is
        /// bilinear-filtered on the way up to screen size, so this is about
        /// banding rather than detail — 256 is past the point where a stretched
        /// gradient shows steps, and costs 256 KB.
        /// </summary>
        private const int Size = 256;

        /// <summary>
        /// One gradient as a texture, or null when it has no usable stops.
        /// Colour strings are resolved through <paramref name="resolve"/> so the
        /// palette tokens inside a scrim (`@bg/40`) resolve against the same
        /// document as the rest of the screen.
        /// </summary>
        public static Texture2D Bake(RevnixGradient gradient, System.Func<string, Color?> resolve)
        {
            if (gradient == null || gradient.Stops.Count < 2) return null;

            var colors = new Color[gradient.Stops.Count];
            var positions = new float[gradient.Stops.Count];
            for (var i = 0; i < gradient.Stops.Count; i++)
            {
                colors[i] = resolve(gradient.Stops[i].Color) ?? new Color(0f, 0f, 0f, 0f);
                positions[i] = (float)gradient.Stops[i].Position;
            }

            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };

            var pixels = new Color[Size * Size];
            for (var y = 0; y < Size; y++)
            {
                // Texture v runs bottom-to-top; the descriptor is in screen
                // coordinates, where y runs top-to-bottom.
                var v = 1f - (y / (float)(Size - 1));
                for (var x = 0; x < Size; x++)
                {
                    var u = x / (float)(Size - 1);
                    pixels[(y * Size) + x] = Sample(gradient, u, v, colors, positions);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static Color Sample(
            RevnixGradient gradient,
            float u,
            float v,
            Color[] colors,
            float[] positions)
        {
            float t;
            if (gradient.Kind == RevnixGradientKind.Linear)
            {
                // The gradient line runs from centre - dir/2 to centre + dir/2,
                // so the position along it is the projection of the pixel onto
                // that vector.
                var dx = (float)gradient.DirX;
                var dy = (float)gradient.DirY;
                var lengthSq = (dx * dx) + (dy * dy);
                if (lengthSq < 0.000001f) return colors[0];
                t = 0.5f + ((((u - 0.5f) * dx) + ((v - 0.5f) * dy)) / lengthSq);
            }
            else
            {
                var rx = (float)gradient.RadiusX;
                var ry = (float)gradient.RadiusY;
                if (rx < 0.000001f || ry < 0.000001f) return colors[0];
                var dx = (u - (float)gradient.CenterX) / rx;
                var dy = (v - (float)gradient.CenterY) / ry;
                t = Mathf.Sqrt((dx * dx) + (dy * dy));
            }
            return Interpolate(Mathf.Clamp01(t), colors, positions);
        }

        private static Color Interpolate(float t, Color[] colors, float[] positions)
        {
            if (t <= positions[0]) return colors[0];
            var last = positions.Length - 1;
            if (t >= positions[last]) return colors[last];
            for (var i = 1; i <= last; i++)
            {
                if (t > positions[i]) continue;
                var span = positions[i] - positions[i - 1];
                var k = span <= 0f ? 0f : (t - positions[i - 1]) / span;
                return Color.Lerp(colors[i - 1], colors[i], k);
            }
            return colors[last];
        }
    }

    /// <summary>
    /// Destroys a texture this package created when its GameObject goes away.
    /// <para>
    /// Unity textures are unmanaged: a baked gradient is 256 KB and a
    /// downloaded photo far more, and neither is collected with the
    /// GameObject that referenced it. The legacy paywall destroys its hero
    /// texture for the same reason.
    /// </para>
    /// </summary>
    public sealed class RevnixOwnedTexture : MonoBehaviour
    {
        private Texture _texture;

        public void Own(Texture texture) => _texture = texture;

        private void OnDestroy()
        {
            if (_texture == null) return;
            if (Application.isPlaying) Destroy(_texture);
            else DestroyImmediate(_texture);
            _texture = null;
        }
    }

    /// <summary>
    /// Loads the background photo.
    /// <para>
    /// Image loading is otherwise the host project's job in this package — it
    /// ships no downloader for block art — but a background the designer chose
    /// is not optional decoration, so the same UnityWebRequestTexture fetch the
    /// legacy paywall uses for its hero is reused here. A failed load leaves the
    /// ground and scrim in place rather than blacking out the screen.
    /// </para>
    /// </summary>
    public sealed class RevnixBackgroundPhoto : MonoBehaviour
    {
        private RawImage _target;
        private RevnixBackgroundImage _spec;
        private UnityWebRequest _request;
        private Texture2D _texture;

        /// <summary>Called before the object is enabled, so Start does the work.</summary>
        public void Configure(RawImage target, RevnixBackgroundImage spec)
        {
            _target = target;
            _spec = spec;
        }

        private IEnumerator Start()
        {
            if (_target == null || _spec == null || string.IsNullOrEmpty(_spec.Url)) yield break;
            var request = UnityWebRequestTexture.GetTexture(_spec.Url);
            _request = request;
            yield return request.SendWebRequest();
            _request = null;
            if (request.result == UnityWebRequest.Result.Success)
            {
                _texture = DownloadHandlerTexture.GetContent(request);
                _target.texture = _texture;
                _target.enabled = true;
                Place();
            }
            request.Dispose();
        }

        private void OnRectTransformDimensionsChange() => Place();

        private void OnDestroy()
        {
            if (_request != null)
            {
                _request.Abort();
                _request.Dispose();
                _request = null;
            }
            if (_texture != null)
            {
                if (_target != null) _target.texture = null;
                if (Application.isPlaying) Destroy(_texture);
                else DestroyImmediate(_texture);
                _texture = null;
            }
        }

        /// <summary>
        /// Cover-fit geometry with a focal point, expressed as a UV rect — the
        /// same rule CSS applies for `object-position: X% Y%`: the X% point of
        /// the image aligns to the X% point of the box, clamped so no edge
        /// shows. A RawImage crops through uvRect rather than by scaling, so
        /// this is where the crop lives.
        /// </summary>
        private void Place()
        {
            if (_target == null || _texture == null || _spec == null) return;
            var rt = _target.rectTransform;
            var boxW = rt.rect.width;
            var boxH = rt.rect.height;
            var srcW = (float)_texture.width;
            var srcH = (float)_texture.height;
            if (boxW <= 0f || boxH <= 0f || srcW <= 0f || srcH <= 0f) return;

            if (_spec.Fit == RevnixBackgroundFit.Contain)
            {
                // Contain shows the whole photo: no crop, the RawImage's own
                // stretch is close enough and never hides the subject.
                _target.uvRect = new Rect(0f, 0f, 1f, 1f);
                return;
            }

            var boxAspect = boxW / boxH;
            var srcAspect = srcW / srcH;
            var width = 1f;
            var height = 1f;
            if (srcAspect > boxAspect)
            {
                // The photo is wider than the box: crop its sides.
                width = boxAspect / srcAspect;
            }
            else
            {
                height = srcAspect / boxAspect;
            }

            // uv y runs bottom-to-top; the focal point is in screen coordinates.
            var x = (1f - width) * Mathf.Clamp01((float)_spec.FocalX / 100f);
            var y = (1f - height) * (1f - Mathf.Clamp01((float)_spec.FocalY / 100f));
            _target.uvRect = new Rect(x, y, width, height);
        }
    }
}
