public static class BitwardenConstants
{
    public const string DEFAULT_SERVER_URL = "https://vault.bitwarden.com";
    public const string EU_SERVER_URL = "https://vault.bitwarden.eu";
    
    public static string GetOfficialServerUrl(string region)
    {
        return region?.ToLower() == "eu" ? EU_SERVER_URL : DEFAULT_SERVER_URL;
    }
}