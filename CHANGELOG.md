# Changelog

## Unreleased

- **Dashboard QR/link paywall preview.** `HandleDeepLink` now recognises
  `<scheme>://revnix-preview?revnix_preview=<token>`, fetches the draft
  paywall from `GET /v1/paywalls/preview/{token}` and hands it to
  `onImplicitPaywall` (`Resolution.PlacementKey == "revnix_preview"`) without
  ever firing `deeplink_open`. A preview never sends paywall analytics and
  disables purchases in the UI.

- **`IsEntitled` honours a revoked key.** A deliberate rejection
  (401/403/404/409) now answers false instead of the cached snapshot;
  transient failures still serve the cache.

- **Device attribute contract.** `ResolvePlacement` sends the device facts —
  platform, OS version, app version, locale, currency, model, install date,
  SDK version, sandbox, first open — as `X-Revnix-Device`, so targeting rules
  and audiences can use them from the first launch and the dashboard shows
  them on the customer as `device.*` attributes. `RevnixSdk.Configure` fills
  `RevnixConfig.Device` from `UnityDeviceFacts.Detect()`; a partial
  `Device` you set keeps its fields; `SendDeviceFacts = false` disables. Unity
  IAP exposes no storefront, so set `Device.Storefront` yourself if the app
  knows it. (REV-268)

- **Three style fields the designs use now reach the renderer.** `translate`,
  `clipPath` and `fillSize` were named by no field on the block style, so the
  decoder dropped them before the renderer ever saw them. `translate` is now
  drawn — folded into the UGUI pivot, so a badge pinned with `left: 50%` plus
  `translate: "-50% 0"` centres instead of sitting half its own width to the
  right. `clipPath` and `fillSize` are decoded and reported through
  `onDiagnostic` rather than silently ignored: UGUI allows one graphic per
  element, so an arbitrary polygon clip needs a mask mesh and a tiled gradient
  needs a repeating material. `filter` is reported the same way. `textWrap` is
  decoded but not reported: it moves a line break, not the design.
- **`LogPaywallEvent` — the six paywall interactions** (`Selected`,
  `PurchaseStarted`, `PurchaseAbandoned`, `PurchaseFailed`, `Restore`,
  `Error`), i.e. what the customer did BETWEEN the display and the close.
  `RevnixPaywallView` sends all but the purchase outcome, which only your game
  can see. All six are pure history: over-reporting skews a report, it never
  grants or revokes access.
- **`LogPaywallDisplay` and `LogPaywallClosed`** complete the impression.
  `LogPaywallDisplay` is the same beacon as `LogPaywallShown` but hands back
  the view id it minted; pass that id to `LogPaywallClosed` and
  `LogPaywallEvent` so the halves of one display pair up. `LogPaywallClosed` is
  idempotent per view id, so a retry or a double-dismiss cannot count two.
- **The selected plan is drawn from the design.** Every block carries
  `SelectedStyle` and `Visibility`, so a plan card can change its fill, border
  and text when its package is the selected one, and a block can be drawn only
  while selected (a filled radio dot) or only while not.
- **Element gradients paint on every block**, keeping their transparency, and
  a `text` element paints no fill of its own (UGUI's one-graphic limit — give a
  text badge a card behind it).

## 0.2.0

A/B experiments (REV-219).

- `ResolvePlacement` sends the customer id (`?customer=`) so the server can
  assign a sticky experiment variant; the served offering/paywall are
  already the variant's
- `PlacementResolution.Experiment` (`PlacementExperiment` — `Key`,
  `VariantId`) identifies the assignment for attribution; null when no
  experiment applies or the server predates experiments
- Cached placement resolutions round-trip the experiment through the
  offline fallback
- `SetAttributes(Dictionary<string, object>)` (REV-033) — the write half of
  audience targeting. String/number values upsert, `null` deletes; anything
  else throws `ArgumentException` before a request goes out. Awaits the
  write and throws on failure, unlike the fire-and-forget beacons, since the
  next `ResolvePlacement` may depend on it. `email`/`username` are reserved
  (secret key only) and a server-set attribute cannot be changed from a
  device; both reject the whole batch

## 0.1.0

Initial release — client-contract parity with revnix-swift 0.1.0,
revnix-kotlin 0.1.0, and revnix_flutter 0.1.0.

- `RevnixSdk.Configure` facade (UnityWebRequest transport, PlayerPrefs
  storage, launch-time queue drain + install beacon)
- Entitlements: `Entitlements()` (network-first, offline cache with `Stale`,
  soft TTL, in-flight coalescing, clock-rollback defense),
  `CachedEntitlements()`, fail-closed `IsEntitled()`,
  read-your-writes `WaitForEntitlements(seq)`
- Purchases: `RegisterPurchase` with durable PlayerPrefs retry queue
  (`RetryPendingPurchases`, `PendingPurchaseCount`), canonical-id adoption
- Placements: `ResolvePlacement` with offline fallback; typed remote paywall
  contract (`PlacementPaywall`/`PaywallConfig` — 9 layout templates, mode,
  review + offer blocks, footer links); `LogPaywallShown`,
  `RegisterInstall` beacons
- Typed error taxonomy (`RevnixException` subclasses), retryable vs
  deliberate, `Retry-After` support
- Optional Unity IAP bridge (`RevnixUnityIap`) behind
  `com.unity.purchasing` version defines
