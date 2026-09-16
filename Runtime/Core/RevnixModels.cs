using System.Collections.Generic;

namespace Revnix
{
    /// <summary>Models are 1:1 with `revnix-app/public/openapi.yaml` schemas and
    /// identical in shape to the Swift, Kotlin, and Flutter ports.</summary>
    public enum RevnixStore
    {
        Apple,
        Google,
    }

    public static class RevnixStoreWire
    {
        public static string Wire(this RevnixStore store)
            => store == RevnixStore.Google ? "google" : "apple";

        public static RevnixStore FromWire(string value)
            => value == "google" ? RevnixStore.Google : RevnixStore.Apple;
    }

    /// <summary>
    /// REV-263: the six paywall interactions
    /// <see cref="RevnixClient.LogPaywallEvent"/> can report — what the
    /// customer did on a display, between the view that opened it and the
    /// close or purchase that ended it. The server turns each into the ledger
    /// type <c>paywall.&lt;wire name&gt;</c>.
    /// </summary>
    public enum RevnixPaywallEvent
    {
        /// <summary>A package was picked.</summary>
        Selected,

        /// <summary>Checkout was started.</summary>
        PurchaseStarted,

        /// <summary>The customer backed out at the store sheet
        /// (<c>PurchaseFailureReason.UserCancelled</c>).</summary>
        PurchaseAbandoned,

        /// <summary>The store refused the payment.</summary>
        PurchaseFailed,

        /// <summary>Restore purchases was tapped.</summary>
        Restore,

        /// <summary>The paywall itself failed — config, products, or
        /// render.</summary>
        Error,
    }

    public static class RevnixPaywallEventNames
    {
        /// <summary>The wire name the API validates against. Spelled out
        /// rather than derived from the enum name so the snake_case contract
        /// is visible here and cannot drift with a rename.</summary>
        public static string Wire(RevnixPaywallEvent evt)
        {
            switch (evt)
            {
                case RevnixPaywallEvent.Selected: return "selected";
                case RevnixPaywallEvent.PurchaseStarted: return "purchase_started";
                case RevnixPaywallEvent.PurchaseAbandoned: return "purchase_abandoned";
                case RevnixPaywallEvent.PurchaseFailed: return "purchase_failed";
                case RevnixPaywallEvent.Restore: return "restore";
                default: return "error";
            }
        }
    }

    // ——— GET /v1/customers/{id}/entitlements ———

    public sealed class EntitlementSource
    {
        public string Kind;
        public string Key;
        public bool IsActive;
        public long? ExpiresAt;

        public static EntitlementSource FromJson(Dictionary<string, object> map) => new EntitlementSource
        {
            Kind = RevnixJson.GetString(map, "kind", ""),
            Key = RevnixJson.GetString(map, "key", ""),
            IsActive = RevnixJson.GetBool(map, "isActive"),
            ExpiresAt = RevnixJson.GetNullableLong(map, "expiresAt"),
        };

        internal EntitlementSource Inactive() => new EntitlementSource
        {
            Kind = Kind, Key = Key, IsActive = false, ExpiresAt = ExpiresAt,
        };
    }

    public sealed class Entitlement
    {
        public string EntitlementId;
        public bool IsActive;

        /// <summary>Unix ms; null for a lifetime purchase or an open-ended grant.</summary>
        public long? ExpiresAt;

        public List<EntitlementSource> Sources = new List<EntitlementSource>();

        public static Entitlement FromJson(Dictionary<string, object> map)
        {
            var ent = new Entitlement
            {
                EntitlementId = RevnixJson.GetString(map, "entitlementId", ""),
                IsActive = RevnixJson.GetBool(map, "isActive"),
                ExpiresAt = RevnixJson.GetNullableLong(map, "expiresAt"),
            };
            foreach (var item in RevnixJson.GetList(map, "sources"))
            {
                if (item is Dictionary<string, object> src) ent.Sources.Add(EntitlementSource.FromJson(src));
            }
            return ent;
        }

        internal Entitlement Inactive()
        {
            var ent = new Entitlement
            {
                EntitlementId = EntitlementId, IsActive = false, ExpiresAt = ExpiresAt,
            };
            foreach (var src in Sources) ent.Sources.Add(src.Inactive());
            return ent;
        }
    }

    public sealed class CustomerEntitlements
    {
        public string CustomerId;

        /// <summary>Ledger position this read reflects (read-your-writes).</summary>
        public long Cursor;

        public List<Entitlement> Entitlements = new List<Entitlement>();

        /// <summary>Client-populated: true when served from the offline cache.</summary>
        public bool? Stale;

        /// <summary>Client-populated: unix ms this snapshot was fetched.</summary>
        public long? FetchedAt;

        public bool IsEntitled(string entitlementId)
        {
            foreach (var ent in Entitlements)
            {
                if (ent.EntitlementId == entitlementId && ent.IsActive) return true;
            }
            return false;
        }

        public static CustomerEntitlements FromJson(Dictionary<string, object> map)
        {
            var snapshot = new CustomerEntitlements
            {
                CustomerId = RevnixJson.GetString(map, "customerId", ""),
                Cursor = RevnixJson.GetLong(map, "cursor"),
                Stale = RevnixJson.GetNullableBool(map, "stale"),
                FetchedAt = RevnixJson.GetNullableLong(map, "fetchedAt"),
            };
            foreach (var item in RevnixJson.GetList(map, "entitlements"))
            {
                if (item is Dictionary<string, object> ent) snapshot.Entitlements.Add(Entitlement.FromJson(ent));
            }
            return snapshot;
        }

        internal Dictionary<string, object> ToJson()
        {
            var entitlements = new List<object>();
            foreach (var ent in Entitlements)
            {
                var sources = new List<object>();
                foreach (var src in ent.Sources)
                {
                    var s = new Dictionary<string, object>
                    {
                        ["kind"] = src.Kind, ["key"] = src.Key, ["isActive"] = src.IsActive,
                    };
                    if (src.ExpiresAt.HasValue) s["expiresAt"] = src.ExpiresAt.Value;
                    sources.Add(s);
                }
                var e = new Dictionary<string, object>
                {
                    ["entitlementId"] = ent.EntitlementId,
                    ["isActive"] = ent.IsActive,
                    ["sources"] = sources,
                };
                if (ent.ExpiresAt.HasValue) e["expiresAt"] = ent.ExpiresAt.Value;
                entitlements.Add(e);
            }
            var map = new Dictionary<string, object>
            {
                ["customerId"] = CustomerId,
                ["cursor"] = Cursor,
                ["entitlements"] = entitlements,
            };
            if (Stale.HasValue) map["stale"] = Stale.Value;
            if (FetchedAt.HasValue) map["fetchedAt"] = FetchedAt.Value;
            return map;
        }
    }

    // ——— POST /v1/purchases ———

    public sealed class RegisterPurchaseInput
    {
        public RevnixStore Source;

        /// <summary>Apple: originalTransactionId · Google: purchaseToken.</summary>
        public string Token;

        public string ProductId;

        /// <summary>Google: this is the purchaseToken too (the established rule).</summary>
        public string TransactionId;

        public long? OccurredAt;
        public long? ExpiresAt;

        /// <summary>StoreKit 2 JWS — the proof path; claims without it are provisional.</summary>
        public string SignedTransactionInfo;

        /// <summary>Optional raw store payload, retained server-side for reprocessing.</summary>
        public Dictionary<string, object> RawPayload;

        /// <summary>Convenience for Google, where the purchase token is BOTH the
        /// token and the transaction id — Play has no separate transaction id.</summary>
        public static RegisterPurchaseInput Google(string purchaseToken, string productId, long? occurredAt = null)
            => new RegisterPurchaseInput
            {
                Source = RevnixStore.Google,
                Token = purchaseToken,
                ProductId = productId,
                TransactionId = purchaseToken,
                OccurredAt = occurredAt,
            };
    }

    public sealed class RegisterPurchaseResult
    {
        public string EventId;

        /// <summary>Ledger position — poll entitlements until cursor &gt;= seq.</summary>
        public long Seq;

        public bool Duplicate;

        /// <summary>The id THIS caller should use going forward. The SDK adopts it
        /// automatically.</summary>
        public string CustomerId;

        public bool Transferred;
        public bool? OwnedByOtherCustomer;
        public string Refused;
        public bool? Restored;

        /// <summary>Recorded without store proof — live but time-boxed until the
        /// store confirms. Expect true for Google device claims.</summary>
        public bool? Provisional;

        public static RegisterPurchaseResult FromJson(Dictionary<string, object> map) => new RegisterPurchaseResult
        {
            EventId = RevnixJson.GetString(map, "eventId", ""),
            Seq = RevnixJson.GetLong(map, "seq"),
            Duplicate = RevnixJson.GetBool(map, "duplicate"),
            CustomerId = RevnixJson.GetString(map, "customerId", ""),
            Transferred = RevnixJson.GetBool(map, "transferred"),
            OwnedByOtherCustomer = RevnixJson.GetNullableBool(map, "ownedByOtherCustomer"),
            Refused = RevnixJson.GetString(map, "refused"),
            Restored = RevnixJson.GetNullableBool(map, "restored"),
            Provisional = RevnixJson.GetNullableBool(map, "provisional"),
        };
    }

    // ——— GET /v1/placements/{key}/offering ———

    public sealed class PlacementPackage
    {
        public string PackageId;
        public string ProductId;
        public Dictionary<string, object> Metadata;
        public Dictionary<string, object> Product;

        public static PlacementPackage FromJson(Dictionary<string, object> map) => new PlacementPackage
        {
            PackageId = RevnixJson.GetString(map, "packageId", ""),
            ProductId = RevnixJson.GetString(map, "productId", ""),
            Metadata = RevnixJson.GetObject(map, "metadata"),
            Product = RevnixJson.GetObject(map, "product"),
        };
    }

    public sealed class PlacementOffering
    {
        public string OfferingId;
        public string DisplayName;
        public Dictionary<string, object> Metadata;
        public List<PlacementPackage> Packages = new List<PlacementPackage>();

        public static PlacementOffering FromJson(Dictionary<string, object> map)
        {
            var offering = new PlacementOffering
            {
                OfferingId = RevnixJson.GetString(map, "offeringId", ""),
                DisplayName = RevnixJson.GetString(map, "displayName", ""),
                Metadata = RevnixJson.GetObject(map, "metadata"),
            };
            foreach (var item in RevnixJson.GetList(map, "packages"))
            {
                if (item is Dictionary<string, object> pkg) offering.Packages.Add(PlacementPackage.FromJson(pkg));
            }
            return offering;
        }
    }

    /// <summary>One feature row on the paywall.</summary>
    public sealed class PaywallFeature
    {
        /// <summary>Icon name/emoji chosen in the dashboard; null = layout default.</summary>
        public string Icon;

        public string Title;
        public string Description;

        public static PaywallFeature FromJson(Dictionary<string, object> map) => new PaywallFeature
        {
            Icon = RevnixJson.GetString(map, "icon"),
            Title = RevnixJson.GetString(map, "title", ""),
            Description = RevnixJson.GetString(map, "description"),
        };
    }

    /// <summary>Social proof, dashboard-configured. Any layout renders the
    /// pieces that are set: stars/quote card above the packages, `Count`
    /// under the CTA.</summary>
    public sealed class PaywallReview
    {
        /// <summary>0–5; rendered as a star row. Null = no star row.</summary>
        public double? Rating;

        public string Quote;
        public string Author;

        /// <summary>e.g. "Join 2M+ users" — small line under the CTA.</summary>
        public string Count;

        public static PaywallReview FromJson(Dictionary<string, object> map) => new PaywallReview
        {
            Rating = RevnixJson.GetNullableDouble(map, "rating"),
            Quote = RevnixJson.GetString(map, "quote"),
            Author = RevnixJson.GetString(map, "author"),
            Count = RevnixJson.GetString(map, "count"),
        };
    }

    /// <summary>Win-back/offer presentation: anchor price struck through on the
    /// highlighted package, urgency line above the CTA. Any layout.</summary>
    public sealed class PaywallOffer
    {
        public string StrikethroughPrice;
        public string UrgencyText;

        public static PaywallOffer FromJson(Dictionary<string, object> map) => new PaywallOffer
        {
            StrikethroughPrice = RevnixJson.GetString(map, "strikethroughPrice"),
            UrgencyText = RevnixJson.GetString(map, "urgencyText"),
        };
    }

    /// <summary>Footer links, dashboard-configured. A null footer on the config
    /// (legacy) means show all three. When a URL is set the app should open it
    /// directly; otherwise run its own terms/privacy handler.</summary>
    public sealed class PaywallFooter
    {
        public bool ShowRestore;
        public bool ShowTerms;
        public bool ShowPrivacy;
        public string TermsUrl;
        public string PrivacyUrl;

        public static PaywallFooter FromJson(Dictionary<string, object> map) => new PaywallFooter
        {
            ShowRestore = RevnixJson.GetBool(map, "showRestore", true),
            ShowTerms = RevnixJson.GetBool(map, "showTerms", true),
            ShowPrivacy = RevnixJson.GetBool(map, "showPrivacy", true),
            TermsUrl = RevnixJson.GetString(map, "termsUrl"),
            PrivacyUrl = RevnixJson.GetString(map, "privacyUrl"),
        };
    }

    /// <summary>Remote paywall render contract — the app draws this with its
    /// own components; prices still come from the store (StoreKit / Play
    /// Billing) so the display never disagrees with the charge.</summary>
    public sealed class PaywallConfig
    {
        /// <summary>Layout — the screen structure to render. Known values:
        /// "focus", "feature-list", "minimal", "hero", "timeline", "plans",
        /// "feature-grid", "offer", "reveal". The dashboard's template gallery
        /// is presets over these layouts. Kept as a plain string so configs
        /// published by a newer dashboard never fail to parse — treat an
        /// unknown value as "focus".</summary>
        public string Template;

        /// <summary>Color scheme: "dark" or "light". Null (legacy config) =
        /// dark.</summary>
        public string Mode;

        public string Headline;
        public string Subheadline;
        public List<PaywallFeature> Features = new List<PaywallFeature>();
        public string CtaLabel;

        /// <summary>packageId of the visually highlighted package.</summary>
        public string HighlightPackageId;

        /// <summary>Badge on the highlighted package, e.g. "SAVE 17%".</summary>
        public string BadgeText;

        /// <summary>Accent hex like "#6478ff"; fall back to the app theme when
        /// absent.</summary>
        public string Accent;

        /// <summary>Hero image URL rendered above the headline in place of the
        /// icon tile.</summary>
        public string HeroImageUrl;

        public PaywallReview Review;
        public PaywallOffer Offer;

        /// <summary>Null (legacy config) = show restore/terms/privacy.</summary>
        public PaywallFooter Footer;

        /// <summary>A designed paywall: the block tree the dashboard's builder
        /// authored. When present RevnixPaywallView draws THIS and the fields
        /// above act as the fallback for apps on an SDK that predates block
        /// rendering — so an older app keeps showing a sane classic screen
        /// instead of nothing.
        ///
        /// Held as the raw parsed JSON rather than a typed tree so a document
        /// from a NEWER dashboard can never fail to parse here;
        /// <see cref="PaywallBlockDoc.Parse"/> turns it into the parts this SDK
        /// understands.</summary>
        public object Blocks;

        public static PaywallConfig FromJson(Dictionary<string, object> map)
        {
            var review = RevnixJson.GetObject(map, "review");
            var offer = RevnixJson.GetObject(map, "offer");
            var footer = RevnixJson.GetObject(map, "footer");
            var config = new PaywallConfig
            {
                Template = RevnixJson.GetString(map, "template", "focus"),
                Mode = RevnixJson.GetString(map, "mode"),
                Headline = RevnixJson.GetString(map, "headline", ""),
                Subheadline = RevnixJson.GetString(map, "subheadline"),
                CtaLabel = RevnixJson.GetString(map, "ctaLabel", ""),
                HighlightPackageId = RevnixJson.GetString(map, "highlightPackageId"),
                BadgeText = RevnixJson.GetString(map, "badgeText"),
                Accent = RevnixJson.GetString(map, "accent"),
                HeroImageUrl = RevnixJson.GetString(map, "heroImageUrl"),
                Review = review != null ? PaywallReview.FromJson(review) : null,
                Offer = offer != null ? PaywallOffer.FromJson(offer) : null,
                Footer = footer != null ? PaywallFooter.FromJson(footer) : null,
                Blocks = map != null && map.TryGetValue("blocks", out var blocks) ? blocks : null,
            };
            foreach (var item in RevnixJson.GetList(map, "features"))
            {
                if (item is Dictionary<string, object> feature) config.Features.Add(PaywallFeature.FromJson(feature));
            }
            return config;
        }
    }

    /// <summary>Remote paywall design attached to a placement.</summary>
    public sealed class PlacementPaywall
    {
        public string PaywallId;
        public string Name;
        public PaywallConfig Config;

        public static PlacementPaywall FromJson(Dictionary<string, object> map) => new PlacementPaywall
        {
            PaywallId = RevnixJson.GetString(map, "paywallId", ""),
            Name = RevnixJson.GetString(map, "name", ""),
            Config = PaywallConfig.FromJson(RevnixJson.GetObject(map, "config") ?? new Dictionary<string, object>()),
        };
    }

    /// <summary>REV-219: the running experiment's sticky assignment for this
    /// customer. Attribution only — the served offering/paywall are already
    /// the variant's, so the app just renders what it gets.</summary>
    public sealed class PlacementExperiment
    {
        public string Key;
        public string VariantId;

        public static PlacementExperiment FromJson(Dictionary<string, object> map) => new PlacementExperiment
        {
            Key = RevnixJson.GetString(map, "key", ""),
            VariantId = RevnixJson.GetString(map, "variantId", ""),
        };
    }

    public sealed class PlacementResolution
    {
        public string Status;
        public string PlacementKey;

        /// <summary>Published catalog revision this resolution came from.</summary>
        public long Revision;

        public PlacementOffering Offering;

        /// <summary>Remote paywall render contract — app-rendered in v1. Null
        /// when the placement has no paywall attached.</summary>
        public PlacementPaywall Paywall;

        /// <summary>Null when no experiment applies — the server sends null,
        /// and older servers omit the key entirely; both parse to null.</summary>
        public PlacementExperiment Experiment;

        /// <summary>Set only on a dashboard QR/link preview resolution
        /// (<c>GET /v1/paywalls/preview/{token}</c>), never on a real
        /// resolve.</summary>
        public bool Preview;

        public static PlacementResolution FromJson(Dictionary<string, object> map)
        {
            var paywall = RevnixJson.GetObject(map, "paywall");
            var experiment = RevnixJson.GetObject(map, "experiment");
            return new PlacementResolution
            {
                Status = RevnixJson.GetString(map, "status", "ok"),
                PlacementKey = RevnixJson.GetString(map, "placementKey", ""),
                Revision = RevnixJson.GetLong(map, "revision"),
                Offering = PlacementOffering.FromJson(RevnixJson.GetObject(map, "offering") ?? new Dictionary<string, object>()),
                Paywall = paywall != null ? PlacementPaywall.FromJson(paywall) : null,
                Experiment = experiment != null ? PlacementExperiment.FromJson(experiment) : null,
                Preview = RevnixJson.GetBool(map, "preview"),
            };
        }
    }

    /// <summary>How a deferred deep link was resolved. <c>Exact</c> comes
    /// from the Android Play Install Referrer; <c>Probabilistic</c> is a
    /// best-effort match on iOS.</summary>
    public enum DeferredDeepLinkMatch
    {
        Exact,
        Probabilistic,
    }

    /// <summary>Swallowed background failure (queue drains, telemetry beacons).</summary>
    public sealed class RevnixDiagnostic
    {
        public readonly string Op;
        public readonly string Message;

        public RevnixDiagnostic(string op, string message)
        {
            Op = op;
            Message = message;
        }
    }
}
