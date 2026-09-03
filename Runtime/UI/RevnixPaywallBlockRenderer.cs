// RevnixPaywallBlockRenderer — draws a designed paywall.
//
// A paywall built in the dashboard's block builder publishes a TREE of styled
// elements on `PaywallConfig.Blocks`. RevnixPaywallView draws that tree through
// this renderer when it is present, and falls back to the classic `template`
// layouts when it is not — so every paywall published before the block builder
// keeps rendering exactly as it did.
//
// revnix-app's src/components/paywall-blocks/BlockScreen.tsx is the reference
// renderer; keep the two in lockstep. The model, its parser, the tag resolver
// and the colour maths live in Runtime/Core/RevnixPaywallBlocks.cs, which
// carries no UnityEngine dependency and is unit-tested off-device.
//
// Built from runtime-generated UGUI, like the rest of this package: no
// prefabs, no bundled assets, no TextMeshPro. The container layouts map onto
// UGUI's own primitives — column → VerticalLayoutGroup, row →
// HorizontalLayoutGroup, stack → a bare RectTransform parent whose children
// anchor themselves, grid → GridLayoutGroup.
//
// Two rules matter more here than in the dashboard, because a shipped app
// cannot be patched from our side:
//
//   * An unknown block type, layout or style field is SKIPPED and the rest of
//     the screen still draws. Never crash, never blank.
//   * A tag with no data behind it stays visible (`{price}` draws as
//     `{price}`) rather than resolving to something wrong.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Revnix.Unity.UI
{
    /// <summary>Everything the tree needs that is not in the document itself.</summary>
    public sealed class BlockRenderContext
    {
        public PaywallBlockDoc Doc;
        public List<BlockPackage> Packages = new List<BlockPackage>();

        /// <summary>The selected package (render contract v2): what pinned
        /// and repeated cards compare themselves against, what the CTA buys,
        /// and what tags outside any package card resolve with. Falls back to
        /// the first offered package when null or not offered.</summary>
        public string SelectedPackageId;

        /// <summary>Host `loading`: purchase buttons go inert and show a
        /// spinner in place of their label. Close buttons are unaffected.</summary>
        public bool Loading;

        /// <summary>Fills an image block that publishes no URL of its own.</summary>
        public string HeroImageUrl;

        /// <summary>Paywall-level Terms/Privacy, used when a links block sets none.</summary>
        public string FooterTermsUrl;
        public string FooterPrivacyUrl;

        public Action<string> OnPurchase;

        /// <summary>Reports a plan card tap. Selection is the paywall's own
        /// state, so a design's plan cards work without the host wiring
        /// anything.</summary>
        public Action<string> OnSelect;

        public Action OnRestore;
        public Action OnTerms;
        public Action OnPrivacy;

        /// <summary>Dismissal (REV-252). Null means the host wired none, and
        /// no close is drawn at all — a dead close button is worse than
        /// none.</summary>
        public Action OnClose;

        /// <summary>Where the renderer reports a paint string it could not
        /// read. Local only — it never leaves the device. The screen still
        /// draws (a fill falls back to a colour from the design), so this is
        /// the only way a host learns that a paywall is rendering
        /// approximately.</summary>
        public Action<string> OnDiagnostic;

        public Font Font;
    }

    /// <summary>
    /// Draws a block document into a UGUI hierarchy.
    ///
    /// Every method answers with a GameObject or null; nothing throws. A block
    /// it cannot draw contributes nothing and its siblings are unaffected.
    /// </summary>
    public sealed class RevnixPaywallBlockRenderer
    {
        private readonly BlockRenderContext _ctx;
        private readonly PaywallBlockDoc _doc;
        /// <summary>Paint strings already reported, so one is not sent once per
        /// block that uses it. See <see cref="Diagnostic"/>.</summary>
        private readonly HashSet<string> _reported = new HashSet<string>();

        /// <summary>The selected package id, validated against the offering
        /// (see <see cref="RevnixPaywallSelection.ResolveSelectedPackageId"/>).
        /// Null only when nothing is offered.</summary>
        private readonly string _selected;

        public RevnixPaywallBlockRenderer(BlockRenderContext ctx)
        {
            _ctx = ctx;
            _doc = ctx.Doc;
            _selected = RevnixPaywallSelection.ResolveSelectedPackageId(
                ctx.Packages, ctx.SelectedPackageId, null, null);
        }

        /// <summary>
        /// Builds the whole screen under <paramref name="parent"/>.
        ///
        /// A `canvas` document is authored against a fixed 393×852 device
        /// screen; it is laid out at that size and scaled as a whole, so
        /// absolute placement inside `stack` containers stays true at any
        /// width. A `flow` document lays out as an ordinary column.
        /// </summary>
        public GameObject Build(Transform parent)
        {
            var screen = NewUI("RevnixBlockScreen", parent);
            var screenRt = (RectTransform)screen.transform;
            Fill(screenRt);
            var layers = RevnixBackground.Resolve(_doc.BackgroundSpec);
            var background = screen.AddComponent<Image>();
            // The flat colour under everything. A gradient resolves to its first
            // stop here, so a form the parser does not understand still shows a
            // colour from the design rather than black.
            background.color = Color(
                RevnixBackground.BaseColor(layers.Ground ?? _doc.Background),
                UnityEngine.Color.black);
            background.raycastTarget = false;
            BuildBackgroundArt(layers, screen.transform);

            // The scroll scaffold (render contract v2, section 3). Both
            // layouts sit in a vertical ScrollRect with no scrollbars: a
            // `canvas` design scrolls only when its scaled height is taller
            // than the viewport, a `flow` design when its column is; either is
            // pinned to the top and does not bounce when it fits
            // (RevnixScrollWhenOverflowing). The background stays on the
            // screen, outside the scroll, filling the whole view.
            var scrollGo = NewUI("Scroll", screen.transform);
            Fill((RectTransform)scrollGo.transform);
            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            var viewport = NewUI("Viewport", scrollGo.transform);
            var viewportRt = (RectTransform)viewport.transform;
            Fill(viewportRt);
            viewport.AddComponent<RectMask2D>();
            // A drag has to start over a raycast target, and the blocks' own
            // text is not one, so the viewport carries an invisible surface
            // that lets a swipe anywhere scroll.
            var surface = viewport.AddComponent<Image>();
            surface.color = UnityEngine.Color.clear;
            surface.raycastTarget = true;

            var content = NewUI("Content", viewport.transform);
            var contentRt = (RectTransform)content.transform;
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.sizeDelta = Vector2.zero;
            contentRt.anchoredPosition = Vector2.zero;

            scroll.viewport = viewportRt;
            scroll.content = contentRt;
            var overflow = scrollGo.AddComponent<RevnixScrollWhenOverflowing>();
            overflow.Scroll = scroll;

            GameObject body;
            if (IsCanvas)
            {
                // Laid out at the design width and scaled as a whole, centred
                // under the viewport's top edge. The scale is applied against
                // the live viewport, so it stays correct on any device rather
                // than being baked at build time (RevnixCanvasScaler).
                body = NewUI("Blocks", content.transform);
                var bodyRt = (RectTransform)body.transform;
                bodyRt.anchorMin = new Vector2(0.5f, 1f);
                bodyRt.anchorMax = new Vector2(0.5f, 1f);
                bodyRt.pivot = new Vector2(0.5f, 1f);
                bodyRt.anchoredPosition = Vector2.zero;
                bodyRt.sizeDelta = new Vector2(PaywallBlockDoc.CanvasWidth, PaywallBlockDoc.CanvasHeight);
                var fitter = viewport.AddComponent<RevnixCanvasScaler>();
                fitter.Target = bodyRt;
                fitter.Content = contentRt;
            }
            else
            {
                body = content;
                var column = body.AddComponent<VerticalLayoutGroup>();
                column.childControlWidth = true;
                column.childControlHeight = true;
                column.childForceExpandWidth = true;
                column.childForceExpandHeight = false;
                column.padding = new RectOffset(20, 20, 20, 20);
                var sizer = body.AddComponent<ContentSizeFitter>();
                sizer.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }

            foreach (var block in _doc.Blocks)
            {
                BlockStyle style;
                var rendered = Render(block, body.transform, null, out style);
                // The canvas root IS the device screen: a root block pins
                // itself to it like a stack child, and one that names no
                // placement fills it — which is what lets a taller viewport
                // fill, with root cards following the body's height.
                if (rendered != null && IsCanvas && !PlaceInStack(rendered, style))
                {
                    Fill((RectTransform)rendered.transform);
                }
            }
            // Parented to the SCREEN, not the scaled body, so the fallback
            // close keeps its tap size and its distance from the screen edge
            // whatever the device width does to the design (REV-252).
            BuildFallbackClose(screen.transform);
            return screen;
        }

        /// <summary>
        /// The dismiss affordance the renderer supplies itself (REV-252).
        ///
        /// Drawn only when the design authors no close of its own AND the host
        /// wired an OnClose — which is what makes every paywall published
        /// before close existed dismissible without being re-authored, while a
        /// design that DOES carry a close chip never ends up showing two.
        ///
        /// Deliberately plain: it is a safety net, not a design element.
        /// Tinted from the screen's own ink rather than a fixed white, so it
        /// stays legible on a light design as well as a dark one.
        /// </summary>
        private void BuildFallbackClose(Transform parent)
        {
            var onClose = _ctx.OnClose;
            if (onClose == null || RevnixPaywallClose.HasCloseAction(_doc.Blocks)) return;

            var ink = Color(_doc.TextColor, UnityEngine.Color.white);
            var go = NewUI("RevnixFallbackClose", parent);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(30f, 30f);
            rt.anchoredPosition = new Vector2(-14f, -14f);
            // The canvas is full-bleed under the status bar; the chip moves
            // down by the safe-area inset, in unscaled canvas units.
            var inset = go.AddComponent<RevnixSafeAreaInset>();
            inset.Margin = 14f;

            var image = go.AddComponent<Image>();
            var sprite = RevnixPaywallSprites.Rounded(15);
            if (sprite != null)
            {
                image.sprite = sprite;
                image.type = Image.Type.Sliced;
            }
            image.color = new UnityEngine.Color(ink.r, ink.g, ink.b, 0.14f);
            image.raycastTarget = true;

            var button = go.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = image;
            button.onClick.AddListener(() => onClose());

            var label = NewUI("Glyph", go.transform);
            Fill((RectTransform)label.transform);
            var text = label.AddComponent<Text>();
            if (_ctx.Font != null) text.font = _ctx.Font;
            text.text = "\u00d7";
            text.fontSize = 17;
            text.color = ink;
            text.alignment = TextAnchor.MiddleCenter;
            text.raycastTarget = false;
        }

        private bool IsCanvas => _doc.Layout == "canvas";

        // ── blocks ────────────────────────────────────────────────────────

        /// <summary>
        /// One block, or null when it contributes nothing — a kind this SDK
        /// does not know, a pinned card the offering does not reach, or a
        /// block whose `visibility` hides it in this context.
        ///
        /// <paramref name="pkg"/> is the package the enclosing card describes
        /// (null at the root). The selected-context rules — which package a
        /// pinned card compares against, whether `selectedStyle` merges,
        /// whether the block draws at all — live in
        /// <see cref="RevnixPaywallSelection"/>, so the style the block
        /// actually drew with comes back through <paramref name="style"/>: a
        /// selected merge can change its flex or its placement, and the
        /// container that placed it must see the same style it was drawn with.
        /// </summary>
        private GameObject Render(PaywallBlock block, Transform parent, BlockPackage pkg, out BlockStyle style)
        {
            style = null;
            var resolved = RevnixPaywallSelection.Resolve(block, pkg, _ctx.Packages, _selected);
            if (resolved == null || !resolved.Visible) return null;
            style = resolved.Block.Style;
            return Draw(resolved, parent);
        }

        private GameObject Render(PaywallBlock block, Transform parent, BlockPackage pkg)
        {
            BlockStyle ignored;
            return Render(block, parent, pkg, out ignored);
        }

        private GameObject Draw(BlockResolution resolved, Transform parent)
        {
            var block = resolved.Block;
            switch (block.Kind)
            {
                case PaywallBlockKind.Text:
                    return CloseOnTap(RenderText(block, parent, resolved.TagPackage), block.Action);
                case PaywallBlockKind.Image:
                    return CloseOnTap(RenderImage(block, parent), block.Action);
                case PaywallBlockKind.List:
                    return RenderList(block, parent);
                case PaywallBlockKind.Products:
                    return RenderProducts(block, parent);
                case PaywallBlockKind.Button:
                    return RenderButton(block, parent, resolved.TagPackage);
                case PaywallBlockKind.Links:
                    return RenderLinks(block, parent);
                case PaywallBlockKind.Line:
                    return RenderLine(block, parent);
                case PaywallBlockKind.Spacer:
                    return RenderSpacer(block, parent);
                case PaywallBlockKind.Card:
                    return RenderCard(block, parent, resolved.Package);
                default:
                    // A block type from a newer dashboard: skip it, keep the
                    // screen.
                    return null;
            }
        }

        /// <summary><paramref name="pkg"/> is the package the copy's tags
        /// resolve against — the enclosing card's, or the selected package at
        /// the root (contract v2, section 2).</summary>
        private GameObject RenderText(PaywallBlock block, Transform parent, BlockPackage pkg)
        {
            var go = NewUI("Text", parent);
            var text = go.AddComponent<Text>();
            ApplyText(text, block.Style, RevnixPaywallTags.Resolve(block.Text ?? "", pkg, _ctx.Packages));
            ApplyBox(go, block.Style, paint: false);
            return go;
        }

        /// <summary>
        /// An image slot. The photo loads through the same fetch and cache the
        /// screen background uses (RevnixBackgroundPhoto), so a redraw on
        /// every selection tap does not refetch it; the placeholder tint and
        /// caption stay until it lands and remain if the fetch fails. A block
        /// with no URL of its own takes the config's hero image, then the
        /// placeholder alone.
        /// </summary>
        private GameObject RenderImage(PaywallBlock block, Transform parent)
        {
            var go = NewUI("Image", parent);
            var image = go.AddComponent<Image>();
            image.color = new Color32(125, 135, 155, 56);
            image.raycastTarget = false;

            var style = block.Style;
            // The 160px default suits a slot dropped into a flow column; a
            // converted design sizes its own slot, and the default must not
            // fight it.
            var sized = style != null
                && (style.Inset == true || style.Height.HasValue
                    || style.AspectRatio.HasValue || style.Flex.HasValue
                    || (style.Top.HasValue && style.Bottom.HasValue));
            if (!sized)
            {
                Le(go).preferredHeight = 160f;
            }
            else if (style.Height == null && style.Inset != true && !style.Flex.HasValue
                && !(style.Top.HasValue && style.Bottom.HasValue)
                && style.AspectRatio.HasValue && style.AspectRatio.Value.Ratio is double ratio && ratio > 0)
            {
                // Only an aspect ratio: the height follows the width the
                // layout gives the slot.
                var aspect = go.AddComponent<RevnixPaywallAspectHeight>();
                aspect.Element = Le(go);
                aspect.Ratio = (float)ratio;
            }

            // Shape: a circle, or the style's corner radius (16 by default on
            // an unsized slot, like the dashboard), as a stencil mask on the
            // slot's own graphic. The tint stays visible through the mask
            // until the photo lands, and the mask then clips the photo alone.
            var round = block.Shape == "circle";
            var radius = (int)Math.Round(style?.Radius ?? (sized ? 0 : 16));
            Mask mask = null;
            if (round || radius > 0)
            {
                var shape = round
                    ? RevnixPaywallSprites.Circle()
                    : RevnixPaywallSprites.Rounded(Math.Min(radius, 64));
                if (shape != null)
                {
                    image.sprite = shape;
                    image.type = round ? Image.Type.Simple : Image.Type.Sliced;
                    mask = go.AddComponent<Mask>();
                    mask.showMaskGraphic = true;
                }
            }

            GameObject caption = null;
            var label = !string.IsNullOrEmpty(block.Placeholder) ? block.Placeholder : block.Url;
            if (!string.IsNullOrEmpty(label))
            {
                caption = NewUI("Placeholder", go.transform);
                Fill((RectTransform)caption.transform);
                var text = caption.AddComponent<Text>();
                if (_ctx.Font != null) text.font = _ctx.Font;
                text.text = label;
                text.fontSize = 11;
                text.color = new Color(1f, 1f, 1f, 0.62f);
                text.alignment = TextAnchor.MiddleCenter;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Truncate;
                text.raycastTarget = false;
            }

            var url = !string.IsNullOrEmpty(block.Url) ? block.Url : _ctx.HeroImageUrl;
            if (!string.IsNullOrEmpty(url))
            {
                var photo = NewUI("Photo", go.transform);
                Fill((RectTransform)photo.transform);
                var raw = photo.AddComponent<RawImage>();
                raw.raycastTarget = false;
                // Nothing to show until the fetch lands; enabling an empty
                // RawImage paints a white box over the placeholder.
                raw.enabled = false;
                var loader = photo.AddComponent<RevnixBackgroundPhoto>();
                loader.Configure(raw, new RevnixBackgroundImage
                {
                    Url = url,
                    Fit = block.Fit == "contain" ? RevnixBackgroundFit.Contain : RevnixBackgroundFit.Cover,
                });
                var slot = image;
                var slotMask = mask;
                var slotCaption = caption;
                loader.OnLoaded = () =>
                {
                    if (slotCaption != null) slotCaption.SetActive(false);
                    // A masked slot keeps its graphic (the mask needs its
                    // alpha) but stops painting it; a plain slot just clears.
                    if (slotMask != null) slotMask.showMaskGraphic = false;
                    else if (slot != null) slot.color = UnityEngine.Color.clear;
                };
            }
            ApplyBox(go, style, paint: false);
            return go;
        }

        private GameObject RenderList(PaywallBlock block, Transform parent)
        {
            var go = NewUI("List", parent);
            var column = go.AddComponent<VerticalLayoutGroup>();
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;
            column.spacing = (float)(block.Style?.Gap ?? 8);

            var iconColor = Color(block.IconColor, Color(_doc.Accent, UnityEngine.Color.white));
            var items = block.Items ?? new List<BlockListItem>();
            foreach (var item in items)
            {
                var row = NewUI("Item", go.transform);
                var line = row.AddComponent<HorizontalLayoutGroup>();
                line.childControlWidth = true;
                line.childControlHeight = true;
                line.childForceExpandWidth = false;
                line.childForceExpandHeight = false;
                line.spacing = 9f;
                line.childAlignment = TextAnchor.UpperLeft;

                var icon = NewUI("Icon", row.transform);
                var iconText = icon.AddComponent<Text>();
                if (_ctx.Font != null) iconText.font = _ctx.Font;
                iconText.text = string.IsNullOrEmpty(item.Icon) ? "✓" : item.Icon;
                iconText.fontSize = 15;
                iconText.fontStyle = FontStyle.Bold;
                iconText.color = iconColor;
                iconText.raycastTarget = false;

                var body = NewUI("Body", row.transform);
                var bodyColumn = body.AddComponent<VerticalLayoutGroup>();
                bodyColumn.childControlWidth = true;
                bodyColumn.childControlHeight = true;
                bodyColumn.childForceExpandWidth = true;
                bodyColumn.childForceExpandHeight = false;
                Le(body).flexibleWidth = 1f;

                var title = NewUI("Title", body.transform);
                var titleText = title.AddComponent<Text>();
                if (_ctx.Font != null) titleText.font = _ctx.Font;
                titleText.text = item.Title ?? "";
                titleText.fontSize = 15;
                titleText.fontStyle = string.IsNullOrEmpty(item.Description) ? FontStyle.Normal : FontStyle.Bold;
                titleText.color = Color(_doc.TextColor, UnityEngine.Color.white);
                titleText.horizontalOverflow = HorizontalWrapMode.Wrap;
                titleText.raycastTarget = false;

                if (!string.IsNullOrEmpty(item.Description))
                {
                    var description = NewUI("Description", body.transform);
                    var descriptionText = description.AddComponent<Text>();
                    if (_ctx.Font != null) descriptionText.font = _ctx.Font;
                    descriptionText.text = item.Description;
                    descriptionText.fontSize = 12;
                    var faded = Color(_doc.TextColor, UnityEngine.Color.white);
                    faded.a *= 0.7f;
                    descriptionText.color = faded;
                    descriptionText.horizontalOverflow = HorizontalWrapMode.Wrap;
                    descriptionText.raycastTarget = false;
                }
            }
            ApplyBox(go, block.Style, paint: true);
            return go;
        }

        private GameObject RenderButton(PaywallBlock block, Transform parent, BlockPackage pkg)
        {
            var go = NewUI("Button", parent);
            var image = go.AddComponent<Image>();
            var radius = (int)Math.Round(block.Style?.Radius ?? 12);
            var sprite = RevnixPaywallSprites.Rounded(Math.Max(0, Math.Min(radius, 64)));
            if (sprite != null)
            {
                image.sprite = sprite;
                image.type = Image.Type.Sliced;
            }
            // A button the design marks as the close dismisses instead of
            // buying, and takes no accent fill: the CTA must stay the one
            // accented thing on the screen, or a "Not now" competes with
            // "Subscribe" for the eye.
            var closes = block.Action == BlockAction.Close && _ctx.OnClose != null;
            var buttonGradients = FillGradients(block.Style?.Fill);
            image.color = FillColor(
                block.Style?.Fill,
                closes
                    ? new UnityEngine.Color(0f, 0f, 0f, 0f)
                    : Color(_doc.Accent, UnityEngine.Color.blue),
                buttonGradients);

            var button = go.AddComponent<Button>();
            // Pressed feedback is the whole button at 80% opacity (contract
            // v2, section 4), applied through a CanvasGroup so the label and
            // any gradient layers dim with the fill — UGUI's own ColorTint
            // would tint the fill graphic alone.
            button.transition = Selectable.Transition.None;
            button.targetGraphic = image;
            go.AddComponent<RevnixPaywallPressOpacity>();
            if (closes)
            {
                var onClose = _ctx.OnClose;
                button.onClick.AddListener(() => onClose());
            }
            else
            {
                var selected = _selected;
                if (selected != null && _ctx.OnPurchase != null)
                {
                    button.onClick.AddListener(() => _ctx.OnPurchase(selected));
                }
            }

            var label = NewUI("Label", go.transform);
            Fill((RectTransform)label.transform);
            var text = label.AddComponent<Text>();
            if (_ctx.Font != null) text.font = _ctx.Font;
            text.text = RevnixPaywallTags.Resolve(block.Label ?? "", pkg, _ctx.Packages);
            text.fontSize = (int)Math.Round(block.Style?.FontSize ?? 15);
            text.fontStyle = FontStyle.Bold;
            var ink = FillColor(
                block.Style?.TextColor,
                closes
                    ? Color(_doc.TextColor, UnityEngine.Color.white)
                    : Color(_doc.AccentInk, UnityEngine.Color.white));
            text.color = ink;
            text.alignment = TextAnchor.MiddleCenter;
            text.raycastTarget = false;

            if (_ctx.Loading && !closes)
            {
                // Loading: the purchase button goes inert, its label gives
                // way to a spinner in the same ink, and it keeps its size and
                // fill. The classic CTA's arc is reused.
                button.interactable = false;
                label.SetActive(false);
                var spinner = NewUI("Spinner", go.transform);
                var spinnerRt = (RectTransform)spinner.transform;
                spinnerRt.anchorMin = new Vector2(0.5f, 0.5f);
                spinnerRt.anchorMax = new Vector2(0.5f, 0.5f);
                spinnerRt.pivot = new Vector2(0.5f, 0.5f);
                spinnerRt.sizeDelta = new Vector2(20f, 20f);
                spinnerRt.anchoredPosition = Vector2.zero;
                var arc = spinner.AddComponent<Image>();
                arc.sprite = RevnixPaywallSprites.Arc();
                arc.color = ink;
                arc.raycastTarget = false;
                spinner.AddComponent<RevnixPaywallSpinner>();
            }

            var height = block.Style?.Height?.Px;
            Le(go).preferredHeight = height.HasValue ? (float)height.Value : 52f;
            ApplyBox(go, block.Style, paint: false, skipHeight: true);
            // After the label, so the gradient sits behind it rather than over.
            PaintFillGradients(go, buttonGradients, block.Style?.Radius ?? 12);
            return go;
        }

        private GameObject RenderLinks(PaywallBlock block, Transform parent)
        {
            // An explicit host handler wins over the config URL — the app knows
            // best how to open its own legal pages; the URL is the fallback.
            var entries = new List<KeyValuePair<string, Action>>();
            if (block.ShowRestore != false) entries.Add(Pair("Restore", _ctx.OnRestore, null));
            if (block.ShowTerms != false)
            {
                entries.Add(Pair("Terms", _ctx.OnTerms, block.TermsUrl ?? _ctx.FooterTermsUrl));
            }
            if (block.ShowPrivacy != false)
            {
                entries.Add(Pair("Privacy", _ctx.OnPrivacy, block.PrivacyUrl ?? _ctx.FooterPrivacyUrl));
            }
            if (entries.Count == 0) return null;

            var go = NewUI("Links", parent);
            var row = go.AddComponent<HorizontalLayoutGroup>();
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;
            row.childAlignment = TextAnchor.MiddleCenter;
            row.spacing = 20f;

            foreach (var entry in entries)
            {
                var item = NewUI(entry.Key, go.transform);
                var text = item.AddComponent<Text>();
                if (_ctx.Font != null) text.font = _ctx.Font;
                text.text = entry.Key;
                text.fontSize = 12;
                var color = Color(_doc.TextColor, UnityEngine.Color.white);
                color.a *= 0.65f;
                text.color = color;
                text.alignment = TextAnchor.MiddleCenter;
                var button = item.AddComponent<Button>();
                button.targetGraphic = text;
                var action = entry.Value;
                if (action != null) button.onClick.AddListener(() => action());
            }
            ApplyBox(go, block.Style, paint: false);
            return go;
        }

        private KeyValuePair<string, Action> Pair(string label, Action handler, string url)
        {
            Action action = () =>
            {
                if (handler != null)
                {
                    handler();
                    return;
                }
                if (!string.IsNullOrEmpty(url)) Application.OpenURL(url);
            };
            return new KeyValuePair<string, Action>(label, action);
        }

        private GameObject RenderLine(PaywallBlock block, Transform parent)
        {
            var go = NewUI("Line", parent);
            var image = go.AddComponent<Image>();
            var lineGradients = FillGradients(block.Style?.Fill);
            var color = FillColor(
                block.Style?.Fill, Color(_doc.TextColor, UnityEngine.Color.white), lineGradients);
            if (block.Style?.Fill == null) color.a *= 0.16f;
            image.color = color;
            image.raycastTarget = false;
            var height = block.Style?.Height?.Px ?? 1;
            Le(go).preferredHeight = (float)height;
            PaintFillGradients(go, lineGradients, block.Style?.Radius);
            return go;
        }

        private GameObject RenderSpacer(PaywallBlock block, Transform parent)
        {
            var go = NewUI("Spacer", parent);
            var element = Le(go);
            if (block.Flex == true)
            {
                // Grows to push what follows to the bottom.
                element.flexibleHeight = 1f;
            }
            else
            {
                element.preferredHeight = (float)(block.Style?.Height?.Px ?? 16);
            }
            return go;
        }

        private GameObject RenderProducts(PaywallBlock block, Transform parent)
        {
            var shown = _ctx.Packages;
            var highlightId = _selected;
            if (highlightId == null || !ContainsPackage(shown, highlightId))
            {
                highlightId = shown.Count > 0 ? shown[0].PackageId : null;
            }
            var horizontal = block.Direction == "row";

            var go = NewUI("Products", parent);
            var gap = (float)(block.Style?.Gap ?? 8);
            if (horizontal)
            {
                var row = go.AddComponent<HorizontalLayoutGroup>();
                row.childControlWidth = true;
                row.childControlHeight = true;
                row.childForceExpandWidth = true;
                row.childForceExpandHeight = false;
                row.spacing = gap;
            }
            else
            {
                var column = go.AddComponent<VerticalLayoutGroup>();
                column.childControlWidth = true;
                column.childControlHeight = true;
                column.childForceExpandWidth = true;
                column.childForceExpandHeight = false;
                column.spacing = gap;
            }

            var accent = Color(_doc.Accent, UnityEngine.Color.blue);
            foreach (var pkg in shown)
            {
                var highlighted = pkg.PackageId == highlightId;
                var card = NewUI("Plan", go.transform);
                var image = card.AddComponent<Image>();
                var sprite = RevnixPaywallSprites.RoundedOutline(14, 2);
                if (sprite != null)
                {
                    image.sprite = sprite;
                    image.type = Image.Type.Sliced;
                }
                image.color = highlighted ? accent : new Color(0.5f, 0.5f, 0.5f, 0.35f);

                var inner = card.AddComponent<VerticalLayoutGroup>();
                inner.childControlWidth = true;
                inner.childControlHeight = true;
                inner.childForceExpandWidth = true;
                inner.childForceExpandHeight = false;
                inner.padding = new RectOffset(14, 14, 12, 12);
                inner.spacing = 3f;

                AddPlanLine(card.transform, WithFallback(block.TitleTpl ?? "{title}", pkg, pkg.Title), 14, true);
                AddPlanLine(card.transform, WithFallback(block.PriceTpl ?? "{price}", pkg, pkg.PriceLabel), 13, false);
                if (highlighted && !string.IsNullOrEmpty(block.HighlightSub))
                {
                    AddPlanLine(card.transform, block.HighlightSub, 12, false, 0.75f);
                }
                if (highlighted && !string.IsNullOrEmpty(block.BadgeText))
                {
                    AddPlanLine(card.transform, block.BadgeText, 10, true);
                }
                ApplyBox(card, highlighted ? block.HighlightStyle : block.CardStyle, paint: false);
                // The whole card is the target, not just its glyphs — a plan
                // row is mostly padding, and tapping beside the price must
                // select. The card's own Image is the raycast target.
                MakeSelectable(card, pkg.PackageId);
            }
            ApplyBox(go, block.Style, paint: false);
            return go;
        }

        /// <summary>
        /// Makes an element the paywall's dismiss target when the design marks
        /// it as one (REV-252), and returns it either way.
        ///
        /// A block with no close action gets no Button at all, so it never
        /// intercepts a tap meant for what sits behind it. Like
        /// <see cref="MakeSelectable"/>, an element with no art of its own
        /// needs a transparent raycast target or the tap falls through.
        /// </summary>
        private GameObject CloseOnTap(GameObject go, BlockAction action)
        {
            if (go == null || action != BlockAction.Close || _ctx.OnClose == null) return go;
            var target = go.GetComponent<Graphic>();
            if (target == null)
            {
                var image = go.AddComponent<Image>();
                image.color = new UnityEngine.Color(0f, 0f, 0f, 0f);
                target = image;
            }
            target.raycastTarget = true;

            var button = go.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = target;
            var onClose = _ctx.OnClose;
            button.onClick.AddListener(() => onClose());
            return go;
        }

        /// <summary>
        /// Makes a card its package's selection target. UGUI routes clicks
        /// through a raycast target, so a card with no art of its own gets a
        /// fully transparent Image — without one the tap falls straight
        /// through to whatever the design draws behind it.
        /// </summary>
        private void MakeSelectable(GameObject go, string packageId)
        {
            if (_ctx.OnSelect == null || string.IsNullOrEmpty(packageId)) return;
            var target = go.GetComponent<Graphic>();
            if (target == null)
            {
                var image = go.AddComponent<Image>();
                image.color = new UnityEngine.Color(0f, 0f, 0f, 0f);
                target = image;
            }
            target.raycastTarget = true;

            var button = go.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = target;
            var id = packageId;
            button.onClick.AddListener(() => _ctx.OnSelect(id));
        }

        private void AddPlanLine(Transform parent, string content, int size, bool bold, float alpha = 1f)
        {
            var go = NewUI("Line", parent);
            var text = go.AddComponent<Text>();
            if (_ctx.Font != null) text.font = _ctx.Font;
            text.text = content ?? "";
            text.fontSize = size;
            text.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            var color = Color(_doc.TextColor, UnityEngine.Color.white);
            color.a *= alpha;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.raycastTarget = false;
        }

        /// <summary>
        /// A template that resolves to nothing useful falls back to the plain
        /// value — a card must show a title and a price even if its template
        /// names a tag this SDK does not know.
        /// </summary>
        private string WithFallback(string tpl, BlockPackage pkg, string fallback)
        {
            var resolved = RevnixPaywallTags.Resolve(tpl, pkg, _ctx.Packages);
            return string.IsNullOrEmpty(resolved) || resolved.Trim().Length == 0 ? fallback : resolved;
        }

        private static bool ContainsPackage(List<BlockPackage> packages, string id)
        {
            foreach (var pkg in packages)
            {
                if (pkg.PackageId == id) return true;
            }
            return false;
        }

        // ── containers ────────────────────────────────────────────────────

        /// <summary>
        /// The container. `layout` maps onto UGUI's own primitives: column →
        /// VerticalLayoutGroup, row → HorizontalLayoutGroup, stack → a bare
        /// parent whose children anchor themselves, grid → GridLayoutGroup. Any
        /// value this SDK does not know falls back to a column rather than
        /// drawing nothing.
        /// </summary>
        private GameObject RenderCard(PaywallBlock block, Transform parent, BlockPackage pkg)
        {
            if (RevnixPaywallSelection.IsRepeat(block))
            {
                // One designed card, drawn per package. With nothing attached a
                // single instance still draws, so the design stays visible.
                var wrapper = NewUI("Repeat", parent);
                var column = wrapper.AddComponent<VerticalLayoutGroup>();
                column.childControlWidth = true;
                column.childControlHeight = true;
                column.childForceExpandWidth = true;
                column.childForceExpandHeight = false;
                column.spacing = (float)(block.Style?.Gap ?? 10);

                if (_ctx.Packages.Count == 0)
                {
                    var single = RevnixPaywallSelection.ResolveInstance(block, null, _ctx.Packages, _selected);
                    if (single != null && single.Visible) Container(single.Block, wrapper.transform, null);
                }
                else
                {
                    foreach (var each in _ctx.Packages)
                    {
                        // Each instance decides its own context, so the
                        // repeated card's `selectedStyle` and `visibility`
                        // apply per package and its children inherit that.
                        var instance = RevnixPaywallSelection.ResolveInstance(block, each, _ctx.Packages, _selected);
                        if (instance == null || !instance.Visible) continue;
                        var drawn = Container(instance.Block, wrapper.transform, each);
                        MakeSelectable(drawn, each.PackageId);
                    }
                }
                return wrapper;
            }

            // Render already resolved this card: a pinned card arrives with its
            // own package as `pkg` and its `selectedStyle` merged in while it
            // is the selected one, and a card pinned past the offering never
            // gets here. A pinned card doubles as its package's selection
            // target — that is how hand-styled plan rows (a highlighted annual
            // beside a plain monthly) become tappable without a products
            // block. A card that names no package is decoration and stays
            // inert.
            var card = Container(block, parent, pkg);
            if (RevnixPaywallSelection.IsPinned(block) && pkg != null) MakeSelectable(card, pkg.PackageId);
            return card;
        }

        /// <summary>Draws a container with the style it carries — the
        /// effective one, since <see cref="Render"/> merged any selected
        /// style in before handing the block over.</summary>
        private GameObject Container(PaywallBlock block, Transform parent, BlockPackage pkg)
        {
            var go = NewUI("Card", parent);
            var gap = (float)(block.Style?.Gap ?? 10);
            var children = block.Children ?? new List<PaywallBlock>();
            var layout = block.Layout;

            if (layout == "stack")
            {
                // No layout group: a stack's children place themselves against
                // the parent's rect, which is what `inset` and the edge offsets
                // describe.
                foreach (var child in children)
                {
                    BlockStyle style;
                    var rendered = Render(child, go.transform, pkg, out style);
                    if (rendered != null) PlaceInStack(rendered, style);
                }
            }
            else if (layout == "grid")
            {
                var grid = go.AddComponent<GridLayoutGroup>();
                grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                grid.constraintCount = GridColumnCount(block);
                grid.spacing = new Vector2(gap, gap);
                foreach (var child in children) Render(child, go.transform, pkg);
            }
            else if (layout == "row")
            {
                var row = go.AddComponent<HorizontalLayoutGroup>();
                row.childControlWidth = true;
                row.childControlHeight = true;
                row.childForceExpandWidth = false;
                row.childForceExpandHeight = false;
                row.spacing = gap;
                row.childAlignment = RowAlignment(block.Style);
                foreach (var child in children)
                {
                    BlockStyle style;
                    var rendered = Render(child, go.transform, pkg, out style);
                    if (rendered != null) PlaceInFlow(rendered, style, horizontal: true);
                }
            }
            else
            {
                // column, and anything unrecognized.
                var column = go.AddComponent<VerticalLayoutGroup>();
                column.childControlWidth = true;
                column.childControlHeight = true;
                column.childForceExpandWidth = true;
                column.childForceExpandHeight = false;
                column.spacing = gap;
                column.childAlignment = ColumnAlignment(block.Style);
                foreach (var child in children)
                {
                    BlockStyle style;
                    var rendered = Render(child, go.transform, pkg, out style);
                    if (rendered != null) PlaceInFlow(rendered, style, horizontal: false);
                }
            }
            ApplyBox(go, block.Style, paint: true);
            return go;
        }

        /// <summary>
        /// A child of a row or column: flex sizing — unless the design pins it
        /// to an edge of its card. Every container is a positioning context in
        /// the dashboard, so a SAVE badge pinned top/right anchors to its own
        /// card and takes no slot in the flow.
        /// </summary>
        private static void PlaceInFlow(GameObject go, BlockStyle style, bool horizontal)
        {
            if (PlaceInStack(go, style))
            {
                Le(go).ignoreLayout = true;
                return;
            }
            ApplyFlex(go, style, horizontal);
        }

        private static int GridColumnCount(PaywallBlock block)
        {
            // The design's own track list wins; `columns` is the simple form.
            if (!string.IsNullOrEmpty(block.GridColumns))
            {
                var tracks = block.GridColumns.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (tracks.Length > 0) return tracks.Length;
            }
            return Math.Max(1, block.Columns ?? 2);
        }

        /// <summary>flex-grow, flex-basis and flex-shrink, as UGUI expresses them.</summary>
        private static void ApplyFlex(GameObject go, BlockStyle style, bool horizontal)
        {
            if (style == null) return;
            var element = Le(go);
            if (style.Flex.HasValue && style.Flex.Value > 0)
            {
                if (horizontal) element.flexibleWidth = (float)style.Flex.Value;
                else element.flexibleHeight = (float)style.Flex.Value;
            }
            if (style.Basis.HasValue)
            {
                // Carousel cards and side rails are sized by basis and collapse
                // to nothing without it.
                if (horizontal) element.preferredWidth = (float)style.Basis.Value;
                else element.preferredHeight = (float)style.Basis.Value;
            }
            // shrink 0 stops a row item from being squashed.
            if (style.Shrink.HasValue && style.Shrink.Value == 0 && horizontal)
            {
                element.flexibleWidth = 0f;
            }
        }

        /// <summary>
        /// Stack placement — `inset` fills the parent, the offsets pin an
        /// edge, and opposite offsets together (`left` and `right`, `top` and
        /// `bottom`) stretch between them, which is how a bottom-anchored CTA
        /// group is written. A percentage offset has no fixed pixel value, so
        /// it is left to the anchor rather than guessed. Answers whether the
        /// style placed the box at all.
        /// </summary>
        private static bool PlaceInStack(GameObject go, BlockStyle style)
        {
            var rt = (RectTransform)go.transform;
            if (style == null) return false;
            if (style.Inset == true)
            {
                Fill(rt);
                return true;
            }
            var top = style.Top?.Px;
            var bottom = style.Bottom?.Px;
            var left = style.Left?.Px;
            var right = style.Right?.Px;
            if (!top.HasValue && !bottom.HasValue && !left.HasValue && !right.HasValue) return false;

            var stretchX = left.HasValue && right.HasValue;
            var stretchY = top.HasValue && bottom.HasValue;
            var anchorMinX = stretchX ? 0f : (left.HasValue ? 0f : (right.HasValue ? 1f : 0.5f));
            var anchorMaxX = stretchX ? 1f : anchorMinX;
            var anchorMinY = stretchY ? 0f : (top.HasValue ? 1f : (bottom.HasValue ? 0f : 0.5f));
            var anchorMaxY = stretchY ? 1f : anchorMinY;
            rt.anchorMin = new Vector2(anchorMinX, anchorMinY);
            rt.anchorMax = new Vector2(anchorMaxX, anchorMaxY);
            rt.pivot = new Vector2(stretchX ? 0.5f : anchorMinX, stretchY ? 0.5f : anchorMinY);

            // Pinned to an edge, the box takes no size from a layout group: an
            // explicit width/height applies here, a stretched axis is the
            // parent minus its two offsets, and whatever is left is sized to
            // the content (a pinned label, a badge).
            var width = style.Width?.Px;
            var height = style.Height?.Px;
            var size = rt.sizeDelta;
            if (stretchX) size.x = -(float)(left.Value + right.Value);
            else if (width.HasValue) size.x = (float)width.Value;
            if (stretchY) size.y = -(float)(top.Value + bottom.Value);
            else if (height.HasValue) size.y = (float)height.Value;
            rt.sizeDelta = size;

            var fitWidth = !stretchX && !width.HasValue;
            var fitHeight = !stretchY && !height.HasValue;
            if (fitWidth || fitHeight)
            {
                var fitter = go.GetComponent<ContentSizeFitter>();
                if (fitter == null) fitter = go.AddComponent<ContentSizeFitter>();
                if (fitWidth) fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                if (fitHeight) fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }

            var x = stretchX ? (float)(left.Value - right.Value) * 0.5f
                : left.HasValue ? (float)left.Value : (right.HasValue ? -(float)right.Value : 0f);
            var y = stretchY ? (float)(bottom.Value - top.Value) * 0.5f
                : top.HasValue ? -(float)top.Value : (bottom.HasValue ? (float)bottom.Value : 0f);
            rt.anchoredPosition = new Vector2(x, y);
            return true;
        }

        private static TextAnchor RowAlignment(BlockStyle style)
        {
            var cross = style?.Items;
            var main = style?.Justify;
            if (cross == "center")
            {
                return main == "center" ? TextAnchor.MiddleCenter
                    : main == "end" ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
            }
            if (cross == "end")
            {
                return main == "center" ? TextAnchor.LowerCenter
                    : main == "end" ? TextAnchor.LowerRight : TextAnchor.LowerLeft;
            }
            return main == "center" ? TextAnchor.UpperCenter
                : main == "end" ? TextAnchor.UpperRight : TextAnchor.UpperLeft;
        }

        private static TextAnchor ColumnAlignment(BlockStyle style)
        {
            var cross = style?.Items;
            var main = style?.Justify;
            if (main == "center")
            {
                return cross == "center" ? TextAnchor.MiddleCenter
                    : cross == "end" ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
            }
            if (main == "end")
            {
                return cross == "center" ? TextAnchor.LowerCenter
                    : cross == "end" ? TextAnchor.LowerRight : TextAnchor.LowerLeft;
            }
            return cross == "center" ? TextAnchor.UpperCenter
                : cross == "end" ? TextAnchor.UpperRight : TextAnchor.UpperLeft;
        }

        // ── style ─────────────────────────────────────────────────────────

        private void ApplyText(Text text, BlockStyle style, string content)
        {
            if (_ctx.Font != null) text.font = _ctx.Font;
            text.text = content ?? "";
            var size = (int)Math.Round(style?.FontSize ?? 15);
            text.fontSize = Math.Max(1, size);
            var weight = style?.FontWeight ?? 0;
            var italic = style?.FontStyle == "italic";
            text.fontStyle = weight >= 600
                ? (italic ? FontStyle.BoldAndItalic : FontStyle.Bold)
                : (italic ? FontStyle.Italic : FontStyle.Normal);
            text.color = FillColor(style?.TextColor, Color(_doc.TextColor, UnityEngine.Color.white));
            text.alignment = style?.Align == "center" ? TextAnchor.UpperCenter
                : style?.Align == "right" ? TextAnchor.UpperRight : TextAnchor.UpperLeft;
            text.horizontalOverflow = style?.Nowrap == true
                ? HorizontalWrapMode.Overflow
                : HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            // A unitless line-height is a multiplier, which is what UGUI's
            // lineSpacing already is.
            if (style?.LineHeight.HasValue == true) text.lineSpacing = (float)style.LineHeight.Value;
            if (style?.TextTransform == "uppercase") text.text = text.text.ToUpperInvariant();
        }

        /// <summary>
        /// Applies the box half of a style.
        ///
        /// Every field is read independently and only when present, which is
        /// what lets a design authored against a newer dashboard draw here
        /// minus the one effect this SDK does not know, rather than failing.
        /// </summary>
        private void ApplyBox(GameObject go, BlockStyle style, bool paint, bool skipHeight = false)
        {
            if (style == null) return;

            if (paint && (style.Fill != null || style.BorderColor != null))
            {
                var existing = go.GetComponent<Image>();
                var image = existing != null ? existing : go.AddComponent<Image>();
                var radius = (int)Math.Round(style.Radius ?? 0);
                if (radius > 0)
                {
                    var sprite = RevnixPaywallSprites.Rounded(Math.Min(radius, 64));
                    if (sprite != null)
                    {
                        image.sprite = sprite;
                        image.type = Image.Type.Sliced;
                    }
                }
                var paintValue = style.Fill ?? style.BorderColor;
                var gradients = FillGradients(paintValue);
                image.color = FillColor(paintValue, UnityEngine.Color.clear, gradients);
                image.raycastTarget = false;
                PaintFillGradients(go, gradients, style.Radius);
            }

            var padLeft = style.PaddingLeft ?? style.PaddingX ?? style.Padding;
            var padRight = style.PaddingRight ?? style.PaddingX ?? style.Padding;
            var padTop = style.PaddingTop ?? style.PaddingY ?? style.Padding;
            var padBottom = style.PaddingBottom ?? style.PaddingY ?? style.Padding;
            if (padLeft.HasValue || padRight.HasValue || padTop.HasValue || padBottom.HasValue)
            {
                var group = go.GetComponent<HorizontalOrVerticalLayoutGroup>();
                if (group != null)
                {
                    group.padding = new RectOffset(
                        (int)Math.Round(padLeft ?? 0), (int)Math.Round(padRight ?? 0),
                        (int)Math.Round(padTop ?? 0), (int)Math.Round(padBottom ?? 0));
                }
            }

            if (!skipHeight && style.Height?.Px is double height) Le(go).preferredHeight = (float)height;
            if (style.MinHeight.HasValue) Le(go).minHeight = (float)style.MinHeight.Value;
            if (style.Width?.Px is double width) Le(go).preferredWidth = (float)width;

            if (style.Opacity.HasValue)
            {
                var existingGroup = go.GetComponent<CanvasGroup>();
                var canvasGroup = existingGroup != null ? existingGroup : go.AddComponent<CanvasGroup>();
                canvasGroup.alpha = Mathf.Clamp01((float)(style.Opacity.Value / 100));
            }
            if (style.Rotate.HasValue)
            {
                go.transform.localRotation = Quaternion.Euler(0f, 0f, -(float)style.Rotate.Value);
            }
        }

        /// <summary>
        /// The gradient, photo and scrim layers, bottom first. Nothing is built
        /// for an unedited paywall whose background is a flat colour, so that
        /// case renders exactly as it did before.
        ///
        /// Sibling order is paint order in UGUI, and these are added before the
        /// block body, so the content always sits above the art.
        /// </summary>
        private void BuildBackgroundArt(RevnixBackgroundLayers layers, Transform parent)
        {
            if (layers == null) return;

            if (!string.IsNullOrEmpty(layers.Ground))
            {
                var gradients = RevnixBackground.ParseGradients(
                    layers.Ground,
                    value => RevnixBlockColor.Resolve(value, _doc).HasValue);
                foreach (var gradient in gradients)
                {
                    AddGradient(gradient, parent, "RevnixBackgroundGradient", 1f);
                }
            }

            if (layers.Image != null)
            {
                var go = NewUI("RevnixBackgroundPhoto", parent);
                Fill((RectTransform)go.transform);
                var raw = go.AddComponent<RawImage>();
                raw.raycastTarget = false;
                raw.color = new Color(1f, 1f, 1f, (float)layers.Image.Opacity);
                // Nothing to show until the fetch lands; enabling an empty
                // RawImage paints a white box over the ground.
                raw.enabled = false;
                var loader = go.AddComponent<RevnixBackgroundPhoto>();
                loader.Configure(raw, layers.Image);
            }

            if (layers.Overlay != null)
            {
                var opacity = (float)layers.Overlay.Opacity;
                var gradients = RevnixBackground.ParseGradients(
                    layers.Overlay.Fill,
                    value => RevnixBlockColor.Resolve(value, _doc).HasValue);
                if (gradients.Count > 0)
                {
                    foreach (var gradient in gradients)
                    {
                        AddGradient(gradient, parent, "RevnixBackgroundScrim", opacity);
                    }
                }
                else
                {
                    var solid = RevnixBlockColor.Resolve(layers.Overlay.Fill, _doc);
                    if (solid.HasValue)
                    {
                        var go = NewUI("RevnixBackgroundScrim", parent);
                        Fill((RectTransform)go.transform);
                        var image = go.AddComponent<Image>();
                        var c = solid.Value;
                        image.color = new Color(c.R, c.G, c.B, c.A * opacity);
                        image.raycastTarget = false;
                    }
                }
            }
        }

        private GameObject AddGradient(RevnixGradient gradient, Transform parent, string name, float opacity)
        {
            var texture = RevnixGradientTexture.Bake(
                gradient,
                value =>
                {
                    var resolved = RevnixBlockColor.Resolve(value, _doc);
                    if (!resolved.HasValue) return null;
                    var c = resolved.Value;
                    return new Color(c.R, c.G, c.B, c.A);
                });
            if (texture == null) return null;
            var go = NewUI(name, parent);
            Fill((RectTransform)go.transform);
            var raw = go.AddComponent<RawImage>();
            raw.texture = texture;
            raw.color = new Color(1f, 1f, 1f, opacity);
            raw.raycastTarget = false;
            // The baked texture is unmanaged and belongs to this object alone.
            go.AddComponent<RevnixOwnedTexture>().Own(texture);
            return go;
        }

        /// <summary>
        /// Paints a block's gradient fill as layers behind its content.
        ///
        /// <para>
        /// UGUI's Image takes one flat colour, so a gradient cannot live on the
        /// box's own graphic: it is baked and shown in RawImage children.
        /// Sibling order is paint order, so the layers are moved to the FRONT of
        /// the child list — after the box's own Image (the flat base the fill
        /// collapses to) and before everything the design put inside it. They
        /// are marked ignoreLayout so a layout group treats them as paint
        /// rather than as another item to lay out.
        /// </para>
        ///
        /// <para>
        /// Call this AFTER the block's children exist, or the layers land
        /// behind nothing and paint over the content.
        /// </para>
        /// </summary>
        private void PaintFillGradients(GameObject go, List<RevnixGradient> gradients, double? radius)
        {
            if (go == null || gradients == null || gradients.Count == 0) return;
            for (var i = 0; i < gradients.Count; i++)
            {
                var layer = AddGradient(gradients[i], go.transform, "RevnixBlockFill", 1f);
                if (layer == null) continue;
                var element = layer.GetComponent<LayoutElement>();
                if (element == null) element = layer.AddComponent<LayoutElement>();
                element.ignoreLayout = true;
                layer.transform.SetSiblingIndex(i);
            }
            ClipToRoundedBox(go, radius);
        }

        /// <summary>
        /// Clips a box's gradient layers to its corner radius.
        ///
        /// <para>
        /// A baked gradient is a stretched RawImage with square corners, so a
        /// rounded card filled with one would show them. UGUI's stencil Mask
        /// uses the GameObject's OWN graphic as the shape, which here is the
        /// rounded sliced sprite ApplyBox already put there —
        /// <c>showMaskGraphic</c> keeps that sprite visible so the mask costs
        /// nothing visually and only clips what is inside.
        /// </para>
        ///
        /// <para>
        /// Only ever added for a rounded box that actually has gradient layers:
        /// a mask also clips the block's CONTENT, and stencil masks nest only
        /// eight deep, so it is not something to hand out freely.
        /// </para>
        /// </summary>
        private static void ClipToRoundedBox(GameObject go, double? radius)
        {
            if (!radius.HasValue || radius.Value <= 0) return;
            if (go.GetComponent<Image>() == null) return;
            if (go.GetComponent<Mask>() != null) return;
            var mask = go.AddComponent<Mask>();
            mask.showMaskGraphic = true;
        }

        /// <summary>
        /// The FLAT colour a fill paints — a plain colour as-is, and for
        /// anything else the colour it collapses to. Never black unless the
        /// design asked for black.
        /// </summary>
        ///
        /// <para>
        /// A fill with gradient layers answers with <c>UnityEngine.Color.clear</c>:
        /// the layers carry their own alpha, and 83 of the library's 139
        /// gradient fills fade through a translucent stop — they are drawn over
        /// the screen's art precisely so it shows through, and a flat base
        /// under them would make every one a solid block.
        /// </para>
        private Color FillColor(string fill, Color fallback, List<RevnixGradient> gradients = null)
        {
            if (string.IsNullOrEmpty(fill)) return fallback;
            if (gradients != null && gradients.Count > 0) return UnityEngine.Color.clear;
            var resolved = RevnixBlockColor.ResolveFlat(fill, _doc, Diagnostic);
            if (!resolved.HasValue) return fallback;
            var c = resolved.Value;
            return new Color(c.R, c.G, c.B, c.A);
        }

        /// <summary>
        /// The gradient layers of a fill, bottom first. Empty for a plain
        /// colour — and for a form this build cannot read, which is reported
        /// once, here, rather than again by every colour path that touches it.
        /// </summary>
        private List<RevnixGradient> FillGradients(string fill)
        {
            if (string.IsNullOrEmpty(fill)) return null;
            if (RevnixBlockColor.Resolve(fill, _doc).HasValue) return null;
            var gradients = RevnixBackground.ParseGradients(
                fill, value => RevnixBlockColor.Resolve(value, _doc).HasValue);
            return gradients.Count > 0 ? gradients : null;
        }

        /// <summary>
        /// Reports a paint string this build could not read, ONCE per screen.
        ///
        /// A design usually reuses the same fill across a dozen blocks, and the
        /// renderer redraws on every selection change — without the guard a
        /// single unreadable value would arrive at the host's sink dozens of
        /// times per tap, which buries it rather than surfacing it.
        /// </summary>
        private void Diagnostic(string message)
        {
            if (_ctx.OnDiagnostic == null) return;
            if (!_reported.Add(message)) return;
            _ctx.OnDiagnostic(message);
        }

        private Color Color(string value, Color fallback)
        {
            var resolved = RevnixBlockColor.Resolve(value, _doc);
            if (!resolved.HasValue) return fallback;
            var c = resolved.Value;
            return new Color(c.R, c.G, c.B, c.A);
        }

        // ── UGUI plumbing ─────────────────────────────────────────────────

        private static GameObject NewUI(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void Fill(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static LayoutElement Le(GameObject go)
        {
            // Not `??`: Unity overrides == on Object, and the null-coalescing
            // operator bypasses that override.
            var element = go.GetComponent<LayoutElement>();
            return element != null ? element : go.AddComponent<LayoutElement>();
        }
    }

    /// <summary>
    /// Scales a `canvas` document to the viewport it is given (render contract
    /// v2, section 3).
    ///
    /// The design is authored against a fixed 393×852 screen; the whole tree is
    /// laid out at the design width and then scaled, so absolute placement
    /// inside `stack` containers stays true on any device. The scale is
    /// <c>min(width, 480) / 393</c> — a tablet centres the design rather than
    /// blowing it up — and the layout height is <c>max(852, height / scale)</c>
    /// so a taller viewport fills instead of leaving a band. The numbers come
    /// from <see cref="RevnixPaywallCanvasLayout"/>. The viewport is not known
    /// until layout runs, so the scale is applied every time the rect changes
    /// rather than computed once.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RevnixCanvasScaler : MonoBehaviour
    {
        /// <summary>The design body, laid out at 393 wide.</summary>
        public RectTransform Target;

        /// <summary>The scroll content the body sits in — sized to the scaled
        /// height so the ScrollRect knows how far there is to go.</summary>
        public RectTransform Content;

        private RectTransform _self;
        private float _lastWidth = -1f;
        private float _lastHeight = -1f;

        private void Awake() => _self = (RectTransform)transform;

        private void OnEnable() => Apply();

        private void OnRectTransformDimensionsChange() => Apply();

        private void Update()
        {
            // A canvas can be resized without the callback firing (a rotated
            // device, a resized editor game view), so the size is also checked
            // each frame — it is two float comparisons.
            Apply();
        }

        private void Apply()
        {
            if (Target == null) return;
            if (_self == null) _self = (RectTransform)transform;
            var width = _self.rect.width;
            var height = _self.rect.height;
            if (width <= 0f || height <= 0f) return;
            if (Mathf.Approximately(width, _lastWidth) && Mathf.Approximately(height, _lastHeight)) return;
            _lastWidth = width;
            _lastHeight = height;

            var scale = RevnixPaywallCanvasLayout.Scale(width);
            var designHeight = RevnixPaywallCanvasLayout.DesignHeight(height, scale);
            Target.localScale = new Vector3(scale, scale, 1f);
            Target.sizeDelta = new Vector2(PaywallBlockDoc.CanvasWidth, designHeight);
            if (Content != null)
            {
                Content.sizeDelta = new Vector2(
                    Content.sizeDelta.x, RevnixPaywallCanvasLayout.ScaledHeight(designHeight, scale));
            }
        }
    }

    /// <summary>
    /// Enables vertical scrolling only while the content is taller than the
    /// viewport (contract v2: no bounce when it fits), keeping a fit pinned to
    /// the top. RevnixPaywallCenterOnShort's idea, minus the centring — a
    /// designed screen is authored from its top edge down.
    /// </summary>
    internal sealed class RevnixScrollWhenOverflowing : MonoBehaviour
    {
        public ScrollRect Scroll;

        private void LateUpdate()
        {
            if (Scroll == null || Scroll.content == null || Scroll.viewport == null) return;
            var overflows = RevnixPaywallCanvasLayout.Overflows(
                Scroll.viewport.rect.height, Scroll.content.rect.height);
            if (Scroll.vertical == overflows) return;
            Scroll.vertical = overflows;
            if (overflows) return;
            Scroll.StopMovement();
            Scroll.content.anchoredPosition = new Vector2(Scroll.content.anchoredPosition.x, 0f);
        }
    }

    /// <summary>
    /// Keeps the fallback close chip below the status bar: its anchored
    /// position is <c>(-Margin, -(Margin + safeAreaTop))</c> in canvas units,
    /// re-evaluated as the safe area changes (a rotation). A world-space
    /// canvas has no screen to be safe from, so it keeps the plain margin.
    /// </summary>
    internal sealed class RevnixSafeAreaInset : MonoBehaviour
    {
        public float Margin = 14f;

        private Canvas _canvas;
        private float _applied = -1f;

        private void LateUpdate()
        {
            if (_canvas == null) _canvas = GetComponentInParent<Canvas>();
            var factor = 1f;
            if (_canvas != null)
            {
                var root = _canvas.rootCanvas;
                if (root == null) root = _canvas;
                if (root.renderMode == RenderMode.WorldSpace) return;
                factor = root.scaleFactor;
            }
            var safe = Screen.safeArea;
            var inset = RevnixPaywallCanvasLayout.SafeAreaTopInset(
                Screen.height, safe.y, safe.height, factor);
            if (Mathf.Approximately(inset, _applied)) return;
            _applied = inset;
            ((RectTransform)transform).anchoredPosition = new Vector2(-Margin, -(Margin + inset));
        }
    }

    /// <summary>
    /// The pressed state of a designed button (contract v2, section 4): the
    /// whole button at 80% opacity while the pointer is down, restored on
    /// release or when the pointer leaves. Multiplies whatever alpha the
    /// block's own `opacity` set, and stays put while the button is not
    /// interactable (loading).
    /// </summary>
    internal sealed class RevnixPaywallPressOpacity : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        public const float PressedAlpha = 0.8f;

        private CanvasGroup _group;
        private Selectable _selectable;
        private float _rest = 1f;
        private bool _down;

        private void Awake()
        {
            _group = GetComponent<CanvasGroup>();
            if (_group == null) _group = gameObject.AddComponent<CanvasGroup>();
            _selectable = GetComponent<Selectable>();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_down || _group == null) return;
            if (_selectable == null) _selectable = GetComponent<Selectable>();
            if (_selectable != null && !_selectable.IsInteractable()) return;
            _down = true;
            _rest = _group.alpha;
            _group.alpha = _rest * PressedAlpha;
        }

        public void OnPointerUp(PointerEventData eventData) => Release();

        public void OnPointerExit(PointerEventData eventData) => Release();

        private void OnDisable() => Release();

        private void Release()
        {
            if (!_down) return;
            _down = false;
            if (_group != null) _group.alpha = _rest;
        }
    }

    /// <summary>
    /// An image slot that names only an aspect ratio: its height follows the
    /// width the layout gives it. Written to the LayoutElement rather than the
    /// rect, the way RevnixPaywallPillSize does, because a layout group drives
    /// the rect itself and an AspectRatioFitter would fight it.
    /// </summary>
    internal sealed class RevnixPaywallAspectHeight : MonoBehaviour
    {
        public LayoutElement Element;
        public float Ratio = 1f;

        private void LateUpdate()
        {
            if (Element == null || Ratio <= 0f) return;
            var width = ((RectTransform)transform).rect.width;
            if (width <= 0f) return;
            var height = width / Ratio;
            if (!Mathf.Approximately(Element.preferredHeight, height)) Element.preferredHeight = height;
        }
    }
}
