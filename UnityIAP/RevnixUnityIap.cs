// Compiled only when com.unity.purchasing is in the project (the asmdef's
// versionDefines emit REVNIX_UNITY_IAP) — projects without Unity IAP still
// compile the SDK and register purchases by hand.
#if REVNIX_UNITY_IAP
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Purchasing;

namespace Revnix.Unity.IAP
{
    /// <summary>
    /// Bridges Unity IAP purchases into Revnix. Unity IAP owns the store flow
    /// (initialization, purchase UI, restoration); this helper's only job is
    /// turning a completed purchase into a RegisterPurchaseInput.
    ///
    /// Proof model per store:
    /// - Google: the purchaseToken IS the claim, and it is also the
    ///   transactionId (the established Revnix rule). The claim lands
    ///   provisional until the server corroborates via RTDN /
    ///   subscriptionsv2.get.
    /// - Apple: the Unity IAP 5 Order path sends the StoreKit 2 JWS
    ///   (Order.Info.Apple.jwsRepresentation) as proof, so the server verifies
    ///   the purchase immediately. The Product path (Unity IAP 4) has no JWS,
    ///   so its claim lands provisional until the App Store Server API /
    ///   notifications confirm.
    /// </summary>
    public static class RevnixUnityIap
    {
        /// <summary>Build the registration input for a purchased product, or
        /// null when the receipt is missing/unparseable (nothing to claim).</summary>
        public static RegisterPurchaseInput InputFrom(Product product)
            => product == null ? null : FromReceipt(product.receipt, product.definition?.storeSpecificId);

        /// <summary>Register a completed Unity IAP purchase with Revnix. On a
        /// transient failure the claim is already queued durably (retried by
        /// RetryPendingPurchases / the next Configure), so callers may treat a
        /// null return as "recorded eventually".</summary>
        public static Task<RegisterPurchaseResult> Register(RevnixClient client, Product product)
            => Send(client, InputFrom(product));

#if REVNIX_UNITY_IAP_5
        /// <summary>Build the registration input for a Unity IAP 5 order
        /// (pending or confirmed). Apple orders carrying a decodable StoreKit 2
        /// JWS become verified claims; anything else falls back to the receipt
        /// mapping. Null when there is nothing to claim.</summary>
        public static RegisterPurchaseInput InputFrom(Order order)
        {
            if (order?.Info == null) return null;
            var proof = AppleProof(order.Info.Apple?.jwsRepresentation);
            if (proof != null) return proof;
            var items = order.CartOrdered?.Items();
            var productId = items != null && items.Count > 0 ? items[0]?.Product?.definition?.storeSpecificId : null;
            return FromReceipt(order.Info.Receipt, productId);
        }

        /// <summary>Register a Unity IAP 5 order with Revnix (call from
        /// OnPurchasePending). Same retry semantics as the Product overload: a
        /// null return on a transient failure means "recorded eventually".</summary>
        public static Task<RegisterPurchaseResult> Register(RevnixClient client, Order order)
            => Send(client, InputFrom(order));

        static RegisterPurchaseInput AppleProof(string jws)
        {
            var parts = jws?.Split('.');
            if (parts == null || parts.Length != 3) return null;

            Dictionary<string, object> claims;
            try
            {
                var base64 = parts[1].Replace('-', '+').Replace('_', '/');
                base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
                claims = RevnixJson.ParseObject(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
            }
            catch (FormatException)
            {
                return null;
            }

            var originalTransactionId = RevnixJson.GetString(claims, "originalTransactionId");
            var transactionId = RevnixJson.GetString(claims, "transactionId");
            var productId = RevnixJson.GetString(claims, "productId");
            if (string.IsNullOrEmpty(originalTransactionId) || string.IsNullOrEmpty(transactionId) || string.IsNullOrEmpty(productId)) return null;

            return new RegisterPurchaseInput
            {
                Source = RevnixStore.Apple,
                Token = originalTransactionId,
                ProductId = productId,
                TransactionId = transactionId,
                SignedTransactionInfo = jws,
            };
        }
#endif

        static RegisterPurchaseInput FromReceipt(string unifiedReceipt, string productId)
        {
            if (string.IsNullOrEmpty(unifiedReceipt)) return null;

            Dictionary<string, object> receipt;
            try
            {
                receipt = RevnixJson.ParseObject(unifiedReceipt);
            }
            catch (FormatException)
            {
                return null;
            }

            var store = RevnixJson.GetString(receipt, "Store", "");
            var transactionId = RevnixJson.GetString(receipt, "TransactionID", "");
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
                catch (FormatException)
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

        static async Task<RegisterPurchaseResult> Send(RevnixClient client, RegisterPurchaseInput input)
        {
            if (input == null) return null;
            try
            {
                return await client.RegisterPurchase(input);
            }
            catch (RevnixException err) when (err.IsRetryable)
            {
                return null;
            }
        }
    }
}
#endif
