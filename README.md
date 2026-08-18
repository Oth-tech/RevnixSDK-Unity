# Revnix Unity SDK (`com.revnix.sdk`)

[Revnix](https://revnix.io) subscriptions and entitlements for Unity —
the same client contract as `revnix-react`, `revnix-swift`, `revnix-kotlin`,
and `revnix_flutter`, ported to C#:

- **Offline-resilient entitlements** — network-first reads; transient
  failures serve the cached snapshot flagged `Stale`, deliberate rejections
  (401/403/404/409) always throw, so a kill-switch can never be defeated by a
  cache. Clock-rollback detection, 3-day expiry grace, 14-day cache ceiling.
- **One fetch per screen** — soft TTL + in-flight coalescing; a screen full
  of `IsEntitled` gates costs one request.
- **Durable purchase registration** — failed registrations queue in
  PlayerPrefs and drain idempotently on every launch (the server dedupes on
  the purchase key).
- **Read-your-writes** — `WaitForEntitlements(seq)` polls until the ledger
  reflects the purchase, so the unlock is immediate, not eventually.
- **Placements** — `ResolvePlacement` returns the offering + typed remote
  paywall config (layout template, copy, review/offer blocks), with an
  offline fallback to the last resolution.
- **Paywall UI** — `RevnixPaywallView` renders the published config as
  runtime-generated UGUI (all nine layout templates, same structure as the
  revnix-react renderer); no prefabs, no TextMeshPro, no extra dependencies.
- **A/B experiments** — placements resolve with the customer id, so a
  running experiment serves a sticky variant per customer;
  `PlacementResolution.Experiment` carries the assignment (`Key`,
  `VariantId`) for attribution, null when no experiment applies.
- **Audience targeting** — `SetAttributes` writes the customer attributes
  those experiments target, so a mobile-only game can run "US players on
  4.2+" tests.
- **Optional Unity IAP bridge** — auto-detected via version defines; maps a
  purchased `Product` straight to a registration.

## Install

Package Manager → *Add package from git URL*:

```
https://github.com/Oth-tech/RevnixSDK-Unity.git
```

(or add `"com.revnix.sdk": "https://github.com/Oth-tech/RevnixSDK-Unity.git"`
to `Packages/manifest.json`.)

## Quick start

```csharp
using Revnix;
using Revnix.Unity;

var revnix = RevnixSdk.Configure(
    "rvx_pk_live_…",                      // publishable key only — never rvx_sk_
    "https://your-deployment.convex.site");

// Gate. Never throws; unknown or unreachable means locked.
if (await revnix.IsEntitled("pro")) { /* … */ }

// Paywall
var placement = await revnix.ResolvePlacement("main_paywall");
await revnix.LogPaywallShown(placementKey: "main_paywall");

// With Unity IAP (com.unity.purchasing in the project):
var result = await RevnixUnityIap.Register(revnix, purchasedProduct);
if (result != null) await revnix.WaitForEntitlements(result.Seq);
```

`Configure` also drains the purchase retry queue and reports the install —
both idempotent, both off the critical path.

## Paywall UI

`RevnixPaywallView` (namespace `Revnix.Unity.UI`, its own asmdef) draws the
placement's published paywall config with runtime-generated UGUI — every
layout template the dashboard offers, in lockstep with the revnix-react
renderer. Deliberately zero extra dependencies: no prefabs, no bundled
assets, legacy `UnityEngine.UI.Text` instead of TextMeshPro. Parent it under
a Canvas (the scene needs an EventSystem for taps):

```csharp
using Revnix.Unity.UI;

var placement = await revnix.ResolvePlacement("main_paywall");
var packages = new List<RevnixPaywallPackage>
{
    // PriceLabel must be the store's localized price (Unity IAP metadata) —
    // the display must never disagree with the charge.
    new RevnixPaywallPackage { PackageId = "monthly", Title = "Monthly", PriceLabel = "$4.99/mo" },
    new RevnixPaywallPackage { PackageId = "yearly",  Title = "Yearly",  PriceLabel = "$39.99/yr" },
};

var paywall = RevnixPaywallView.Create(canvas.transform, new RevnixPaywallOptions
{
    Config = placement.Paywall.Config,
    Packages = packages,
    OnPurchase = packageId => StartPurchase(packageId),
    OnRestore = () => RestorePurchases(),
    Client = revnix,                  // reports one paywall.viewed per Create
    PlacementKey = "main_paywall",
    PaywallId = placement.Paywall.PaywallId,
});

paywall.SetLoading(true);  // spinner in the CTA while the purchase runs
paywall.Dismiss();         // tear down after the unlock
```

`SelectedPackageId` reads the current selection — or set it to drive the
selection from the app (controlled mode, RN semantics; set null to hand
control back to the view). Footer links honor the dashboard's footer config;
an explicit `OnTerms`/`OnPrivacy` handler wins over a config URL, which
otherwise opens via `Application.OpenURL`.

## Targeting an A/B audience

An experiment can be narrowed to an audience — conditions over customer
attributes. `SetAttributes` supplies the facts those conditions read:

```csharp
await revnix.SetAttributes(new Dictionary<string, object> {
    ["country"] = "US",
    ["app_version"] = "4.2.0",
    ["levels_completed"] = 12,
    ["stale_key"] = null,          // null deletes the key
});
```

Values must be a string, a number, or null — anything else throws
`ArgumentException` before a request goes out. This awaits the write and
throws on failure, unlike the fire-and-forget beacons, because the next
`ResolvePlacement` may depend on it. Set an audience's attributes *before* the
first resolve on a covered placement; eligibility is checked at that resolve.
`email` and `username` are reserved (secret key, from your own backend), and
an attribute your backend already set cannot be changed from a device — both
reject the whole batch rather than applying part of it.

## Design notes

- The resilience policy is a **product contract** shared by every Revnix SDK;
  `revnix-sdk`'s `resilience.test.ts` is the behavioral spec. Don't "fix"
  policy here — change the spec first.
- The core (`Runtime/Core`) is engine-free C# with injected transport,
  storage, and clock — it compiles against .NET and is verified off-device.
  Unity specifics live in `Runtime/Unity` (UnityWebRequest, PlayerPrefs).
- Purchases are **claims**: Google purchase tokens and Apple transaction ids
  register without device-side proof and land `Provisional` until the server
  corroborates with the store (RTDN / App Store Server API). Nothing about
  that is Unity-specific — it is how the platform works.
- Main thread: call the SDK from the main thread (UnityWebRequest's
  requirement). Awaited continuations resume on Unity's SynchronizationContext.
