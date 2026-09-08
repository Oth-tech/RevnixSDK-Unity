using System;
using UnityEngine;

namespace Revnix.Unity
{
    /// <summary>
    /// REV-272: foreground detection for implicit placements.
    ///
    /// <c>Runtime/Core</c> is plain C# with no UnityEngine dependency (so the
    /// suite runs under dotnet without an Editor), so the platform half lives
    /// here — a hidden, scene-surviving GameObject whose
    /// <c>OnApplicationPause</c> / <c>OnApplicationFocus</c> callbacks are the
    /// only foreground signal Unity gives a non-Editor build.
    ///
    /// Both callbacks are used, not one: Android delivers pause/resume,
    /// desktop and the Editor deliver focus, and iOS delivers both. Each is
    /// mapped to a state rather than an edge — the client's own first-report-
    /// wins rule is what turns a burst of callbacks into one transition.
    /// </summary>
    public sealed class RevnixLifecycleBehaviour : MonoBehaviour, IRevnixLifecycle
    {
        private Action<RevnixAppState> _handler;

        /// <summary>Create the hidden host object. Call from the main thread.</summary>
        public static RevnixLifecycleBehaviour Create()
        {
            var go = new GameObject("RevnixLifecycle")
            {
                // Hidden so it never shows in a designer's hierarchy, and kept
                // across scene loads so a level change does not silently end
                // foreground detection for the rest of the session.
                hideFlags = HideFlags.HideAndDontSave,
            };
            DontDestroyOnLoad(go);
            return go.AddComponent<RevnixLifecycleBehaviour>();
        }

        public Action OnStateChange(Action<RevnixAppState> handler)
        {
            _handler = handler;
            return () => { _handler = null; };
        }

        private void OnApplicationPause(bool paused)
        {
            Fire(paused ? RevnixAppState.Background : RevnixAppState.Foreground);
        }

        private void OnApplicationFocus(bool focused)
        {
            Fire(focused ? RevnixAppState.Foreground : RevnixAppState.Background);
        }

        private void Fire(RevnixAppState state)
        {
            var handler = _handler;
            if (handler == null) return;
            try
            {
                handler(state);
            }
            catch (Exception err)
            {
                // A Unity message must never throw into the engine loop.
                Debug.LogWarning("[Revnix] lifecycle handler failed: " + err.Message);
            }
        }
    }
}
