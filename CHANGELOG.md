# Changelog

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
