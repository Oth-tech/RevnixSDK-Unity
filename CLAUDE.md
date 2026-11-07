# RevnixSDK-Unity

Unity (UPM) client SDK for the Revnix backend (`../revnix-app`, Convex HTTP `/v1`). Package `com.revnix.sdk`, Unity 2021.3+, MIT. Repo `Oth-tech/RevnixSDK-Unity`, default branch `main`.

## Install (README)
OpenUPM `openupm add com.revnix.sdk`, or git URL `https://github.com/Oth-tech/RevnixSDK-Unity.git#vX.Y.Z`. No npm registry; the git tag IS the release.

## Layout
- `package.json`: name, `version`, dependency `com.unity.modules.video`, sample `Samples~/QuickStart`.
- `Runtime/Core/` (asmdef `Revnix.Sdk`, no references): engine-free logic. `RevnixClient.cs` (entitlements, placements, purchases, events, attribution; `SdkVersion` const), `RevnixConfig.cs`, `RevnixHttp.cs` / `RevnixStorage.cs` (`IRevnixHttp` / `IRevnixStorage` seams), `RevnixModels.cs`, `RevnixJson.cs`, `RevnixError.cs`, `RevnixPaywall*.cs`, `RevnixImplicitPlacements.cs`, `RevnixDeviceFacts.cs`. Must never reference `UnityEngine`.
- `Runtime/Unity/`: Unity glue. `RevnixFacade.cs` = entry point `RevnixSdk.Configure(apiKey, baseUrl)`, `UnityWebRequestHttp`, `PlayerPrefsStorage`, `RevnixLifecycleBehaviour`, `UnityDeviceFacts`, `RevnixDeviceIntegrity` (App Attest / Play Integrity).
- `Runtime/UI/` (asmdef `Revnix.Sdk.UI`, refs `Revnix.Sdk` + `UnityEngine.UI`): UGUI paywall renderer `RevnixPaywallView.Create`, block renderer, backgrounds.
- `Runtime/Plugins/iOS/RevnixAppAttest.mm`: the only native plugin (links `DeviceCheck`). No Android .aar; Android needs Gradle dep `com.google.android.play:integrity:1.6.0`, declared in `Editor/RevnixDependencies.xml` (EDM4U). `Editor/` has no C#.
- `UnityIAP/` (asmdef `Revnix.Sdk.UnityIAP`, refs `Revnix.Sdk` + `UnityEngine.Purchasing`): `RevnixUnityIap.cs`. `versionDefines` on `com.unity.purchasing`: 4.0.0+ → `REVNIX_UNITY_IAP`, 5.0.0+ → `REVNIX_UNITY_IAP_5`. IAP 5: `Register(client, Order)` carries the StoreKit 2 JWS; the `Product` overloads are `[Obsolete]` and Apple purchases from them stay provisional.
- `Tests/Editor/` (asmdef `Revnix.Sdk.Tests`, Editor-only, NUnit via Unity Test Framework, refs `Revnix.Sdk` only): ~24 files testing `Core` with fake `IRevnixHttp` / storage / clock (`Now`, `Delay` injectable). `Runtime/Unity`, `Runtime/UI` and `UnityIAP` have no tests.
- `Samples~/QuickStart/QuickStart.cs`, `scripts~/release.sh`: `~` folders are ignored by Unity (no `.meta`).
- Every other file and folder has a `.meta` (Unity GUID). Always commit the `.meta` with its file; new files and folders need one; never regenerate GUIDs.

## Wire contract
- `RevnixSdk.Configure(apiKey, baseUrl)`: publishable `rvx_pk_…` key only (never ship `rvx_sk_`), `baseUrl = https://<deployment>.convex.site`. Headers `Authorization: Bearer <key>`, `X-Revnix-SDK: revnix-unity/<SdkVersion>`, `X-Revnix-Device`.
- Endpoints in `RevnixClient.cs`: `/v1/installs`, `/v1/config`, `/v1/purchases`, `/v1/placements/{key}/offering`, `/v1/placements/triggered`, `/v1/customers/{id}/entitlements`, `/v1/paywalls/events`, `/v1/events`, `/v1/paywalls/preview/{token}`. `identify` is server-side; the SDK does not call `/v1/identify`. Keep wire shapes in sync with `revnix-app/convex` (see `Tests/Editor/PaywallSelectionWireTests.cs` + `Resources/paywall-selection-wire.json`).
- Entitlements cached on device (30 s soft TTL, 14 d offline max); unknown = locked; `IsEntitled` never throws. Failed purchase registrations go to a durable retry queue (`RetryPendingPurchases`, retried on launch); non-retryable errors are dropped. Events batch 100 per request, 30 s hold after a retryable failure, 429 honors `Retry-After`.
- Attribution: install report + `GetAttribution`, deferred deep links, Apple Search Ads token, optional device integrity (`DeviceIntegrity` off by default). Call from the main thread.

## Commands (no Unity Editor on this Mac)
The only harness is `.github/ci/Revnix.Sdk.Tests.csproj` (net10.0, LangVersion 9.0, NUnit 3.14). It compiles `Runtime/Core/**` + `Tests/Editor/**`, excluding `PaywallSelectionWireTests.cs` and `PaywallLocalizationTests.cs`.
```bash
/opt/homebrew/bin/dotnet test .github/ci/Revnix.Sdk.Tests.csproj
```
CI runs the same. `Runtime/Unity`, `Runtime/UI` and `UnityIAP` are compiled by nothing, locally or in CI: review them by eye and keep the `#if REVNIX_UNITY_IAP*` branches consistent. `.github/ci/bin` and `obj` are local build output, gitignored.

## Git, CI, release
- `.github/workflows/ci.yml`: PRs to `main` + manual; ubuntu, `actions/setup-dotnet` 10.0.x, `dotnet test .github/ci/Revnix.Sdk.Tests.csproj`. No Unity, no game-ci, no license secrets.
- `.github/workflows/release.yml`: manual dispatch (`bump`, `version`, `app_sha`, `run_key`), dispatched by `../revnix-app/.github/workflows/release-sdks.yml` (job `revnix-unity`). Runs tests, skips if nothing but md/.github/scripts~ changed since the last tag, runs `scripts~/release.sh` (CHANGELOG `## Unreleased` → dated version; `### Breaking` → major, `### Added` → minor, else patch), PR `release/vX.Y.Z` merged with `--merge`, tag `vX.Y.Z`, GitHub release "Revnix Unity SDK X.Y.Z".
- Versions must agree: `package.json`, `RevnixClient.SdkVersion` (`Runtime/Core/RevnixClient.cs`), README `#vX.Y.Z` URLs. release.sh edits all three; never hand-bump.
- Every user-visible change gets a bullet under `## Unreleased` in `CHANGELOG.md`.
- Merge commits only into `main` (ruleset; squash is refused). Opposite of revnix-app, which squash-merges into `dev`.
- Docs for this SDK live in revnix-app (`/docs/unity*`); API changes need a separate `dev` PR there.

## Gotchas
- `/usr/bin/git` is blocked by the Xcode license prompt on this Mac; use `/Applications/GitHub Desktop.app/Contents/Resources/app/git/bin/git`.
- OpenUPM mirrors the git tags; nothing to publish after a release.
