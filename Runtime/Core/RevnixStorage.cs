using System;
using System.Collections.Generic;

namespace Revnix
{
    /// <summary>
    /// Small synchronous key-value store. The offline entitlement cache and
    /// the purchase retry queue must survive relaunch, so the production
    /// implementation is durable (PlayerPrefs on Unity); MemoryStorage is for
    /// tests and editor tooling.
    /// </summary>
    public interface IRevnixStorage
    {
        string Get(string key);
        void Set(string key, string value);
        void Remove(string key);
    }

    public sealed class MemoryStorage : IRevnixStorage
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>();
        private readonly object _lock = new object();

        public string Get(string key)
        {
            lock (_lock) return _values.TryGetValue(key, out var v) ? v : null;
        }

        public void Set(string key, string value)
        {
            lock (_lock) _values[key] = value;
        }

        public void Remove(string key)
        {
            lock (_lock) _values.Remove(key);
        }
    }

    internal static class RevnixIdentity
    {
        internal static string GenerateAnonymousId()
            => "rvx_anon_" + Guid.NewGuid().ToString("N").ToLowerInvariant();
    }
}
