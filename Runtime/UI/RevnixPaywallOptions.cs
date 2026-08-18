using System;
using System.Collections.Generic;

namespace Revnix.Unity.UI
{
    /// <summary>
    /// Everything RevnixPaywallView.Create needs — the C# analogue of
    /// revnix-react's RevnixPaywallProps. The config decides template, copy,
    /// accent, badge, and highlight; the app supplies package titles/prices
    /// (from the store, localized) and the purchase handlers, so the display
    /// never disagrees with the charge.
    /// </summary>
    public sealed class RevnixPaywallOptions
    {
        public PaywallConfig Config;

        /// <summary>Purchasable rows, in display order. `PriceLabel` must be
        /// the store's localized price string.</summary>
        public List<RevnixPaywallPackage> Packages = new List<RevnixPaywallPackage>();

        /// <summary>Called with the selected packageId when the CTA is pressed.</summary>
        public Action<string> OnPurchase;

        public Action<string> OnSelectPackage;

        /// <summary>Footer handlers. Explicit handlers win over config URLs —
        /// the app knows best how to open its own legal pages; a config URL is
        /// the no-handler fallback (opened via Application.OpenURL).</summary>
        public Action OnRestore;
        public Action OnTerms;
        public Action OnPrivacy;

        /// <summary>Partial theme override; null fields inherit the config's
        /// dark/light base scheme.</summary>
        public RevnixPaywallTheme Theme;

        /// <summary>Controlled selection; leave null to let the paywall manage
        /// it (initial selection is the config's highlight package, else the
        /// first package). The view's SelectedPackageId setter is the same
        /// control after Create.</summary>
        public string SelectedPackageId;

        /// <summary>When given, the paywall reports one paywall.viewed per
        /// Create (REV-094) — the analytics funnel's "Paywall displayed"
        /// stage. Failures are swallowed into the client's diagnostics.</summary>
        public RevnixClient Client;

        /// <summary>Placement/paywall attribution attached to the view report.</summary>
        public string PlacementKey;
        public string PaywallId;

        /// <summary>Opt out of the automatic view report while still passing
        /// `Client`.</summary>
        public bool DisableViewTracking;
    }
}
