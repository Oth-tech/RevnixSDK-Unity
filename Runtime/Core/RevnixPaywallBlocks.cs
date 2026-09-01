// The paywall block model — a portable description of a designed paywall.
//
// A paywall built in the dashboard's block builder publishes a TREE of styled
// elements on `PaywallConfig.Blocks`, and that tree takes precedence over the
// classic `template` layouts. This file is the model, its parser, the tag
// resolver and the colour maths; the Unity UI renderer that draws it lives in
// Runtime/UI/RevnixPaywallBlockRenderer.cs.
//
// Mirrors revnix-app's src/lib/paywall-blocks/types.ts one-for-one. The two
// must stay in lockstep: the dashboard preview and this SDK renderer are two
// interpreters of the SAME document, and a field only one side knows is a
// design that ships looking different from the design that was approved.
//
// It lives in Core, which carries NO UnityEngine dependency, for the same
// reason the rest of Core does: it is ordinary C# that can be compiled and
// tested without the editor, and that is where the parsing bugs live.
//
// Parsing is deliberately TOTAL — nothing here throws. A shipped app cannot be
// patched from our side, so a document from a newer dashboard has to parse to
// "the parts this SDK understands": an unknown block type becomes
// PaywallBlockKind.Unknown and is skipped when drawing, leaving the rest of
// the screen intact.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Revnix
{
    /// <summary>
    /// A length the design may write either as a number of pixels or as a CSS
    /// string ("50%", "auto", "16/9").
    ///
    /// Kept as both so a percentage survives parsing instead of being dropped
    /// for not being a number — a rail sized at 78% of its parent is a real
    /// design, and silently discarding it would collapse the box.
    /// </summary>
    public struct BlockDimension
    {
        /// <summary>The value in px, when it is one.</summary>
        public double? Px;

        /// <summary>The raw string, when the design wrote one.</summary>
        public string Text;

        /// <summary>The value as a fraction of the parent, when a percentage.</summary>
        public double? Fraction
        {
            get
            {
                if (Text == null || !Text.EndsWith("%", StringComparison.Ordinal)) return null;
                var body = Text.Substring(0, Text.Length - 1);
                return double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                    ? n / 100
                    : (double?)null;
            }
        }

        public bool IsAuto => Text == "auto";

        /// <summary>An aspect ratio, from either a number or a "16/9" string.</summary>
        public double? Ratio
        {
            get
            {
                if (Px.HasValue) return Px.Value > 0 ? Px : null;
                if (Text == null) return null;
                var parts = Text.Split('/');
                if (parts.Length == 2
                    && double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                    && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
                    && h != 0)
                {
                    return w / h;
                }
                return double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var single)
                    ? single
                    : (double?)null;
            }
        }

        internal static BlockDimension? From(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.TryGetValue(key, out var v)) return null;
            if (v is long l) return new BlockDimension { Px = l };
            if (v is double d) return new BlockDimension { Px = d };
            if (v is string s)
            {
                if (s.EndsWith("px", StringComparison.Ordinal)
                    && double.TryParse(s.Substring(0, s.Length - 2), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var px))
                {
                    return new BlockDimension { Px = px, Text = s };
                }
                return new BlockDimension { Text = s };
            }
            return null;
        }
    }

    /// <summary>
    /// Per-block visual style. Everything optional — a block draws sensibly
    /// with no style at all. Sizes are px; colors are any hex/rgb string, or a
    /// palette token (<c>@accent</c>, <c>@text</c>, <c>@bg</c>,
    /// <c>@accentInk</c>, optionally with an alpha percentage: <c>@text/12</c>).
    /// </summary>
    public sealed class BlockStyle
    {
        public string Fill;
        public string TextColor;

        /// <summary>0–100, like the dashboard's opacity inputs.</summary>
        public double? Opacity;

        public string BorderColor;
        public double? BorderWidth;

        /// <summary>Per-side rules, as a CSS border shorthand ("1px solid @text/12").</summary>
        public string BorderTop;
        public string BorderRight;
        public string BorderBottom;
        public string BorderLeft;

        public double? Radius;
        public double? Padding;
        public double? PaddingX;
        public double? PaddingY;
        public double? PaddingTop;
        public double? PaddingRight;
        public double? PaddingBottom;
        public double? PaddingLeft;
        public double? Margin;
        public BlockDimension? MarginTop;
        public BlockDimension? MarginRight;
        public BlockDimension? MarginBottom;
        public BlockDimension? MarginLeft;
        public double? FontSize;
        public double? FontWeight;

        /// <summary>A design font family name. Drawn only when the host project
        /// ships that font; otherwise the default face is used, so copy never
        /// vanishes.</summary>
        public string FontFamily;

        public string FontStyle;
        public string Align;

        /// <summary>In em, like CSS. Converted against the block's font size.</summary>
        public double? LetterSpacing;

        /// <summary>Unitless multiplier, like CSS.</summary>
        public double? LineHeight;

        public string TextTransform;
        public string Decoration;
        public bool? Nowrap;

        /// <summary>Gap between a container's children.</summary>
        public double? Gap;

        public BlockDimension? Height;
        public double? MinHeight;
        public BlockDimension? Width;
        public BlockDimension? MaxWidth;

        /// <summary>Width-to-height ratio; "16/9" strings are parsed.</summary>
        public BlockDimension? AspectRatio;

        /// <summary>flex-grow inside a row/column container.</summary>
        public double? Flex;

        /// <summary>flex-shrink; 0 stops a row item from being squashed.</summary>
        public double? Shrink;

        /// <summary>flex-basis in px.</summary>
        public double? Basis;

        public bool? Wrap;
        public string Justify;
        public string Items;
        public string SelfAlign;
        public string Shadow;
        public double? Blur;
        public double? Rotate;

        /// <summary>Placement inside a `stack` container. `inset` fills the
        /// stack; the individual offsets pin an edge. Ignored elsewhere.</summary>
        public bool? Inset;

        public BlockDimension? Top;
        public BlockDimension? Right;
        public BlockDimension? Bottom;
        public BlockDimension? Left;
        public double? ZIndex;
        public string Overflow;

        /// <summary>
        /// Merges another style over this one, field by field — how a plan
        /// card's <c>selectedStyle</c> is applied on top of its base style.
        /// </summary>
        public BlockStyle Merging(BlockStyle other)
        {
            if (other == null) return this;
            return new BlockStyle
            {
                Fill = other.Fill ?? Fill,
                TextColor = other.TextColor ?? TextColor,
                Opacity = other.Opacity ?? Opacity,
                BorderColor = other.BorderColor ?? BorderColor,
                BorderWidth = other.BorderWidth ?? BorderWidth,
                BorderTop = other.BorderTop ?? BorderTop,
                BorderRight = other.BorderRight ?? BorderRight,
                BorderBottom = other.BorderBottom ?? BorderBottom,
                BorderLeft = other.BorderLeft ?? BorderLeft,
                Radius = other.Radius ?? Radius,
                Padding = other.Padding ?? Padding,
                PaddingX = other.PaddingX ?? PaddingX,
                PaddingY = other.PaddingY ?? PaddingY,
                PaddingTop = other.PaddingTop ?? PaddingTop,
                PaddingRight = other.PaddingRight ?? PaddingRight,
                PaddingBottom = other.PaddingBottom ?? PaddingBottom,
                PaddingLeft = other.PaddingLeft ?? PaddingLeft,
                Margin = other.Margin ?? Margin,
                MarginTop = other.MarginTop ?? MarginTop,
                MarginRight = other.MarginRight ?? MarginRight,
                MarginBottom = other.MarginBottom ?? MarginBottom,
                MarginLeft = other.MarginLeft ?? MarginLeft,
                FontSize = other.FontSize ?? FontSize,
                FontWeight = other.FontWeight ?? FontWeight,
                FontFamily = other.FontFamily ?? FontFamily,
                FontStyle = other.FontStyle ?? FontStyle,
                Align = other.Align ?? Align,
                LetterSpacing = other.LetterSpacing ?? LetterSpacing,
                LineHeight = other.LineHeight ?? LineHeight,
                TextTransform = other.TextTransform ?? TextTransform,
                Decoration = other.Decoration ?? Decoration,
                Nowrap = other.Nowrap ?? Nowrap,
                Gap = other.Gap ?? Gap,
                Height = other.Height ?? Height,
                MinHeight = other.MinHeight ?? MinHeight,
                Width = other.Width ?? Width,
                MaxWidth = other.MaxWidth ?? MaxWidth,
                AspectRatio = other.AspectRatio ?? AspectRatio,
                Flex = other.Flex ?? Flex,
                Shrink = other.Shrink ?? Shrink,
                Basis = other.Basis ?? Basis,
                Wrap = other.Wrap ?? Wrap,
                Justify = other.Justify ?? Justify,
                Items = other.Items ?? Items,
                SelfAlign = other.SelfAlign ?? SelfAlign,
                Shadow = other.Shadow ?? Shadow,
                Blur = other.Blur ?? Blur,
                Rotate = other.Rotate ?? Rotate,
                Inset = other.Inset ?? Inset,
                Top = other.Top ?? Top,
                Right = other.Right ?? Right,
                Bottom = other.Bottom ?? Bottom,
                Left = other.Left ?? Left,
                ZIndex = other.ZIndex ?? ZIndex,
                Overflow = other.Overflow ?? Overflow,
            };
        }

        /// <summary>
        /// Reads a style object. Every field is read independently and only
        /// when its type matches — RevnixJson's accessors answer with the
        /// fallback rather than throwing — which is what lets a design authored
        /// against a newer dashboard draw here minus the one effect this SDK
        /// does not know, rather than failing.
        /// </summary>
        public static BlockStyle FromJson(Dictionary<string, object> map)
        {
            if (map == null) return null;
            return new BlockStyle
            {
                Fill = RevnixJson.GetString(map, "fill"),
                TextColor = RevnixJson.GetString(map, "textColor"),
                Opacity = RevnixJson.GetNullableDouble(map, "opacity"),
                BorderColor = RevnixJson.GetString(map, "borderColor"),
                BorderWidth = RevnixJson.GetNullableDouble(map, "borderWidth"),
                BorderTop = RevnixJson.GetString(map, "borderTop"),
                BorderRight = RevnixJson.GetString(map, "borderRight"),
                BorderBottom = RevnixJson.GetString(map, "borderBottom"),
                BorderLeft = RevnixJson.GetString(map, "borderLeft"),
                Radius = RevnixJson.GetNullableDouble(map, "radius"),
                Padding = RevnixJson.GetNullableDouble(map, "padding"),
                PaddingX = RevnixJson.GetNullableDouble(map, "paddingX"),
                PaddingY = RevnixJson.GetNullableDouble(map, "paddingY"),
                PaddingTop = RevnixJson.GetNullableDouble(map, "paddingTop"),
                PaddingRight = RevnixJson.GetNullableDouble(map, "paddingRight"),
                PaddingBottom = RevnixJson.GetNullableDouble(map, "paddingBottom"),
                PaddingLeft = RevnixJson.GetNullableDouble(map, "paddingLeft"),
                Margin = RevnixJson.GetNullableDouble(map, "margin"),
                MarginTop = BlockDimension.From(map, "marginTop"),
                MarginRight = BlockDimension.From(map, "marginRight"),
                MarginBottom = BlockDimension.From(map, "marginBottom"),
                MarginLeft = BlockDimension.From(map, "marginLeft"),
                FontSize = RevnixJson.GetNullableDouble(map, "fontSize"),
                FontWeight = RevnixJson.GetNullableDouble(map, "fontWeight"),
                FontFamily = RevnixJson.GetString(map, "fontFamily"),
                FontStyle = RevnixJson.GetString(map, "fontStyle"),
                Align = RevnixJson.GetString(map, "align"),
                LetterSpacing = RevnixJson.GetNullableDouble(map, "letterSpacing"),
                LineHeight = RevnixJson.GetNullableDouble(map, "lineHeight"),
                TextTransform = RevnixJson.GetString(map, "textTransform"),
                Decoration = RevnixJson.GetString(map, "decoration"),
                Nowrap = RevnixJson.GetNullableBool(map, "nowrap"),
                Gap = RevnixJson.GetNullableDouble(map, "gap"),
                Height = BlockDimension.From(map, "height"),
                MinHeight = RevnixJson.GetNullableDouble(map, "minHeight"),
                Width = BlockDimension.From(map, "width"),
                MaxWidth = BlockDimension.From(map, "maxWidth"),
                AspectRatio = BlockDimension.From(map, "aspectRatio"),
                Flex = RevnixJson.GetNullableDouble(map, "flex"),
                Shrink = RevnixJson.GetNullableDouble(map, "shrink"),
                Basis = RevnixJson.GetNullableDouble(map, "basis"),
                Wrap = RevnixJson.GetNullableBool(map, "wrap"),
                Justify = RevnixJson.GetString(map, "justify"),
                Items = RevnixJson.GetString(map, "items"),
                SelfAlign = RevnixJson.GetString(map, "selfAlign"),
                Shadow = RevnixJson.GetString(map, "shadow"),
                Blur = RevnixJson.GetNullableDouble(map, "blur"),
                Rotate = RevnixJson.GetNullableDouble(map, "rotate"),
                Inset = RevnixJson.GetNullableBool(map, "inset"),
                Top = BlockDimension.From(map, "top"),
                Right = BlockDimension.From(map, "right"),
                Bottom = BlockDimension.From(map, "bottom"),
                Left = BlockDimension.From(map, "left"),
                ZIndex = RevnixJson.GetNullableDouble(map, "zIndex"),
                Overflow = RevnixJson.GetString(map, "overflow"),
            };
        }
    }

    /// <summary>Which kind of node a <see cref="PaywallBlock"/> is.</summary>
    public enum PaywallBlockKind
    {
        /// <summary>A block type this SDK does not know. Skipped when drawing.</summary>
        Unknown = 0,
        Text,
        Image,
        List,
        Products,
        Button,
        Links,
        Line,
        Spacer,
        Card,
    }

    /// <summary>One entry of a list block.</summary>
    public sealed class BlockListItem
    {
        public string Icon;
        public string Title;
        public string Description;
    }

    /// <summary>
    /// One node of the tree.
    ///
    /// A single type with a <see cref="Kind"/> discriminator rather than a
    /// class hierarchy: the renderer switches on the kind, and a block type
    /// introduced after this SDK shipped lands as
    /// <see cref="PaywallBlockKind.Unknown"/> and is skipped — the screen
    /// loses that one element rather than failing to draw.
    /// </summary>
    public sealed class PaywallBlock
    {
        public PaywallBlockKind Kind = PaywallBlockKind.Unknown;
        public string Id = "";
        public BlockStyle Style;

        // text / button
        public string Text;
        public string Label;

        // image
        public string Url;
        public string Shape;
        public string Fit;
        public string Placeholder;

        // list
        public List<BlockListItem> Items;
        public string IconColor;

        // products
        public string Direction;
        public string TitleTpl;
        public string PriceTpl;
        public string HighlightSub;
        public string BadgeText;
        public BlockStyle CardStyle;
        public BlockStyle HighlightStyle;

        // links
        public bool? ShowRestore;
        public bool? ShowTerms;
        public bool? ShowPrivacy;
        public string TermsUrl;
        public string PrivacyUrl;

        // spacer
        public bool? Flex;

        // card
        public string Layout;

        /// <summary>Renders this container once per package ("packages").</summary>
        public string Repeat;

        /// <summary>Merged over Style on the package the customer selected.</summary>
        public BlockStyle SelectedStyle;

        /// <summary>"This card describes package N of the offering". A card
        /// whose index the offering does not reach is hidden.</summary>
        public int? PackageIndex;

        /// <summary>grid only; defaults to 2.</summary>
        public int? Columns;

        /// <summary>grid only — a CSS track list ("1fr 60px 66px").</summary>
        public string GridColumns;

        public List<PaywallBlock> Children;
    }

    /// <summary>
    /// The published document: screen palette plus the block tree.
    ///
    /// Build one with <see cref="Parse"/>, which never throws: a null result
    /// means "this is not a designed paywall", and the caller falls back to the
    /// classic layouts.
    /// </summary>
    public sealed class PaywallBlockDoc
    {
        /// <summary>The device screen `canvas` designs are authored against.</summary>
        public const float CanvasWidth = 393f;
        public const float CanvasHeight = 852f;

        public int Version = 1;

        /// <summary>"canvas" designs are authored against a fixed device screen
        /// and scale as a whole; "flow" designs lay out in a scrolling
        /// column.</summary>
        public string Layout;

        public string Background = "#000000";
        public string TextColor = "#FFFFFF";
        public string Accent = "#6478ff";
        public string AccentInk = "#FFFFFF";
        public string FontFamily;
        public List<PaywallBlock> Blocks = new List<PaywallBlock>();

        /// <summary>
        /// Turns the raw <c>config.blocks</c> into a document, or null when it
        /// is not one. Never throws — a malformed tree costs the DESIGN, and
        /// the caller still shows the classic paywall the customer can buy
        /// from.
        /// </summary>
        public static PaywallBlockDoc Parse(object value)
        {
            if (!(value is Dictionary<string, object> map)) return null;
            if (!map.TryGetValue("blocks", out var rawBlocks) || !(rawBlocks is List<object> list)) return null;
            if (list.Count == 0) return null;

            var doc = new PaywallBlockDoc
            {
                Version = (int)RevnixJson.GetLong(map, "version", 1),
                Layout = RevnixJson.GetString(map, "layout"),
                TextColor = RevnixJson.GetString(map, "textColor", "#FFFFFF"),
                Accent = RevnixJson.GetString(map, "accent", "#6478ff"),
                AccentInk = RevnixJson.GetString(map, "accentInk", "#FFFFFF"),
                FontFamily = RevnixJson.GetString(map, "fontFamily"),
            };
            // `background` is a plain string in the original form and an object
            // in the layered one; both reduce to the ground colour drawn here.
            doc.Background = RevnixJson.GetString(map, "background")
                ?? RevnixJson.GetString(RevnixJson.GetObject(map, "background"), "ground", "#000000");
            foreach (var item in list) doc.Blocks.Add(ParseBlock(item));
            return doc;
        }

        private static PaywallBlock ParseBlock(object value)
        {
            if (!(value is Dictionary<string, object> map)) return new PaywallBlock();
            var block = new PaywallBlock
            {
                Id = RevnixJson.GetString(map, "id", ""),
                Style = BlockStyle.FromJson(RevnixJson.GetObject(map, "style")),
            };
            switch (RevnixJson.GetString(map, "type"))
            {
                case "text":
                    block.Kind = PaywallBlockKind.Text;
                    block.Text = RevnixJson.GetString(map, "text", "");
                    break;
                case "image":
                    block.Kind = PaywallBlockKind.Image;
                    block.Url = RevnixJson.GetString(map, "url");
                    block.Shape = RevnixJson.GetString(map, "shape");
                    block.Fit = RevnixJson.GetString(map, "fit");
                    block.Placeholder = RevnixJson.GetString(map, "placeholder");
                    break;
                case "list":
                    block.Kind = PaywallBlockKind.List;
                    block.Items = new List<BlockListItem>();
                    foreach (var item in RevnixJson.GetList(map, "items"))
                    {
                        if (!(item is Dictionary<string, object> row)) continue;
                        block.Items.Add(new BlockListItem
                        {
                            Icon = RevnixJson.GetString(row, "icon"),
                            Title = RevnixJson.GetString(row, "title", ""),
                            Description = RevnixJson.GetString(row, "description"),
                        });
                    }
                    block.IconColor = RevnixJson.GetString(map, "iconColor");
                    break;
                case "products":
                    block.Kind = PaywallBlockKind.Products;
                    block.Direction = RevnixJson.GetString(map, "direction");
                    block.TitleTpl = RevnixJson.GetString(map, "titleTpl");
                    block.PriceTpl = RevnixJson.GetString(map, "priceTpl");
                    block.HighlightSub = RevnixJson.GetString(map, "highlightSub");
                    block.BadgeText = RevnixJson.GetString(map, "badgeText");
                    block.CardStyle = BlockStyle.FromJson(RevnixJson.GetObject(map, "cardStyle"));
                    block.HighlightStyle = BlockStyle.FromJson(RevnixJson.GetObject(map, "highlightStyle"));
                    break;
                case "button":
                    block.Kind = PaywallBlockKind.Button;
                    block.Label = RevnixJson.GetString(map, "label", "");
                    break;
                case "links":
                    block.Kind = PaywallBlockKind.Links;
                    block.ShowRestore = RevnixJson.GetNullableBool(map, "showRestore");
                    block.ShowTerms = RevnixJson.GetNullableBool(map, "showTerms");
                    block.ShowPrivacy = RevnixJson.GetNullableBool(map, "showPrivacy");
                    block.TermsUrl = RevnixJson.GetString(map, "termsUrl");
                    block.PrivacyUrl = RevnixJson.GetString(map, "privacyUrl");
                    break;
                case "line":
                    block.Kind = PaywallBlockKind.Line;
                    break;
                case "spacer":
                    block.Kind = PaywallBlockKind.Spacer;
                    block.Flex = RevnixJson.GetNullableBool(map, "flex");
                    break;
                case "card":
                    block.Kind = PaywallBlockKind.Card;
                    block.Layout = RevnixJson.GetString(map, "layout");
                    block.Repeat = RevnixJson.GetString(map, "repeat");
                    block.SelectedStyle = BlockStyle.FromJson(RevnixJson.GetObject(map, "selectedStyle"));
                    var index = RevnixJson.GetNullableLong(map, "packageIndex");
                    block.PackageIndex = index.HasValue ? (int)index.Value : (int?)null;
                    var columns = RevnixJson.GetNullableLong(map, "columns");
                    block.Columns = columns.HasValue ? (int)columns.Value : (int?)null;
                    block.GridColumns = RevnixJson.GetString(map, "gridColumns");
                    block.Children = new List<PaywallBlock>();
                    foreach (var child in RevnixJson.GetList(map, "children")) block.Children.Add(ParseBlock(child));
                    break;
                default:
                    // A block type from a newer dashboard. Skipped when drawing.
                    block.Kind = PaywallBlockKind.Unknown;
                    break;
            }
            return block;
        }
    }

    /// <summary>
    /// One purchasable row, as the block renderer needs it.
    ///
    /// The money fields are optional: without them the price tags stay visible
    /// rather than resolving to a number the store would not charge.
    /// </summary>
    public sealed class BlockPackage
    {
        public string PackageId;
        public string Title;
        public string PriceLabel;

        /// <summary>Renewal cycle from the product ("annual", "monthly", …).</summary>
        public string Period;

        public long? AmountMinor;
        public string Currency;
    }

    /// <summary>
    /// Tag variables — the vocabulary a designed paywall's copy uses to talk
    /// about the packages attached to it.
    /// </summary>
    public static class RevnixPaywallTags
    {
        /// <summary>Renewal cycles, in months. Lifetime and one-time products
        /// have no cycle.</summary>
        private static readonly Dictionary<string, double> Months = new Dictionary<string, double>
        {
            { "weekly", 1 / 4.345 },
            { "monthly", 1 },
            { "two_months", 2 },
            { "three_months", 3 },
            { "six_months", 6 },
            { "annual", 12 },
        };

        private static readonly Dictionary<string, string> PeriodWord = new Dictionary<string, string>
        {
            { "weekly", "week" }, { "monthly", "month" }, { "two_months", "2 months" },
            { "three_months", "3 months" }, { "six_months", "6 months" },
            { "annual", "year" }, { "lifetime", "lifetime" },
        };

        private static readonly Dictionary<string, string> PeriodShort = new Dictionary<string, string>
        {
            { "weekly", "wk" }, { "monthly", "mo" }, { "two_months", "2mo" },
            { "three_months", "3mo" }, { "six_months", "6mo" },
            { "annual", "yr" }, { "lifetime", "once" },
        };

        /// <summary>Currencies whose smallest unit IS the major unit.</summary>
        private static readonly HashSet<string> ZeroDecimal = new HashSet<string>
        {
            "BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW", "PYG",
            "RWF", "UGX", "UYI", "VND", "VUV", "XAF", "XOF", "XPF",
        };

        /// <summary>Currencies with three decimal places rather than two.</summary>
        private static readonly HashSet<string> ThreeDecimal = new HashSet<string>
        {
            "BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND",
        };

        /// <summary>
        /// Minor units per major unit.
        ///
        /// Not every currency is a hundredth: JPY and KRW have no minor unit at
        /// all, so dividing by 100 would understate a price by 100×, and the
        /// Gulf currencies have three. The table is ISO 4217's exponent for the
        /// exceptions; everything else is the usual hundredth.
        /// </summary>
        public static double MinorUnits(string currency)
        {
            if (string.IsNullOrEmpty(currency)) return 100;
            var code = currency.ToUpperInvariant();
            if (ZeroDecimal.Contains(code)) return 1;
            if (ThreeDecimal.Contains(code)) return 1000;
            return 100;
        }

        private static double? PerMonthMinor(BlockPackage pkg)
        {
            if (pkg?.Period == null || !pkg.AmountMinor.HasValue) return null;
            if (!Months.TryGetValue(pkg.Period, out var months) || months <= 0) return null;
            return pkg.AmountMinor.Value / months;
        }

        /// <summary>
        /// Formats a computed amount the way the STORE formatted this package's
        /// own price.
        ///
        /// The store already localized <c>PriceLabel</c> — symbol, placement,
        /// separators and all — so the per-month figure is produced by
        /// substituting the number inside that label rather than reformatting
        /// from scratch. That keeps "¥1,000" next to "¥12,000" instead of
        /// pairing it with a differently built string.
        /// </summary>
        private static string Money(double amountMinor, string currency, string priceLabel)
        {
            var per = MinorUnits(currency);
            var major = amountMinor / per;
            var decimals = per == 1 ? 0 : (per == 1000 ? 3 : 2);
            // A whole amount reads better without its ".00".
            var digits = Math.Abs(amountMinor % per) < 1e-9 ? 0 : decimals;

            if (!string.IsNullOrEmpty(priceLabel))
            {
                var start = -1;
                var end = -1;
                for (var i = 0; i < priceLabel.Length; i++)
                {
                    var c = priceLabel[i];
                    var isNumeric = char.IsDigit(c) || ((c == '.' || c == ',' || c == ' ') && start >= 0);
                    if (isNumeric)
                    {
                        if (start < 0) start = i;
                        if (char.IsDigit(c)) end = i;
                    }
                    else if (start >= 0)
                    {
                        break;
                    }
                }
                if (start >= 0 && end >= start)
                {
                    var sample = priceLabel.Substring(start, end - start + 1);
                    var grouped = Group(major, digits, SeparatorIn(sample));
                    return priceLabel.Substring(0, start) + grouped + priceLabel.Substring(end + 1);
                }
            }
            return major.ToString("F" + digits, CultureInfo.InvariantCulture) + " " + currency;
        }

        /// <summary>The thousands separator the store used, so the substituted
        /// number groups the same way.</summary>
        private static string SeparatorIn(string sample)
        {
            var comma = sample.LastIndexOf(',');
            var dot = sample.LastIndexOf('.');
            if (comma >= 0 && comma < dot) return ",";
            if (dot >= 0 && dot < comma) return ".";
            if (comma >= 0) return ",";
            return sample.IndexOf(' ') >= 0 ? " " : ",";
        }

        private static string Group(double value, int digits, string separator)
        {
            var fixedText = value.ToString("F" + digits, CultureInfo.InvariantCulture);
            var dot = fixedText.IndexOf('.');
            var intPart = dot < 0 ? fixedText : fixedText.Substring(0, dot);
            var rest = dot < 0 ? "" : fixedText.Substring(dot);
            var builder = new StringBuilder();
            for (var i = 0; i < intPart.Length; i++)
            {
                if (i > 0 && (intPart.Length - i) % 3 == 0) builder.Append(separator);
                builder.Append(intPart[i]);
            }
            return builder + rest;
        }

        /// <summary>
        /// Fills a copy template from one package. <paramref name="all"/> is the
        /// rest of the offering, which <c>{save_percent}</c> needs to have
        /// something to compare against.
        ///
        /// A tag this cannot answer from real package data is left in place,
        /// VISIBLE. That is deliberate: a design that says <c>{price}</c> and
        /// renders a stale sample is worse than one that visibly did not
        /// resolve.
        /// </summary>
        public static string Resolve(string text, BlockPackage pkg, IList<BlockPackage> all = null)
        {
            if (text == null || pkg == null || text.IndexOf('{') < 0) return text;
            var builder = new StringBuilder();
            var i = 0;
            while (i < text.Length)
            {
                var open = text.IndexOf('{', i);
                if (open < 0)
                {
                    builder.Append(text, i, text.Length - i);
                    break;
                }
                var close = text.IndexOf('}', open);
                if (close < 0)
                {
                    // An unclosed brace is copy, not a tag — leave the rest.
                    builder.Append(text, i, text.Length - i);
                    break;
                }
                builder.Append(text, i, open - i);
                var name = text.Substring(open + 1, close - open - 1);
                builder.Append(ResolveOne(name, "{" + name + "}", pkg, all));
                i = close + 1;
            }
            return builder.ToString();
        }

        private static string ResolveOne(string name, string raw, BlockPackage pkg, IList<BlockPackage> all)
        {
            switch (name)
            {
                case "title":
                    return pkg.Title ?? raw;
                case "price":
                    return pkg.PriceLabel ?? raw;
                case "period":
                    return pkg.Period != null && PeriodWord.TryGetValue(pkg.Period, out var word) ? word : raw;
                case "period_short":
                    return pkg.Period != null && PeriodShort.TryGetValue(pkg.Period, out var abbr) ? abbr : raw;
                case "price_per_month":
                {
                    var per = PerMonthMinor(pkg);
                    if (!per.HasValue || string.IsNullOrEmpty(pkg.Currency)) return raw;
                    return Money(Math.Round(per.Value), pkg.Currency, pkg.PriceLabel);
                }
                case "save_percent":
                {
                    var mine = PerMonthMinor(pkg);
                    if (!mine.HasValue || mine.Value <= 0) return raw;
                    var dearest = 0.0;
                    if (all != null)
                    {
                        foreach (var other in all)
                        {
                            var value = PerMonthMinor(other);
                            if (value.HasValue && value.Value > dearest) dearest = value.Value;
                        }
                    }
                    if (dearest <= mine.Value) return raw;
                    return (int)Math.Round((1 - mine.Value / dearest) * 100) + "%";
                }
                default:
                    // Unknown tag: leave it visible rather than guess.
                    return raw;
            }
        }
    }

    /// <summary>
    /// Colour handling for block paywalls — palette tokens and CSS colour
    /// strings, resolved to plain RGBA components so Core stays free of
    /// UnityEngine types.
    /// </summary>
    public static class RevnixBlockColor
    {
        /// <summary>An RGBA colour, each channel 0–1.</summary>
        public struct Rgba
        {
            public float R;
            public float G;
            public float B;
            public float A;
        }

        /// <summary>
        /// Expands a palette token (<c>@accent</c>, <c>@text/12</c>) and parses
        /// the result. Returns null for anything it cannot parse — a gradient, a
        /// named colour — so the caller keeps its own default rather than
        /// painting a wrong one.
        /// </summary>
        public static Rgba? Resolve(string value, PaywallBlockDoc doc)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var raw = value.Trim();
            if (raw.Length == 0) return null;
            var alpha = 1.0;
            if (raw[0] == '@')
            {
                var body = raw.Substring(1);
                var slash = body.IndexOf('/');
                var name = slash >= 0 ? body.Substring(0, slash) : body;
                if (slash >= 0
                    && double.TryParse(body.Substring(slash + 1), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var pct))
                {
                    alpha = Math.Max(0, Math.Min(100, pct)) / 100;
                }
                if (doc == null) return null;
                switch (name)
                {
                    case "accent": raw = doc.Accent; break;
                    case "accentInk": raw = doc.AccentInk; break;
                    case "bg": raw = doc.Background; break;
                    case "text": raw = doc.TextColor; break;
                    default: return null;
                }
            }
            var parsed = ParseColor(raw);
            if (!parsed.HasValue) return null;
            if (alpha == 1.0) return parsed;
            var c = parsed.Value;
            c.A = (float)Math.Max(0, Math.Min(1, c.A * alpha));
            return c;
        }

        /// <summary>Parses "#rgb", "#rrggbb", "#rrggbbaa", "rgb()" and "rgba()".</summary>
        public static Rgba? ParseColor(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var s = value.Trim();
            if (s.Length > 0 && s[0] == '#')
            {
                var hex = s.Substring(1);
                if (hex.Length == 3 || hex.Length == 4)
                {
                    var expanded = new StringBuilder();
                    foreach (var c in hex) expanded.Append(c).Append(c);
                    hex = expanded.ToString();
                }
                if (hex.Length != 6 && hex.Length != 8) return null;
                if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
                var hasAlpha = hex.Length == 8;
                return new Rgba
                {
                    R = (float)((v >> (hasAlpha ? 24 : 16)) & 0xFF) / 255f,
                    G = (float)((v >> (hasAlpha ? 16 : 8)) & 0xFF) / 255f,
                    B = (float)((v >> (hasAlpha ? 8 : 0)) & 0xFF) / 255f,
                    A = hasAlpha ? (float)(v & 0xFF) / 255f : 1f,
                };
            }
            if (!s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)) return null;
            var open = s.IndexOf('(');
            var close = s.IndexOf(')');
            if (open < 0 || close < 0 || close < open) return null;
            var parts = s.Substring(open + 1, close - open - 1)
                .Split(new[] { ',', '/', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return null;
            var numbers = new double[Math.Min(4, parts.Length)];
            for (var i = 0; i < numbers.Length; i++)
            {
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                {
                    return null;
                }
            }
            return new Rgba
            {
                R = (float)(numbers[0] / 255),
                G = (float)(numbers[1] / 255),
                B = (float)(numbers[2] / 255),
                A = numbers.Length > 3 ? (float)numbers[3] : 1f,
            };
        }
    }
}
