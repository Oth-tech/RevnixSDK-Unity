using System;
using System.Collections.Generic;
using System.Globalization;

namespace Revnix
{
    /// <summary>Colors the paywall renders with. Defaults mirror the dashboard
    /// preview chrome (a fixed dark screen) — override to match the host app's
    /// theme. On an override passed to ResolveTheme, null fields inherit the
    /// base scheme (the partial-override semantics of revnix-react's `theme`
    /// prop).</summary>
    public sealed class RevnixPaywallTheme
    {
        public string Background;
        public string TextPrimary;
        public string TextSecondary;
        public string TextFaint;
        public string Border;

        /// <summary>Text color on accent-filled surfaces (CTA, badge).</summary>
        public string AccentInk;

        /// <summary>Base theme for dark configs — and legacy configs without a
        /// mode. Keep in lockstep with revnix-react's DEFAULT_PAYWALL_THEME.</summary>
        public static RevnixPaywallTheme Dark() => new RevnixPaywallTheme
        {
            Background = "#0f1116",
            TextPrimary = "#ffffff",
            TextSecondary = "#9aa0a8",
            TextFaint = "#6b7078",
            Border = "#2a2e36",
            AccentInk = "#0a0b0d",
        };

        /// <summary>Base theme when the dashboard config sets `mode: "light"`.
        /// Keep in lockstep with revnix-react's LIGHT_PAYWALL_THEME (and the
        /// builder preview's SCREEN_PALETTES).</summary>
        public static RevnixPaywallTheme Light() => new RevnixPaywallTheme
        {
            Background = "#ffffff",
            TextPrimary = "#16181d",
            TextSecondary = "#5b6068",
            TextFaint = "#9aa0a8",
            Border = "#e2e5ea",
            AccentInk = "#ffffff",
        };
    }

    /// <summary>One purchasable row. `PriceLabel` must come from the store
    /// (localized) — the display must never disagree with the charge.</summary>
    public sealed class RevnixPaywallPackage
    {
        public string PackageId;
        public string Title;
        public string PriceLabel;

        /// <summary>Renewal cycle from the product ("annual", "monthly",
        /// "weekly", …). Drives the {period} / {period_short} tags on a
        /// designed paywall; null for lifetime and one-time products.</summary>
        public string Period;

        /// <summary>The store's price in MINOR units, with its currency — what
        /// {price_per_month} and {save_percent} are computed from. Leave them
        /// null and those tags stay visible rather than resolving to a wrong
        /// number; see <see cref="RevnixPaywallTags.MinorUnits"/> before
        /// converting from major units.</summary>
        public long? AmountMinor;

        public string Currency;

        /// <summary>REV-263: the catalog product behind this package. Only
        /// telemetry reads it — a Selected or PurchaseStarted report names the
        /// plan the way the rest of the ledger does. Optional: without it the
        /// interaction is still reported, just with no plan attached.</summary>
        public string ProductId;
    }

    /// <summary>The screen structures ResolveLayout maps a config's template
    /// string onto. "focus" | "feature-list" | "minimal" are the original
    /// three and render byte-identically to earlier SDK versions; the newer
    /// layouts are distinct screen structures the dashboard's template gallery
    /// presets over. An unrecognized template (config published by a newer
    /// dashboard) resolves to Focus — the classic structure — instead of
    /// rendering nothing.</summary>
    public enum RevnixPaywallLayout
    {
        Focus,
        FeatureList,
        Minimal,
        Hero,
        Timeline,
        Plans,
        FeatureGrid,
        Offer,
        Reveal,
    }

    /// <summary>One resolved footer link. Explicit host handlers win over
    /// config URLs — the app knows best how to open its own legal pages
    /// (in-app browser etc.); the URL is the no-handler fallback. `Url` is
    /// null when a handler is present or the config has no usable URL; an
    /// item with neither still renders, inert.</summary>
    public sealed class RevnixPaywallFooterItem
    {
        public string Label;
        public bool HasHandler;
        public string Url;

        public bool IsInteractive => HasHandler || Url != null;
    }

    /// <summary>
    /// Engine-free half of the paywall renderer — every decision RevnixPaywall
    /// (revnix-react/ui) makes that is not literally drawing pixels, ported
    /// 1:1 so the UGUI view (`Revnix.Unity.UI.RevnixPaywallView`) stays a dumb
    /// projection of these answers. RevnixPaywall.tsx is the behavioral spec;
    /// keep the two in lockstep.
    /// </summary>
    public static class RevnixPaywallLogic
    {
        public const string DefaultAccent = "#6478ff";

        // Soft card surface used by the feature-grid / reveal / review blocks.
        // Not part of the public theme — derived from the config's mode, in
        // lockstep with the dashboard preview's SCREEN_PALETTES.card.
        public const string CardBackgroundDark = "#181b22";
        public const string CardBackgroundLight = "#f4f5f7";

        // Currency symbols the anchor-price guard recognizes (majors; a
        // symbol-less anchor can't be judged and renders as entered). Same set
        // as revnix-react's CURRENCY_SYMBOL regex.
        private static readonly char[] CurrencySymbols =
            { '$', '€', '£', '¥', '₹', '₩', '₽', '₺', '₫', '₪', '฿', '₴', '₦', '₱' };

        /// <summary>JS truthiness for the config's optional strings — the TSX
        /// renderer gates blocks on `config.x ?`, where both undefined and ""
        /// are falsy.</summary>
        public static bool Truthy(string value) => !string.IsNullOrEmpty(value);

        /// <summary>Template string → layout. Unknown/null templates fall back
        /// to the classic structure (Focus), never to nothing.</summary>
        public static RevnixPaywallLayout ResolveLayout(string template)
        {
            switch (template)
            {
                case "feature-list": return RevnixPaywallLayout.FeatureList;
                case "minimal": return RevnixPaywallLayout.Minimal;
                case "hero": return RevnixPaywallLayout.Hero;
                case "timeline": return RevnixPaywallLayout.Timeline;
                case "plans": return RevnixPaywallLayout.Plans;
                case "feature-grid": return RevnixPaywallLayout.FeatureGrid;
                case "offer": return RevnixPaywallLayout.Offer;
                case "reveal": return RevnixPaywallLayout.Reveal;
                default: return RevnixPaywallLayout.Focus;
            }
        }

        /// <summary>True only for an explicit `mode: "light"`; absent (legacy
        /// config) = dark.</summary>
        public static bool IsLight(PaywallConfig config) => config.Mode == "light";

        /// <summary>Base scheme from the dashboard config; the host app's
        /// explicit theme override wins on top, field by field (null override
        /// fields inherit).</summary>
        public static RevnixPaywallTheme ResolveTheme(PaywallConfig config, RevnixPaywallTheme overrides)
        {
            var theme = IsLight(config) ? RevnixPaywallTheme.Light() : RevnixPaywallTheme.Dark();
            if (overrides != null)
            {
                if (overrides.Background != null) theme.Background = overrides.Background;
                if (overrides.TextPrimary != null) theme.TextPrimary = overrides.TextPrimary;
                if (overrides.TextSecondary != null) theme.TextSecondary = overrides.TextSecondary;
                if (overrides.TextFaint != null) theme.TextFaint = overrides.TextFaint;
                if (overrides.Border != null) theme.Border = overrides.Border;
                if (overrides.AccentInk != null) theme.AccentInk = overrides.AccentInk;
            }
            return theme;
        }

        /// <summary>Accent hex — config wins, absent falls back to the Revnix
        /// default. Nullish semantics (`config.accent ?? DEFAULT_ACCENT`): an
        /// empty string passes through, matching the TSX renderer.</summary>
        public static string Accent(PaywallConfig config) => config.Accent ?? DefaultAccent;

        public static string CardBackground(PaywallConfig config)
            => IsLight(config) ? CardBackgroundLight : CardBackgroundDark;

        /// <summary>Accent at 0x26 alpha — icon tiles, timeline dots, reveal
        /// chips (the TSX `${accent}26` tint).</summary>
        public static string AccentTint26(string accent) => accent + "26";

        /// <summary>Accent at 0x40 alpha — timeline rail, inactive progress
        /// dots (the TSX `${accent}40` tint).</summary>
        public static string AccentTint40(string accent) => accent + "40";

        /// <summary>Same template semantics as the dashboard preview:
        /// "minimal" shows only the highlighted package (when one is set);
        /// every other layout shows all packages.</summary>
        public static List<RevnixPaywallPackage> VisiblePackages(
            PaywallConfig config, IReadOnlyList<RevnixPaywallPackage> packages)
        {
            var shown = new List<RevnixPaywallPackage>();
            if (packages == null) return shown;
            var minimalOnly = ResolveLayout(config.Template) == RevnixPaywallLayout.Minimal
                && Truthy(config.HighlightPackageId);
            foreach (var pkg in packages)
            {
                if (!minimalOnly || pkg.PackageId == config.HighlightPackageId) shown.Add(pkg);
            }
            return shown;
        }

        /// <summary>Plans layout: columns stay readable up to 3 — beyond that
        /// the layout falls back to stacked rows (never drop a purchasable
        /// package), same rule as the dashboard preview.</summary>
        public static bool UsePlanColumns(int shownCount) => shownCount <= 3;

        /// <summary>Initial/uncontrolled selection: the config's highlight
        /// package when visible, else the first visible package, else null.</summary>
        public static string FallbackSelection(PaywallConfig config, IReadOnlyList<RevnixPaywallPackage> shown)
        {
            if (shown == null || shown.Count == 0) return null;
            foreach (var pkg in shown)
            {
                if (pkg.PackageId == config.HighlightPackageId) return pkg.PackageId;
            }
            return shown[0].PackageId;
        }

        /// <summary>Selection resolution, in the TSX renderer's order: a
        /// controlled id (non-null, nullish semantics) wins outright; else an
        /// internal (tapped) selection counts only while it is still visible;
        /// else the fallback.</summary>
        public static string ResolveSelection(
            PaywallConfig config,
            IReadOnlyList<RevnixPaywallPackage> shown,
            string controlledId,
            string internalId)
        {
            if (controlledId != null) return controlledId;
            if (internalId != null && shown != null)
            {
                foreach (var pkg in shown)
                {
                    if (pkg.PackageId == internalId) return internalId;
                }
            }
            return FallbackSelection(config, shown);
        }

        /// <summary>The anchor is dashboard free text while `priceLabel` is
        /// the store's localized price — never pair them when their currency
        /// symbols disagree, or a EUR customer would see a struck-through USD
        /// anchor next to the real charge (the renderer's "display never
        /// disagrees with the charge" rule; kept in lockstep with the
        /// preview's anchorPriceFor). Symbol-less anchors can't be judged and
        /// pass through. Null when there is no anchor to show.</summary>
        public static string AnchorPriceFor(PaywallConfig config, string priceLabel)
        {
            var anchor = config.Offer != null ? config.Offer.StrikethroughPrice : null;
            if (!Truthy(anchor)) return null;
            var symbolAt = anchor.IndexOfAny(CurrencySymbols);
            if (symbolAt < 0) return anchor;
            return priceLabel != null && priceLabel.IndexOf(anchor[symbolAt]) >= 0 ? anchor : null;
        }

        /// <summary>Offer layout: the highlighted (else first) visible package
        /// renders as the spotlight card. Null when nothing is visible.</summary>
        public static RevnixPaywallPackage SpotlightPackage(
            PaywallConfig config, IReadOnlyList<RevnixPaywallPackage> shown)
        {
            if (shown == null || shown.Count == 0) return null;
            foreach (var pkg in shown)
            {
                if (pkg.PackageId == config.HighlightPackageId) return pkg;
            }
            return shown[0];
        }

        /// <summary>Footer links are dashboard-configured (config.footer); a
        /// legacy config without the field keeps the original always-on
        /// footer (all show flags default true). Explicit host handlers win
        /// over config URLs; a Restore item never has a URL.</summary>
        public static List<RevnixPaywallFooterItem> FooterItems(
            PaywallConfig config,
            bool hasRestoreHandler,
            bool hasTermsHandler,
            bool hasPrivacyHandler)
        {
            var footer = config.Footer;
            var items = new List<RevnixPaywallFooterItem>();
            if (footer == null || footer.ShowRestore)
            {
                items.Add(new RevnixPaywallFooterItem
                {
                    Label = "Restore",
                    HasHandler = hasRestoreHandler,
                    Url = null,
                });
            }
            if (footer == null || footer.ShowTerms)
            {
                items.Add(new RevnixPaywallFooterItem
                {
                    Label = "Terms",
                    HasHandler = hasTermsHandler,
                    Url = !hasTermsHandler && footer != null && Truthy(footer.TermsUrl)
                        ? footer.TermsUrl
                        : null,
                });
            }
            if (footer == null || footer.ShowPrivacy)
            {
                items.Add(new RevnixPaywallFooterItem
                {
                    Label = "Privacy",
                    HasHandler = hasPrivacyHandler,
                    Url = !hasPrivacyHandler && footer != null && Truthy(footer.PrivacyUrl)
                        ? footer.PrivacyUrl
                        : null,
                });
            }
            return items;
        }

        /// <summary>Social proof card renders when a review is configured with
        /// a rating or a quote; author/count alone don't earn the card.</summary>
        public static bool HasReviewCard(PaywallConfig config)
            => config.Review != null
                && (config.Review.Rating.HasValue || Truthy(config.Review.Quote));

        /// <summary>Stars colored active for n &lt;= round(rating). JS
        /// Math.round semantics — halves round toward +∞, unlike C#'s
        /// banker's rounding (Math.round(2.5) is 3, not 2).</summary>
        public static int StarCount(double rating) => (int)Math.Floor(rating + 0.5);

        /// <summary>The numeric label next to the stars, formatted as JS
        /// `String(rating)`: invariant culture, no trailing ".0" on whole
        /// numbers (5 → "5", 4.5 → "4.5").</summary>
        public static string RatingLabel(double rating)
            => rating.ToString(CultureInfo.InvariantCulture);
    }
}
