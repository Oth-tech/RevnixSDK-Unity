using Revnix;
using Revnix.Unity;
using UnityEngine;

/// <summary>
/// Minimal end-to-end usage: configure at startup, gate a feature, resolve a
/// placement for a paywall, and (with Unity IAP) register a purchase.
/// </summary>
public class QuickStart : MonoBehaviour
{
    [SerializeField] private string apiKey = "rvx_pk_test_…";
    [SerializeField] private string baseUrl = "https://your-deployment.convex.site";

    private async void Start()
    {
        // Once per launch. Also drains the purchase retry queue and reports
        // the install — both idempotent.
        var revnix = RevnixSdk.Configure(apiKey, baseUrl);

        // Gate. Never throws; unknown or unreachable means locked.
        var pro = await revnix.IsEntitled("pro");
        Debug.Log("pro entitlement: " + (pro ? "unlocked" : "locked"));

        // Paywall: resolve what to show at this placement.
        try
        {
            var placement = await revnix.ResolvePlacement("main_paywall");
            Debug.Log("offering " + placement.Offering.OfferingId +
                      " with " + placement.Offering.Packages.Count + " packages");
            if (placement.Paywall != null)
            {
                // Typed remote paywall design — render it with your own UI.
                var config = placement.Paywall.Config;
                Debug.Log("paywall '" + placement.Paywall.Name + "': layout " +
                          config.Template + ", headline \"" + config.Headline +
                          "\", CTA \"" + config.CtaLabel + "\"");
            }
            await revnix.LogPaywallShown(placementKey: "main_paywall");
        }
        catch (RevnixException err)
        {
            Debug.Log("placement unavailable: " + err.Code);
        }

        // After a Unity IAP purchase completes, register it:
        //   var result = await RevnixUnityIap.Register(revnix, product);
        //   if (result != null)
        //       await revnix.WaitForEntitlements(result.Seq); // read-your-writes unlock
        // (Without Unity IAP, build a RegisterPurchaseInput by hand.)
    }
}
