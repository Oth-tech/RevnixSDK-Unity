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

    public sealed class PlacementResolution
    {
        public string Status;
        public string PlacementKey;

        /// <summary>Published catalog revision this resolution came from.</summary>
        public long Revision;

        public PlacementOffering Offering;

        /// <summary>Remote paywall render contract — app-rendered in v1.</summary>
        public Dictionary<string, object> Paywall;

        public static PlacementResolution FromJson(Dictionary<string, object> map) => new PlacementResolution
        {
            Status = RevnixJson.GetString(map, "status", ""),
            PlacementKey = RevnixJson.GetString(map, "placementKey", ""),
            Revision = RevnixJson.GetLong(map, "revision"),
            Offering = PlacementOffering.FromJson(RevnixJson.GetObject(map, "offering") ?? new Dictionary<string, object>()),
            Paywall = RevnixJson.GetObject(map, "paywall"),
        };
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
