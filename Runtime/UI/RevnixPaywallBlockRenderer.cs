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
using UnityEngine.UI;

namespace Revnix.Unity.UI
{
    /// <summary>Everything the tree needs that is not in the document itself.</summary>
    public sealed class BlockRenderContext
    {
        public PaywallBlockDoc Doc;
        public List<BlockPackage> Packages = new List<BlockPackage>();

        /// <summary>The package a plan card visually emphasizes, and the one
        /// whose tags a subtree resolves against outside a `repeat`.</summary>
        public string SelectedPackageId;

        /// <summary>Fills an image block that publishes no URL of its own.</summary>
        public string HeroImageUrl;

        /// <summary>Paywall-level Terms/Privacy, used when a links block sets none.</summary>
        public string FooterTermsUrl;
        public string FooterPrivacyUrl;

        public Action<string> OnPurchase;
        public Action OnRestore;
        public Action OnTerms;
        public Action OnPrivacy;
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

        public RevnixPaywallBlockRenderer(BlockRenderContext ctx)
        {
            _ctx = ctx;
            _doc = ctx.Doc;
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
            var background = screen.AddComponent<Image>();
            background.color = Color(_doc.Background, UnityEngine.Color.black);
            background.raycastTarget = false;

            var body = NewUI("Blocks", screen.transform);
            var bodyRt = (RectTransform)body.transform;

            if (IsCanvas)
            {
                // Laid out at the design size and scaled as a whole. The scale
                // is applied against the live width, so it stays correct on any
                // device rather than being baked at build time.
                bodyRt.anchorMin = new Vector2(0f, 1f);
                bodyRt.anchorMax = new Vector2(0f, 1f);
                bodyRt.pivot = new Vector2(0f, 1f);
                bodyRt.sizeDelta = new Vector2(PaywallBlockDoc.CanvasWidth, PaywallBlockDoc.CanvasHeight);
                var fitter = screen.AddComponent<RevnixCanvasScaler>();
                fitter.Target = bodyRt;
            }
            else
            {
                Fill(bodyRt);
                var column = body.AddComponent<VerticalLayoutGroup>();
                column.childControlWidth = true;
                column.childControlHeight = true;
                column.childForceExpandWidth = true;
                column.childForceExpandHeight = false;
                column.padding = new RectOffset(20, 20, 20, 20);
            }

            foreach (var block in _doc.Blocks)
            {
                Render(block, body.transform, null);
            }
            return screen;
        }

        private bool IsCanvas => _doc.Layout == "canvas";

        // ── blocks ────────────────────────────────────────────────────────

        /// <summary>One block, or null when it contributes nothing.</summary>
        private GameObject Render(PaywallBlock block, Transform parent, BlockPackage pkg)
        {
            if (block == null) return null;
            switch (block.Kind)
            {
                case PaywallBlockKind.Text:
                    return RenderText(block, parent, pkg);
                case PaywallBlockKind.Image:
                    return RenderImage(block, parent);
                case PaywallBlockKind.List:
                    return RenderList(block, parent);
                case PaywallBlockKind.Products:
                    return RenderProducts(block, parent);
                case PaywallBlockKind.Button:
                    return RenderButton(block, parent, pkg);
                case PaywallBlockKind.Links:
                    return RenderLinks(block, parent);
                case PaywallBlockKind.Line:
                    return RenderLine(block, parent);
                case PaywallBlockKind.Spacer:
                    return RenderSpacer(block, parent);
                case PaywallBlockKind.Card:
                    return RenderCard(block, parent, pkg);
                default:
                    // A block type from a newer dashboard: skip it, keep the
                    // screen.
                    return null;
            }
        }

        private GameObject RenderText(PaywallBlock block, Transform parent, BlockPackage pkg)
        {
            var go = NewUI("Text", parent);
            var text = go.AddComponent<Text>();
            ApplyText(text, block.Style, RevnixPaywallTags.Resolve(block.Text ?? "", pkg, _ctx.Packages));
            ApplyBox(go, block.Style, paint: false);
            return go;
        }

        private GameObject RenderImage(PaywallBlock block, Transform parent)
        {
            // Image loading is the host project's job — this package ships no
            // downloader for block art — so a slot draws as its placeholder
            // box. A design that publishes a URL still reserves the space.
            var go = NewUI("Image", parent);
            var image = go.AddComponent<Image>();
            image.color = new Color32(125, 135, 155, 56);
            image.raycastTarget = false;

            var style = block.Style;
            var sized = style != null
                && (style.Inset == true || style.Height.HasValue
                    || style.AspectRatio.HasValue || style.Flex.HasValue);
            if (!sized) Le(go).preferredHeight = 160f;

            var label = !string.IsNullOrEmpty(block.Placeholder) ? block.Placeholder : block.Url;
            if (!string.IsNullOrEmpty(label))
            {
                var caption = NewUI("Placeholder", go.transform);
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
            image.color = Color(block.Style?.Fill, Color(_doc.Accent, UnityEngine.Color.blue));

            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            var selected = _ctx.SelectedPackageId
                ?? (_ctx.Packages.Count > 0 ? _ctx.Packages[0].PackageId : null);
            if (selected != null && _ctx.OnPurchase != null)
            {
                button.onClick.AddListener(() => _ctx.OnPurchase(selected));
            }

            var label = NewUI("Label", go.transform);
            Fill((RectTransform)label.transform);
            var text = label.AddComponent<Text>();
            if (_ctx.Font != null) text.font = _ctx.Font;
            text.text = RevnixPaywallTags.Resolve(block.Label ?? "", pkg, _ctx.Packages);
            text.fontSize = (int)Math.Round(block.Style?.FontSize ?? 15);
            text.fontStyle = FontStyle.Bold;
            text.color = Color(block.Style?.TextColor, Color(_doc.AccentInk, UnityEngine.Color.white));
            text.alignment = TextAnchor.MiddleCenter;
            text.raycastTarget = false;

            var height = block.Style?.Height?.Px;
            Le(go).preferredHeight = height.HasValue ? (float)height.Value : 52f;
            ApplyBox(go, block.Style, paint: false, skipHeight: true);
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
            var color = Color(block.Style?.Fill, Color(_doc.TextColor, UnityEngine.Color.white));
            if (block.Style?.Fill == null) color.a *= 0.16f;
            image.color = color;
            image.raycastTarget = false;
            var height = block.Style?.Height?.Px ?? 1;
            Le(go).preferredHeight = (float)height;
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
            var highlightId = _ctx.SelectedPackageId;
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
            }
            ApplyBox(go, block.Style, paint: false);
            return go;
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
            if (block.Repeat == "packages")
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

                var selected = _ctx.SelectedPackageId
                    ?? (_ctx.Packages.Count > 0 ? _ctx.Packages[0].PackageId : null);
                if (_ctx.Packages.Count == 0)
                {
                    Container(block, wrapper.transform, null, block.Style);
                }
                else
                {
                    foreach (var each in _ctx.Packages)
                    {
                        var style = each.PackageId == selected
                            ? (block.Style ?? new BlockStyle()).Merging(block.SelectedStyle)
                            : block.Style;
                        Container(block, wrapper.transform, each, style);
                    }
                }
                return wrapper;
            }

            // A card that names a package the offering does not reach is
            // dropped rather than drawn with unresolved tags.
            if (block.PackageIndex.HasValue && block.PackageIndex.Value >= _ctx.Packages.Count) return null;
            var ctxPackage = block.PackageIndex.HasValue
                ? _ctx.Packages[block.PackageIndex.Value]
                : pkg;
            return Container(block, parent, ctxPackage, block.Style);
        }

        private GameObject Container(PaywallBlock block, Transform parent, BlockPackage pkg, BlockStyle style)
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
                    var rendered = Render(child, go.transform, pkg);
                    if (rendered != null) PlaceInStack(rendered, child.Style);
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
                    var rendered = Render(child, go.transform, pkg);
                    if (rendered != null) ApplyFlex(rendered, child.Style, horizontal: true);
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
                    var rendered = Render(child, go.transform, pkg);
                    if (rendered != null) ApplyFlex(rendered, child.Style, horizontal: false);
                }
            }
            ApplyBox(go, style, paint: true);
            return go;
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

        /// <summary>Stack placement — `inset` fills the parent, the offsets pin
        /// an edge. A percentage offset has no fixed pixel value, so it is left
        /// to the anchor rather than guessed.</summary>
        private static void PlaceInStack(GameObject go, BlockStyle style)
        {
            var rt = (RectTransform)go.transform;
            if (style == null) return;
            if (style.Inset == true)
            {
                Fill(rt);
                return;
            }
            var top = style.Top?.Px;
            var bottom = style.Bottom?.Px;
            var left = style.Left?.Px;
            var right = style.Right?.Px;
            if (!top.HasValue && !bottom.HasValue && !left.HasValue && !right.HasValue) return;

            var anchorX = left.HasValue ? 0f : (right.HasValue ? 1f : 0.5f);
            var anchorY = top.HasValue ? 1f : (bottom.HasValue ? 0f : 0.5f);
            rt.anchorMin = new Vector2(anchorX, anchorY);
            rt.anchorMax = new Vector2(anchorX, anchorY);
            rt.pivot = new Vector2(anchorX, anchorY);
            var x = left.HasValue ? (float)left.Value : (right.HasValue ? -(float)right.Value : 0f);
            var y = top.HasValue ? -(float)top.Value : (bottom.HasValue ? (float)bottom.Value : 0f);
            rt.anchoredPosition = new Vector2(x, y);
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
            text.color = Color(style?.TextColor, Color(_doc.TextColor, UnityEngine.Color.white));
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
                image.color = Color(style.Fill ?? style.BorderColor, UnityEngine.Color.clear);
                image.raycastTarget = false;
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
    /// Scales a `canvas` document to the width it is given.
    ///
    /// The design is authored against a fixed 393×852 screen; the whole tree is
    /// laid out at that size and then scaled, so absolute placement inside
    /// `stack` containers stays true on any device. The width is not known
    /// until layout runs, so the scale is applied every time the rect changes
    /// rather than computed once.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RevnixCanvasScaler : MonoBehaviour
    {
        public RectTransform Target;

        private RectTransform _self;
        private float _lastWidth = -1f;

        private void Awake() => _self = (RectTransform)transform;

        private void OnEnable() => Apply();

        private void OnRectTransformDimensionsChange() => Apply();

        private void Update()
        {
            // A canvas can be resized without the callback firing (a rotated
            // device, a resized editor game view), so the width is also checked
            // each frame — it is one float comparison.
            Apply();
        }

        private void Apply()
        {
            if (Target == null) return;
            if (_self == null) _self = (RectTransform)transform;
            var width = _self.rect.width;
            if (width <= 0f || Mathf.Approximately(width, _lastWidth)) return;
            _lastWidth = width;
            var scale = width / PaywallBlockDoc.CanvasWidth;
            Target.localScale = new Vector3(scale, scale, 1f);
        }
    }
}
