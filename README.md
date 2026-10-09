<p align="center">
  <a href="https://www.revnix.io"><img src="https://www.revnix.io/sdk/logo.png" width="360" alt="Revnix"></a>
</p>

<h1 align="center">Subscriptions, Paywalls and Attribution<br>for Your Unity Game</h1>

<p align="center">
  <a href="https://github.com/Oth-tech/RevnixSDK-Unity/releases"><img src="https://img.shields.io/badge/UPM-com.revnix.sdk-2f6fe0?logo=unity" alt="UPM package"></a>
  <img src="https://img.shields.io/badge/Unity-2021.3%2B-2f6fe0?logo=unity" alt="Unity 2021.3+">
  <a href="https://github.com/Oth-tech/RevnixSDK-Unity/blob/main/LICENSE.md"><img src="https://img.shields.io/badge/license-MIT-2f6fe0" alt="license"></a>
</p>

<p align="center">
  <a href="https://www.revnix.io"><b>Website</b></a> •
  <a href="https://www.revnix.io/docs/unity"><b>Docs</b></a> •
  <a href="https://www.revnix.io/docs/unity/reference"><b>API Reference</b></a>
</p>

![Revnix: subscriptions, paywalls and attribution for mobile apps](https://www.revnix.io/sdk/hero.png)

Revnix SDK makes subscriptions, paywalls and attribution for Unity fast and easy. Unity IAP keeps the store flow; Revnix validates the purchase on the server, unlocks the entitlement and renders your dashboard paywall with plain UGUI. No prefabs, no TextMeshPro, no extra dependencies.

## Table of Contents

- [Why Revnix?](#why-revnix)
- [Getting Started](#getting-started)
- [Quick start](#quick-start)
- [Purchases and entitlements without server code](#purchases-and-entitlements-without-server-code)
- [Paywalls that update without app releases](#paywalls-that-update-without-app-releases)
- [A/B tests with a built-in holdout](#ab-tests-with-a-built-in-holdout)
- [Attribution and deep links](#attribution-and-deep-links)
- [Real-time analytics for your Unity game](#real-time-analytics-for-your-unity-game)
- [Platform Support](#platform-support)
- [Documentation](#documentation)
- [Migrating from another SDK](#migrating-from-another-sdk)
- [Support](#support)
- [License](#license)

## Why Revnix?

- [Unity IAP bridge](https://www.revnix.io/docs/unity/make-purchases). One call registers a Unity IAP order; with Unity IAP 5 the StoreKit 2 proof travels along, so iOS purchases are verified on arrival.
- [Entitlements that work offline](https://www.revnix.io/docs/unity/check-entitlements). Entitlement checks are cached on device, so a player who paid stays unlocked without a network.
- [Remote paywalls](https://www.revnix.io/docs/paywall-builder). Design paywalls in the dashboard and ship copy, prices and layout changes without an app release.
- [A/B tests and holdouts](https://www.revnix.io/docs/ab-tests). Split a placement between variants, target an audience and ship the winner from the dashboard.
- [Attribution and deep links](https://www.revnix.io/docs/attribution). Install attribution, deferred deep links, Apple Search Ads and MMP forwarding from the same package.
- [Integrations](https://www.revnix.io/docs/integrations). Send subscription events to analytics and messaging tools, or to your own server through signed webhooks.
- [Real-time analytics](https://www.revnix.io/docs/analytics). Revenue, MRR, LTV, ROAS, cohorts and funnels, filtered by store, product, channel and campaign.

## Getting Started

In Unity, open **Window → Package Manager → + → Add package from git URL** and paste:

```
https://github.com/Oth-tech/RevnixSDK-Unity.git#v0.4.0
```

Or add it to `Packages/manifest.json`:

```json
"com.revnix.sdk": "https://github.com/Oth-tech/RevnixSDK-Unity.git#v0.4.0"
```

The Unity IAP bridge switches on by itself when `com.unity.purchasing` is in the project. Read the [installation guide](https://www.revnix.io/docs/unity/installation) and [configuration reference](https://www.revnix.io/docs/unity/configuration) to set up the package.

## Quick start

```csharp
using Revnix.Unity;
using Revnix.Unity.IAP;

// 1. Configure once at startup (publishable key only, never rvx_sk_)
var revnix = RevnixSdk.Configure(
    "rvx_pk_live_…",
    "https://<your-deployment>.convex.site");

// 2. Register a Unity IAP 5 purchase in OnPurchasePending
var result = await RevnixUnityIap.Register(revnix, order);
if (result != null) await revnix.WaitForEntitlements(result.Seq);

// 3. Check access anywhere. Never throws; unknown means locked.
if (await revnix.IsEntitled("pro")) { /* unlock */ }
```

Your API key and deployment URL are in the dashboard under Settings. See [API keys](https://www.revnix.io/docs/api-keys).

## Purchases and entitlements without server code

**Unity IAP shows the store sheet. Revnix does everything after it.**

- `RevnixUnityIap.Register(revnix, order)` sends the purchase with its StoreKit 2 proof, so iOS purchases are verified immediately. On Unity IAP 4.x, pass the purchased `Product` instead.
- `WaitForEntitlements(seq)` waits until the purchase is on the ledger, so the unlock is instant.
- Entitlements and placements are cached on device, so paid players stay unlocked offline.
- Failed registrations are queued durably and retried on the next launch.
- No Unity IAP? Call `RegisterPurchase()` with the store token yourself.

Learn more in [Make purchases](https://www.revnix.io/docs/unity/make-purchases) and [Check entitlements](https://www.revnix.io/docs/unity/check-entitlements).

## Paywalls that update without app releases

![Revnix paywall builder: element tree, background library and a live iPhone preview](https://www.revnix.io/sdk/react-native/paywalls.png)

With the [Revnix paywall builder](https://www.revnix.io/docs/paywall-builder) you design the paywall in the dashboard and render it in your game.

- **Native UGUI renderer**: `RevnixPaywallView.Create` draws the published design under any Canvas, all nine layout templates, no prefabs or bundled assets.
- **Update without redeploying**: change prices, copy or layout any time; the next placement resolve picks it up.
- **Implicit placements**: trigger a paywall on install, launch, session start or a deep link from the dashboard; the game only handles `onImplicitPaywall`. See [Implicit placements](https://www.revnix.io/docs/implicit-placements).
- **Localized**: Restore, Terms and Privacy labels in 43 languages; `RevnixSdk.SetLocale("de")` forces one.

```csharp
var placement = await revnix.ResolvePlacement("main_paywall");
var paywall = RevnixPaywallView.Create(canvas.transform, new RevnixPaywallOptions
{
    Config = placement.Paywall.Config,
    Packages = packages,
    OnPurchase = packageId => StartPurchase(packageId),
    Client = revnix,
    PlacementKey = "main_paywall",
});
```

Learn more in [Show paywalls](https://www.revnix.io/docs/unity/show-paywalls) and [Placements](https://www.revnix.io/docs/placements).

## A/B tests with a built-in holdout

![Revnix A/B test results with a winner and credible intervals](https://www.revnix.io/sdk/react-native/ab-test.png)

- Split a placement's traffic between offering and paywall variants; each player gets a sticky variant.
- Add a **holdout** variant that shows no paywall at all, to measure what the paywall is really worth.
- Target an audience with `SetAttributes`, for example "US players on 4.2+". See [Audiences](https://www.revnix.io/docs/audiences).
- Ship the winner from the dashboard; the game needs no change.

Learn more in [A/B tests](https://www.revnix.io/docs/ab-tests).

## Attribution and deep links

![Revnix MRR by country, last 90 days](https://www.revnix.io/sdk/react-native/attribution.png)

- **Install attribution** from Revnix links, read back with `GetAttribution()`. See [Attribution](https://www.revnix.io/docs/attribution).
- **Deferred deep links** resolve on first launch through `onDeferredDeepLink`. See [Deferred deep links](https://www.revnix.io/docs/deferred-deep-links).
- **Apple Search Ads** through `HandleAttributionToken`. See [Apple Search Ads](https://www.revnix.io/docs/apple-search-ads).
- **MMP forwarding**, **ad revenue** and **uninstall measurement**. See [MMP attribution](https://www.revnix.io/docs/mmp-attribution), [Ad revenue](https://www.revnix.io/docs/ad-revenue) and [Uninstall measurement](https://www.revnix.io/docs/uninstall-measurement).

## Real-time analytics for your Unity game

![Revnix overview: active subscriptions, revenue and MRR over 90 days](https://www.revnix.io/sdk/react-native/analytics.png)

- Revenue, MRR, ARR, ARPU, LTV and ROAS, updated from the ledger as purchases arrive.
- Cohorts, retention, funnels and predicted LTV. See [Analytics](https://www.revnix.io/docs/analytics).
- Custom in-game events with `Track("level_complete")`.
- Saved reports, alerts and a REST API. See [Reports](https://www.revnix.io/docs/reports) and [REST API](https://www.revnix.io/docs/rest-api).

## Platform Support

| Requirement | Version |
| --- | --- |
| Unity | 2021.3 and later |
| Unity IAP (optional) | 5.0+ for verified iOS purchases, 4.0+ supported |
| Purchases | iOS (App Store) and Android (Google Play) |
| Entitlements, placements, paywalls | Every platform Unity builds for, including the Editor |

Call the SDK from the main thread. Awaited calls resume on Unity's main thread.

## Documentation

- [Overview](https://www.revnix.io/docs/unity)
- [Installation](https://www.revnix.io/docs/unity/installation)
- [Configuration](https://www.revnix.io/docs/unity/configuration)
- [Make purchases](https://www.revnix.io/docs/unity/make-purchases)
- [Check entitlements](https://www.revnix.io/docs/unity/check-entitlements)
- [Show paywalls](https://www.revnix.io/docs/unity/show-paywalls)
- [API reference](https://www.revnix.io/docs/unity/reference)

Revnix also ships SDKs for [React Native](https://www.revnix.io/docs/react-native), [iOS](https://www.revnix.io/docs/ios), [Android](https://www.revnix.io/docs/android), [Flutter](https://www.revnix.io/docs/flutter) and [Capacitor](https://www.revnix.io/docs/capacitor).

## Migrating from another SDK

Moving from Adapty or RevenueCat? Revnix imports your customers and purchase history.

- [Migrate from Adapty](https://www.revnix.io/docs/migrate-from-adapty)
- [Migrate from RevenueCat](https://www.revnix.io/docs/migrate-from-revenuecat)

## Support

- Email [support@revnix.io](mailto:support@revnix.io) with questions, bugs or feature requests.
- Check the [status page](https://www.revnix.io/status) for incidents.

## License

Revnix SDK is available under the MIT license. See [LICENSE](https://github.com/Oth-tech/RevnixSDK-Unity/blob/main/LICENSE.md) for details.
