using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using Newtonsoft.Json;
using System.Linq;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public class CachedVaultItem
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Username { get; set; }
        public bool HasTotp { get; set; }
        public List<string> Uris { get; set; } = new List<string>();
        public DateTime CacheTime { get; set; }
    }

    public class VaultItemCache
    {
        private readonly string _cacheFilePath;
        private readonly object _cacheLock = new object();
        private Dictionary<string, CachedVaultItem> _cache;
        private readonly TimeSpan _cacheExpiration = TimeSpan.FromHours(24);

        public VaultItemCache(string cacheDirectory)
        {
            Directory.CreateDirectory(cacheDirectory);
            _cacheFilePath = Path.Combine(cacheDirectory, "vault_items_cache.json");
            _cache = new Dictionary<string, CachedVaultItem>();
            LoadCache();
        }

        private void LoadCache()
        {
            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    var json = File.ReadAllText(_cacheFilePath);
                    _cache = JsonConvert.DeserializeObject<Dictionary<string, CachedVaultItem>>(json) 
                        ?? new Dictionary<string, CachedVaultItem>();
                    
                    // Clean expired entries during load
                    CleanExpiredEntries();
                }
                Logger.Log($"Loaded {_cache.Count} items from vault cache", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Logger.LogError("Error loading vault cache", ex);
                _cache = new Dictionary<string, CachedVaultItem>();
            }
        }

        public void SaveCache()
        {
            lock (_cacheLock)
            {
                try
                {
                    CleanExpiredEntries();
                    var json = JsonConvert.SerializeObject(_cache, Formatting.Indented);
                    File.WriteAllText(_cacheFilePath, json);
                    Logger.Log($"Saved {_cache.Count} items to vault cache", LogLevel.Debug);
                }
                catch (Exception ex)
                {
                    Logger.LogError("Error saving vault cache", ex);
                }
            }
        }

        private void CleanExpiredEntries()
        {
            var now = DateTime.Now;
            var expiredKeys = _cache
                .Where(kvp => (now - kvp.Value.CacheTime) > _cacheExpiration)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _cache.Remove(key);
            }

            if (expiredKeys.Count > 0)
            {
                Logger.Log($"Removed {expiredKeys.Count} expired entries from vault cache", LogLevel.Debug);
            }
        }

        public void UpdateCache(List<BitwardenItem> items)
        {
            lock (_cacheLock)
            {
                foreach (var item in items)
                {
                    var cachedItem = new CachedVaultItem
                    {
                        Id = item.id,
                        Name = item.name,
                        Username = item.login?.username,
                        HasTotp = item.hasTotp,
                        Uris = item.login?.uris?.Select(u => u.uri).ToList() ?? new List<string>(),
                        CacheTime = DateTime.Now
                    };
                    _cache[item.id] = cachedItem;
                }
                SaveCache();
            }
        }

        public List<BitwardenItem> SearchCache(string searchTerm)
        {
            lock (_cacheLock)
            {
                CleanExpiredEntries();
                var searchTermLower = searchTerm.ToLower();
                
                return _cache
                    .Values
                    .Where(item => 
                        item.Name.ToLower().Contains(searchTermLower) ||
                        (item.Username?.ToLower().Contains(searchTermLower) ?? false) ||
                        item.Uris.Any(uri => uri.ToLower().Contains(searchTermLower)))
                    .Select(item => new BitwardenItem
                    {
                        id = item.Id,
                        name = item.Name,
                        hasTotp = item.HasTotp,
                        login = new BitwardenLogin
                        {
                            username = item.Username,
                            uris = item.Uris.Select(uri => new BitwardenUri { uri = uri }).ToList()
                        }
                    })
                    .ToList();
            }
        }

        public bool IsCacheValid()
        {
            lock (_cacheLock)
            {
                if (_cache.Count == 0) return false;

                // Check if any item is still valid (not expired)
                var now = DateTime.Now;
                return _cache.Any(kvp => (now - kvp.Value.CacheTime) <= _cacheExpiration);
            }
        }

        public void ClearCache()
        {
            lock (_cacheLock)
            {
                _cache.Clear();
                if (File.Exists(_cacheFilePath))
                {
                    File.Delete(_cacheFilePath);
                }
                Logger.Log("Vault cache cleared", LogLevel.Debug);
            }
        }
    }
}