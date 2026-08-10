using System;
using System.Collections.Generic;
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
        public const string SdkVersion = "0.2.0";

        private const long ExpiryGraceMs = 3L * 24 * 3600 * 1000;
        private const long RollbackToleranceMs = 5L * 60 * 1000;
        private const int CacheCustomers = 4;

        private const string KeyCustomerId = "revnix.customerId";
        private const string KeyWallClock = "revnix.lastWallClock";
        private const string KeyQueue = "revnix.pendingPurchases";
        private const string KeyCacheIndex = "revnix.entIndex";

        private readonly RevnixConfig _config;
        private readonly object _lock = new object();
        private readonly Random _random = new Random();
        private Task<CustomerEntitlements> _inflight;
        private int _bgFailures;

        public RevnixClient(RevnixConfig config)
        {
            if (string.IsNullOrEmpty(config.ApiKey)) throw new ArgumentException("ApiKey is required");
            if (string.IsNullOrEmpty(config.BaseUrl)) throw new ArgumentException("BaseUrl is required");
            if (config.Http == null) throw new ArgumentException("Http transport is required");
            _config = config;
        }

        // ── Identity ─────────────────────────────────────────────────────────

        /// <summary>Anonymous id, minted and persisted on first call.</summary>
        public string CustomerId()
        {
            var existing = _config.Storage.Get(KeyCustomerId);
            if (existing != null) return existing;
            var minted = RevnixIdentity.GenerateAnonymousId();
            _config.Storage.Set(KeyCustomerId, minted);
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

        /// <summary>Gate helper — never throws; unknown/unreachable = locked.</summary>
        public async Task<bool> IsEntitled(string entitlementId)
        {
            CustomerEntitlements snapshot;
            try
            {
                snapshot = await Entitlements();
            }
            catch (RevnixException)
            {
                snapshot = CachedEntitlements();
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
            try
            {
                var raw = await Request("GET", new[] { "v1", "placements", key, "offering" }, null, query);
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
                await Request("POST", new[] { "v1", "installs" }, body);
                _config.Storage.Set(InstallReportedKey(cid), "1");
            }
            catch (RevnixException err)
            {
                _bgFailures += 1;
                Diagnostic("registerInstall", err.Message);
            }
        }

        /// <summary>Fire-and-forget impression beacon (feeds funnels + view
        /// conversions). Call when the paywall becomes visible.</summary>
        public async Task LogPaywallShown(string placementKey = null, string paywallId = null)
        {
            var body = new Dictionary<string, object>
            {
                ["customerId"] = CustomerId(),
                ["viewId"] = Guid.NewGuid().ToString("D").ToLowerInvariant(),
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
            Dictionary<string, object> body = null, Dictionary<string, string> query = null)
        {
            var url = BuildUrl(segments, query);
            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + _config.ApiKey,
                ["X-Revnix-SDK"] = "revnix-unity/" + SdkVersion,
            };
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

        private void Diagnostic(string op, string message)
        {
            _config.OnDiagnostic?.Invoke(new RevnixDiagnostic(op, message));
        }
    }
}
