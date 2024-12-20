using System;
using System.ComponentModel;
using System.Security;
using Flow.Launcher.Plugin;
using Newtonsoft.Json;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public class BitwardenFlowSettings : IPluginI18n
    {
        [JsonProperty("bwExecutablePath")]
        public string BwExecutablePath { get; set; } = string.Empty;

        [JsonProperty("clientId")]
        public string ClientId { get; set; } = string.Empty;

        [JsonProperty("sessionKey")]
        public string SessionKey { get; set; } = string.Empty;

        [JsonProperty("useCustomServer")]
        public bool UseCustomServer { get; set; } = false;

        [JsonProperty("customServerUrl")]
        public string CustomServerUrl { get; set; } = string.Empty;

        [JsonProperty("customIdentityUrl")]
        public string CustomIdentityUrl { get; set; } = string.Empty;

        [JsonProperty("customApiUrl")]
        public string CustomApiUrl { get; set; } = string.Empty;

        [JsonProperty("customNotificationsUrl")]
        public string CustomNotificationsUrl { get; set; } = string.Empty;

        [JsonProperty("customWebVaultUrl")]
        public string CustomWebVaultUrl { get; set; } = string.Empty;

        [JsonProperty("customIconsUrl")]
        public string CustomIconsUrl { get; set; } = string.Empty;

        [JsonProperty("customKeysUrl")]
        public string CustomKeysUrl { get; set; } = string.Empty;

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

        [JsonProperty("autoLockDuration")]
        public int AutoLockDuration { get; set; } = 0;

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
        public int ClipboardClearSeconds { get; set; } = 0;

        [JsonProperty("isPathEnvironmentValid")]
        public bool IsPathEnvironmentValid { get; set; } = false;

        [JsonProperty("notifyOnAutoLock")]
        public bool NotifyOnAutoLock { get; set; } = true;

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