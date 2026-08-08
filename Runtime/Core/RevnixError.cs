using System;
using System.Globalization;

namespace Revnix
{
    /// <summary>
    /// Error taxonomy mirroring revnix-react / revnix-swift / revnix-kotlin:
    /// every case is either RETRYABLE (transient — offline, timeout, 429, 5xx,
    /// captive portal) or DELIBERATE (401/403/404/409 — the server refused on
    /// purpose; a kill-switch must never be defeated by a cache or a retry).
    /// </summary>
    public class RevnixException : Exception
    {
        /// <summary>One of the v1 taxonomy codes (network, timeout, rate_limited,
        /// server, bad_response, auth, not_found, purchase_blocked, invalid).</summary>
        public string Code { get; }

        /// <summary>HTTP status, when the failure came from a response. 0 = none.</summary>
        public int Status { get; }

        /// <summary>True when retrying, or serving the offline cache, is the right move.</summary>
        public bool IsRetryable { get; }

        public RevnixException(string code, string message, bool isRetryable, int status = 0)
            : base(message)
        {
            Code = code;
            IsRetryable = isRetryable;
            Status = status;
        }

        public static RevnixException FromHttp(int status, string message, string retryAfterHeader = null)
        {
            switch (status)
            {
                case 401:
                case 403:
                    return new RevnixAuthException(status);
                case 404:
                    return new RevnixNotFoundException();
                case 409:
                    return new RevnixPurchaseBlockedException(message);
                case 429:
                    return new RevnixRateLimitException(ParseRetryAfter(retryAfterHeader, DateTimeOffset.UtcNow));
                default:
                    if (status >= 500 && status <= 599) return new RevnixServerException(status);
                    return new RevnixInvalidException(status, message);
            }
        }

        /// <summary>`Retry-After` is either delta-seconds or an HTTP date (RFC 9110).
        /// Returns milliseconds, or null when absent/unparseable.</summary>
        public static long? ParseRetryAfter(string raw, DateTimeOffset now)
        {
            var value = (raw ?? "").Trim();
            if (value.Length == 0) return null;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return seconds > 0 ? (long)(seconds * 1000) : 0L;
            }
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed))
            {
                var delta = (long)(parsed - now).TotalMilliseconds;
                return delta < 0 ? 0L : delta;
            }
            return null;
        }
    }

    /// <summary>Connectivity failure (offline, DNS, reset). Retryable.</summary>
    public sealed class RevnixNetworkException : RevnixException
    {
        public RevnixNetworkException(string detail)
            : base("network", "network failure: " + detail, isRetryable: true) { }
    }

    /// <summary>Request exceeded the configured timeout. Retryable.</summary>
    public sealed class RevnixTimeoutException : RevnixException
    {
        public RevnixTimeoutException()
            : base("timeout", "request timed out", isRetryable: true) { }
    }

    /// <summary>HTTP 429. Retryable with backoff; carries the server's
    /// Retry-After as milliseconds when it sent one.</summary>
    public sealed class RevnixRateLimitException : RevnixException
    {
        public long? RetryAfterMs { get; }

        public RevnixRateLimitException(long? retryAfterMs)
            : base("rate_limited", "rate limited", isRetryable: true, status: 429)
        {
            RetryAfterMs = retryAfterMs;
        }
    }

    /// <summary>HTTP 5xx. Retryable.</summary>
    public sealed class RevnixServerException : RevnixException
    {
        public RevnixServerException(int status)
            : base("server", "server error " + status, isRetryable: true, status: status) { }
    }

    /// <summary>A 200 whose body was not the expected JSON (captive portal). Retryable.</summary>
    public sealed class RevnixBadResponseException : RevnixException
    {
        public RevnixBadResponseException(string detail = "unparseable response")
            : base("bad_response", detail, isRetryable: true) { }
    }

    /// <summary>HTTP 401/403 — missing/refused key or key-kind. Deliberate.</summary>
    public sealed class RevnixAuthException : RevnixException
    {
        public RevnixAuthException(int status)
            : base("auth", "unauthorized (" + status + ")", isRetryable: false, status: status) { }
    }

    /// <summary>HTTP 404 — unknown route/resource. Deliberate.</summary>
    public sealed class RevnixNotFoundException : RevnixException
    {
        public RevnixNotFoundException()
            : base("not_found", "not found", isRetryable: false, status: 404) { }
    }

    /// <summary>HTTP 409 — purchase blocked by the app's transfer policy. Deliberate.</summary>
    public sealed class RevnixPurchaseBlockedException : RevnixException
    {
        public RevnixPurchaseBlockedException(string detail)
            : base("purchase_blocked",
                string.IsNullOrEmpty(detail) ? "purchase blocked" : detail,
                isRetryable: false, status: 409) { }
    }

    /// <summary>Any other non-2xx (400 validation, 413 payload cap). Deliberate.</summary>
    public sealed class RevnixInvalidException : RevnixException
    {
        public RevnixInvalidException(int status, string detail)
            : base("invalid",
                string.IsNullOrEmpty(detail) ? "request rejected (" + status + ")" : detail,
                isRetryable: false, status: status) { }
    }
}
