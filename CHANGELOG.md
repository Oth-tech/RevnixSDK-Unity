# Changelog

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
