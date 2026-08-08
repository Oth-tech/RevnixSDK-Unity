using UnityEngine;

namespace Revnix.Unity
{
    /// <summary>
    /// Durable storage over PlayerPrefs. Values are small JSON strings (an
    /// entitlement snapshot, the purchase retry queue), well inside
    /// PlayerPrefs limits. Save() runs on every write: the retry queue's whole
    /// point is surviving a crash immediately after a failed purchase
    /// registration, so buffering writes would defeat it.
    /// </summary>
    public sealed class PlayerPrefsStorage : IRevnixStorage
    {
        public string Get(string key)
            => PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null;

        public void Set(string key, string value)
        {
            PlayerPrefs.SetString(key, value);
            PlayerPrefs.Save();
        }

        public void Remove(string key)
        {
            PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }
    }
}
