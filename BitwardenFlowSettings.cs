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
        public bool LogTrace { get; set; } = false;

        [JsonProperty("logDebug")]
        public bool LogDebug { get; set; } = false;

        [JsonProperty("logInfo")]
        public bool LogInfo { get; set; } = false;

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