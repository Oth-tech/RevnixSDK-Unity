using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Revnix.Unity
{
    /// <summary>
    /// IRevnixHttp over UnityWebRequest. Must be used from the main thread
    /// (SendWebRequest's requirement) — which is where game code calls the SDK
    /// anyway; awaited continuations resume via Unity's SynchronizationContext.
    /// </summary>
    public sealed class UnityWebRequestHttp : IRevnixHttp
    {
        public Task<RevnixHttpResponse> Send(
            string method,
            string url,
            string jsonBody,
            IReadOnlyDictionary<string, string> headers,
            int timeoutMs)
        {
            var tcs = new TaskCompletionSource<RevnixHttpResponse>();
            var request = new UnityWebRequest(url, method)
            {
                downloadHandler = new DownloadHandlerBuffer(),
                // UnityWebRequest.timeout is whole seconds; round up so a
                // sub-second config doesn't mean "no timeout at all" (0).
                timeout = (timeoutMs + 999) / 1000,
            };
            if (jsonBody != null)
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
            }
            foreach (var header in headers)
            {
                request.SetRequestHeader(header.Key, header.Value);
            }

            var operation = request.SendWebRequest();
            operation.completed += _ =>
            {
                try
                {
                    switch (request.result)
                    {
                        case UnityWebRequest.Result.ConnectionError:
                        case UnityWebRequest.Result.DataProcessingError:
                        {
                            // Unity folds timeouts into ConnectionError; the
                            // error string is the only discriminator it offers.
                            var error = request.error ?? "unreachable";
                            if (error.ToLowerInvariant().Contains("timeout"))
                            {
                                tcs.TrySetException(new RevnixTimeoutException());
                            }
                            else
                            {
                                tcs.TrySetException(new RevnixNetworkException(error));
                            }
                            break;
                        }
                        default:
                        {
                            // Success AND ProtocolError both carry a real HTTP
                            // exchange — the client maps non-2xx to the typed
                            // taxonomy, so hand the response through as-is.
                            tcs.TrySetResult(new RevnixHttpResponse(
                                (int)request.responseCode,
                                request.downloadHandler.text,
                                request.GetResponseHeader("Retry-After")));
                            break;
                        }
                    }
                }
                finally
                {
                    request.Dispose();
                }
            };
            return tcs.Task;
        }
    }
}
