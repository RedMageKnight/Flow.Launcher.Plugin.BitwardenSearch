using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Newtonsoft.Json;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public class GitHubApiHandler
    {
        private static readonly Dictionary<string, DateTime> LastRequestTimes = new();
        private static readonly SemaphoreSlim RateLimitSemaphore = new(1, 1);
        private const int MinRequestInterval = 1000; // Milliseconds between requests

        public static async Task WaitForRateLimit(string endpoint)
        {
            await RateLimitSemaphore.WaitAsync();
            try
            {
                if (LastRequestTimes.TryGetValue(endpoint, out DateTime lastRequest))
                {
                    var timeSinceLastRequest = DateTime.UtcNow - lastRequest;
                    if (timeSinceLastRequest.TotalMilliseconds < MinRequestInterval)
                    {
                        await Task.Delay(MinRequestInterval - (int)timeSinceLastRequest.TotalMilliseconds);
                    }
                }
                LastRequestTimes[endpoint] = DateTime.UtcNow;
            }
            finally
            {
                RateLimitSemaphore.Release();
            }
        }

        public static async Task<T> GetWithRateLimit<T>(string url, HttpClient client)
        {
            await WaitForRateLimit(url);

            var response = await client.GetAsync(url);
            
            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues))
            {
                if (int.TryParse(remainingValues.FirstOrDefault(), out int remaining) && remaining < 10)
                {
                    Logger.Log($"GitHub API rate limit running low. {remaining} requests remaining.", LogLevel.Warning);
                }
            }

            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<T>(content) ?? throw new Exception("Failed to deserialize GitHub API response");
        }
    }
}