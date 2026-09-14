using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace Revnix.Unity.UI
{
    /// <summary>
    /// RevnixPaywallView — renders a published PaywallConfig exactly as the
    /// dashboard paywall-builder previews it. Faithful UGUI port of
    /// revnix-react's RevnixPaywall (RevnixPaywall.tsx is the byte-level
    /// spec; RevnixPaywallLogic carries the engine-free decisions).
    ///
    /// The whole hierarchy is generated at runtime — no prefabs, no bundled
    /// assets, and deliberately no TextMeshPro: legacy UnityEngine.UI.Text
    /// keeps the package dependency-free, at the cost of TMP's crisper
    /// rendering. Rounded corners come from procedurally generated 9-slice
    /// sprites (RevnixPaywallSprites).
    ///
    /// Usage:
    ///
    ///     var paywall = RevnixPaywallView.Create(canvas.transform, new RevnixPaywallOptions
    ///     {
    ///         Config = placement.Paywall.Config,
    ///         Packages = packagesFromStore,
    ///         OnPurchase = packageId => { … },
    ///         Client = revnix,
    ///         PlacementKey = "main_paywall",
    ///         PaywallId = placement.Paywall.PaywallId,
    ///     });
    ///
    /// The parent must live under a Canvas, and the scene needs an
    /// EventSystem for the buttons to receive taps. One paywall.viewed is
    /// reported per Create (a re-shown paywall is a genuine new display);
    /// report failures are swallowed into the client's diagnostics.
    /// </summary>
    public sealed class RevnixPaywallView : MonoBehaviour
    {
        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>Build a paywall under <paramref name="parent"/> and return
        /// the live view. Throws on a null parent/options/config.</summary>
        public static RevnixPaywallView Create(Transform parent, RevnixPaywallOptions options)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.Config == null) throw new ArgumentException("options.Config is required", nameof(options));
            var go = new GameObject("RevnixPaywall", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Fill((RectTransform)go.transform);
            var view = go.AddComponent<RevnixPaywallView>();
            view.Initialize(options);
            return view;
        }

        /// <summary>Controlled selection, RN semantics: a non-null value wins
        /// over taps (pair with OnSelectPackage to drive it); setting null
        /// hands control back to the view. The getter always returns the
        /// resolved selection.</summary>
        public string SelectedPackageId
        {
            get => RevnixPaywallLogic.ResolveSelection(_config, _shown, _controlledSelected, _internalSelected);
            set
            {
                _controlledSelected = value;
                // Same split as Select(): a designed paywall redraws, because
                // its selected treatment is structural and no selectables are
                // registered on that path.
                if (_blockPaywall) RebuildBlocks(); else RefreshSelection();
            }
        }

        /// <summary>Renders a spinner in the CTA and disables purchasing. A
        /// designed paywall redraws, since its loading state is structural
        /// (the button's label gives way to a spinner) — the same split as
        /// selection.</summary>
        public void SetLoading(bool loading)
        {
            _loading = loading;
            if (_blockPaywall)
            {
                RebuildBlocks();
                return;
            }
            if (_ctaLabel != null) _ctaLabel.SetActive(!loading);
            if (_ctaSpinner != null) _ctaSpinner.SetActive(loading);
            RefreshSelection();
        }

        /// <summary>Tear the paywall down (destroys the whole GameObject).</summary>
        public void Dismiss()
        {
            if (gameObject != null) Destroy(gameObject);
        }

        // ── State ────────────────────────────────────────────────────────────

        private RevnixPaywallOptions _options;
        private PaywallConfig _config;
        private RevnixPaywallLayout _layout;
        private List<RevnixPaywallPackage> _shown;
        private Font _font;

        private Color _background;
        private Color _textPrimary;
        private Color _textSecondary;
        private Color _textFaint;
        private Color _borderColor;
        private Color _accentInk;
        private Color _accent;
        private Color _accentTint26;
        private Color _accentTint40;
        private Color _cardBg;

        private bool _loading;
        private string _controlledSelected;
        private string _internalSelected;

        /// <summary>Whether the view drew a designed (block) paywall.</summary>
        private bool _blockPaywall;

        private sealed class SelectableRecord
        {
            public string PackageId;
            public Image Border;
            public int Radius;
            /// <summary>Spotlight keeps borderWidth 2 either way; only the
            /// color tracks selection.</summary>
            public bool AlwaysThick;
        }

        private readonly List<SelectableRecord> _selectables = new List<SelectableRecord>();
        private Button _ctaButton;
        private GameObject _ctaLabel;
        private GameObject _ctaSpinner;

        private readonly List<RawImage> _heroTargets = new List<RawImage>();
        private UnityWebRequest _heroRequest;
        private Texture2D _heroTexture;
        private bool _heroLoadStarted;

        // ── Setup ────────────────────────────────────────────────────────────

        private void Initialize(RevnixPaywallOptions options)
        {
            _options = options;
            _config = options.Config;
            _layout = RevnixPaywallLogic.ResolveLayout(_config.Template);
            _shown = RevnixPaywallLogic.VisiblePackages(_config, options.Packages);
            _controlledSelected = options.SelectedPackageId;
            _font = LoadLegacyFont();

            var theme = RevnixPaywallLogic.ResolveTheme(_config, options.Theme);
            var accentHex = RevnixPaywallLogic.Accent(_config);
            _background = Hex(theme.Background, new Color(0.06f, 0.07f, 0.09f));
            _textPrimary = Hex(theme.TextPrimary, Color.white);
            _textSecondary = Hex(theme.TextSecondary, Color.gray);
            _textFaint = Hex(theme.TextFaint, Color.gray);
            _borderColor = Hex(theme.Border, Color.gray);
            _accentInk = Hex(theme.AccentInk, Color.black);
            _accent = Hex(accentHex, Hex(RevnixPaywallLogic.DefaultAccent, Color.blue));
            _accentTint26 = Hex(RevnixPaywallLogic.AccentTint26(accentHex), WithAlpha(_accent, 0x26 / 255f));
            _accentTint40 = Hex(RevnixPaywallLogic.AccentTint40(accentHex), WithAlpha(_accent, 0x40 / 255f));
            _cardBg = Hex(RevnixPaywallLogic.CardBackground(_config), new Color(0.1f, 0.11f, 0.13f));

            // Precedence: a designed paywall (`config.Blocks`) wins over the
            // classic layouts below, which stay the fallback for every paywall
            // published before the block builder — so anything already live
            // draws unchanged.
            _blockPaywall = BuildBlocks();
            if (!_blockPaywall) BuildClassic();

            // One view per Create: re-creating (a re-shown paywall) is a
            // genuine new display; property updates are not (REV-094).
            if (options.Client != null && !options.DisableViewTracking)
            {
                _viewReport = options.Client.LogPaywallDisplay(
                    options.PlacementKey, options.PaywallId);
                // REV-263: an offering with nothing to sell is the one failure
                // the view can see by itself, and the one most worth knowing
                // about — the paywall drew, the player could not buy.
                if (options.Packages == null || options.Packages.Count == 0)
                {
                    ReportInteraction(
                        RevnixPaywallEvent.Error,
                        code: "no_products",
                        message: "paywall displayed with no packages");
                }
            }
        }

        /// <summary>The in-flight view beacon (REV-252). The close AWAITS this
        /// rather than reading an id off a field: the id only exists once the
        /// request returns, and a player who dismisses in that window would
        /// otherwise report a close with no id and lose the pairing.</summary>
        private Task<string> _viewReport;

        /// <summary>The classic `template` layout — the fallback for every
        /// paywall that is not a designed one, and for a designed one whose
        /// tree failed to build.</summary>
        private void BuildClassic()
        {
            _selectables.Clear();
            var column = BuildScaffold();
            BuildBody(column);
            RefreshSelection();
            BuildClassicClose();
        }

        // ——— REV-263: the interaction vocabulary ———
        //
        // The view reports what it genuinely OBSERVES: the selection change,
        // the CTA press, the restore press, and an offering that arrived with
        // nothing to sell. It never reports the purchase OUTCOME — the IAP
        // call happens in the game, so only the game knows whether the player
        // cancelled or the payment was refused. Report those with
        // Client.LogPaywallEvent(...) from your own Unity IAP callbacks.

        /// <summary>Rises per CTA press, so a retry after a failure is its own
        /// occurrence rather than a duplicate of the first try.</summary>
        private int _purchaseAttempts;

        private void ReportInteraction(
            RevnixPaywallEvent evt,
            string productId = null,
            string code = null,
            string message = null,
            string eventId = null)
        {
            if (_options.Client == null || _options.DisableViewTracking) return;
            var report = _viewReport;
            if (report == null) return;
            _ = ReportInteractionAsync(report, _options, evt, productId, code, message, eventId);
        }

        private static async Task ReportInteractionAsync(
            Task<string> viewReport,
            RevnixPaywallOptions options,
            RevnixPaywallEvent evt,
            string productId,
            string code,
            string message,
            string eventId)
        {
            // Awaiting the view beacon for the same reason the close does: an
            // interaction reported before the display id exists could not be
            // tied to the display it happened on.
            string viewId;
            try
            {
                viewId = await viewReport;
            }
            catch (Exception)
            {
                return;
            }
            if (viewId == null) return;
            await options.Client.LogPaywallEvent(
                evt,
                viewId,
                options.PlacementKey,
                options.PaywallId,
                productId,
                code,
                message,
                eventId == null ? null : viewId + ":" + eventId);
        }

        /// <summary>The catalog product behind a package, so a report names the
        /// plan the way the rest of the ledger does. Null when the offering did
        /// not carry one — reporting the package id instead would look like a
        /// product that does not exist.</summary>
        private string ProductIdFor(string packageId)
        {
            var packages = _options.Packages;
            if (packages == null) return null;
            foreach (var pkg in packages)
            {
                if (pkg != null && pkg.PackageId == packageId) return pkg.ProductId;
            }
            return null;
        }

        /// <summary>Every CTA path routes through here, so the start report can
        /// never be wired on one draw path and forgotten on the other.</summary>
        private void PurchaseAndReport(string packageId)
        {
            if (_options.PlacementKey == RevnixImplicitPlacements.PreviewPlacementKey)
            {
                Debug.LogWarning("Revnix: purchases are disabled in preview.");
                return;
            }
            _purchaseAttempts += 1;
            ReportInteraction(
                RevnixPaywallEvent.PurchaseStarted,
                ProductIdFor(packageId),
                eventId: "buy:" + _purchaseAttempts);
            _options.OnPurchase?.Invoke(packageId);
        }

        /// <summary>Restore — the report rides along with the host's
        /// handler.</summary>
        private void RestoreAndReport()
        {
            ReportInteraction(RevnixPaywallEvent.Restore);
            _options.OnRestore?.Invoke();
        }

        /// <summary>
        /// Runs the host's dismissal, reporting <c>paywall.closed</c> alongside
        /// it (REV-252). The host's callback runs FIRST and unconditionally:
        /// the beacon is best-effort, and an analytics failure must never be
        /// able to trap the player on the screen.
        /// </summary>
        private void CloseAndReport()
        {
            if (_options.OnClose != null) _options.OnClose();
            if (_options.Client == null || _options.DisableViewTracking) return;
            var report = _viewReport;
            if (report == null) return;
            _ = ReportClose(report, _options);
        }

        private static async Task ReportClose(Task<string> viewReport, RevnixPaywallOptions options)
        {
            // Awaiting the view beacon is what keeps the pair intact when the
            // player dismisses before it lands. It has usually finished long
            // ago, in which case this resumes immediately.
            string viewId;
            try
            {
                viewId = await viewReport;
            }
            catch (Exception)
            {
                return;
            }
            if (viewId == null) return;
            await options.Client.LogPaywallClosed(
                viewId, options.PlacementKey, options.PaywallId);
        }

        /// <summary>
        /// Draws a designed paywall, reporting whether it succeeded.
        ///
        /// Wrapped so a malformed document costs the paywall its DESIGN, not
        /// the purchase: if the tree fails to build, Initialize carries on into
        /// the classic layout, which is a working screen the customer can still
        /// buy from. A shipped app cannot be patched from our side, so the
        /// fallback matters more than the failure being loud.
        /// </summary>
        private bool BuildBlocks()
        {
            // REV-271: the language overlay is applied ONCE, here, so every
            // draw path below reads plain strings and none can forget to
            // localize one. A paywall with no translations returns itself.
            var doc = RevnixLocale.Localize(
                PaywallBlockDoc.Parse(_config.Blocks),
                _options.Locale ?? RevnixLocale.DeviceLocale());
            if (doc == null) return false;

            var packages = new List<BlockPackage>();
            foreach (var pkg in _options.Packages ?? new List<RevnixPaywallPackage>())
            {
                packages.Add(new BlockPackage
                {
                    PackageId = pkg.PackageId,
                    Title = pkg.Title,
                    PriceLabel = pkg.PriceLabel,
                    Period = pkg.Period,
                    AmountMinor = pkg.AmountMinor,
                    Currency = pkg.Currency,
                });
            }

            // The render contract's selection rule, so a tapped selection
            // survives the redraw: host → internal → highlight → first, each
            // counting only while it names an offered package.
            var selected = RevnixPaywallSelection.ResolveSelectedPackageId(
                packages, _controlledSelected, _internalSelected, _config.HighlightPackageId);

            try
            {
                new RevnixPaywallBlockRenderer(new BlockRenderContext
                {
                    Doc = doc,
                    Packages = packages,
                    SelectedPackageId = selected,
                    Loading = _loading,
                    HeroImageUrl = _config.HeroImageUrl,
                    FooterTermsUrl = _config.Footer != null ? _config.Footer.TermsUrl : null,
                    FooterPrivacyUrl = _config.Footer != null ? _config.Footer.PrivacyUrl : null,
                    OnPurchase = id =>
                    {
                        if (!_loading) PurchaseAndReport(id);
                    },
                    OnSelect = Select,
                    OnRestore = _options.OnRestore == null
                        ? null
                        : (Action)RestoreAndReport,
                    OnTerms = _options.OnTerms,
                    OnPrivacy = _options.OnPrivacy,
                    OnClose = _options.OnClose == null ? null : (Action)CloseAndReport,
                    OnDiagnostic = RenderDiagnostic(),
                    Font = _font,
                }).Build(transform);
                return true;
            }
            catch (Exception)
            {
                // Clear whatever was half-built before falling back, so the
                // classic layout does not draw on top of a partial design.
                // Detached first: Destroy only takes effect at the end of the
                // frame.
                for (var i = transform.childCount - 1; i >= 0; i--)
                {
                    var child = transform.GetChild(i).gameObject;
                    child.transform.SetParent(null, false);
                    Destroy(child);
                }
                return false;
            }
        }

        /// <summary>
        /// The renderer's diagnostic sink, or null when the host wired neither
        /// a client nor a callback — in which case building a message nobody
        /// will read is pure waste, and the null is what suppresses it.
        /// </summary>
        private Action<string> RenderDiagnostic()
        {
            var client = _options.Client;
            var explicitSink = _options.OnDiagnostic;
            if (client == null && explicitSink == null) return null;
            return message =>
            {
                if (client != null) client.ReportRenderDiagnostic(message);
                if (explicitSink != null) explicitSink(message);
            };
        }

        private void OnDestroy()
        {
            if (_heroRequest != null)
            {
                _heroRequest.Abort();
                _heroRequest.Dispose();
                _heroRequest = null;
            }
            if (_heroTexture != null)
            {
                Destroy(_heroTexture);
                _heroTexture = null;
            }
        }

        // ── Scaffold: root → ScrollRect → padded content → 440px column ──────

        private Transform BuildScaffold()
        {
            var rootImage = gameObject.AddComponent<Image>();
            rootImage.color = _background;

            var scrollGO = NewUI("Scroll", transform);
            Fill((RectTransform)scrollGO.transform);
            var scroll = scrollGO.AddComponent<ScrollRect>();
            scroll.horizontal = false;

            var viewport = NewUI("Viewport", scrollGO.transform);
            Fill((RectTransform)viewport.transform);
            viewport.AddComponent<RectMask2D>();

            var content = NewUI("Content", viewport.transform);
            var contentRt = (RectTransform)content.transform;
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.sizeDelta = Vector2.zero;
            contentRt.anchoredPosition = Vector2.zero;
            VColumn(content, new RectOffset(24, 24, 28, 32), 0f, TextAnchor.UpperCenter);
            content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = (RectTransform)viewport.transform;
            scroll.content = contentRt;
            var center = scrollGO.AddComponent<RevnixPaywallCenterOnShort>();
            center.Scroll = scroll;

            var columnGO = NewUI("Column", content.transform);
            VColumn(columnGO, null, 0f, TextAnchor.UpperLeft);
            var columnLe = Le(columnGO);
            var width = columnGO.AddComponent<RevnixPaywallColumnWidth>();
            width.Element = columnLe;
            width.Reference = contentRt;
            width.MaxWidth = 440f;
            width.HorizontalPadding = 48f;
            return columnGO.transform;
        }

        // ── Layout composition (same switch as the TSX renderer) ─────────────

        private void BuildBody(Transform col)
        {
            switch (_layout)
            {
                case RevnixPaywallLayout.Hero:
                    BuildHeroBanner(col);
                    BuildFeatureChecks(col);
                    BuildReviewCard(col);
                    BuildPackageRows(col, _shown, true);
                    BuildTail(col);
                    break;
                case RevnixPaywallLayout.Timeline:
                    BuildHeroOrIcon(col);
                    BuildHeadline(col);
                    BuildTimeline(col);
                    BuildReviewCard(col);
                    BuildPackageRows(col, _shown, true);
                    BuildTail(col);
                    break;
                case RevnixPaywallLayout.Plans:
                    BuildHeroOrIcon(col);
                    BuildHeadline(col);
                    if (RevnixPaywallLogic.UsePlanColumns(_shown.Count)) BuildPlanColumns(col);
                    else BuildPackageRows(col, _shown, true);
                    BuildFeatureChecks(col);
                    BuildReviewCard(col);
                    BuildTail(col);
                    break;
                case RevnixPaywallLayout.FeatureGrid:
                    BuildHeroOrIcon(col);
                    BuildHeadline(col);
                    BuildFeatureGrid(col);
                    BuildReviewCard(col);
                    BuildPackageRows(col, _shown, true);
                    BuildTail(col);
                    break;
                case RevnixPaywallLayout.Offer:
                    BuildHeroOrIcon(col);
                    BuildOfferPill(col);
                    BuildHeadline(col);
                    BuildOfferSpotlight(col);
                    BuildReviewCard(col);
                    BuildTail(col);
                    break;
                case RevnixPaywallLayout.Reveal:
                    BuildProgressDots(col);
                    BuildHeroOrIcon(col);
                    BuildHeadline(col);
                    BuildRevealCards(col);
                    BuildPackageRows(col, _shown, true);
                    BuildTail(col);
                    break;
                default:
                    // Focus | FeatureList | Minimal — the original structure,
                    // unchanged for legacy configs (review/offer blocks only
                    // exist when configured).
                    BuildHeroOrIcon(col);
                    BuildHeadline(col);
                    if (_layout == RevnixPaywallLayout.FeatureList) BuildFeatureBullets(col);
                    BuildReviewCard(col);
                    BuildPackageRows(col, _shown, true);
                    BuildTail(col);
                    break;
            }
        }

        /// <summary>Tail shared by every layout: urgency → CTA → count → footer.</summary>
        private void BuildTail(Transform col)
        {
            BuildUrgency(col);
            BuildCta(col);
            BuildCountLine(col);
            BuildFooter(col);
        }

        // ── Shared blocks (keep every block in lockstep with the same-named
        //    block in RevnixPaywall.tsx / PaywallPhonePreview.tsx) ────────────

        /// <summary>Classic header: hero image card (or accent icon tile).</summary>
        private void BuildHeroOrIcon(Transform col)
        {
            Spacer(col, 8f);
            if (RevnixPaywallLogic.Truthy(_config.HeroImageUrl))
            {
                var wrap = NewUI("HeroImage", col);
                var le = Le(wrap);
                le.preferredHeight = 180f;
                le.flexibleWidth = 1f;
                Panel(wrap, RevnixPaywallSprites.Rounded(16), Color.white, raycast: false);
                wrap.AddComponent<Mask>().showMaskGraphic = false;
                AddHeroTarget(wrap.transform);
            }
            else
            {
                var holder = NewUI("IconTileHolder", col);
                var le = Le(holder);
                le.preferredHeight = 64f;
                le.flexibleWidth = 1f;
                var tile = NewUI("IconTile", holder.transform);
                var rt = (RectTransform)tile.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(64f, 64f);
                Panel(tile, RevnixPaywallSprites.Rounded(16), _accentTint26, raycast: false);
                var glyph = MakeText(tile.transform, "◆", 27, false, _accent, TextAnchor.MiddleCenter, 0f, stretch: false);
                Fill((RectTransform)glyph.transform);
            }
            Spacer(col, 22f);
        }

        /// <summary>Centered headline/subheadline.</summary>
        private void BuildHeadline(Transform col)
        {
            MakeText(col, _config.Headline, 26, true, _textPrimary, TextAnchor.MiddleCenter);
            Spacer(col, 10f);
            if (RevnixPaywallLogic.Truthy(_config.Subheadline))
            {
                MakeText(col, _config.Subheadline, 15, false, _textSecondary, TextAnchor.MiddleCenter, 21f);
                Spacer(col, 26f);
            }
        }

        /// <summary>Hero layout banner: full-width media with a content-safe
        /// scrim overlay carrying the headline/subheadline (always light on
        /// scrim). Falls back to an accent field with the brand glyph when no
        /// hero image is set.</summary>
        private void BuildHeroBanner(Transform col)
        {
            Spacer(col, 8f);
            var hasImage = RevnixPaywallLogic.Truthy(_config.HeroImageUrl);
            var banner = NewUI("HeroBanner", col);
            var le = Le(banner);
            le.minHeight = 260f;
            le.flexibleWidth = 1f;
            Panel(banner, RevnixPaywallSprites.Rounded(20), hasImage ? Color.white : _accent, raycast: false);
            banner.AddComponent<Mask>().showMaskGraphic = !hasImage;
            if (hasImage)
            {
                AddHeroTarget(banner.transform);
            }
            else
            {
                // Brand glyph, centered above the scrim (TSX paddingBottom 72
                // shifts the optical center up by 36).
                var glyphGO = NewUI("Glyph", banner.transform);
                var grt = (RectTransform)glyphGO.transform;
                grt.anchorMin = grt.anchorMax = new Vector2(0.5f, 0.5f);
                grt.pivot = new Vector2(0.5f, 0.5f);
                grt.anchoredPosition = new Vector2(0f, 36f);
                grt.sizeDelta = new Vector2(96f, 96f);
                var glyph = MakeText(glyphGO.transform, "◆", 64, false,
                    new Color(1f, 1f, 1f, 0.35f), TextAnchor.MiddleCenter, 0f, stretch: false);
                Fill((RectTransform)glyph.transform);
                glyph.horizontalOverflow = HorizontalWrapMode.Overflow;
            }

            var scrim = NewUI("Scrim", banner.transform);
            var srt = (RectTransform)scrim.transform;
            srt.anchorMin = new Vector2(0f, 0f);
            srt.anchorMax = new Vector2(1f, 0f);
            srt.pivot = new Vector2(0.5f, 0f);
            srt.sizeDelta = Vector2.zero;
            srt.anchoredPosition = Vector2.zero;
            Panel(scrim, null, new Color(0f, 0f, 0f, 0.45f), raycast: false);
            VColumn(scrim, new RectOffset(18, 18, 16, 16), 4f, TextAnchor.UpperLeft);
            scrim.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            MakeText(scrim.transform, _config.Headline, 24, true, Color.white, TextAnchor.MiddleLeft);
            if (RevnixPaywallLogic.Truthy(_config.Subheadline))
            {
                MakeText(scrim.transform, _config.Subheadline, 14, false,
                    new Color(1f, 1f, 1f, 0.85f), TextAnchor.MiddleLeft, 19f);
            }
            Spacer(col, 22f);
        }

        /// <summary>Legacy feature bullets (feature-list layout).</summary>
        private void BuildFeatureBullets(Transform col)
        {
            if (_config.Features.Count == 0) return;
            var wrap = NewUI("Features", col);
            Le(wrap).flexibleWidth = 1f;
            VColumn(wrap, null, 14f, TextAnchor.UpperLeft);
            foreach (var feature in _config.Features)
            {
                var row = NewUI("Feature", wrap.transform);
                HRow(row, null, 12f, TextAnchor.UpperLeft);
                var icon = MakeText(row.transform, IconOrCheck(feature.Icon), 15, false,
                    _accent, TextAnchor.UpperLeft, 21f, stretch: false);
                var iconLe = Le(icon.gameObject);
                iconLe.minWidth = 24f;
                iconLe.preferredWidth = 24f;
                var body = NewUI("Body", row.transform);
                Le(body).flexibleWidth = 1f;
                VColumn(body, null, 0f, TextAnchor.UpperLeft);
                MakeText(body.transform, feature.Title, 16, true, _textPrimary, TextAnchor.MiddleLeft, 21f);
                if (RevnixPaywallLogic.Truthy(feature.Description))
                {
                    Spacer(body.transform, 1f);
                    MakeText(body.transform, feature.Description, 13, false, _textSecondary, TextAnchor.MiddleLeft, 18f);
                }
            }
            Spacer(col, 26f);
        }

        /// <summary>Compact single-line checks (hero banner body, plans
        /// checklist).</summary>
        private void BuildFeatureChecks(Transform col)
        {
            if (_config.Features.Count == 0) return;
            var wrap = NewUI("Checks", col);
            Le(wrap).flexibleWidth = 1f;
            VColumn(wrap, null, 10f, TextAnchor.UpperLeft);
            foreach (var feature in _config.Features)
            {
                var row = NewUI("Check", wrap.transform);
                HRow(row, null, 10f, TextAnchor.MiddleLeft);
                var icon = MakeText(row.transform, IconOrCheck(feature.Icon), 14, false,
                    _accent, TextAnchor.MiddleLeft, 0f, stretch: false);
                var iconLe = Le(icon.gameObject);
                iconLe.minWidth = 20f;
                iconLe.preferredWidth = 20f;
                MakeText(row.transform, feature.Title, 15, false, _textPrimary, TextAnchor.MiddleLeft, 20f);
            }
            Spacer(col, 24f);
        }

        /// <summary>Trial timeline: icon dots joined by an accent rail; the
        /// first step is filled solid ("you are here"), later steps are
        /// tinted.</summary>
        private void BuildTimeline(Transform col)
        {
            if (_config.Features.Count == 0) return;
            var wrap = NewUI("Timeline", col);
            Le(wrap).flexibleWidth = 1f;
            VColumn(wrap, null, 0f, TextAnchor.UpperLeft);
            for (var i = 0; i < _config.Features.Count; i++)
            {
                var feature = _config.Features[i];
                var first = i == 0;
                var last = i == _config.Features.Count - 1;
                var row = NewUI("TimelineRow", wrap.transform);
                HRow(row, null, 12f, TextAnchor.UpperLeft, expandHeight: true);

                var rail = NewUI("Rail", row.transform);
                var railLe = Le(rail);
                railLe.minWidth = 34f;
                railLe.preferredWidth = 34f;

                var dot = NewUI("Dot", rail.transform);
                var drt = (RectTransform)dot.transform;
                drt.anchorMin = drt.anchorMax = new Vector2(0.5f, 1f);
                drt.pivot = new Vector2(0.5f, 1f);
                drt.anchoredPosition = Vector2.zero;
                drt.sizeDelta = new Vector2(34f, 34f);
                Panel(dot, RevnixPaywallSprites.Circle(), first ? _accent : _accentTint26, raycast: false);
                var dotIcon = MakeText(dot.transform, IconOrCheck(feature.Icon), 14, false,
                    first ? _accentInk : _accent, TextAnchor.MiddleCenter, 0f, stretch: false);
                Fill((RectTransform)dotIcon.transform);

                if (!last)
                {
                    var line = NewUI("Line", rail.transform);
                    var lrt = (RectTransform)line.transform;
                    lrt.anchorMin = new Vector2(0.5f, 0f);
                    lrt.anchorMax = new Vector2(0.5f, 1f);
                    lrt.pivot = new Vector2(0.5f, 0f);
                    lrt.offsetMin = new Vector2(-1f, 4f);
                    lrt.offsetMax = new Vector2(1f, -38f); // below the 34px dot + 4px margin
                    Panel(line, null, _accentTint40, raycast: false);
                }

                var body = NewUI("Body", row.transform);
                Le(body).flexibleWidth = 1f;
                VColumn(body, new RectOffset(0, 0, 6, last ? 0 : 22), 0f, TextAnchor.UpperLeft);
                MakeText(body.transform, feature.Title, 16, true, _textPrimary, TextAnchor.MiddleLeft, 21f);
                if (RevnixPaywallLogic.Truthy(feature.Description))
                {
                    Spacer(body.transform, 1f);
                    MakeText(body.transform, feature.Description, 13, false, _textSecondary, TextAnchor.MiddleLeft, 18f);
                }
            }
            Spacer(col, 26f);
        }

        /// <summary>Feature grid: two-column soft cards with an accent icon
        /// tile each; an odd last card spans the full width.</summary>
        private void BuildFeatureGrid(Transform col)
        {
            if (_config.Features.Count == 0) return;
            var grid = NewUI("FeatureGrid", col);
            Le(grid).flexibleWidth = 1f;
            VColumn(grid, null, 10f, TextAnchor.UpperLeft);
            for (var i = 0; i < _config.Features.Count; i += 2)
            {
                var row = NewUI("GridRow", grid.transform);
                HRow(row, null, 10f, TextAnchor.UpperLeft, expandHeight: true);
                BuildGridCard(row.transform, _config.Features[i]);
                if (i + 1 < _config.Features.Count) BuildGridCard(row.transform, _config.Features[i + 1]);
            }
            Spacer(col, 24f);
        }

        private void BuildGridCard(Transform row, PaywallFeature feature)
        {
            var card = NewUI("GridCard", row);
            Le(card).flexibleWidth = 1f;
            Panel(card, RevnixPaywallSprites.Rounded(14), _cardBg, raycast: false);
            VColumn(card, new RectOffset(13, 13, 13, 13), 0f, TextAnchor.UpperLeft);

            var tileHolder = NewUI("IconTileHolder", card.transform);
            var holderLe = Le(tileHolder);
            holderLe.preferredHeight = 34f;
            holderLe.flexibleWidth = 1f;
            var tile = NewUI("IconTile", tileHolder.transform);
            var trt = (RectTransform)tile.transform;
            trt.anchorMin = trt.anchorMax = new Vector2(0f, 0.5f);
            trt.pivot = new Vector2(0f, 0.5f);
            trt.anchoredPosition = Vector2.zero;
            trt.sizeDelta = new Vector2(34f, 34f);
            Panel(tile, RevnixPaywallSprites.Rounded(10), _accentTint26, raycast: false);
            var icon = MakeText(tile.transform, IconOrCheck(feature.Icon), 15, false,
                _accent, TextAnchor.MiddleCenter, 0f, stretch: false);
            Fill((RectTransform)icon.transform);

            Spacer(card.transform, 9f);
            MakeText(card.transform, feature.Title, 14, true, _textPrimary, TextAnchor.MiddleLeft, 18f);
            if (RevnixPaywallLogic.Truthy(feature.Description))
            {
                Spacer(card.transform, 3f);
                MakeText(card.transform, feature.Description, 12, false, _textSecondary, TextAnchor.MiddleLeft, 16f);
            }
        }

        /// <summary>Reveal: 3 progress dots (first accent, rest tinted).</summary>
        private void BuildProgressDots(Transform col)
        {
            var row = NewUI("Dots", col);
            Le(row).flexibleWidth = 1f;
            HRow(row, null, 6f, TextAnchor.MiddleCenter);
            for (var i = 0; i < 3; i++)
            {
                var dot = NewUI("Dot", row.transform);
                var le = Le(dot);
                le.preferredWidth = 6f;
                le.preferredHeight = 6f;
                Panel(dot, RevnixPaywallSprites.Circle(), i == 0 ? _accent : _accentTint40, raycast: false);
            }
            Spacer(col, 18f);
        }

        /// <summary>Reveal: onboarding-style numbered benefit cards.</summary>
        private void BuildRevealCards(Transform col)
        {
            if (_config.Features.Count == 0) return;
            var wrap = NewUI("Reveal", col);
            Le(wrap).flexibleWidth = 1f;
            VColumn(wrap, null, 10f, TextAnchor.UpperLeft);
            for (var i = 0; i < _config.Features.Count; i++)
            {
                var feature = _config.Features[i];
                var card = NewUI("RevealCard", wrap.transform);
                Panel(card, RevnixPaywallSprites.Rounded(14), _cardBg, raycast: false);
                HRow(card, new RectOffset(14, 14, 14, 14), 12f, TextAnchor.UpperLeft);

                var chip = NewUI("Chip", card.transform);
                var chipLe = Le(chip);
                chipLe.minWidth = 26f;
                chipLe.preferredWidth = 26f;
                chipLe.preferredHeight = 26f;
                Panel(chip, RevnixPaywallSprites.Circle(), _accentTint26, raycast: false);
                var number = MakeText(chip.transform, (i + 1).ToString(), 13, true,
                    _accent, TextAnchor.MiddleCenter, 0f, stretch: false);
                Fill((RectTransform)number.transform);

                var body = NewUI("Body", card.transform);
                Le(body).flexibleWidth = 1f;
                VColumn(body, null, 0f, TextAnchor.UpperLeft);
                MakeText(body.transform, feature.Title, 15, true, _textPrimary, TextAnchor.MiddleLeft, 20f);
                if (RevnixPaywallLogic.Truthy(feature.Description))
                {
                    Spacer(body.transform, 1f);
                    MakeText(body.transform, feature.Description, 13, false, _textSecondary, TextAnchor.MiddleLeft, 18f);
                }
            }
            Spacer(col, 24f);
        }

        /// <summary>Social proof card: star row (+ numeric rating), quote,
        /// attribution.</summary>
        private void BuildReviewCard(Transform col)
        {
            if (!RevnixPaywallLogic.HasReviewCard(_config)) return;
            var review = _config.Review;
            var card = NewUI("ReviewCard", col);
            Le(card).flexibleWidth = 1f;
            Panel(card, RevnixPaywallSprites.Rounded(14), _cardBg, raycast: false);
            VColumn(card, new RectOffset(14, 14, 14, 14), 0f, TextAnchor.UpperCenter);

            if (review.Rating.HasValue)
            {
                var stars = NewUI("Stars", card.transform);
                HRow(stars, null, 2f, TextAnchor.MiddleCenter);
                var active = RevnixPaywallLogic.StarCount(review.Rating.Value);
                for (var n = 1; n <= 5; n++)
                {
                    MakeText(stars.transform, "★", 15, false,
                        n <= active ? _accent : _borderColor, TextAnchor.MiddleCenter, 0f, stretch: false);
                }
                SpacerW(stars.transform, 3f);
                MakeText(stars.transform, RevnixPaywallLogic.RatingLabel(review.Rating.Value),
                    13, true, _textPrimary, TextAnchor.MiddleCenter, 0f, stretch: false);
            }
            if (RevnixPaywallLogic.Truthy(review.Quote))
            {
                Spacer(card.transform, 8f);
                MakeText(card.transform, "“" + review.Quote + "”", 14, false,
                    _textPrimary, TextAnchor.MiddleCenter, 19f, stretch: true, italic: true);
            }
            if (RevnixPaywallLogic.Truthy(review.Author))
            {
                Spacer(card.transform, 6f);
                MakeText(card.transform, "— " + review.Author, 12, false,
                    _textSecondary, TextAnchor.MiddleCenter, 0f, stretch: false);
            }
            Spacer(col, 22f);
        }

        /// <summary>Small social-proof line under the CTA ("Join 2M+ users").</summary>
        private void BuildCountLine(Transform col)
        {
            var count = _config.Review != null ? _config.Review.Count : null;
            if (!RevnixPaywallLogic.Truthy(count)) return;
            MakeText(col, count, 13, false, _textSecondary, TextAnchor.MiddleCenter);
            Spacer(col, 14f);
        }

        /// <summary>Urgency line above the CTA.</summary>
        private void BuildUrgency(Transform col)
        {
            var urgency = _config.Offer != null ? _config.Offer.UrgencyText : null;
            if (!RevnixPaywallLogic.Truthy(urgency)) return;
            MakeText(col, urgency, 13, true, _accent, TextAnchor.MiddleCenter);
            Spacer(col, 10f);
        }

        /// <summary>Standard package rows. `withBadge=false` suppresses the
        /// row badge where the layout already presents the badge elsewhere
        /// (offer pill).</summary>
        private void BuildPackageRows(Transform col, List<RevnixPaywallPackage> list, bool withBadge)
        {
            var wrap = NewUI("Packages", col);
            Le(wrap).flexibleWidth = 1f;
            VColumn(wrap, null, 12f, TextAnchor.UpperLeft);
            foreach (var pkg in list)
            {
                BuildPackageRow(wrap.transform, pkg, withBadge);
            }
            Spacer(col, 22f);
        }

        private void BuildPackageRow(Transform parent, RevnixPaywallPackage pkg, bool withBadge)
        {
            var highlighted = pkg.PackageId == _config.HighlightPackageId;
            var row = NewUI("Package " + pkg.PackageId, parent);
            Le(row).flexibleWidth = 1f;
            var border = row.AddComponent<Image>();
            border.type = Image.Type.Sliced;
            AddSelectButton(row, border, pkg.PackageId);
            VColumn(row, new RectOffset(16, 16, 15, 15), 0f, TextAnchor.UpperLeft);

            MakeText(row.transform, pkg.Title, 16, true, _textPrimary, TextAnchor.MiddleLeft);
            Spacer(row.transform, 2f);

            // Anchor strikethrough renders on the highlighted package only —
            // and only past the currency-symbol guard.
            var anchor = highlighted ? RevnixPaywallLogic.AnchorPriceFor(_config, pkg.PriceLabel) : null;
            if (anchor != null)
            {
                var priceRow = NewUI("PriceRow", row.transform);
                HRow(priceRow, null, 6f, TextAnchor.LowerLeft);
                var anchorText = MakeText(priceRow.transform, anchor, 13, false,
                    _textFaint, TextAnchor.LowerLeft, 0f, stretch: false);
                Strikethrough(anchorText);
                MakeText(priceRow.transform, pkg.PriceLabel, 14, false,
                    _textSecondary, TextAnchor.LowerLeft, 0f, stretch: false);
            }
            else
            {
                MakeText(row.transform, pkg.PriceLabel, 14, false, _textSecondary, TextAnchor.MiddleLeft);
            }

            if (withBadge && highlighted && RevnixPaywallLogic.Truthy(_config.BadgeText))
            {
                BuildRowBadge(row);
            }

            _selectables.Add(new SelectableRecord
            {
                PackageId = pkg.PackageId,
                Border = border,
                Radius = 12,
            });
        }

        /// <summary>Badge pill overlapping the card's top-right corner. The
        /// pill takes no width constraint from the card, so it is clamped to
        /// 80% of the card and single-line truncated (RectMask2D).</summary>
        private void BuildRowBadge(GameObject row)
        {
            var badge = NewUI("Badge", row.transform);
            Le(badge).ignoreLayout = true;
            var rt = (RectTransform)badge.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-14f, 11f);
            Panel(badge, RevnixPaywallSprites.Rounded(10), _accent, raycast: false);
            badge.AddComponent<RectMask2D>();
            var label = MakeText(badge.transform, _config.BadgeText, 11, true,
                _accentInk, TextAnchor.MiddleCenter, 0f, stretch: false);
            Fill((RectTransform)label.transform);
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            var size = badge.AddComponent<RevnixPaywallPillSize>();
            size.Label = label;
            size.Reference = (RectTransform)row.transform;
            size.MaxFraction = 0.8f;
            size.PadX = 10f;
            size.PadY = 3f;
        }

        /// <summary>Plans layout: packages side by side as equal-width tier
        /// columns; the highlighted tier carries the badge pill inside the
        /// column.</summary>
        private void BuildPlanColumns(Transform col)
        {
            var row = NewUI("Plans", col);
            Le(row).flexibleWidth = 1f;
            HRow(row, null, 8f, TextAnchor.UpperLeft, expandHeight: true);
            foreach (var pkg in _shown)
            {
                var highlighted = pkg.PackageId == _config.HighlightPackageId;
                var column = NewUI("Plan " + pkg.PackageId, row.transform);
                Le(column).flexibleWidth = 1f;
                var border = column.AddComponent<Image>();
                border.type = Image.Type.Sliced;
                AddSelectButton(column, border, pkg.PackageId);
                VColumn(column, new RectOffset(8, 8, 14, 14), 0f, TextAnchor.UpperCenter);

                if (highlighted && RevnixPaywallLogic.Truthy(_config.BadgeText))
                {
                    var pill = NewUI("PlanBadge", column.transform);
                    Panel(pill, RevnixPaywallSprites.Rounded(8), _accent, raycast: false);
                    pill.AddComponent<RectMask2D>();
                    var label = MakeText(pill.transform, _config.BadgeText, 11, true,
                        _accentInk, TextAnchor.MiddleCenter, 0f, stretch: false);
                    Fill((RectTransform)label.transform);
                    label.horizontalOverflow = HorizontalWrapMode.Overflow;
                    var size = pill.AddComponent<RevnixPaywallPillSize>();
                    size.Label = label;
                    size.Reference = (RectTransform)column.transform;
                    size.Element = Le(pill);
                    size.MaxFraction = 1f;
                    size.PadX = 8f;
                    size.PadY = 2f;
                    Spacer(column.transform, 7f);
                }

                MakeText(column.transform, pkg.Title, 14, true, _textPrimary, TextAnchor.MiddleCenter, 19f);
                var anchor = highlighted ? RevnixPaywallLogic.AnchorPriceFor(_config, pkg.PriceLabel) : null;
                if (anchor != null)
                {
                    var anchorText = MakeText(column.transform, anchor, 13, false,
                        _textFaint, TextAnchor.MiddleCenter, 0f, stretch: false);
                    Strikethrough(anchorText);
                }
                Spacer(column.transform, 4f);
                MakeText(column.transform, pkg.PriceLabel, 15, true, _textPrimary, TextAnchor.MiddleCenter, 0f, stretch: false);

                _selectables.Add(new SelectableRecord
                {
                    PackageId = pkg.PackageId,
                    Border = border,
                    Radius = 14,
                });
            }
            Spacer(col, 22f);
        }

        /// <summary>Offer layout: the badge becomes a large centered pill.</summary>
        private void BuildOfferPill(Transform col)
        {
            if (!RevnixPaywallLogic.Truthy(_config.BadgeText)) return;
            var holder = NewUI("OfferPillRow", col);
            Le(holder).flexibleWidth = 1f;
            HRow(holder, null, 0f, TextAnchor.MiddleCenter);
            var pill = NewUI("OfferPill", holder.transform);
            Panel(pill, RevnixPaywallSprites.Rounded(12), _accent, raycast: false);
            pill.AddComponent<RectMask2D>();
            var label = MakeText(pill.transform, _config.BadgeText, 12, true,
                _accentInk, TextAnchor.MiddleCenter, 0f, stretch: false);
            Fill((RectTransform)label.transform);
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            var size = pill.AddComponent<RevnixPaywallPillSize>();
            size.Label = label;
            size.Reference = (RectTransform)holder.transform;
            size.Element = Le(pill);
            size.MaxFraction = 0.8f;
            size.PadX = 12f;
            size.PadY = 5f;
            Spacer(col, 14f);
        }

        /// <summary>Offer layout: the highlighted (else first) package renders
        /// as a spotlight card with the anchor price; the remainder stack as
        /// badge-less rows.</summary>
        private void BuildOfferSpotlight(Transform col)
        {
            var spotlight = RevnixPaywallLogic.SpotlightPackage(_config, _shown);
            if (spotlight == null) return;
            var card = NewUI("Spotlight", col);
            Le(card).flexibleWidth = 1f;
            var border = card.AddComponent<Image>();
            border.type = Image.Type.Sliced;
            AddSelectButton(card, border, spotlight.PackageId);
            VColumn(card, new RectOffset(18, 18, 18, 18), 0f, TextAnchor.UpperCenter);

            MakeText(card.transform, spotlight.Title, 16, true, _textPrimary, TextAnchor.MiddleCenter, 0f, stretch: false);
            Spacer(card.transform, 6f);
            var priceRow = NewUI("PriceRow", card.transform);
            HRow(priceRow, null, 8f, TextAnchor.LowerCenter);
            var anchor = RevnixPaywallLogic.AnchorPriceFor(_config, spotlight.PriceLabel);
            if (anchor != null)
            {
                var anchorText = MakeText(priceRow.transform, anchor, 15, false,
                    _textFaint, TextAnchor.LowerCenter, 0f, stretch: false);
                Strikethrough(anchorText);
            }
            MakeText(priceRow.transform, spotlight.PriceLabel, 24, true,
                _textPrimary, TextAnchor.LowerCenter, 0f, stretch: false);

            _selectables.Add(new SelectableRecord
            {
                PackageId = spotlight.PackageId,
                Border = border,
                Radius = 16,
                AlwaysThick = true,
            });
            Spacer(col, 12f);

            if (_shown.Count > 1)
            {
                var remainder = new List<RevnixPaywallPackage>();
                foreach (var pkg in _shown)
                {
                    if (pkg.PackageId != spotlight.PackageId) remainder.Add(pkg);
                }
                BuildPackageRows(col, remainder, false);
            }
        }

        /// <summary>Accent CTA — label swaps for a rotating arc while loading.</summary>
        private void BuildCta(Transform col)
        {
            var cta = NewUI("CTA", col);
            Le(cta).flexibleWidth = 1f;
            var bg = Panel(cta, RevnixPaywallSprites.Rounded(12), _accent, raycast: true);
            VColumn(cta, new RectOffset(16, 16, 16, 16), 0f, TextAnchor.MiddleCenter);
            _ctaButton = cta.AddComponent<Button>();
            _ctaButton.transition = Selectable.Transition.None;
            _ctaButton.targetGraphic = bg;
            _ctaButton.onClick.AddListener(OnCtaPressed);

            var label = MakeText(cta.transform, _config.CtaLabel, 17, true,
                _accentInk, TextAnchor.MiddleCenter, 0f, stretch: false);
            _ctaLabel = label.gameObject;

            var spinner = NewUI("Spinner", cta.transform);
            var spinnerLe = Le(spinner);
            spinnerLe.preferredWidth = 20f;
            spinnerLe.preferredHeight = 20f;
            Panel(spinner, RevnixPaywallSprites.Arc(), _accentInk, raycast: false);
            spinner.AddComponent<RevnixPaywallSpinner>();
            spinner.SetActive(false);
            _ctaSpinner = spinner;

            Spacer(col, 14f);
        }

        private void OnCtaPressed()
        {
            if (_loading) return;
            var selected = SelectedPackageId;
            if (selected == null) return;
            PurchaseAndReport(selected);
        }

        /// <summary>Footer Restore · Terms · Privacy. Handlers win over config
        /// URLs; a URL-only item opens via Application.OpenURL.</summary>
        private void BuildFooter(Transform col)
        {
            var items = RevnixPaywallLogic.FooterItems(
                _config,
                _options.OnRestore != null,
                _options.OnTerms != null,
                _options.OnPrivacy != null);
            if (items.Count == 0) return;

            var row = NewUI("Footer", col);
            Le(row).flexibleWidth = 1f;
            HRow(row, null, 0f, TextAnchor.MiddleCenter);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (i > 0)
                {
                    MakeText(row.transform, " · ", 13, false, _textFaint, TextAnchor.MiddleCenter, 0f, stretch: false);
                }
                var link = NewUI(item.Label, row.transform);
                HRow(link, new RectOffset(4, 4, 4, 4), 0f, TextAnchor.MiddleCenter);
                var text = MakeText(link.transform, item.Label, 13, false,
                    _textFaint, TextAnchor.MiddleCenter, 0f, stretch: false);
                text.raycastTarget = true;
                var button = link.AddComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.targetGraphic = text;
                var handler = item.Label == "Restore"
                    ? (_options.OnRestore == null ? null : (Action)RestoreAndReport)
                    : item.Label == "Terms" ? _options.OnTerms
                    : _options.OnPrivacy;
                var url = item.Url;
                button.onClick.AddListener(() =>
                {
                    if (handler != null) handler();
                    else if (!string.IsNullOrEmpty(url)) Application.OpenURL(url);
                });
            }
        }

        // ── Selection ────────────────────────────────────────────────────────

        private void AddSelectButton(GameObject go, Image target, string packageId)
        {
            var button = go.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = target;
            button.onClick.AddListener(() => Select(packageId));
        }

        private void Select(string packageId)
        {
            _internalSelected = packageId;
            // REV-263: one report per (display, package) — a player toggling
            // monthly → yearly → monthly weighed two plans, not three, and the
            // server's default key (the viewId alone) would keep only the first.
            ReportInteraction(
                RevnixPaywallEvent.Selected,
                ProductIdFor(packageId),
                eventId: "sel:" + packageId);
            _options.OnSelectPackage?.Invoke(packageId);
            if (_blockPaywall) RebuildBlocks(); else RefreshSelection();
        }

        /// <summary>
        /// Redraws a designed paywall after a selection or loading change. Its
        /// selected treatment is structural — a badge and a sub-line appear,
        /// and an arbitrary `selectedStyle` merges in — so it is rebuilt rather
        /// than restyled in place, the way the classic layouts are.
        ///
        /// Should the rebuild fail where the first build did not, the view
        /// falls back to the classic layout exactly as Initialize does, rather
        /// than leaving the customer an empty screen.
        /// </summary>
        private void RebuildBlocks()
        {
            for (var i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i).gameObject;
                // Destroy only takes effect at the end of the frame, so the
                // old tree is detached first — otherwise it would draw on top
                // of the one being built here.
                child.transform.SetParent(null, false);
                Destroy(child);
            }
            _blockPaywall = BuildBlocks();
            if (!_blockPaywall) BuildClassic();
        }

        /// <summary>2px accent border on the selected package, 1px theme
        /// border otherwise (spotlight: always 2px, color tracks selection).</summary>
        private void RefreshSelection()
        {
            var current = SelectedPackageId;
            foreach (var record in _selectables)
            {
                if (record.Border == null) continue;
                var selected = record.PackageId == current;
                record.Border.sprite = RevnixPaywallSprites.RoundedOutline(
                    record.Radius, record.AlwaysThick || selected ? 2 : 1);
                record.Border.color = selected ? _accent : _borderColor;
            }
            if (_ctaButton != null) _ctaButton.interactable = !_loading && current != null;
        }

        // ── Hero image download (cancelled on destroy) ───────────────────────

        private void AddHeroTarget(Transform parent)
        {
            var go = NewUI("Hero", parent);
            Fill((RectTransform)go.transform);
            var raw = go.AddComponent<RawImage>();
            raw.enabled = false;
            raw.raycastTarget = false;
            var cover = go.AddComponent<RevnixPaywallCoverImage>();
            cover.Target = raw;
            _heroTargets.Add(raw);
            if (!_heroLoadStarted)
            {
                _heroLoadStarted = true;
                StartCoroutine(LoadHeroImage(_config.HeroImageUrl));
            }
        }

        private IEnumerator LoadHeroImage(string url)
        {
            var request = UnityWebRequestTexture.GetTexture(url);
            _heroRequest = request;
            yield return request.SendWebRequest();
            _heroRequest = null;
            if (request.result == UnityWebRequest.Result.Success)
            {
                _heroTexture = DownloadHandlerTexture.GetContent(request);
                foreach (var target in _heroTargets)
                {
                    if (target == null) continue;
                    target.texture = _heroTexture;
                    target.enabled = true;
                }
            }
            request.Dispose();
        }

        // ── Small builders ───────────────────────────────────────────────────

        private static string IconOrCheck(string icon)
            => RevnixPaywallLogic.Truthy(icon) ? icon : "✓";

        /// <summary>
        /// The dismiss affordance the classic layouts get (REV-252).
        ///
        /// The nine `template` layouts have the same problem the designed ones
        /// had — nothing on the screen closes them — and OnClose is an option
        /// of the shared view, so a host that wires it must get a close on
        /// either path rather than silently nothing. Classic layouts author no
        /// elements of their own, so there is never a design chip to suppress:
        /// the rule reduces to "draw it whenever the host wired a handler".
        ///
        /// The designed path draws its own (inside the block renderer, where it
        /// can see the tree), so this is never called there.
        /// </summary>
        private void BuildClassicClose()
        {
            if (_options.OnClose == null) return;

            var go = NewUI("RevnixClassicClose", transform);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(30f, 30f);
            rt.anchoredPosition = new Vector2(-14f, -14f);
            // Below the status bar, like the designed path's chip.
            var inset = go.AddComponent<RevnixSafeAreaInset>();
            inset.Margin = 14f;

            var image = go.AddComponent<Image>();
            var sprite = RevnixPaywallSprites.Rounded(15);
            if (sprite != null)
            {
                image.sprite = sprite;
                image.type = Image.Type.Sliced;
            }
            image.color = new Color(_textPrimary.r, _textPrimary.g, _textPrimary.b, 0.14f);
            image.raycastTarget = true;

            var button = go.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = image;
            button.onClick.AddListener(CloseAndReport);

            var label = NewUI("Glyph", go.transform);
            var lrt = (RectTransform)label.transform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero;
            lrt.offsetMax = Vector2.zero;
            var text = label.AddComponent<Text>();
            if (_font != null) text.font = _font;
            text.text = "\u00d7";
            text.fontSize = 17;
            text.color = _textPrimary;
            text.alignment = TextAnchor.MiddleCenter;
            text.raycastTarget = false;
        }

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
            var le = go.GetComponent<LayoutElement>();
            return le != null ? le : go.AddComponent<LayoutElement>();
        }

        private static VerticalLayoutGroup VColumn(GameObject go, RectOffset padding, float spacing, TextAnchor alignment)
        {
            var group = go.AddComponent<VerticalLayoutGroup>();
            group.padding = padding ?? new RectOffset();
            group.spacing = spacing;
            group.childAlignment = alignment;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            return group;
        }

        private static HorizontalLayoutGroup HRow(GameObject go, RectOffset padding, float spacing, TextAnchor alignment, bool expandHeight = false)
        {
            var group = go.AddComponent<HorizontalLayoutGroup>();
            group.padding = padding ?? new RectOffset();
            group.spacing = spacing;
            group.childAlignment = alignment;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = expandHeight;
            return group;
        }

        private static Image Panel(GameObject go, Sprite sprite, Color color, bool raycast)
        {
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.type = sprite != null && sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
            image.color = color;
            image.raycastTarget = raycast;
            return image;
        }

        private void Spacer(Transform parent, float height)
        {
            var go = NewUI("Spacer", parent);
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
        }

        private void SpacerW(Transform parent, float width)
        {
            var go = NewUI("Spacer", parent);
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = width;
            le.preferredWidth = width;
        }

        /// <summary>Legacy UI.Text has no strikethrough — a 1px line overlaid
        /// across the vertical center stands in.</summary>
        private static void Strikethrough(Text text)
        {
            var line = NewUI("Strike", text.transform);
            var rt = (RectTransform)line.transform;
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(0f, 1f);
            rt.anchoredPosition = Vector2.zero;
            var image = line.AddComponent<Image>();
            image.color = text.color;
            image.raycastTarget = false;
        }

        /// <summary>RN px sizes carried over 1:1; the .5 sizes (13.5, 14.5,
        /// 15.5, 16.5, 12.5) round up since UI.Text font sizes are ints.
        /// Weights 600+ map to Bold — legacy Text has no in-between weights.
        /// Line spacing approximates RN's px line height as px/fontSize.</summary>
        private Text MakeText(Transform parent, string content, int size, bool bold, Color color,
            TextAnchor alignment, float lineHeightPx = 0f, bool stretch = true, bool italic = false)
        {
            var go = NewUI("Text", parent);
            var text = go.AddComponent<Text>();
            if (_font != null) text.font = _font;
            text.text = content;
            text.fontSize = size;
            text.fontStyle = bold
                ? (italic ? FontStyle.BoldAndItalic : FontStyle.Bold)
                : (italic ? FontStyle.Italic : FontStyle.Normal);
            text.color = color;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            if (lineHeightPx > 0f) text.lineSpacing = lineHeightPx / size;
            if (stretch) Le(go).flexibleWidth = 1f;
            return text;
        }

        private static Font LoadLegacyFont()
        {
            // 2022.2 renamed the built-in font; the repo baseline is 2021.3,
            // so try the new name first and fall back to Arial.
            return TryBuiltinFont("LegacyRuntime.ttf") ?? TryBuiltinFont("Arial.ttf");
        }

        private static Font TryBuiltinFont(string name)
        {
            try
            {
                return Resources.GetBuiltinResource<Font>(name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Color Hex(string value, Color fallback)
        {
            Color parsed;
            if (!string.IsNullOrEmpty(value) && ColorUtility.TryParseHtmlString(value, out parsed))
            {
                return parsed;
            }
            return fallback;
        }

        private static Color WithAlpha(Color color, float alpha)
            => new Color(color.r, color.g, color.b, alpha);
    }
}
