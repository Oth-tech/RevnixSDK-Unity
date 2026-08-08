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

        public RevnixHttpResponse(int status, string body, string retryAfterHeader = null)
        {
            Status = status;
            Body = body ?? "";
            RetryAfterHeader = retryAfterHeader;
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
