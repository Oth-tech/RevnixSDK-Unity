using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
using AOT;
#elif UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;
#endif

namespace Revnix.Unity
{
    /// <summary>Device integrity evidence for <see cref="RevnixConfig.DeviceIntegrity"/>:
    /// Apple App Attest on iOS, a classic Google Play Integrity token on
    /// Android (needs <c>com.google.android.play:integrity</c> in the Gradle
    /// build). Null in the Editor and on every other platform.</summary>
    public static class RevnixDeviceIntegrity
    {
        public static Func<string, Task<Dictionary<string, object>>> Provider
        {
            get
            {
#if UNITY_IOS && !UNITY_EDITOR
                return AppAttest;
#elif UNITY_ANDROID && !UNITY_EDITOR
                return PlayIntegrity;
#else
                return null;
#endif
            }
        }

        private static byte[] Sha256(string customerId)
        {
            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(Encoding.UTF8.GetBytes(customerId));
            }
        }

#if UNITY_IOS && !UNITY_EDITOR
        private delegate void AttestCallback(int requestId, string keyId, string attestation, string error);

        [DllImport("__Internal")]
        private static extern void RevnixAppAttest(int requestId, byte[] clientDataHash, int length, AttestCallback callback);

        private static readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> Pending =
            new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        private static int _nextRequestId;

        private static Task<Dictionary<string, object>> AppAttest(string customerId)
        {
            var tcs = new TaskCompletionSource<Dictionary<string, object>>();
            int requestId;
            lock (Pending)
            {
                requestId = ++_nextRequestId;
                Pending[requestId] = tcs;
            }
            var hash = Sha256(customerId);
            RevnixAppAttest(requestId, hash, hash.Length, OnAttested);
            return tcs.Task;
        }

        [MonoPInvokeCallback(typeof(AttestCallback))]
        private static void OnAttested(int requestId, string keyId, string attestation, string error)
        {
            TaskCompletionSource<Dictionary<string, object>> tcs;
            lock (Pending)
            {
                if (!Pending.TryGetValue(requestId, out tcs)) return;
                Pending.Remove(requestId);
            }
            if (string.IsNullOrEmpty(keyId) || string.IsNullOrEmpty(attestation))
            {
                tcs.TrySetException(new Exception(error ?? "App Attest failed"));
                return;
            }
            tcs.TrySetResult(new Dictionary<string, object>
            {
                ["platform"] = "ios",
                ["keyId"] = keyId,
                ["attestation"] = attestation,
            });
        }
#elif UNITY_ANDROID && !UNITY_EDITOR
        private static Task<Dictionary<string, object>> PlayIntegrity(string customerId)
        {
            var tcs = new TaskCompletionSource<Dictionary<string, object>>();
            var nonce = DeviceFacts.Base64Url(Sha256(customerId));
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var context = activity.Call<AndroidJavaObject>("getApplicationContext"))
            using (var factory = new AndroidJavaClass("com.google.android.play.core.integrity.IntegrityManagerFactory"))
            using (var manager = factory.CallStatic<AndroidJavaObject>("create", context))
            using (var requests = new AndroidJavaClass("com.google.android.play.core.integrity.IntegrityTokenRequest"))
            using (var builder = requests.CallStatic<AndroidJavaObject>("builder"))
            {
                builder.Call<AndroidJavaObject>("setNonce", nonce).Dispose();
                using (var request = builder.Call<AndroidJavaObject>("build"))
                using (var task = manager.Call<AndroidJavaObject>("requestIntegrityToken", request))
                {
                    task.Call<AndroidJavaObject>("addOnCompleteListener", new TokenListener(tcs)).Dispose();
                }
            }
            return tcs.Task;
        }

        private sealed class TokenListener : AndroidJavaProxy
        {
            private readonly TaskCompletionSource<Dictionary<string, object>> _tcs;

            public TokenListener(TaskCompletionSource<Dictionary<string, object>> tcs)
                : base("com.google.android.gms.tasks.OnCompleteListener")
            {
                _tcs = tcs;
            }

            public void onComplete(AndroidJavaObject task)
            {
                try
                {
                    if (!task.Call<bool>("isSuccessful"))
                    {
                        using (var error = task.Call<AndroidJavaObject>("getException"))
                        {
                            _tcs.TrySetException(new Exception(error?.Call<string>("getMessage") ?? "Play Integrity failed"));
                        }
                        return;
                    }
                    using (var response = task.Call<AndroidJavaObject>("getResult"))
                    {
                        var token = response.Call<string>("token");
                        _tcs.TrySetResult(string.IsNullOrEmpty(token)
                            ? null
                            : new Dictionary<string, object> { ["platform"] = "android", ["token"] = token });
                    }
                }
                catch (Exception err)
                {
                    _tcs.TrySetException(err);
                }
            }
        }
#endif
    }
}
