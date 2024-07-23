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
using System.Security;
using System.Runtime.InteropServices;

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
        private bool _isLocked = false;
        private Timer? _autoLockTimer;
        private bool _needsInitialSetup = false;
        private SecureString? _clientSecret;

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
            _clientSecret = SecureCredentialManager.RetrieveCredential(_settings.ClientId);
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
            SetupAutoLockTimer();
        }

        private void SetupAutoLockTimer()
        {
            if (_autoLockTimer != null)
            {
                _autoLockTimer.Dispose();
            }

            if (!_settings.KeepUnlocked && _settings.LockTime > 0)
            {
                _autoLockTimer = new Timer(AutoLockVault, null, TimeSpan.FromMinutes(_settings.LockTime), Timeout.InfiniteTimeSpan);
            }
        }

        private void AutoLockVault(object? state)
        {
            if (!_isLocked)
            {
                LockVault();
                Logger.Log("Vault auto-locked due to inactivity", LogLevel.Info);
            }
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
            if (string.IsNullOrEmpty(_settings.ClientId) || _clientSecret == null || _clientSecret.Length == 0)
            {
                Logger.Log("Client ID or Client Secret not set. Initial setup required.", LogLevel.Info);
                _needsInitialSetup = true;
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

                if (!string.IsNullOrEmpty(_settings.SessionKey))
                {
                    Logger.Log("Existing session found, attempting to use it", LogLevel.Info);
                    UpdateHttpClientAuthorization();
                    if (await IsSessionValid())
                    {
                        Logger.Log("Existing session is valid", LogLevel.Info);
                        _isLocked = false;
                        return;
                    }
                    Logger.Log("Existing session is invalid, need to re-authenticate", LogLevel.Info);
                }

                if (_clientSecret == null || _clientSecret.Length == 0)
                {
                    Logger.Log("Client secret is missing, vault is locked", LogLevel.Info);
                    _isLocked = true;
                    return;
                }

                // At this point, we have a client secret but no valid session.
                // We should prompt the user to enter their master password to unlock the vault.
                // This should be handled in the UI layer (e.g., in HandleBitwardenSearch),
                // so we'll just set the locked state here.

                Logger.Log("No valid session found, vault is locked", LogLevel.Info);
                _isLocked = true;
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed during login and unlock process", ex);
                _isLocked = true;
            }
        }

        private async Task<bool> IsSessionValid()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{ApiBaseUrl}/sync");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
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
            if (!_isLocked && !string.IsNullOrEmpty(_settings.SessionKey))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.SessionKey.Trim());
                Logger.Log("Session key updated", LogLevel.Info);
            }
            else
            {
                _httpClient.DefaultRequestHeaders.Authorization = null;
                Logger.Log("Authorization cleared due to locked state or missing session key", LogLevel.Info);
            }
        }

        public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
        {
            if (_needsInitialSetup)
            {
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Bitwarden plugin needs setup",
                        SubTitle = "Click here to open settings and enter your Client ID and Client Secret",
                        IcoPath = "Images/bitwarden.png",
                        Action = _ => 
                        {
                            _context.API.OpenSettingDialog();
                            return true;
                        }
                    }
                };
            }
            
            if (query.Search.Contains("/unlock"))
            {
                Logger.Log($"QueryAsync called with ActionKeyword: {query.ActionKeyword}, Search: /unlock ******", LogLevel.Debug);
            }
            else 
            {
                Logger.Log($"QueryAsync called with ActionKeyword: {query.ActionKeyword}, Search: {query.Search}", LogLevel.Debug);
            }

            if (query.ActionKeyword.ToLower() == "bw")
            {
                Logger.Log("Executing HandleBitwardenSearch", LogLevel.Debug);
                return await HandleBitwardenSearch(query, token);
            }

            Logger.Log($"No matching action keyword found for: {query.ActionKeyword}", LogLevel.Warning);
            return new List<Result>();
        }

        private async Task<List<Result>> HandleBitwardenSearch(Query query, CancellationToken token)
        {
            if (_clientSecret == null || _clientSecret.Length == 0)
            {
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Bitwarden client secret is missing",
                        SubTitle = "Please set up the client secret in the plugin settings",
                        IcoPath = "Images/bitwarden.png",
                        Action = _ => 
                        {
                            _context.API.OpenSettingDialog();
                            return true;
                        }
                    }
                };
            }

            if (query.FirstSearch?.ToLower() == "/lock")
            {
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Lock Bitwarden Vault",
                        SubTitle = "Press Enter to confirm locking the vault",
                        IcoPath = "Images/bitwarden.png",
                        Action = _ => 
                        {
                            var lockResults = LockVault();
                            _context.API.ChangeQuery(""); // Clear the query after locking
                            return true;
                        }
                    }
                };
            }
            else if (query.FirstSearch?.ToLower() == "/unlock")
            {
                if (string.IsNullOrEmpty(query.SecondSearch))
                {
                    return new List<Result>
                    {
                        new Result
                        {
                            Title = "Unlock Bitwarden Vault",
                            SubTitle = "Enter your master password and press Enter to unlock",
                            IcoPath = "Images/bitwarden.png"
                        }
                    };
                }
                else
                {
                    return new List<Result>
                    {
                        new Result
                        {
                            Title = "Unlock Bitwarden Vault",
                            SubTitle = "Press Enter to confirm unlocking with the provided password",
                            IcoPath = "Images/bitwarden.png",
                            Action = _ => 
                            {
                                Task.Run(async () =>
                                {
                                    var unlockResults = await UnlockVault(query.SecondSearch);
                                    _context.API.ChangeQuery(""); // Clear the query after unlocking
                                });
                                return true;
                            }
                        }
                    };
                }
            }

            if (_isLocked)
            {
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Bitwarden vault is locked",
                        SubTitle = "Use 'bw /unlock <password>' to unlock",
                        IcoPath = "Images/bitwarden.png"
                    }
                };
            }

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
                    },
                    new Result
                    {
                        Title = "/lock",
                        SubTitle = "Lock your Bitwarden vault",
                        IcoPath = "Images/bitwarden.png",
                        Action = _ => 
                        {
                            _context.API.ChangeQuery($"{_context.CurrentPluginMetadata.ActionKeyword} /lock");
                            return false;
                        }
                    },
                    new Result
                    {
                        Title = "/unlock",
                        SubTitle = "Unlock your Bitwarden vault",
                        IcoPath = "Images/bitwarden.png",
                        Action = _ => 
                        {
                            _context.API.ChangeQuery($"{_context.CurrentPluginMetadata.ActionKeyword} /unlock ");
                            return false;
                        }
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
                    var mainResult = new Result
                    {
                        Title = item.name,
                        SubTitle = BuildSubTitle(item),
                        IcoPath = "Images/bitwarden.png", // Use default icon initially
                        Action = context => HandleItemAction(context, item, ActionType.Default)
                    };
                    results.Add(mainResult);

                    Logger.Log($"Processing item: {item.name}, hasTotp: {item.hasTotp}", LogLevel.Debug);

                    if (item.hasTotp == true)
                    {
                        Logger.Log($"Adding TOTP result for item: {item.name}", LogLevel.Debug);
                        results.Add(new Result
                        {
                            Title = $"Copy TOTP for {item.name}",
                            SubTitle = "Click to copy TOTP code",
                            IcoPath = "Images/totp.png",
                            Action = context => HandleItemAction(context, item, ActionType.CopyTotp)
                        });
                    }

                    // Start favicon download asynchronously
                    _ = Task.Run(() => UpdateFaviconAsync(item, mainResult));
                }

                Logger.Log($"Total results generated: {results.Count}", LogLevel.Debug);
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

        private List<Result> LockVault()
        {
            _isLocked = true;
            _settings.SessionKey = string.Empty;
            _context.API.SaveSettingJsonStorage<BitwardenFlowSettings>();
            UpdateHttpClientAuthorization();

            if (_autoLockTimer != null)
            {
                _autoLockTimer.Dispose();
                _autoLockTimer = null;
            }

            return new List<Result>
            {
                new Result
                {
                    Title = "Bitwarden vault locked",
                    SubTitle = "Use 'bw /unlock <password>' to unlock",
                    IcoPath = "Images/bitwarden.png"
                }
            };
        }

        private async Task<List<Result>> UnlockVault(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                Logger.Log("Unlock attempt with empty password", LogLevel.Warning);
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Enter your master password",
                        SubTitle = "Type your password after 'bw /unlock'",
                        IcoPath = "Images/bitwarden.png"
                    }
                };
            }

            try
            {
                Logger.Log("Attempting to unlock vault", LogLevel.Debug);
                
                if (_clientSecret == null || _clientSecret.Length == 0)
                {
                    Logger.Log("Client secret is missing", LogLevel.Error);
                    return new List<Result>
                    {
                        new Result
                        {
                            Title = "Failed to unlock vault",
                            SubTitle = "Client secret is missing. Please set it up in the plugin settings.",
                            IcoPath = "Images/bitwarden.png"
                        }
                    };
                }

                var unlockProcess = new Process
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

                // Set environment variables for client ID
                unlockProcess.StartInfo.EnvironmentVariables["BW_CLIENTID"] = _settings.ClientId;

                // Handle the client secret securely
                IntPtr clientSecretPtr = IntPtr.Zero;
                try
                {
                    clientSecretPtr = Marshal.SecureStringToGlobalAllocUnicode(_clientSecret);
                    unlockProcess.StartInfo.EnvironmentVariables["BW_CLIENTSECRET"] = Marshal.PtrToStringUni(clientSecretPtr);

                    Logger.Log("Starting unlock process", LogLevel.Debug);
                    unlockProcess.Start();

                    // Write only the master password
                    Logger.Log("Writing master password to process", LogLevel.Debug);
                    await unlockProcess.StandardInput.WriteLineAsync(password);
                    await unlockProcess.StandardInput.FlushAsync();

                    Logger.Log("Reading session key from process output", LogLevel.Debug);
                    string sessionKey = await unlockProcess.StandardOutput.ReadToEndAsync();
                    string errorOutput = await unlockProcess.StandardError.ReadToEndAsync();

                    if (!string.IsNullOrWhiteSpace(errorOutput))
                    {
                        Logger.Log($"CLI process output: {errorOutput}", LogLevel.Debug);
                    }

                    if (string.IsNullOrWhiteSpace(sessionKey))
                    {
                        Logger.Log("Failed to obtain session key", LogLevel.Error);
                        return new List<Result>
                        {
                            new Result
                            {
                                Title = "Failed to unlock vault",
                                SubTitle = "Incorrect credentials or server error",
                                IcoPath = "Images/bitwarden.png"
                            }
                        };
                    }

                    Logger.Log("Session key obtained successfully", LogLevel.Debug);
                    _settings.SessionKey = sessionKey.Trim();
                    _context.API.SaveSettingJsonStorage<BitwardenFlowSettings>();
                    UpdateHttpClientAuthorization();
                    _isLocked = false;
                    _needsInitialSetup = false;

                    Logger.Log($"Attempting to start Bitwarden server with session key: {_settings.SessionKey.Substring(0, Math.Min(10, _settings.SessionKey.Length))}...", LogLevel.Debug);
                    await StartBitwardenServer();
                    Logger.Log($"Bitwarden server process ID: {_serveProcess?.Id}", LogLevel.Debug);

                    SetupAutoLockTimer();

                    Logger.Log("Vault unlocked successfully", LogLevel.Info);
                    return new List<Result>
                    {
                        new Result
                        {
                            Title = "Bitwarden vault unlocked",
                            SubTitle = "You can now search for your items",
                            IcoPath = "Images/bitwarden.png"
                        }
                    };
                }
                finally
                {
                    if (clientSecretPtr != IntPtr.Zero)
                    {
                        Marshal.ZeroFreeGlobalAllocUnicode(clientSecretPtr);
                    }
                    // Clear the environment variable
                    unlockProcess.StartInfo.EnvironmentVariables.Remove("BW_CLIENTSECRET");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to unlock Bitwarden vault or start server", ex);
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Error unlocking vault or starting server",
                        SubTitle = "Check logs for details",
                        IcoPath = "Images/bitwarden.png"
                    }
                };
            }
        }

        private enum ActionType
        {
            Default,
            CopyTotp
        }

        private bool HandleItemAction(ActionContext context, BitwardenItem item, ActionType actionType)
        {
            switch (actionType)
            {
                case ActionType.CopyTotp:
                    return CopyTotpCode(item);
                case ActionType.Default:
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
                    break;
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
                parts.Add($"User: {item.login.username}");
            }

            if (item.login?.uris != null && item.login.uris.Any())
            {
                parts.Add($"URLs: {item.login.uris.Count}");
            }

            if (item.hasTotp == true)
            {
                parts.Add("TOTP Available");
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
                if (_serveProcess == null || _serveProcess.HasExited)
                {
                    Logger.Log("Bitwarden server is not running. Attempting to start...", LogLevel.Warning);
                    await StartBitwardenServer();
                }

                var encodedSearchTerm = Uri.EscapeDataString(searchTerm);
                var url = $"{ApiBaseUrl}/list/object/items?search={encodedSearchTerm}";
                Logger.Log($"Sending request to: {url}", LogLevel.Debug);
                
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(20));

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

                var items = new List<BitwardenItem>();
                foreach (var item in dataArray)
                {
                    var bitwardenItem = item.ToObject<BitwardenItem>();
                    if (bitwardenItem != null)
                    {
                        // Check if the item has a non-null and non-empty TOTP
                        var totpToken = item["login"]?["totp"]?.ToString();
                        bitwardenItem.hasTotp = !string.IsNullOrWhiteSpace(totpToken);
                        
                        Logger.Log($"Item: {bitwardenItem.name}, HasTotp: {bitwardenItem.hasTotp}", LogLevel.Debug);
                        
                        items.Add(bitwardenItem);
                    }
                }

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
                
                bool shouldNotify = false;
                switch (itemType.ToLower())
                {
                    case "password":
                        shouldNotify = _settings.NotifyOnPasswordCopy;
                        break;
                    case "username":
                        shouldNotify = _settings.NotifyOnUsernameCopy;
                        break;
                    case "uri":
                        shouldNotify = _settings.NotifyOnUriCopy;
                        break;
                    case "totp code":
                        shouldNotify = _settings.NotifyOnTotpCopy;
                        break;
                }

                if (shouldNotify)
                {
                    _context.API.ShowMsg($"{itemType} Copied", "Press Ctrl+V to paste in your previous window", string.Empty);
                }
            });
        }

        public Control CreateSettingPanel()
        {
            return new BitwardenFlowSettingPanel(_settings, UpdateSettings);
        }

        private void UpdateSettings(BitwardenFlowSettings newSettings)
        {
            _settings = newSettings;
            _context.API.SaveSettingJsonStorage<BitwardenFlowSettings>();
            Logger.Log("Settings updated");
            SetupAutoLockTimer();
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            _serveProcess?.Kill();
            _serveProcess?.Dispose();
            _initializationLock.Dispose();
            Logger.Log("Plugin disposed", LogLevel.Debug);
            _autoLockTimer?.Dispose();
            if (_clientSecret != null)
            {
                _clientSecret.Dispose();
            }
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

        private bool CopyTotpCode(BitwardenItem item)
        {
            Task.Run(async () =>
            {
                try
                {
                    var totpCode = await GetTotpCodeAsync(item.id);
                    if (!string.IsNullOrEmpty(totpCode))
                    {
                        CopyToClipboard(totpCode, "TOTP Code");
                    }
                    else
                    {
                        _context.API.ShowMsg("No TOTP Code", "This item does not have a TOTP code associated with it.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError("Error fetching TOTP code", ex);
                    _context.API.ShowMsg("Error", "Failed to fetch TOTP code. Check logs for details.");
                }
            });
            return true;
        }

        private async Task<string> GetTotpCodeAsync(string itemId)
        {
            var url = $"{ApiBaseUrl}/object/totp/{itemId}";
            var response = await _httpClient.GetAsync(url);
            
            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var jObject = JObject.Parse(responseContent);
                return jObject["data"]?["data"]?.ToString() ?? string.Empty;
            }
            
            return string.Empty;
        }
    }

    public class BitwardenItem
    {
        public string id { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public BitwardenLogin? login { get; set; }
        public bool? hasTotp { get; set; }
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