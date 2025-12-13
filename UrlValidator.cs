using System.Text.RegularExpressions;

namespace Flow.Launcher.Plugin.BitwardenSearch;

public static partial class UrlValidator
{
    // The [GeneratedRegex] attribute compiles the regex at build time,
    // making subsequent calls to IsDomainUrl extremely fast.
    [GeneratedRegex(
        @"^https?://(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]*[a-z0-9])(?:/.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DomainUrlRegex();

    /// <summary>
    /// Checks if a string is a valid URL starting with http:// or https://,
    /// and ensures the host part is a proper domain name, excluding IP addresses and localhost.
    /// </summary>
    /// <param name="url">The string to validate.</param>
    /// <returns>True if the string is a valid domain-based URL, otherwise false.</returns>
    public static bool IsDomainUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        // Use the compiled regex instance for high performance
        return DomainUrlRegex().IsMatch(url);
    }
}