using System.ComponentModel;
using Flow.Launcher.Plugin;
using Newtonsoft.Json;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public class BitwardenFlowSettings : IPluginI18n
    {
        [JsonProperty("clientId")]
        public string ClientId { get; set; } = string.Empty;

        [JsonProperty("clientSecret")]
        public string ClientSecret { get; set; } = string.Empty;

        [JsonProperty("masterPassword")]
        public string MasterPassword { get; set; } = string.Empty;

        [JsonProperty("sessionKey")]
        public string SessionKey { get; set; } = string.Empty;

        [JsonProperty("logDebug")]
        public bool LogDebug { get; set; } = false;

        [JsonProperty("logInfo")]
        public bool LogInfo { get; set; } = false;

        [JsonProperty("logWarning")]
        public bool LogWarning { get; set; } = true;

        [JsonProperty("logError")]
        public bool LogError { get; set; } = true;

        public BitwardenFlowSettings()
        {
            // Set default values
            ClientId = string.Empty;
            ClientSecret = string.Empty;
            MasterPassword = string.Empty;
            SessionKey = string.Empty;
            LogDebug = false;
            LogInfo = false;
            LogWarning = true;
            LogError = true;
        }

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