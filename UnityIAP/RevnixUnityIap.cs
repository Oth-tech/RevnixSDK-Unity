// Compiled only when com.unity.purchasing is in the project (the asmdef's
// versionDefines emit REVNIX_UNITY_IAP) — projects without Unity IAP still
// compile the SDK and register purchases by hand.
#if REVNIX_UNITY_IAP
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine.Purchasing;

namespace Revnix.Unity.IAP
{
    /// <summary>
    /// Bridges Unity IAP purchases into Revnix. Unity IAP owns the store flow
    /// (initialization, purchase UI, restoration); this helper's only job is
    /// turning a completed Product into a RegisterPurchaseInput.
    ///
    /// Proof model per store:
    /// - Google: the purchaseToken IS the claim, and it is also the
    ///   transactionId (the established Revnix rule). The claim lands
    ///   provisional until the server corroborates via RTDN /
    ///   subscriptionsv2.get.
    /// - Apple: Unity IAP surfaces the StoreKit transaction id but no
    ///   StoreKit 2 JWS, so the claim carries no device-side proof and lands
    ///   provisional until the App Store Server API / notifications confirm.
    /// </summary>
    public static class RevnixUnityIap
    {
        /// <summary>Build the registration input for a purchased product, or
        /// null when the receipt is missing/unparseable (nothing to claim).</summary>
        public static RegisterPurchaseInput InputFrom(Product product)
        {
            if (product == null || string.IsNullOrEmpty(product.receipt)) return null;

            Dictionary<string, object> receipt;
            try
            {
                receipt = RevnixJson.ParseObject(product.receipt);
            }
            catch (System.FormatException)
            {
                return null;
            }

            var store = RevnixJson.GetString(receipt, "Store", "");
            var transactionId = RevnixJson.GetString(receipt, "TransactionID", "");
            var productId = product.definition.storeSpecificId;
            if (string.IsNullOrEmpty(transactionId) || string.IsNullOrEmpty(productId)) return null;

            if (store == "GooglePlay")
            {
                // Unified receipt Payload: {"json": "<purchase data>", "signature": …}
                var token = transactionId;
                try
                {
                    var payload = RevnixJson.ParseObject(RevnixJson.GetString(receipt, "Payload", "{}"));
                    var inner = RevnixJson.GetString(payload, "json");
                    if (inner != null)
                    {
                        var data = RevnixJson.ParseObject(inner);
                        var purchaseToken = RevnixJson.GetString(data, "purchaseToken");
                        if (!string.IsNullOrEmpty(purchaseToken)) token = purchaseToken;
                    }
                }
                catch (System.FormatException)
                {
                    // fall back to TransactionID (Unity sets it to the token)
                }
                return RegisterPurchaseInput.Google(token, productId);
            }

            return new RegisterPurchaseInput
            {
                Source = RevnixStore.Apple,
                Token = transactionId,
                ProductId = productId,
                TransactionId = transactionId,
            };
        }

        /// <summary>Register a completed Unity IAP purchase with Revnix. On a
        /// transient failure the claim is already queued durably (retried by
        /// RetryPendingPurchases / the next Configure), so callers may treat a
        /// null return as "recorded eventually".</summary>
        public static async Task<RegisterPurchaseResult> Register(RevnixClient client, Product product)
        {
            var input = InputFrom(product);
            if (input == null) return null;
            try
            {
                return await client.RegisterPurchase(input);
            }
            catch (RevnixException err) when (err.IsRetryable)
            {
                return null; // queued; the drain will deliver it
            }
        }
    }
}
#endif
