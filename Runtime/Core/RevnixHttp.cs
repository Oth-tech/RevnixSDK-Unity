using System.Collections.Generic;
using System.Threading.Tasks;

namespace Revnix
{
    /// <summary>A completed HTTP exchange. Transport-level failures (offline,
    /// DNS, timeout) must be thrown by the transport as RevnixNetworkException /
    /// RevnixTimeoutException rather than encoded here — the client's offline
    /// policy keys off those types.</summary>
    public sealed class RevnixHttpResponse
    {
        public readonly int Status;
        public readonly string Body;
        public readonly string RetryAfterHeader;
        public readonly string SignedAtHeader;
        public readonly string SignatureHeader;

        public RevnixHttpResponse(int status, string body, string retryAfterHeader = null)
            : this(status, body, retryAfterHeader, null, null) { }

        /// <summary>Entitlement responses need <c>Revnix-Signed-At</c> and <c>Revnix-Signature</c>; without them the client rejects the response.</summary>
        public RevnixHttpResponse(int status, string body, string retryAfterHeader,
            string signedAtHeader, string signatureHeader)
        {
            Status = status;
            Body = body ?? "";
            RetryAfterHeader = retryAfterHeader;
            SignedAtHeader = signedAtHeader;
            SignatureHeader = signatureHeader;
        }
    }

    /// <summary>Pluggable transport so the core stays engine-free: Unity ships
    /// a UnityWebRequest implementation, tests inject fakes.</summary>
    public interface IRevnixHttp
    {
        /// <param name="method">"GET" or "POST".</param>
        /// <param name="url">Absolute URL.</param>
        /// <param name="jsonBody">Serialized JSON body, or null for GET.</param>
        /// <param name="headers">Request headers (Authorization etc.).</param>
        /// <param name="timeoutMs">Per-request timeout in milliseconds.</param>
        Task<RevnixHttpResponse> Send(
            string method,
            string url,
            string jsonBody,
            IReadOnlyDictionary<string, string> headers,
            int timeoutMs);
    }
}
