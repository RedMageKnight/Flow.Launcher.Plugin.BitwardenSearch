using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public class IconStatus
    {
        public string? Url { get; set; }
        public bool Exists { get; set; }
        public DateTime LastChecked { get; set; }
    }

    public class IconStatusCache
    {
        private readonly string _cacheFilePath;
        private Dictionary<string, IconStatus> _cache = new Dictionary<string, IconStatus>();

        public IconStatusCache(string cacheDirectory)
        {
            _cacheFilePath = Path.Combine(cacheDirectory, "icon_status_cache.json");
            LoadCache();
        }

        private void LoadCache()
        {
            if (File.Exists(_cacheFilePath))
            {
                var json = File.ReadAllText(_cacheFilePath);
                _cache = JsonConvert.DeserializeObject<Dictionary<string, IconStatus>>(json) ?? new Dictionary<string, IconStatus>();
            }
            else
            {
                _cache = new Dictionary<string, IconStatus>();
            }
        }

        public void SaveCache()
        {
            var json = JsonConvert.SerializeObject(_cache);
            File.WriteAllText(_cacheFilePath, json);
        }

        public bool ShouldCheckIcon(string url)
        {
            if (_cache.TryGetValue(url, out var status))
            {
                // Retry failed icons after 7 days
                return status.Exists || (DateTime.Now - status.LastChecked).TotalDays >= 7;
            }
            return true;
        }

        public void UpdateIconStatus(string url, bool exists)
        {
            _cache[url] = new IconStatus { Url = url, Exists = exists, LastChecked = DateTime.Now };
        }
    }

    public class IconCacheManager
    {
        private readonly IconStatusCache _iconStatusCache;
        private readonly string _faviconCacheDir;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _throttler;
        private const int MaxConcurrentDownloads = 10;
        private const int InitialCacheLimit = 100;

        public IconCacheManager(string faviconCacheDir, HttpClient httpClient)
        {
            _faviconCacheDir = faviconCacheDir;
            _iconStatusCache = new IconStatusCache(faviconCacheDir);
            _httpClient = httpClient;
            _throttler = new SemaphoreSlim(MaxConcurrentDownloads);
        }

        public async Task CacheAllIconsAsync(List<BitwardenItem> items, IProgress<int>? progress = null)
        {
            Logger.Log($"Starting to cache icons for {items.Count} items", LogLevel.Info);
            try
            {
                var tasks = new List<Task>();
                var processedCount = 0;

                foreach (var item in items)
                {
                    if (item?.login?.uris != null && item.login.uris.Any())
                    {
                        var webUri = item.login.uris
                            .Select(u => u.uri)
                            .FirstOrDefault(u => u.StartsWith("http://") || u.StartsWith("https://"));

                        if (!string.IsNullOrEmpty(webUri) && UrlValidator.IsDomainUrl(webUri))
                        {
                            await _throttler.WaitAsync();
                            tasks.Add(Task.Run(async () =>
                            {
                                try
                                {
                                    var success = await DownloadAndCacheFaviconAsync(webUri, CancellationToken.None);
                                    _iconStatusCache.UpdateIconStatus(webUri, success);
                                    Logger.Log($"Cached icon for {webUri}: {(success ? "Success" : "Failed")}", LogLevel.Debug);
                                }
                                finally
                                {
                                    _throttler.Release();
                                    var completed = Interlocked.Increment(ref processedCount);
                                    if (completed % 10 == 0 || completed == items.Count) // Report every 10 items or at completion
                                    {
                                        progress?.Report((int)((float)completed / items.Count * 100));
                                    }
                                }
                            }));
                        }
                    }
                }

                await Task.WhenAll(tasks);
                _iconStatusCache.SaveCache();

                Logger.Log($"Icon caching completed. Processed {processedCount} icons.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Logger.LogError("Error during icon caching", ex);
            }
        }

        private async Task<bool> DownloadAndCacheFaviconAsync(string url, CancellationToken token)
        {
            var uri = new Uri(url);
            var domain = uri.Host;
            var safeFileName = string.Join("_", domain.Split(Path.GetInvalidFileNameChars()));
            var filePath = Path.Combine(_faviconCacheDir, $"{safeFileName}.ico");

            Logger.Log($"Attempting to cache favicon for {domain}", LogLevel.Debug);

            if (File.Exists(filePath) && (DateTime.Now - File.GetLastWriteTime(filePath)).TotalDays < 1)
            {
                Logger.Log($"Using cached favicon for {domain}", LogLevel.Debug);
                return true;
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                var faviconUrl = $"https://www.google.com/s2/favicons?domain={domain}&sz=32";
                Logger.Log($"Downloading favicon for {domain} from {faviconUrl}", LogLevel.Debug);

                using var response = await _httpClient.GetAsync(faviconUrl, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                    using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await stream.CopyToAsync(fileStream);
                    Logger.Log($"Downloaded and cached favicon for {domain}", LogLevel.Debug);
                    return true;
                }
                else
                {
                    Logger.Log($"Failed to download favicon for {domain}: {response.StatusCode}", LogLevel.Warning);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error downloading favicon for {domain}: {ex.Message}", LogLevel.Warning);
                return false;
            }
        }

        public string GetCachedIconPath(string url)
        {
            var uri = new Uri(url);
            var domain = uri.Host;
            var safeFileName = string.Join("_", domain.Split(Path.GetInvalidFileNameChars()));
            var filePath = Path.Combine(_faviconCacheDir, $"{safeFileName}.ico");

            return File.Exists(filePath) ? filePath : "Images/bitwarden.png";
        }
    }
}