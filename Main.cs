using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Linq;
using System.Text;
using Flow.Launcher.Plugin;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public class Main : IAsyncPlugin, ISettingProvider, IDisposable
    {
        private readonly HttpClient _httpClient;
        private const string ApiBaseUrl = "http://localhost:8087"; // Bitwarden CLI server URL
        private Process? _serveProcess;
        private bool _isInitialized = false;
        private readonly SemaphoreSlim _initializationLock = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, string> _faviconUrls = new Dictionary<string, string>();
        private string? _faviconCacheDir;
        private Dictionary<string, Result> _currentResults = new Dictionary<string, Result>();
        private const int DebounceDelay = 300; // milliseconds
        private CancellationTokenSource _debounceTokenSource;
        private PluginInitContext _context = null!;
        private BitwardenFlowSettings _settings = null!;

        public Main()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _debounceTokenSource = new CancellationTokenSource();

            // Initialize favicon cache directory
            var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var assemblyDirectory = Path.GetDirectoryName(assemblyLocation);
            if (assemblyDirectory != null)
            {
                _faviconCacheDir = Path.Combine(assemblyDirectory, "FaviconCache");
                try
                {
                    Directory.CreateDirectory(_faviconCacheDir);
                    Logger.Log($"Favicon cache directory created: {_faviconCacheDir}", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to create favicon cache directory: {ex.Message}", LogLevel.Error);
                    _faviconCacheDir = null;
                }
            }
            else
            {
                Logger.Log("Unable to determine assembly directory.", LogLevel.Error);
                _faviconCacheDir = null;
            }
        }

        public void Init(PluginInitContext context)
        {
            _context = context;
            var pluginDirectory = context.CurrentPluginMetadata.PluginDirectory;
            
            // Load settings
            _settings = context.API.LoadSettingJsonStorage<BitwardenFlowSettings>();
            
            // If settings are null, initialize with default values
            if (_settings == null)
            {
                _settings = new BitwardenFlowSettings();
                context.API.SaveSettingJsonStorage<BitwardenFlowSettings>();
            }

            // Initialize logger
            Logger.Initialize(pluginDirectory, _settings);
            Logger.Log("Plugin initialization started", LogLevel.Info);

            // Start the initialization process
            _ = Task.Run(async () =>
            {
                try
                {
                    await EnsureInitialized();
                }
                catch (Exception ex)
                {
                    Logger.LogError("Error during background initialization", ex);
                }
            });
        }

        public async Task InitAsync(PluginInitContext context)
        {
            _context = context;
            _settings = context.API.LoadSettingJsonStorage<BitwardenFlowSettings>();
            await Task.Run(() => 
            {
                Logger.Initialize(context.CurrentPluginMetadata.PluginDirectory, _settings);
                Logger.Log("Plugin initialization started", LogLevel.Info);
            });
            Logger.Log("Plugin initialization completed", LogLevel.Info);

            // Start the initialization process
            _ = Task.Run(async () =>
            {
                try
                {
                    await EnsureInitialized();
                }
                catch (Exception ex)
                {
                    Logger.LogError("Error during background initialization", ex);
                }
            });
        }

        private async Task EnsureInitialized()
        {
            if (_isInitialized) return;

            await _initializationLock.WaitAsync();
            try
            {
                if (_isInitialized) return;
                Logger.Log("Starting full initialization", LogLevel.Info);
                await EnsureBitwardenSetup();
                _isInitialized = true;
                Logger.Log("Full initialization completed", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Logger.LogError("Error during initialization", ex);
                _isInitialized = false;
            }
            finally
            {
                _initializationLock.Release();
            }
        }

        private async Task EnsureBitwardenSetup()
        {
            if (string.IsNullOrEmpty(_settings.ClientId) || string.IsNullOrEmpty(_settings.ClientSecret))
            {
                Logger.Log("Client ID or Client Secret not set. Skipping Bitwarden setup.", LogLevel.Error);
                return;
            }

            try
            {
                Logger.Log("Starting Bitwarden setup");
                if (!IsBitwardenCliInstalled())
                {
                    Logger.Log("Bitwarden CLI not found. Please install it and restart the plugin.", LogLevel.Error);
                    return;
                }
                Logger.Log("Bitwarden CLI found");

                // Always attempt to login and unlock
                await LoginAndUnlock();
                
                _isInitialized = true;
                Logger.Log("Bitwarden setup completed successfully", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Logger.LogError("Error during Bitwarden setup", ex);
                _isInitialized = false;
            }
        }

        private bool IsBitwardenCliInstalled()
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "bw",
                        Arguments = "--version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return !string.IsNullOrEmpty(output);
            }
            catch
            {
                return false;
            }
        }

        private async Task LoginAndUnlock()
        {
            try
            {
                Logger.Log("Starting login and unlock process", LogLevel.Info);
                Logger.Log("Setting environment variables for Bitwarden CLI", LogLevel.Debug);

                Logger.Log("Attempting to unlock Bitwarden vault", LogLevel.Info);
                using var unlockProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "bw",
                        Arguments = "unlock --raw",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                unlockProcess.Start();

                if (string.IsNullOrEmpty(_settings.MasterPassword))
                {
                    Logger.Log("Master password not set. Please set it in the plugin settings.", LogLevel.Error);
                    throw new Exception("Master password not set");
                }

                await unlockProcess.StandardInput.WriteLineAsync(_settings.MasterPassword);
                await unlockProcess.StandardInput.FlushAsync();

                Logger.Log("Login completed, attempting to unlock vault", LogLevel.Info);

                var unlockTask = unlockProcess.WaitForExitAsync();
                if (await Task.WhenAny(unlockTask, Task.Delay(TimeSpan.FromSeconds(30))) != unlockTask)
                {
                    throw new TimeoutException("Unlock process timed out after 30 seconds");
                }
                string sessionKey = await unlockProcess.StandardOutput.ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(sessionKey))
                {
                    throw new Exception("Failed to obtain session key");
                }

                _settings.SessionKey = sessionKey.Trim();
                _context.API.SaveSettingJsonStorage<BitwardenFlowSettings>();
                UpdateHttpClientAuthorization();
                Logger.Log("Bitwarden vault unlocked successfully", LogLevel.Info);

                await StartBitwardenServer();
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to login or unlock Bitwarden", ex);
                throw;
            }
        }

        private async Task StartBitwardenServer()
        {
            if (_serveProcess != null && !_serveProcess.HasExited)
            {
                Logger.Log("Bitwarden server already running");
                return;
            }

            Logger.Log("Starting Bitwarden server");
            _serveProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "bw",
                    Arguments = $"serve --session {_settings.SessionKey}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            _serveProcess.OutputDataReceived += (sender, e) => Logger.Log($"Bitwarden server output: {e.Data}", LogLevel.Debug);
            _serveProcess.ErrorDataReceived += (sender, e) => Logger.Log($"Bitwarden server error: {e.Data}", LogLevel.Error);

            Logger.Log("Starting Bitwarden server process");
            _serveProcess.Start();
            _serveProcess.BeginOutputReadLine();
            _serveProcess.BeginErrorReadLine();

            Logger.Log("Waiting for Bitwarden server to initialize");
            await Task.Delay(5000);

            if (_serveProcess.HasExited)
            {
                throw new Exception("Bitwarden server process exited unexpectedly");
            }

            try
            {
                using var client = new HttpClient();
                var response = await client.GetAsync($"{ApiBaseUrl}/status");
                response.EnsureSuccessStatusCode();
                Logger.Log("Bitwarden server started successfully", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to connect to Bitwarden server", ex);
                throw;
            }
        }

        private void UpdateHttpClientAuthorization()
        {
            if (!string.IsNullOrEmpty(_settings.SessionKey))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.SessionKey.Trim());
                Logger.Log("Session key updated", LogLevel.Info);
            }
            else
            {
                _httpClient.DefaultRequestHeaders.Authorization = null;
                Logger.Log("Session key cleared", LogLevel.Info);
            }
        }

        public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
        {
            if (!_isInitialized)
            {
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Bitwarden plugin is initializing...",
                        SubTitle = "Please wait a moment and try again",
                        IcoPath = "Images/bitwarden.png"
                    }
                };
            }

            if (string.IsNullOrWhiteSpace(query.Search))
            {
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Search Bitwarden",
                        SubTitle = "Type to search your Bitwarden vault",
                        IcoPath = "Images/bitwarden.png"
                    }
                };
            }

            // Cancel any previous debounce task
            _debounceTokenSource.Cancel();
            _debounceTokenSource = new CancellationTokenSource();

            try
            {
                // Delay for debounce
                await Task.Delay(DebounceDelay, _debounceTokenSource.Token);

                Logger.Log("Searching Bitwarden", LogLevel.Info);

                var items = await SearchBitwardenAsync(query.Search, token);
                
                if (items.Count == 0)
                {
                    return new List<Result>
                    {
                        new Result
                        {
                            Title = "No results found",
                            SubTitle = "Try a different search term",
                            IcoPath = "Images/bitwarden.png"
                        }
                    };
                }

                var results = new List<Result>();

                foreach (var item in items)
                {
                    if (_currentResults.TryGetValue(item.id, out var cachedResult))
                    {
                        results.Add(cachedResult);
                    }
                    else
                    {
                        var newResult = new Result
                        {
                            Title = item.name,
                            SubTitle = BuildSubTitle(item),
                            IcoPath = "Images/bitwarden.png", // Use default icon initially
                            Action = context => HandleItemAction(context, item)
                        };
                        results.Add(newResult);
                        _currentResults[item.id] = newResult;

                        // Start favicon download asynchronously
                        _ = Task.Run(() => UpdateFaviconAsync(item, newResult));
                    }
                }

                return results;
            }
            catch (TaskCanceledException)
            {
                // Debounce was canceled, return empty list
                return new List<Result>();
            }
            catch (Exception ex)
            {
                Logger.LogError("Error during query execution", ex);
                return new List<Result>
                {
                    new Result
                    {
                        Title = "An error occurred while querying Bitwarden",
                        SubTitle = "Check logs for details",
                        IcoPath = "Images/bitwarden.png",
                        Action = _ =>
                        {
                            _context.API.OpenSettingDialog();
                            return true;
                        }
                    }
                };
            }
        }

        private bool HandleItemAction(ActionContext context, BitwardenItem item)
        {
            if (context.SpecialKeyState.CtrlPressed && context.SpecialKeyState.ShiftPressed)
            {
                return ShowUriListPopup(item);
            }
            else if (context.SpecialKeyState.CtrlPressed)
            {
                CopyToClipboard(item.login?.username, "Username");
            }
            else
            {
                CopyToClipboard(item.login?.password, "Password");
            }
            return true;
        }

        private async Task UpdateFaviconAsync(BitwardenItem item, Result result)
        {
            if (item.login?.uris != null && item.login.uris.Any())
            {
                var webUri = item.login.uris
                    .Select(u => u.uri)
                    .FirstOrDefault(u => u.StartsWith("http://") || u.StartsWith("https://"));

                if (!string.IsNullOrEmpty(webUri))
                {
                    var faviconPath = await DownloadAndCacheFaviconAsync(webUri, CancellationToken.None);
                    if (faviconPath != "Images/bitwarden.png")
                    {
                        result.IcoPath = faviconPath;
                        // Instead of refreshing the result, we'll update the internal cache
                        _currentResults[item.id] = result;
                    }
                }
            }
        }

        private string BuildSubTitle(BitwardenItem item)
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(item.login?.username))
            {
                parts.Add($"Username: {item.login.username}");
            }

            if (item.login?.uris != null && item.login.uris.Any())
            {
                var uriPart = item.login.uris.Count == 1
                    ? $"URL: {item.login.uris[0].uri}"
                    : $"URLs: {item.login.uris.Count}";
                parts.Add(uriPart);
            }

            if (parts.Count == 0)
            {
                return "No additional information available";
            }

            return string.Join(" | ", parts);
        }

        private string GetDetailedItemInfo(BitwardenItem item)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"Name: {item.name}");

            if (item.login != null)
            {
                if (!string.IsNullOrEmpty(item.login.username))
                {
                    sb.AppendLine($"Username: {item.login.username}");
                }

                if (item.login.uris != null && item.login.uris.Any())
                {
                    sb.AppendLine("URLs:");
                    foreach (var uri in item.login.uris)
                    {
                        sb.AppendLine($"  - {uri.uri}");
                    }
                }
            }

            return sb.ToString();
        }

        private bool ShowUriListPopup(BitwardenItem item)
        {
            if (item.login?.uris == null || !item.login.uris.Any())
            {
                _context.API.ShowMsg("No URIs", "This item does not have any associated URIs.");
                return false;
            }

            var uris = item.login.uris
                .Where(u => !string.IsNullOrEmpty(u.uri))
                .Select(u => u.uri)
                .ToList();

            if (!uris.Any())
            {
                _context.API.ShowMsg("No Valid URIs", "This item does not have any valid URIs.");
                return false;
            }

            var window = new UriListWindow($"URIs for {item.name}", uris, _context.API);
            if (window.ShowDialog() == true && window.SelectedUri != null)
            {
                CopyToClipboard(window.SelectedUri, "URI");
                return true;
            }
            return false;
        }

        private async Task<List<BitwardenItem>> SearchBitwardenAsync(string searchTerm, CancellationToken token)
        {
            try
            {
                var encodedSearchTerm = Uri.EscapeDataString(searchTerm);
                var url = $"{ApiBaseUrl}/list/object/items?search={encodedSearchTerm}";
                Logger.Log($"Sending request to: {url}", LogLevel.Debug);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(20)); // Single 20-second timeout

                var response = await _httpClient.GetAsync(url, cts.Token);
                
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"API request failed with status code: {response.StatusCode}", LogLevel.Warning);
                    return new List<BitwardenItem>();
                }

                var responseContent = await response.Content.ReadAsStringAsync(cts.Token);
                Logger.Log("Received response from Bitwarden API", LogLevel.Debug);

                var jObject = JObject.Parse(responseContent);
                var dataArray = jObject["data"]?["data"] as JArray;

                if (dataArray == null)
                {
                    Logger.Log("No data array found in response", LogLevel.Warning);
                    return new List<BitwardenItem>();
                }

                var items = dataArray.ToObject<List<BitwardenItem>>() ?? new List<BitwardenItem>();

                Logger.Log($"Found {items.Count} items matching the search term", LogLevel.Debug);
                return items;
            }
            catch (TaskCanceledException)
            {
                Logger.Log("Search operation timed out", LogLevel.Warning);
                return new List<BitwardenItem>();
            }
            catch (Exception ex)
            {
                Logger.LogError("Unexpected error during Bitwarden search", ex);
                return new List<BitwardenItem>();
            }
        }

        private void CopyToClipboard(string? content, string itemType)
        {
            if (content == null) return;
            
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                System.Windows.Clipboard.SetText(content);
                _context.API.ShowMsg($"{itemType} Copied", "Press Ctrl+V to paste in your previous window", string.Empty);
            });
        }

        public Control CreateSettingPanel()
        {
            return new BitwardenFlowSettingPanel(_settings, UpdateSettings);
        }

        private async void UpdateSettings(BitwardenFlowSettings newSettings)
        {
            _settings = newSettings;
            _context.API.SaveSettingJsonStorage<BitwardenFlowSettings>();
            Logger.Log("Settings updated");
            _isInitialized = false; // Force re-initialization with new settings
            await EnsureInitialized();
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            _serveProcess?.Kill();
            _serveProcess?.Dispose();
            _initializationLock.Dispose();
            Logger.Log("Plugin disposed", LogLevel.Debug);
        }

        private static readonly object _faviconLock = new object();

        private async Task<string> DownloadAndCacheFaviconAsync(string url, CancellationToken token)
        {
            if (string.IsNullOrEmpty(_faviconCacheDir))
            {
                Logger.Log("Favicon cache directory is not set. Using default icon.", LogLevel.Warning);
                return "Images/bitwarden.png";
            }

            var uri = new Uri(url);
            var domain = uri.Host;
            var safeFileName = string.Join("_", domain.Split(Path.GetInvalidFileNameChars()));
            var filePath = Path.Combine(_faviconCacheDir, $"{safeFileName}.ico");

            Logger.Log($"Attempting to cache favicon for {domain}", LogLevel.Debug);

            if (File.Exists(filePath) && (DateTime.Now - File.GetLastWriteTime(filePath)).TotalDays < 1)
            {
                Logger.Log($"Using cached favicon for {domain}", LogLevel.Debug);
                return filePath;
            }

            const int maxRetries = 3;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    cts.CancelAfter(TimeSpan.FromSeconds(5));

                    var faviconUrl = $"https://www.google.com/s2/favicons?domain={domain}&sz=32";
                    Logger.Log($"Downloading favicon for {domain}", LogLevel.Debug);

                    using var response = await _httpClient.GetAsync(faviconUrl, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                        lock (_faviconLock)
                        {
                            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                            stream.CopyTo(fileStream);
                        }
                        Logger.Log($"Downloaded and cached favicon for {domain}", LogLevel.Debug);
                        return filePath;
                    }
                    else
                    {
                        Logger.Log($"Failed to download favicon for {domain}: {response.StatusCode}", LogLevel.Info);
                    }
                }
                catch (TaskCanceledException)
                {
                    Logger.Log($"Favicon download timed out for {domain}", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error downloading favicon for {domain}: {ex.Message}", LogLevel.Info);
                }

                if (attempt < maxRetries - 1)
                {
                    Logger.Log($"Retrying favicon download for {domain} (attempt {attempt + 2}/{maxRetries})", LogLevel.Debug);
                    await Task.Delay(1000 * (attempt + 1), token);
                }
            }

            Logger.Log($"Failed to download favicon for {domain} after {maxRetries} attempts. Using default icon.", LogLevel.Info);
            return "Images/bitwarden.png";
        }
    }

    public class BitwardenItem
    {
        public string id { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public BitwardenLogin? login { get; set; }
    }

    public class BitwardenLogin
    {
        public string? username { get; set; }
        public string? password { get; set; }
        public List<BitwardenUri>? uris { get; set; }
    }

    public class BitwardenUri
    {
        public string uri { get; set; } = string.Empty;
        public int? match { get; set; }
    }
}