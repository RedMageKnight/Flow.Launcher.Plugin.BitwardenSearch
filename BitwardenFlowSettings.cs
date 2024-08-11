using System;
using System.ComponentModel;
using System.Security;
using Flow.Launcher.Plugin;
using Newtonsoft.Json;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public class BitwardenFlowSettings : IPluginI18n
    {
        [JsonProperty("clientId")]
        public string ClientId { get; set; } = string.Empty;

        [JsonProperty("sessionKey")]
        public string SessionKey { get; set; } = string.Empty;

        [JsonProperty("logTrace")]
        public bool LogTrace { get; set; } = true;

        [JsonProperty("logDebug")]
        public bool LogDebug { get; set; } = true;

        [JsonProperty("logInfo")]
        public bool LogInfo { get; set; } = true;

        [JsonProperty("logWarning")]
        public bool LogWarning { get; set; } = true;

        [JsonProperty("logError")]
        public bool LogError { get; set; } = true;

        [JsonProperty("keepUnlocked")]
        public bool KeepUnlocked { get; set; } = false;

        [JsonProperty("lockTime")]
        public int LockTime { get; set; } = 5; 
        
        [JsonProperty("notifyOnPasswordCopy")]
        public bool NotifyOnPasswordCopy { get; set; } = false;

        [JsonProperty("notifyOnUsernameCopy")]
        public bool NotifyOnUsernameCopy { get; set; } = false;

        [JsonProperty("notifyOnUriCopy")]
        public bool NotifyOnUriCopy { get; set; } = false;

        [JsonProperty("notifyOnTotpCopy")]
        public bool NotifyOnTotpCopy { get; set; } = true;

        [JsonProperty("notifyOnSyncStart")]
        public bool NotifyOnSyncStart { get; set; } = true;

        [JsonProperty("notifyOnIconCacheStart")]
        public bool NotifyOnIconCacheStart { get; set; } = true;

        [JsonProperty("notifyOnSyncComplete")]
        public bool NotifyOnSyncComplete { get; set; } = true;

        [JsonProperty("clipboardClearSeconds")]
        public int ClipboardClearSeconds { get; set; } = 0; // 0 means never clear

        public string GetTranslatedPluginTitle()
        {
            return "Bitwarden Vault";
        }

        public string GetTranslatedPluginDescription()
        {
            return "Quick access to your Bitwarden vault (requires Bitwarden CLI)";
        }
    }
}