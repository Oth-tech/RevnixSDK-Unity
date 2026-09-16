using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Revnix
{
    /// <summary>
    /// Core client — a faithful port of revnix-react's resilience policy
    /// (revnix-sdk `resilience.test.ts` is the behavioral spec, matching the
    /// Swift/Kotlin/Flutter ports):
    /// - entitlements are network-first; TRANSIENT failures serve the cache
    ///   flagged `stale`, DELIBERATE rejections (401/403/404/409) always throw;
    /// - cached entitlements past expiry by &gt;3 days serve inactive; snapshots
    ///   older than OfflineMaxCacheAge (14 d) or behind a &gt;5 min clock rollback
    ///   serve all-inactive;
    /// - a soft TTL plus in-flight coalescing keeps a screen of gates to one
    ///   fetch;
    /// - failed purchase registrations persist to a retry queue keyed
    ///   `source:token:transactionId` and drain idempotently (the server
    ///   dedupes on the shared purchaseKey).
    /// </summary>
    public sealed class RevnixClient
    {
        public const string SdkVersion = "0.3.0";

        private const long ExpiryGraceMs = 3L * 24 * 3600 * 1000;
        private const long RollbackToleranceMs = 5L * 60 * 1000;
        private const int CacheCustomers = 4;

        private static readonly Regex PreviewTokenRe =
            new Regex(@"[?&]revnix_preview=([0-9a-f]{64})(?:[&#]|$)");

        private const string KeyCustomerId = "revnix.customerId";
        private const string KeyWallClock = "revnix.lastWallClock";
        private const string KeyQueue = "revnix.pendingPurchases";
        private const string KeyCacheIndex = "revnix.entIndex";
        private const string KeyInstalledAt = "revnix.installedAt";
        private const string KeyDeferredDeepLinkDelivered = "revnix.deferredDeepLinkDelivered";

        private readonly RevnixConfig _config;
        private readonly object _lock = new object();
        private readonly Random _random = new Random();
        private Task<CustomerEntitlements> _inflight;
        private int _bgFailures;
        // REV-268: the encoded X-Revnix-Device value, built on the first
        // resolve and kept for the client's lifetime (so firstOpen holds for
        // the whole first session). _deviceHeaderBuilt distinguishes "built,
        // nothing to send" from "not built yet".
        private string _deviceHeader;
        private bool _deviceHeaderBuilt;

        // REV-272: implicit placements. Off unless the host said what to do
        // with a paywall — without a handler there is nothing to do with the
        // answer, and firing anyway would spend requests and ledger rows on
        // nobody's behalf.
        private bool ImplicitEnabled => _config.ImplicitPlacementsEnabled;
        /// <summary>Which of the six this app has configured. One in-flight
        /// task, coalesced; a SUCCESS is memoised for the client's lifetime, a
        /// FAILURE clears the field so the next moment asks again — an offline
        /// cold start must not disable every implicit moment until the next
        /// launch. <see cref="_implicitConfigRetryAt"/> keeps that retry from
        /// happening on every paywall close of an offline session: one probe
        /// per minute.</summary>
        private Task<HashSet<string>> _implicitConfig;
        private long _implicitConfigRetryAt = long.MinValue;
        private const long ImplicitConfigRetryHoldMs = 60_000;
        private Action _lifecycleCancel;
        /// <summary>When the app last went to the BACKGROUND, or null while it
        /// has not. A return after <c>SessionTimeout</c> away is a new session;
        /// sooner is an app switch. Measured from the background transition,
        /// not from the last return — the latter would mint a session after 35
        /// minutes of continuous play plus a three-second switch.</summary>
        private long? _lastBackgroundAt;
        /// <summary>LOOP GUARD: view ids of displays that came FROM an implicit
        /// trigger. A paywall shown because a paywall was dismissed must not
        /// itself fire `paywall_decline`, or the player is handed the same
        /// screen forever. Never released — a double-tapped close reports two
        /// closes on one id, and releasing on the first would let the second
        /// re-enter the loop. Bounded by implicit displays per process.</summary>
        private readonly HashSet<string> _implicitViewIds = new HashSet<string>();
        private bool _implicitStarted;
        /// <summary>Set by <see cref="StopImplicitPlacements"/>. Checked after
        /// every await on the implicit paths — a launch batch mid-flight when
        /// the client was retired must not hand a paywall to a host that has
        /// moved on.</summary>
        private volatile bool _implicitStopped;
        /// <summary>The cold-start batch while it runs, completing with whether
        /// it presented. A deep link delivered on the first frame waits for it,
        /// or the player gets the launch paywall AND the link paywall.</summary>
        private Task<bool> _launchBatch;
        /// <summary>True when <see cref="CustomerId"/> minted the id on THIS
        /// launch — the install signal, the same one /v1/installs uses. In
        /// memory on purpose: a stored "seen this id" marker would fire
        /// <c>app_install</c> for the entire existing base on the first launch
        /// after an SDK upgrade, and again after every <see cref="Logout"/>.</summary>
        private volatile bool _mintedThisLaunch;

        public RevnixClient(RevnixConfig config)
        {
            if (string.IsNullOrEmpty(config.ApiKey)) throw new ArgumentException("ApiKey is required");
            if (string.IsNullOrEmpty(config.BaseUrl)) throw new ArgumentException("BaseUrl is required");
            if (config.Http == null) throw new ArgumentException("Http transport is required");
            _config = config;
        }

        // ── Implicit placements (REV-272) ────────────────────────────────────

        /// <summary>
        /// Begin watching for the six implicit moments. The Unity facade calls
        /// this after Configure; idempotent, and <see cref="StopImplicitPlacements"/>
        /// makes it callable again. A no-op when implicit placements are off.
        ///
        /// A cold start is always both a launch AND a session — an operator who
        /// configured only <c>session_start</c> still wants the first one — and
        /// it is an install too when this launch minted the customer id. The
        /// three run in order, most specific first, and only the first that
        /// resolves to a paywall is handed to the host. All are still REPORTED
        /// — a launch is a launch whether or not a paywall showed — but a game
        /// that configured all three must not have three paywalls pushed onto
        /// its first frame.
        /// </summary>
        public async Task StartImplicitPlacements()
        {
            _implicitStopped = false;
            if (!ImplicitEnabled || _implicitStarted) return;
            _implicitStarted = true;

            // Subscribed BEFORE the batch, which can take a full network
            // timeout when offline: a player who backgrounds the game during
            // that window and comes back an hour later is a session, and
            // missing the background transition would lose it.
            if (_config.Lifecycle != null)
            {
                try
                {
                    _lifecycleCancel = _config.Lifecycle.OnStateChange(OnAppState);
                }
                catch (Exception err)
                {
                    // A broken adapter costs session detection, not the client.
                    Diagnostic("lifecycleSubscribe", err.Message);
                }
            }

            // CustomerId() is what sets _mintedThisLaunch, so it runs first.
            CustomerId();
            var moments = new List<RevnixImplicitPlacement>();
            if (_mintedThisLaunch) moments.Add(RevnixImplicitPlacement.AppInstall);
            moments.Add(RevnixImplicitPlacement.AppLaunch);
            moments.Add(RevnixImplicitPlacement.SessionStart);

            var batch = RunLaunchBatch(moments);
            _launchBatch = batch;
            await batch;
            _launchBatch = null;
        }

        private async Task<bool> RunLaunchBatch(List<RevnixImplicitPlacement> moments)
        {
            var presented = false;
            foreach (var placement in moments)
            {
                var shown = await FireImplicit(placement, null, !presented);
                presented = presented || shown;
            }
            return presented;
        }

        /// <summary>Stop watching. A replaced client would otherwise keep a
        /// foreground observer alive and mint a session on every return
        /// alongside its successor. Anything mid-flight is told to hand
        /// nothing over.</summary>
        public void StopImplicitPlacements()
        {
            _implicitStopped = true;
            _implicitStarted = false;
            var cancel = _lifecycleCancel;
            _lifecycleCancel = null;
            if (cancel != null) cancel();
        }

        /// <summary>
        /// REV-272: hand the SDK the URL that opened your game, from wherever
        /// you already receive it (<c>Application.deepLinkActivated</c>,
        /// <c>Application.absoluteURL</c>).
        ///
        /// This is the one implicit moment the SDK cannot see for itself — the
        /// URL reaches your own entry point. An ordinary link is always
        /// reported so its <c>link.*</c> attribution facts land on the
        /// customer; a paywall presents only when implicit placements are on
        /// AND <c>deeplink_open</c> is configured in the dashboard. A dashboard QR/link
        /// preview (<c>&lt;scheme&gt;://revnix-preview?revnix_preview=&lt;token&gt;</c>)
        /// is always fetched and handed to <see cref="RevnixConfig.OnImplicitPaywall"/>
        /// — it never fires <c>deeplink_open</c>. Delivered on the first
        /// frame, while the cold-start batch is still deciding what to show,
        /// both paths wait for the batch first. An empty or null URL is
        /// ignored.
        /// </summary>
        public async Task HandleDeepLink(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            var previewMatch = PreviewTokenRe.Match(url);
            if (previewMatch.Success)
            {
                await PresentPreview(previewMatch.Groups[1].Value);
                return;
            }
            var extra = new Dictionary<string, object>
            {
                ["url"] = url.Length > 1024 ? url.Substring(0, 1024) : url,
            };
            var batch = _launchBatch;
            var present = batch == null || !(await batch);
            await FireImplicit(RevnixImplicitPlacement.DeeplinkOpen, extra, present);
        }

        private async Task PresentPreview(string token)
        {
            try
            {
                var batch = _launchBatch;
                if (batch != null) await batch;
                if (_implicitStopped) return;
                var raw = await Request("GET", new[] { "v1", "paywalls", "preview", token });
                if (_implicitStopped) return;
                var handler = _config.OnImplicitPaywall;
                if (handler == null) return;
                var resolution = Decode(raw, PlacementResolution.FromJson);
                handler(new RevnixImplicitTrigger
                {
                    Placement = RevnixImplicitPlacement.DeeplinkOpen,
                    Resolution = resolution,
                });
            }
            catch (Exception err)
            {
                _bgFailures += 1;
                Diagnostic("preview", err.Message);
            }
        }

        private async void OnAppState(RevnixAppState state)
        {
            try
            {
                if (_implicitStopped) return;
                if (state == RevnixAppState.Background)
                {
                    // First report wins: a platform that repeats "background"
                    // (pause AND focus-loss both firing) must not keep resetting
                    // the clock forward.
                    if (_lastBackgroundAt == null) _lastBackgroundAt = _config.Now();
                    return;
                }
                // A foreground with no background before it is the launch
                // itself, which the batch already counted.
                var since = _lastBackgroundAt;
                _lastBackgroundAt = null;
                if (since == null) return;
                // An app switch is not a session.
                if (_config.Now() - since.Value >= (long)_config.SessionTimeout.TotalMilliseconds)
                {
                    // A new session is also when the memoised config is
                    // re-asked: the server promises an operator's change shows
                    // up within ~30 s, and a process backgrounded for days would
                    // otherwise keep firing a moment the operator turned off —
                    // or never fire one they turned on — until the next launch.
                    lock (_lock) { _implicitConfig = null; }
                    await FireImplicit(RevnixImplicitPlacement.SessionStart);
                }
            }
            catch (Exception err)
            {
                // async void: an escaping exception would be an unobserved
                // crash rather than a diagnostic.
                Diagnostic("implicitForeground", err.Message);
            }
        }

        /// <summary>Which of the six this game has configured. See
        /// <see cref="_implicitConfig"/>.</summary>
        private Task<HashSet<string>> ImplicitConfig()
        {
            lock (_lock)
            {
                if (_implicitConfig != null) return _implicitConfig;
                // Inside the hold after a failure: answer "none" without a
                // request.
                if (_config.Now() < _implicitConfigRetryAt)
                {
                    return Task.FromResult(new HashSet<string>());
                }
                _implicitConfig = LoadImplicitConfig();
                return _implicitConfig;
            }
        }

        private async Task<HashSet<string>> LoadImplicitConfig()
        {
            try
            {
                // The id does not change the answer — this route is
                // customer-independent — it only picks the server's rate-limit
                // bucket, so one busy game cannot 429 its own fleet off the
                // feature.
                var raw = await Request(
                    "GET",
                    new[] { "v1", "config" },
                    null,
                    new Dictionary<string, string> { ["customer"] = CustomerId() });
                return RevnixImplicitConfig.Parse(raw);
            }
            catch (Exception err)
            {
                Diagnostic("implicitConfig", err.Message);
                // Only a SUCCESSFUL read is memoised: an offline cold start
                // must not disable every implicit moment for the rest of the
                // session. Retried after a short hold rather than on the very
                // next moment, so an offline burst of paywall interactions
                // costs one probe, not one each.
                lock (_lock)
                {
                    _implicitConfig = null;
                    _implicitConfigRetryAt = _config.Now() + ImplicitConfigRetryHoldMs;
                }
                return new HashSet<string>();
            }
        }

        /// <summary>Report one implicit moment and present whatever it resolves
        /// to. Returns true when a paywall was handed to the host.
        /// <c>present</c> false still reports the moment (its ledger event is a
        /// fact either way) but hands nothing over — how the launch batch keeps
        /// a cold start to ONE paywall. Never throws: this runs on a launch and
        /// on every return to the foreground.</summary>
        private async Task<bool> FireImplicit(
            RevnixImplicitPlacement placement,
            Dictionary<string, object> extra = null,
            bool present = true)
        {
            if (_implicitStopped) return false;
            var key = RevnixImplicitPlacements.KeyOf(placement);
            var resolve = ImplicitEnabled && (await ImplicitConfig()).Contains(key);
            if ((!resolve && placement != RevnixImplicitPlacement.DeeplinkOpen) || _implicitStopped)
            {
                return false;
            }

            var body = new Dictionary<string, object>
            {
                ["customerId"] = CustomerId(),
                ["placement"] = key,
                // One id per occurrence: retries of the same launch are
                // absorbed, a genuine second launch counts separately.
                // Required server-side for the three that append an event.
                ["occurrenceId"] = Guid.NewGuid().ToString("D").ToLowerInvariant(),
                ["occurredAt"] = _config.Now(),
                ["sdkVersion"] = SdkVersion,
            };
            if (extra != null)
            {
                foreach (var pair in extra) body[pair.Key] = pair.Value;
            }
            if (!resolve) body["resolve"] = false;
            var header = DeviceHeader();
            var headers = header == null
                ? null
                : new Dictionary<string, string> { ["X-Revnix-Device"] = header };
            try
            {
                var raw = await Request(
                    "POST", new[] { "v1", "placements", "triggered" }, body, null, headers);
                // The route's own body shape, read before the model decode: an
                // unconfigured moment answers 200 with `paywall: null` and no
                // `status` at all — a normal state, not a failure to count
                // against the server.
                if (!RevnixImplicitConfig.Resolved(raw) || !present || !resolve || _implicitStopped)
                {
                    return false;
                }
                var resolution = Decode(raw, PlacementResolution.FromJson);
                if (resolution.Paywall == null) return false;
                var handler = _config.OnImplicitPaywall;
                if (handler == null) return false;
                handler(new RevnixImplicitTrigger
                {
                    Placement = placement,
                    Resolution = resolution,
                });
                return true;
            }
            catch (Exception err)
            {
                _bgFailures += 1;
                Diagnostic("implicit:" + key, err.Message);
                return false;
            }
        }

        /// <summary>The two moments that happen ON a paywall, with the loop
        /// guard applied. <c>fromPaywallId</c> travels so the server can refuse
        /// to hand back the very paywall being dismissed.</summary>
        private async Task FireImplicitFromPaywall(
            RevnixImplicitPlacement placement, string viewId, string paywallId)
        {
            if (!ImplicitEnabled) return;
            lock (_lock)
            {
                // One hop, never a chain: this display was itself implicit.
                if (_implicitViewIds.Contains(viewId)) return;
            }
            var extra = new Dictionary<string, object> { ["fromViewId"] = viewId };
            if (paywallId != null) extra["fromPaywallId"] = paywallId;
            await FireImplicit(placement, extra);
        }

        // ── Identity ─────────────────────────────────────────────────────────

        /// <summary>Anonymous id, minted and persisted on first call.</summary>
        public string CustomerId()
        {
            var existing = _config.Storage.Get(KeyCustomerId);
            if (existing != null) return existing;
            var minted = RevnixIdentity.GenerateAnonymousId();
            _config.Storage.Set(KeyCustomerId, minted);
            // REV-272: the install signal. Logout() below deliberately does not
            // set it — a new anonymous session is not a new install.
            _mintedThisLaunch = true;
            return minted;
        }

        /// <summary>Fresh anonymous identity. Call at sign-out, or the next
        /// user inherits the previous one's cached entitlements. Per-customer
        /// caches age out via LRU.</summary>
        public string Logout()
        {
            var minted = RevnixIdentity.GenerateAnonymousId();
            _config.Storage.Set(KeyCustomerId, minted);
            return minted;
        }

        // ── Entitlements ─────────────────────────────────────────────────────

        public Task<CustomerEntitlements> Entitlements()
        {
            var cid = CustomerId();
            var nowMs = _config.Now();
            var rolledBack = UpdateWallClock(nowMs);

            // Soft TTL: a live snapshot this fresh is authoritative. A TTL of
            // zero disables the shortcut entirely (always-fetch).
            var ttlMs = (long)_config.EntitlementsTtl.TotalMilliseconds;
            if (ttlMs > 0 && !rolledBack)
            {
                var entry = CacheEntryFor(cid);
                if (entry != null && nowMs - entry.FetchedAt <= ttlMs)
                {
                    var fresh = entry.Snapshot;
                    fresh.Stale = false;
                    fresh.FetchedAt = entry.FetchedAt;
                    return Task.FromResult(fresh);
                }
            }

            // In-flight coalescing: a screen full of gates shares one request.
            Task<CustomerEntitlements> deferred;
            lock (_lock)
            {
                deferred = _inflight;
                if (deferred == null)
                {
                    var started = FetchEntitlements(cid, nowMs, rolledBack);
                    _inflight = started;
                    deferred = started;
                    // The field must clear even when no one awaits the failure.
                    // NOTE: with a synchronously-completing transport this
                    // continuation can run DURING ContinueWith (reentrant on
                    // this thread) — everything it touches must already be
                    // consistent, hence assigning `deferred` first and the
                    // identity check before clearing.
                    started.ContinueWith(t =>
                    {
                        lock (_lock)
                        {
                            if (_inflight == started) _inflight = null;
                        }
                        _ = t.Exception; // observed; callers get it via their await
                    }, TaskContinuationOptions.ExecuteSynchronously);
                }
            }
            return deferred;
        }

        /// <summary>Network-only read — no TTL shortcut, no cache fallback.
        /// Throws on any failure. The read-your-writes poll needs a genuinely
        /// fresh cursor.</summary>
        private async Task<CustomerEntitlements> FetchFreshEntitlements(string cid, long nowMs)
        {
            var raw = await Request("GET", new[] { "v1", "customers", cid, "entitlements" });
            var snapshot = DecodeEntitlements(raw);
            snapshot.Stale = false;
            snapshot.FetchedAt = nowMs;
            StoreCacheEntry(new CacheEntry { Snapshot = snapshot, FetchedAt = nowMs }, cid);
            return snapshot;
        }

        private async Task<CustomerEntitlements> FetchEntitlements(string cid, long nowMs, bool rolledBack)
        {
            try
            {
                return await FetchFreshEntitlements(cid, nowMs);
            }
            catch (RevnixException err)
            {
                // Deliberate rejections rethrow: a kill-switch must not be
                // defeated by the cache.
                if (!err.IsRetryable) throw;
                var entry = CacheEntryFor(cid);
                if (entry == null) throw;
                return ApplyOfflinePolicy(entry, nowMs, rolledBack);
            }
        }

        /// <summary>Last cached snapshot with the offline policy applied; null
        /// when the customer has never had a live read.</summary>
        public CustomerEntitlements CachedEntitlements()
        {
            var cid = CustomerId();
            var nowMs = _config.Now();
            var entry = CacheEntryFor(cid);
            if (entry == null) return null;
            return ApplyOfflinePolicy(entry, nowMs, UpdateWallClock(nowMs));
        }

        /// <summary>Gate helper: never throws. A transient failure answers from the offline cache; a deliberate rejection (401/403/404/409), an unknown id, or no cache answers false.</summary>
        public async Task<bool> IsEntitled(string entitlementId)
        {
            CustomerEntitlements snapshot;
            try
            {
                snapshot = await Entitlements();
            }
            catch (RevnixException)
            {
                snapshot = null;
            }
            return snapshot != null && snapshot.IsEntitled(entitlementId);
        }

        /// <summary>Read-your-writes: poll entitlements until the response's
        /// ledger cursor is at least <paramref name="seq"/>, then return it.
        /// Resolves with the LAST read if the schedule runs out or reads only
        /// come from the stale cache — it never spins forever and never throws
        /// a timeout, so a slow ledger degrades to "not unlocked yet" rather
        /// than an error the app has to handle.</summary>
        public async Task<CustomerEntitlements> WaitForEntitlements(long seq)
        {
            var cid = CustomerId();
            var last = await Entitlements();
            foreach (var delayFor in _config.ReadYourWritesDelays)
            {
                if (last.Stale != true && last.Cursor >= seq) return last;
                // ±20% jitter: promo pushes synchronize a fleet's purchases,
                // and identical schedules keep every device's poll in lockstep
                // against our own rate limiter.
                await _config.Delay(Jittered((int)delayFor.TotalMilliseconds));
                try
                {
                    // TTL-bypassing: the point of this poll is a FRESH cursor,
                    // so the soft TTL must not answer it from the last snapshot.
                    last = await FetchFreshEntitlements(cid, _config.Now());
                }
                catch (RevnixException err)
                {
                    // The server said exactly how long to back off — honor it
                    // instead of fighting our own rate limiter.
                    if (err is RevnixRateLimitException rate && rate.RetryAfterMs is long retryAfter && retryAfter > 0)
                    {
                        await _config.Delay((int)Math.Min(retryAfter, int.MaxValue));
                    }
                    // Transient failure: keep the last read, let the schedule run.
                }
            }
            return last;
        }

        private CustomerEntitlements ApplyOfflinePolicy(CacheEntry entry, long nowMs, bool rolledBack)
        {
            var snapshot = entry.Snapshot;
            var tooOld = nowMs - entry.FetchedAt > (long)_config.OfflineMaxCacheAge.TotalMilliseconds;
            var entitlements = new List<Entitlement>();
            foreach (var ent in snapshot.Entitlements)
            {
                if (tooOld || rolledBack)
                {
                    entitlements.Add(ent.Inactive());
                }
                else if (ent.IsActive && ent.ExpiresAt.HasValue && nowMs > ent.ExpiresAt.Value + ExpiryGraceMs)
                {
                    entitlements.Add(ent.Inactive());
                }
                else
                {
                    entitlements.Add(ent);
                }
            }
            return new CustomerEntitlements
            {
                CustomerId = snapshot.CustomerId,
                Cursor = snapshot.Cursor,
                Entitlements = entitlements,
                Stale = true,
                FetchedAt = entry.FetchedAt,
            };
        }

        // ── Purchases ────────────────────────────────────────────────────────

        public async Task<RegisterPurchaseResult> RegisterPurchase(RegisterPurchaseInput input)
        {
            var cid = CustomerId();
            try
            {
                var result = await PostPurchase(input, cid);
                if (result.CustomerId != cid && !string.IsNullOrEmpty(result.CustomerId))
                {
                    // Identity resolution merged us — adopt the canonical id.
                    _config.Storage.Set(KeyCustomerId, result.CustomerId);
                }
                RemoveQueued(QueueKey(input));
                return result;
            }
            catch (RevnixException err)
            {
                if (err.IsRetryable) Enqueue(input);
                throw;
            }
        }

        /// <summary>Drain the persisted queue. Safe to call on every launch and
        /// foreground — the server dedupes on the shared purchaseKey. Returns
        /// the number delivered.</summary>
        public async Task<int> RetryPendingPurchases()
        {
            var cid = CustomerId();
            var delivered = 0;
            foreach (var item in QueuedItems())
            {
                try
                {
                    await PostPurchase(item.ToInput(), cid);
                    RemoveQueued(item.Key);
                    delivered += 1;
                }
                catch (RevnixException err)
                {
                    if (!err.IsRetryable)
                    {
                        // The server refused on purpose — retrying forever is noise.
                        RemoveQueued(item.Key);
                        Diagnostic("retryPendingPurchases", "dropped " + item.Key + ": " + err.Message);
                    }
                    else
                    {
                        _bgFailures += 1;
                        Diagnostic("retryPendingPurchases", "kept " + item.Key + ": " + err.Message);
                    }
                }
            }
            return delivered;
        }

        public int PendingPurchaseCount() => QueuedItems().Count;

        private async Task<RegisterPurchaseResult> PostPurchase(RegisterPurchaseInput input, string cid)
        {
            var body = new Dictionary<string, object>
            {
                ["customerId"] = cid,
                ["source"] = input.Source.Wire(),
                ["token"] = input.Token,
                ["productId"] = input.ProductId,
                ["transactionId"] = input.TransactionId,
            };
            if (input.OccurredAt.HasValue) body["occurredAt"] = input.OccurredAt.Value;
            if (input.ExpiresAt.HasValue) body["expiresAt"] = input.ExpiresAt.Value;
            if (input.SignedTransactionInfo != null) body["signedTransactionInfo"] = input.SignedTransactionInfo;
            if (input.RawPayload != null) body["rawPayload"] = input.RawPayload;

            var raw = await Request("POST", new[] { "v1", "purchases" }, body);
            return Decode(raw, RegisterPurchaseResult.FromJson);
        }

        // ── Placements & telemetry ───────────────────────────────────────────

        public async Task<PlacementResolution> ResolvePlacement(string key)
        {
            // REV-219: the customer id lets the server assign a sticky
            // experiment variant; older servers ignore the parameter.
            var query = new Dictionary<string, string> { ["customer"] = CustomerId() };
            // REV-268: the device facts ride along so targeting rules see THIS
            // device on THIS request, and the server stores them as device.*
            // attributes. Older servers ignore the header.
            var header = DeviceHeader();
            var headers = header == null
                ? null
                : new Dictionary<string, string> { ["X-Revnix-Device"] = header };
            try
            {
                var raw = await Request("GET", new[] { "v1", "placements", key, "offering" }, null, query, headers);
                var resolution = Decode(raw, PlacementResolution.FromJson);
                _config.Storage.Set(PlacementKey(key), raw);
                return resolution;
            }
            catch (RevnixException err)
            {
                if (!err.IsRetryable) throw;
                var cached = _config.Storage.Get(PlacementKey(key));
                if (cached == null) throw;
                try
                {
                    return Decode(cached, PlacementResolution.FromJson);
                }
                catch (RevnixException)
                {
                    throw err;
                }
            }
        }

        /// <summary>REV-268: assemble the device facts once. installedAt is the
        /// first launch this storage ever saw — written then, read back on every
        /// later one — and firstOpen is true for the whole of that first
        /// session.</summary>
        private string DeviceHeader()
        {
            lock (_lock)
            {
                if (_deviceHeaderBuilt) return _deviceHeader;
                _deviceHeaderBuilt = true;
                if (!_config.SendDeviceFacts || _config.Device == null) return null;
                long installedAt;
                bool firstOpen;
                var stored = _config.Storage.Get(KeyInstalledAt);
                if (stored != null && long.TryParse(stored, out var parsed) && parsed > 0)
                {
                    installedAt = parsed;
                    firstOpen = false;
                }
                else
                {
                    installedAt = _config.Now();
                    firstOpen = true;
                    _config.Storage.Set(KeyInstalledAt, installedAt.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                _deviceHeader = _config.Device.EncodedHeader(SdkVersion, installedAt, firstOpen);
                return _deviceHeader;
            }
        }

        /// <summary>Fire-and-forget install beacon; once per customer id.</summary>
        public async Task RegisterInstall(string platform = null, string appVersion = null)
        {
            var cid = CustomerId();
            if (_config.Storage.Get(InstallReportedKey(cid)) != null) return;
            var body = new Dictionary<string, object>
            {
                ["customerId"] = cid,
                ["sdkVersion"] = SdkVersion,
            };
            if (platform != null) body["platform"] = platform;
            if (appVersion != null) body["appVersion"] = appVersion;
            try
            {
                var raw = await Request("POST", new[] { "v1", "installs" }, body);
                _config.Storage.Set(InstallReportedKey(cid), "1");
                DeliverDeferredDeepLink(raw);
            }
            catch (RevnixException err)
            {
                _bgFailures += 1;
                Diagnostic("registerInstall", err.Message);
            }
        }

        /// <summary>Reports the raw Android Play Install Referrer string
        /// (e.g. <c>utm_source=instagram&amp;utm_medium=cpc</c>), read by the
        /// caller via the Play Install Referrer library. <paramref name="platform"/>
        /// and <paramref name="appVersion"/> mirror <see cref="RegisterInstall"/> —
        /// pass them here too, since whichever of the two calls reaches the
        /// server first is the one that creates the install row, and the
        /// other's fields are discarded as a duplicate. Call from the main
        /// thread, like the rest of the SDK. Safe to call late or twice;
        /// fire-and-forget, never throws.</summary>
        public async Task HandleInstallReferrer(string referrer, string platform = null, string appVersion = null)
        {
            if (string.IsNullOrWhiteSpace(referrer)) return;
            var cid = CustomerId();
            var body = new Dictionary<string, object>
            {
                ["customerId"] = cid,
                ["occurredAt"] = _config.Now(),
                ["sdkVersion"] = SdkVersion,
                ["installReferrer"] = referrer.Length > 1024 ? referrer.Substring(0, 1024) : referrer,
            };
            if (platform != null) body["platform"] = platform;
            if (appVersion != null) body["appVersion"] = appVersion;
            try
            {
                var raw = await Request("POST", new[] { "v1", "installs" }, body);
                DeliverDeferredDeepLink(raw);
            }
            catch (Exception err)
            {
                _bgFailures += 1;
                Diagnostic("handleInstallReferrer", err.Message);
            }
        }

        private void DeliverDeferredDeepLink(string raw)
        {
            try
            {
                var handler = _config.OnDeferredDeepLink;
                if (handler == null) return;
                var map = RevnixJson.ParseObject(raw);
                var dlMap = RevnixJson.GetObject(map, "deferredDeepLink");
                if (dlMap == null) return;
                var url = RevnixJson.GetString(dlMap, "url");
                if (string.IsNullOrEmpty(url)) return;
                DeferredDeepLinkMatch match;
                switch (RevnixJson.GetString(dlMap, "match"))
                {
                    case "exact": match = DeferredDeepLinkMatch.Exact; break;
                    case "probabilistic": match = DeferredDeepLinkMatch.Probabilistic; break;
                    default: return;
                }
                lock (_lock)
                {
                    if (_config.Storage.Get(KeyDeferredDeepLinkDelivered) != null) return;
                    _config.Storage.Set(KeyDeferredDeepLinkDelivered, "1");
                }
                handler(url, match);
            }
            catch (Exception err)
            {
                Diagnostic("deferredDeepLink", err.Message);
            }
        }

        /// <summary>Fire-and-forget impression beacon (feeds funnels + view
        /// conversions). Call when the paywall becomes visible.</summary>
        public async Task LogPaywallShown(string placementKey = null, string paywallId = null)
        {
            await LogPaywallDisplay(placementKey, paywallId);
        }

        /// <summary>
        /// The same beacon, returning the view id it generated (REV-252).
        ///
        /// Hand that id to <see cref="LogPaywallClosed"/> when the customer
        /// dismisses THIS display: the two events sharing one view id is what
        /// lets the ledger pair a close with the display it ended, and the gap
        /// between their timestamps is the customer's dwell on the screen. The
        /// id comes back even when delivery fails — the caller's pairing must
        /// not depend on the network, and the close beacon carries its own
        /// idempotency key.
        /// </summary>
        public async Task<string> LogPaywallDisplay(string placementKey = null, string paywallId = null)
        {
            var viewId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            if (placementKey == RevnixImplicitPlacements.PreviewPlacementKey) return viewId;
            // REV-272 LOOP GUARD: a display whose placement is one of the six
            // came FROM an implicit trigger, so its dismissal must not fire
            // another one — otherwise "show a win-back when a paywall is
            // declined" hands the player the same screen until they quit.
            // Recognised from the placementKey the caller reports; a caller
            // that reports none cannot be protected here, which is why the
            // server keeps its own same-paywall backstop.
            if (ImplicitEnabled && RevnixImplicitPlacements.FromKey(placementKey) != null)
            {
                lock (_lock) { _implicitViewIds.Add(viewId); }
            }
            var body = new Dictionary<string, object>
            {
                ["customerId"] = CustomerId(),
                ["viewId"] = viewId,
                ["sdkVersion"] = SdkVersion,
            };
            if (placementKey != null) body["placementKey"] = placementKey;
            if (paywallId != null) body["paywallId"] = paywallId;
            try
            {
                await Request("POST", new[] { "v1", "paywalls", "viewed" }, body);
            }
            catch (RevnixException err)
            {
                _bgFailures += 1;
                Diagnostic("logPaywallShown", err.Message);
            }
            return viewId;
        }

        /// <summary>
        /// Fire-and-forget dismissal beacon (REV-252) — the other half of a
        /// display's life. Idempotent per view id, exactly like the view
        /// report. Pass the id <see cref="LogPaywallDisplay"/> returned.
        ///
        /// A close is a DECLINE. Do not report one for a display that ended in
        /// a purchase — with implicit placements on, a close is also the
        /// <c>paywall_decline</c> moment, and a win-back offer seconds after a
        /// successful purchase is the one thing an operator never means.
        /// </summary>
        public async Task LogPaywallClosed(string viewId, string placementKey = null, string paywallId = null)
        {
            if (placementKey == RevnixImplicitPlacements.PreviewPlacementKey) return;
            var body = new Dictionary<string, object>
            {
                ["customerId"] = CustomerId(),
                ["viewId"] = viewId ?? "",
                ["sdkVersion"] = SdkVersion,
            };
            if (placementKey != null) body["placementKey"] = placementKey;
            if (paywallId != null) body["paywallId"] = paywallId;
            try
            {
                await Request("POST", new[] { "v1", "paywalls", "closed" }, body);
            }
            catch (RevnixException err)
            {
                _bgFailures += 1;
                Diagnostic("logPaywallClosed", err.Message);
            }
            // REV-272: the dismissal IS the `paywall_decline` moment. No second
            // ledger event — the server reuses the paywall.closed just reported
            // — so this is only the resolve that decides what is attached.
            await FireImplicitFromPaywall(
                RevnixImplicitPlacement.PaywallDecline, viewId ?? "", paywallId);
        }

        /// <summary>
        /// Report one of the six paywall interactions (REV-263) — what the
        /// customer DID on a display, between the
        /// <see cref="LogPaywallDisplay"/> that opened it and the
        /// <see cref="LogPaywallClosed"/> (or purchase) that ended it.
        ///
        /// Fire-and-forget like the other beacons: never throws.
        ///
        /// <paramref name="viewId"/> is the id
        /// <see cref="LogPaywallDisplay"/> returned for THIS display. Passing
        /// it is what threads the whole life of one impression together and
        /// puts the event on the paywall's own analytics row.
        ///
        /// RevnixPaywallView reports Selected, PurchaseStarted, Restore and a
        /// no-products Error for you. The purchase OUTCOME is yours: only your
        /// game performs the IAP call, so report PurchaseAbandoned /
        /// PurchaseFailed from your own Unity IAP failure callbacks
        /// (<c>PurchaseFailureReason.UserCancelled</c> is an abandonment,
        /// anything else is a failure).
        ///
        /// <paramref name="eventId"/> is the idempotency key and defaults to
        /// the view id, which caps the report at one per display per event.
        /// Pass one per occurrence — and reuse it across your own retries — to
        /// record each occurrence.
        /// </summary>
        public async Task LogPaywallEvent(
            RevnixPaywallEvent evt,
            string viewId,
            string placementKey = null,
            string paywallId = null,
            string productId = null,
            string code = null,
            string message = null,
            string eventId = null)
        {
            if (placementKey == RevnixImplicitPlacements.PreviewPlacementKey) return;
            var body = new Dictionary<string, object>
            {
                ["customerId"] = CustomerId(),
                ["viewId"] = viewId ?? "",
                ["event"] = RevnixPaywallEventNames.Wire(evt),
                ["sdkVersion"] = SdkVersion,
            };
            if (eventId != null) body["eventId"] = eventId;
            if (placementKey != null) body["placementKey"] = placementKey;
            if (paywallId != null) body["paywallId"] = paywallId;
            if (productId != null) body["productId"] = productId;
            if (code != null) body["code"] = code;
            // The server bounds `message` at 1024; trimming here keeps a long
            // localized store error from turning the whole report into a 400.
            if (message != null)
            {
                body["message"] = message.Length > 1024 ? message.Substring(0, 1024) : message;
            }
            try
            {
                await Request("POST", new[] { "v1", "paywalls", "events" }, body);
            }
            catch (RevnixException err)
            {
                _bgFailures += 1;
                Diagnostic("logPaywallEvent", err.Message);
            }
            // REV-272: backing out of the store sheet is the
            // `transaction_abandon` moment. Reuses the
            // paywall.purchase_abandoned just reported.
            if (evt == RevnixPaywallEvent.PurchaseAbandoned)
            {
                await FireImplicitFromPaywall(
                    RevnixImplicitPlacement.TransactionAbandon, viewId ?? "", paywallId);
            }
        }

        // ── Transport ────────────────────────────────────────────────────────

        /// <summary>Set attributes on the current customer (REV-033 v2).
        /// Attributes are what A/B-test audiences target — set
        /// <c>country</c>, <c>app_version</c>, <c>locale</c>, or any custom
        /// key you want to segment on. A null value deletes the key.
        ///
        /// Throws, unlike the fire-and-forget beacons: the next placement
        /// resolve may depend on these, so a silent failure would look like
        /// broken targeting. <c>email</c> and <c>username</c> are reserved
        /// (secret key only), and an attribute your backend already set
        /// cannot be changed from a device.</summary>
        public async Task SetAttributes(Dictionary<string, object> attributes)
        {
            foreach (var entry in attributes)
            {
                if (entry.Value != null && !(entry.Value is string) &&
                    !(entry.Value is int || entry.Value is long ||
                      entry.Value is float || entry.Value is double))
                {
                    throw new ArgumentException(
                        $"Attribute \"{entry.Key}\" must be a string, number, or null");
                }
            }
            var body = new Dictionary<string, object> { ["attributes"] = attributes };
            await Request("POST",
                new[] { "v1", "customers", CustomerId(), "attributes" }, body);
        }

        private async Task<string> Request(string method, string[] segments,
            Dictionary<string, object> body = null, Dictionary<string, string> query = null,
            Dictionary<string, string> extraHeaders = null)
        {
            var url = BuildUrl(segments, query);
            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + _config.ApiKey,
                ["X-Revnix-SDK"] = "revnix-unity/" + SdkVersion,
            };
            if (extraHeaders != null)
            {
                foreach (var pair in extraHeaders) headers[pair.Key] = pair.Value;
            }
            if (body != null) headers["Content-Type"] = "application/json";
            if (_bgFailures > 0)
            {
                // Server-visible client pain with zero app wiring.
                headers["X-Revnix-Bg-Failures"] = _bgFailures.ToString();
                _bgFailures = 0;
            }

            var jsonBody = body != null ? RevnixJson.Serialize(body) : null;
            var response = await _config.Http.Send(
                method, url, jsonBody, headers, (int)_config.Timeout.TotalMilliseconds);

            if (response.Status < 200 || response.Status >= 300)
            {
                var message = "";
                try
                {
                    message = RevnixJson.GetString(RevnixJson.ParseObject(response.Body), "error", "");
                }
                catch (FormatException)
                {
                    // status alone will have to do
                }
                throw RevnixException.FromHttp(response.Status, message, response.RetryAfterHeader);
            }
            return response.Body;
        }

        private string BuildUrl(string[] segments, Dictionary<string, string> query = null)
        {
            var sb = new System.Text.StringBuilder(_config.BaseUrl.TrimEnd('/'));
            foreach (var segment in segments)
            {
                sb.Append('/').Append(Uri.EscapeDataString(segment));
            }
            if (query != null)
            {
                var separator = '?';
                foreach (var pair in query)
                {
                    sb.Append(separator)
                      .Append(Uri.EscapeDataString(pair.Key))
                      .Append('=')
                      .Append(Uri.EscapeDataString(pair.Value));
                    separator = '&';
                }
            }
            return sb.ToString();
        }

        private CustomerEntitlements DecodeEntitlements(string raw)
            => Decode(raw, CustomerEntitlements.FromJson);

        private static T Decode<T>(string raw, Func<Dictionary<string, object>, T> fromJson)
        {
            Dictionary<string, object> map;
            try
            {
                map = RevnixJson.ParseObject(raw);
            }
            catch (FormatException)
            {
                // A 200 that is not our JSON = captive portal / interception —
                // retryable, so callers fall back to cache instead of
                // unlocking nothing forever.
                throw new RevnixBadResponseException();
            }
            return fromJson(map);
        }

        /// <summary>±20% jitter around a delay.</summary>
        private int Jittered(int ms)
        {
            double factor;
            lock (_random) factor = 0.8 + _random.NextDouble() * 0.4;
            return (int)(ms * factor);
        }

        // ── Cache plumbing ───────────────────────────────────────────────────

        private sealed class CacheEntry
        {
            public CustomerEntitlements Snapshot;
            public long FetchedAt;
        }

        private sealed class QueuedPurchase
        {
            public string Key;
            public string Source;
            public string Token;
            public string ProductId;
            public string TransactionId;
            public long? OccurredAt;
            public long? ExpiresAt;
            public string SignedTransactionInfo;
            public Dictionary<string, object> RawPayload;

            public RegisterPurchaseInput ToInput() => new RegisterPurchaseInput
            {
                Source = RevnixStoreWire.FromWire(Source),
                Token = Token,
                ProductId = ProductId,
                TransactionId = TransactionId,
                OccurredAt = OccurredAt,
                ExpiresAt = ExpiresAt,
                SignedTransactionInfo = SignedTransactionInfo,
                RawPayload = RawPayload,
            };
        }

        private static string CacheKey(string cid) => "revnix.ent." + cid;
        private static string PlacementKey(string key) => "revnix.placement." + key;
        private static string InstallReportedKey(string cid) => "revnix.installReported." + cid;

        /// <summary>Persist the high-water wall clock; report whether the clock
        /// has been rolled back past tolerance (defeats "set the clock back to
        /// stay subscribed offline").</summary>
        private bool UpdateWallClock(long nowMs)
        {
            var raw = _config.Storage.Get(KeyWallClock);
            long stored = 0;
            if (raw != null) long.TryParse(raw, out stored);
            if (nowMs > stored) _config.Storage.Set(KeyWallClock, nowMs.ToString());
            return nowMs + RollbackToleranceMs < stored;
        }

        private CacheEntry CacheEntryFor(string cid)
        {
            var raw = _config.Storage.Get(CacheKey(cid));
            if (raw == null) return null;
            try
            {
                var map = RevnixJson.ParseObject(raw);
                var snapshotMap = RevnixJson.GetObject(map, "snapshot");
                if (snapshotMap == null) return null;
                return new CacheEntry
                {
                    Snapshot = CustomerEntitlements.FromJson(snapshotMap),
                    FetchedAt = RevnixJson.GetLong(map, "fetchedAt"),
                };
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private void StoreCacheEntry(CacheEntry entry, string cid)
        {
            var map = new Dictionary<string, object>
            {
                ["snapshot"] = entry.Snapshot.ToJson(),
                ["fetchedAt"] = entry.FetchedAt,
            };
            _config.Storage.Set(CacheKey(cid), RevnixJson.Serialize(map));

            // LRU over recent customers so a shared device can't grow unbounded.
            var index = ReadIndex();
            index.Remove(cid);
            index.Insert(0, cid);
            while (index.Count > CacheCustomers)
            {
                var evicted = index[index.Count - 1];
                index.RemoveAt(index.Count - 1);
                _config.Storage.Remove(CacheKey(evicted));
            }
            var serialized = new List<object>();
            foreach (var id in index) serialized.Add(id);
            _config.Storage.Set(KeyCacheIndex, RevnixJson.Serialize(serialized));
        }

        private List<string> ReadIndex()
        {
            var raw = _config.Storage.Get(KeyCacheIndex);
            var result = new List<string>();
            if (raw == null) return result;
            try
            {
                if (RevnixJson.Parse(raw) is List<object> list)
                {
                    foreach (var item in list)
                    {
                        if (item is string s) result.Add(s);
                    }
                }
            }
            catch (FormatException)
            {
                // corrupt index = start over
            }
            return result;
        }

        private static string QueueKey(RegisterPurchaseInput input)
            => input.Source.Wire() + ":" + input.Token + ":" + input.TransactionId;

        private List<QueuedPurchase> QueuedItems()
        {
            var raw = _config.Storage.Get(KeyQueue);
            var result = new List<QueuedPurchase>();
            if (raw == null) return result;
            try
            {
                if (RevnixJson.Parse(raw) is List<object> list)
                {
                    foreach (var item in list)
                    {
                        if (!(item is Dictionary<string, object> map)) continue;
                        result.Add(new QueuedPurchase
                        {
                            Key = RevnixJson.GetString(map, "key", ""),
                            Source = RevnixJson.GetString(map, "source", "apple"),
                            Token = RevnixJson.GetString(map, "token", ""),
                            ProductId = RevnixJson.GetString(map, "productId", ""),
                            TransactionId = RevnixJson.GetString(map, "transactionId", ""),
                            OccurredAt = RevnixJson.GetNullableLong(map, "occurredAt"),
                            ExpiresAt = RevnixJson.GetNullableLong(map, "expiresAt"),
                            SignedTransactionInfo = RevnixJson.GetString(map, "signedTransactionInfo"),
                            RawPayload = RevnixJson.GetObject(map, "rawPayload"),
                        });
                    }
                }
            }
            catch (FormatException)
            {
                // corrupt queue = drop it rather than crash every launch
            }
            return result;
        }

        private void PersistQueue(List<QueuedPurchase> items)
        {
            var list = new List<object>();
            foreach (var item in items)
            {
                var map = new Dictionary<string, object>
                {
                    ["key"] = item.Key,
                    ["source"] = item.Source,
                    ["token"] = item.Token,
                    ["productId"] = item.ProductId,
                    ["transactionId"] = item.TransactionId,
                };
                if (item.OccurredAt.HasValue) map["occurredAt"] = item.OccurredAt.Value;
                if (item.ExpiresAt.HasValue) map["expiresAt"] = item.ExpiresAt.Value;
                if (item.SignedTransactionInfo != null) map["signedTransactionInfo"] = item.SignedTransactionInfo;
                if (item.RawPayload != null) map["rawPayload"] = item.RawPayload;
                list.Add(map);
            }
            _config.Storage.Set(KeyQueue, RevnixJson.Serialize(list));
        }

        private void Enqueue(RegisterPurchaseInput input)
        {
            var items = QueuedItems();
            var key = QueueKey(input);
            foreach (var item in items)
            {
                if (item.Key == key) return;
            }
            items.Add(new QueuedPurchase
            {
                Key = key,
                Source = input.Source.Wire(),
                Token = input.Token,
                ProductId = input.ProductId,
                TransactionId = input.TransactionId,
                OccurredAt = input.OccurredAt,
                ExpiresAt = input.ExpiresAt,
                SignedTransactionInfo = input.SignedTransactionInfo,
                RawPayload = input.RawPayload,
            });
            PersistQueue(items);
        }

        private void RemoveQueued(string key)
        {
            var items = QueuedItems();
            items.RemoveAll(item => item.Key == key);
            PersistQueue(items);
        }

        /// <summary>
        /// A block paywall reporting a paint string it could not read. Routed
        /// to the same sink as every other swallowed failure, so a host that
        /// already wired <c>OnDiagnostic</c> needs no new wiring to see render
        /// fallbacks.
        /// </summary>
        public void ReportRenderDiagnostic(string message)
        {
            Diagnostic("paywall.render", message);
        }

        private void Diagnostic(string op, string message)
        {
            _config.OnDiagnostic?.Invoke(new RevnixDiagnostic(op, message));
        }
    }
}
