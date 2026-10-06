# Revnix Unity SDK (`com.revnix.sdk`)

[Revnix](https://revnix.io) subscriptions and entitlements for Unity, the same client contract as `revnix-react`, `revnix-swift`, `revnix-kotlin`,
and `revnix_flutter`, ported to C#:

- **Offline-resilient entitlements**: network-first reads; transient
  failures serve the cached snapshot flagged `Stale`, deliberate rejections
  (401/403/404/409) always throw, so a kill-switch can never be defeated by a
  cache. Clock-rollback detection, 3-day expiry grace, 14-day cache ceiling.
- **One fetch per screen**: soft TTL + in-flight coalescing; a screen full
  of `IsEntitled` gates costs one request.
- **Durable purchase registration**: failed registrations queue in
  PlayerPrefs and drain idempotently on every launch (the server dedupes on
  the purchase key).
- **Read-your-writes**: `WaitForEntitlements(seq)` polls until the ledger
  reflects the purchase, so the unlock is immediate, not eventually.
- **Placements**: `ResolvePlacement` returns the offering + typed remote
  paywall config (layout template, copy, review/offer blocks), with an
  offline fallback to the last resolution.
- **Paywall UI**: `RevnixPaywallView` renders the published config as
  runtime-generated UGUI (all nine layout templates, same structure as the
  revnix-react renderer); no prefabs, no TextMeshPro, no extra dependencies.
- **A/B experiments**: placements resolve with the customer id, so a
  running experiment serves a sticky variant per customer;
  `PlacementResolution.Experiment` carries the assignment (`Key`,
  `VariantId`) for attribution, null when no experiment applies.
- **Audience targeting**: `SetAttributes` writes the customer attributes
  those experiments target, so a mobile-only game can run "US players on
  4.2+" tests.
- **Device facts**: `ResolvePlacement` sends platform, OS and app version,
  locale, currency, model, sandbox, install date and first open as
  `X-Revnix-Device`, so targeting rules can use them; `SendDeviceFacts = false`
  disables. Install reports also carry `DeviceKey`
  (`SystemInfo.deviceUniqueIdentifier`) so the server can flag a reinstall.
- **Optional Unity IAP bridge**: auto-detected via version defines
  (`com.unity.purchasing` 4.0.0+); maps a purchased `Product` straight to a
  registration.

## Install

Requires Unity 2021.3 or later. Package Manager → *Add package from git URL*:

```
https://github.com/Oth-tech/RevnixSDK-Unity.git
```

(or add `"com.revnix.sdk": "https://github.com/Oth-tech/RevnixSDK-Unity.git"`
to `Packages/manifest.json`.)

## Quick start

```csharp
using Revnix;
using Revnix.Unity;
using Revnix.Unity.IAP;

var revnix = RevnixSdk.Configure(
    "rvx_pk_live_…",                      // publishable key only, never rvx_sk_
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

`Configure` also drains the purchase retry queue and reports the install. Both are idempotent, and both stay off the critical path.

## Paywall UI

`RevnixPaywallView` (namespace `Revnix.Unity.UI`, its own asmdef) draws the
placement's published paywall config with runtime-generated UGUI; every
layout template the dashboard offers, in lockstep with the revnix-react
renderer. Deliberately zero extra dependencies: no prefabs, no bundled
assets, legacy `UnityEngine.UI.Text` instead of TextMeshPro. Parent it under
a Canvas (the scene needs an EventSystem for taps):

```csharp
using Revnix.Unity.UI;

var placement = await revnix.ResolvePlacement("main_paywall");
var packages = new List<RevnixPaywallPackage>
{
    // PriceLabel must be the store's localized price (Unity IAP metadata); // the display must never disagree with the charge.
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

`SelectedPackageId` reads the current selection, or set it to drive the
selection from the app (controlled mode, RN semantics; set null to hand
control back to the view). Footer links honor the dashboard's footer config;
an explicit `OnTerms`/`OnPrivacy` handler wins over a config URL, which
otherwise opens via `Application.OpenURL`.

`RevnixSdk.SetLocale("de")` forces every paywall built afterwards into that
language (null or empty clears it); `RevnixPaywallOptions.Locale` overrides
it for one view.

### Reporting the whole life of a display

Passing `Client` reports the impression, and with `OnClose` set
`RevnixPaywallView` pairs the close for you. Drawing your own paywall, the three calls are yours:

| Call | What it does |
|---|---|
| `LogPaywallDisplay(…) → Task<string>` | The impression beacon, returning the `viewId` it minted. Prefer it over `LogPaywallShown` whenever you intend to report the close or an interaction: that id is what pairs the halves of one display. |
| `LogPaywallClosed(viewId, …)` | Ends that display. Idempotent per view id, so a retry or a double-dismiss cannot count two. Without it a funnel knows how many saw the paywall, not how many left without buying. |
| `LogPaywallEvent(evt, viewId, …)` | One of six interactions (`Selected`, `PurchaseStarted`, `PurchaseAbandoned`, `PurchaseFailed`, `Restore`, `Error`), i.e. what happened BETWEEN the display and the close. |

The purchase **outcome** is always yours, even with the built-in renderer:
your app makes the Unity IAP call, so only your app sees whether the sheet was
cancelled or the store refused the payment.

```csharp
var viewId = await revnix.LogPaywallDisplay("main_paywall", placement.Paywall.PaywallId);

// From IStoreListener.OnPurchaseFailed.
public void OnPurchaseFailed(Product product, PurchaseFailureReason reason)
{
    var evt = reason == PurchaseFailureReason.UserCancelled
        ? RevnixPaywallEvent.PurchaseAbandoned
        : RevnixPaywallEvent.PurchaseFailed;
    _ = revnix.LogPaywallEvent(evt, viewId, productId: product.definition.id);
}

await revnix.LogPaywallClosed(viewId, "main_paywall", placement.Paywall.PaywallId);
```

All of these are fire-and-forget and pure ledger history: over-reporting can
skew a report, it can never grant or revoke access.

### Implicit placements

Six placements resolve without a `ResolvePlacement` call: `app_install`,
`app_launch`, `session_start`, `deeplink_open`, `paywall_decline` and
`transaction_abandon`. Passing `onImplicitPaywall` to `RevnixSdk.Configure`
turns them on (off by default: no extra requests for the other five
moments, though `HandleDeepLink` always reports the link it is handed); the
facade creates a hidden, scene-surviving GameObject whose
`OnApplicationPause` / `OnApplicationFocus` feed `session_start`, and the SDK
asks `GET /v1/config` at launch and at each new session, so a game that
configured none of the six costs that request and nothing else.

```csharp
var mainThread = System.Threading.SynchronizationContext.Current;
RevnixSdk.Configure("rvx_pk_live_…", "https://….convex.site",
    onImplicitPaywall: trigger =>
        // Background continuation - dispatch before touching the scene.
        mainThread.Post(_ => ShowPaywall(trigger.Resolution), null));

// deeplink_open is the one moment the SDK cannot see itself:
if (!string.IsNullOrEmpty(Application.absoluteURL))
    _ = RevnixSdk.Client.HandleDeepLink(Application.absoluteURL);
Application.deepLinkActivated += url => _ = RevnixSdk.Client.HandleDeepLink(url);
```

`Application.absoluteURL` holds the link that launched the closed game;
`deepLinkActivated` fires for links that arrive while it runs. Run both
once per launch, where `Configure` runs: `absoluteURL` keeps the latest
link for the life of the process, so reading it again on a later scene
load would count the same open twice. From v0.3.0,
`HandleDeepLink` also ignores a null or empty URL itself.

Set `PlacementKey = trigger.Resolution.PlacementKey` on the paywall options:
that marks the display as implicit and is what stops a `paywall_decline`
paywall from firing `paywall_decline` again. A close is a decline: never
report one for a display that ended in a purchase.

The same `HandleDeepLink` call also recognises the dashboard's QR/link
preview (`<scheme>://revnix-preview?revnix_preview=<token>`, scanned or
tapped from the paywall builder): it fetches the draft paywall and hands it
to `onImplicitPaywall` with `Resolution.PlacementKey == "revnix_preview"`,
never firing `deeplink_open` or writing to the ledger. Purchases are
disabled on a preview; a tap logs a warning instead.

### Deferred deep links

A click on a Revnix link sends Android to Google Play with the query string
as the install referrer, and iOS to the App Store, remembering the click for
up to an hour. Only a link whose scheme matches the game's configured URL
scheme is ever returned. Pass `onDeferredDeepLink` to `Configure` to get it,
delivered at most once per install:

```csharp
RevnixSdk.Configure("rvx_pk_live_…", "https://….convex.site",
    onDeferredDeepLink: (url, match) =>
        mainThread.Post(_ => OpenUrl(url), null));
```

`RegisterInstall` (fired at startup, install platform reported as
`"ios"`/`"android"` automatically) can only come back with a `probabilistic`
match (a same-network click within the last hour), so it can be wrong on a
shared network. It never carries an install referrer, so it can never answer
`exact`.

`exact` only ever comes from Android's Play Install Referrer, and reading it
is manual here, as it is in Flutter and in revnix-capacitor 1.2.3 (only the
native Android SDK, and Capacitor from the release after 1.2.3, read it for
you). A Unity game must add
`implementation 'com.android.installreferrer:installreferrer:2.2'` to its own
`Assets/Plugins/Android/mainTemplate.gradle`, read the referrer itself, and
hand the raw string to `RevnixSdk.HandleInstallReferrer(referrer)`, which
reports it to the server and delivers to the same `onDeferredDeepLink`
handler. Skip this call on Android and no deferred link (exact or
probabilistic) ever arrives there; iOS has no referrer to read, so it
relies on `RegisterInstall`'s probabilistic match alone. Route the URL
yourself; optionally also pass it to `HandleDeepLink` for `deeplink_open`
paywall rules.

### Apple Search Ads attribution

iOS only. Like the Play Install Referrer above, reading this is manual here:
this package ships no `ios/` native layer, so there is nothing here to call
Apple's `AAAttribution` from. Mint the token yourself in your own native iOS
plugin and hand it over:

```csharp
RevnixSdk.HandleAttributionToken(token);
```

The server resolves it to which Apple Search Ads campaign, if any, drove the
install. `"resolved"` (a campaign was named) and `"organic"` (Apple answered,
not an ASA install) are final; `"pending"` means Apple couldn't yet answer (a
freshly minted token is unregistered on Apple's side for a few seconds) and
is worth retrying with a fresh token on the next cold start. The facade call
is fire-and-forget; call `RevnixSdk.Client.HandleAttributionToken(token)`
directly if you need that verdict to decide whether to retry.

An email link is often wrapped by the sender's click-tracking domain, e.g.
`https://click.mailchimp.com/track/abc` instead of
`com.voigu.app://promo?utm_source=email&utm_campaign=summer50`. Unwrap it
first with `RevnixSdk.ResolveDeepLink(url)`, then route the result yourself
and pass it to `HandleDeepLink`:

```csharp
var resolved = await RevnixSdk.ResolveDeepLink(wrappedUrl);
await RevnixSdk.Client.HandleDeepLink(resolved);
```

Failure returns the input unchanged rather than throwing, so the result can
still be an http(s) URL if the chain could not be unwrapped; check its
scheme before routing.

### Getting the last deep link later

`HandleDeepLink` and a delivered deferred deep link both persist the URL, so
a game that swallowed the original delivery (e.g. behind login or
onboarding) can ask for it again at any point:

```csharp
var last = RevnixSdk.Client.GetLastDeepLink();
if (last != null) OpenUrl(last.Url);
```

Returns null when nothing has been recorded yet. Survives relaunch, and is
not cleared by `Logout`.

### The install-attribution verdict

Which signal the install was matched on, and the campaign facts that came
with it. Pass `onAttribution` to `Configure` to be told whenever the verdict
changes (once when the install is first attributed, again on a
re-attribution, never twice for the same verdict), or ask for it yourself:

```csharp
RevnixSdk.Configure("rvx_pk_live_…", "https://….convex.site",
    onAttribution: a => mainThread.Post(_ => Log(a.InstallMatch, a.Campaign), null));

var attribution = await RevnixSdk.Client.GetAttribution();
if (attribution != null) Log(attribution.InstallMatch, attribution.Campaign);
```

`InstallMatch` is `"referrer"`, `"click"`, `"impression"` or `"organic"`;
`AttributedAt` and `ReattributedAt` are unix ms (`ReattributedAt` is 0 when
the customer was never re-attributed), and `LinkToken`, `ReferrerSource`,
`MatchSignals`, `Source`, `Medium`, `Campaign`, `Term` and `Content` are null
when the server sent none. `GetAttribution` returns null when no install has
been attributed yet (a cold start can ask before its own install report
lands) and on any failure; it never throws. The SDK only asks for the
verdict on its own (after the install, install-referrer and Apple Search Ads
reports) when `onAttribution` is set.

### Ad revenue

Call `LogAdRevenue` from your mediation SDK's paid-event callback (AdMob
`OnPaidEvent`, AppLovin MAX `OnAdRevenuePaidEvent`). Fire-and-forget: a
non-finite or zero-or-less `revenue` is dropped with a diagnostic, and the
optional strings are truncated to 100 characters. It feeds the Return on ad
spend table, see [Ad revenue](https://revnix.io/docs/ad-revenue):

```csharp
// from your mediation SDK's paid-event callback
_ = RevnixSdk.Client.LogAdRevenue(
    revenue, "USD",
    network: networkName,
    mediation: "applovin_max",
    adUnit: adUnitId,
    format: "rewarded");
```

### Custom events

Call `Track` to report a custom in-app event (not a purchase — purchases go
through `RegisterPurchase`). Fire-and-forget, like `LogAdRevenue`; the event
name must match `^[a-z0-9_]{1,64}$` or it's dropped with a diagnostic:

```csharp
_ = RevnixSdk.Client.Track(
    "level_up",
    new Dictionary<string, object> { ["level"] = 5 });
```

### MMP attribution

If you already run an MMP (Adjust, AppsFlyer, Singular, Branch, Kochava,
Tenjin, Airbridge), call `SetAttribution` from its attribution callback so
Revnix credits revenue to the right network/campaign. Fire-and-forget, like
`LogAdRevenue`:

```csharp
// Adjust's attribution callback
void OnAttributionChanged(AdjustAttribution attribution)
{
    _ = RevnixSdk.Client.SetAttribution(
        "adjust", attribution.Network,
        campaign: attribution.Campaign,
        adGroup: attribution.Adgroup,
        creative: attribution.Creative);
}

// AppsFlyer's onConversionDataSuccess
void OnConversionDataSuccess(Dictionary<string, object> data)
{
    if ((string)data["af_status"] == "Organic") return;
    _ = RevnixSdk.Client.SetAttribution(
        "appsflyer", (string)data["media_source"],
        campaign: data.TryGetValue("campaign", out var c) ? (string)c : null,
        adGroup: data.TryGetValue("af_adset", out var g) ? (string)g : null,
        creative: data.TryGetValue("af_ad", out var cr) ? (string)cr : null);
}
```

### Uninstall measurement

Revnix measures uninstalls the way Adjust/AppsFlyer do: register the
device's push token from whatever push plugin the game already uses
(Firebase Messaging on Android, APNs on iOS), and once a day a silent push
probes it; when the store reports the token dead, the customer gets an
`app.uninstalled` event:

```csharp
_ = RevnixSdk.Client.SetPushToken(token);
```

Fire-and-forget, like `SetAttribution`. Only sends when the device facts
platform is `ios`/`android` (no-op elsewhere). Requires the game's own push
setup: Firebase Cloud Messaging on Android, Push Notifications + Background
Modes → Remote notifications on iOS. See
[Uninstall measurement](https://revnix.io/docs/uninstall-measurement).

## Targeting an A/B audience

An experiment can be narrowed to an audience: conditions over customer
attributes. `SetAttributes` supplies the facts those conditions read:

```csharp
await revnix.SetAttributes(new Dictionary<string, object> {
    ["country"] = "US",
    ["app_version"] = "4.2.0",
    ["levels_completed"] = 12,
    ["stale_key"] = null,          // null deletes the key
});
```

Values must be a string, a number, or null, anything else throws
`ArgumentException` before a request goes out. This awaits the write and
throws on failure, unlike the fire-and-forget beacons, because the next
`ResolvePlacement` may depend on it. Set an audience's attributes *before* the
first resolve on a covered placement; eligibility is checked at that resolve.
`email` and `username` are reserved (secret key, from your own backend), and
an attribute your backend already set cannot be changed from a device; both
reject the whole batch rather than applying part of it.

## Design notes

- The resilience policy is a **product contract** shared by every Revnix SDK;
  `revnix-sdk`'s `resilience.test.ts` is the behavioral spec. Don't "fix"
  policy here, change the spec first.
- The core (`Runtime/Core`) is engine-free C# with injected transport,
  storage, and clock; it compiles against .NET and is verified off-device.
  Unity specifics live in `Runtime/Unity` (UnityWebRequest, PlayerPrefs).
- Purchases are **claims**: Google purchase tokens and Apple transaction ids
  register without device-side proof and land `Provisional` until the server
  corroborates with the store (RTDN / App Store Server API). Nothing about
  that is Unity-specific; it is how the platform works.
- Main thread: call the SDK from the main thread (UnityWebRequest's
  requirement). Awaited continuations resume on Unity's SynchronizationContext.
