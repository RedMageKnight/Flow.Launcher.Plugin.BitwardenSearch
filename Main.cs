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
using System.Net.Sockets;
using System.Windows.Input;

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
        private Dictionary<string, (Result Result, BitwardenItem Item)> _currentResults = new Dictionary<string, (Result, BitwardenItem)>();
        private const int DebounceDelay = 300; // milliseconds
        private CancellationTokenSource _debounceTokenSource;
        private PluginInitContext _context = null!;
        private BitwardenFlowSettings _settings = null!;
        private bool _isLocked = false;
        private Timer? _autoLockTimer;
        private bool _needsInitialSetup = false;
        private SecureString? _clientSecret;
        private string _selectedItemId = string.Empty;
        private bool _isFirstSearchAfterUnlock = true;

        public Main()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _debounceTokenSource = new CancellationTokenSource();
            _isLocked = true; // Initialize as locked

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
            try
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

                // Run EnsureBitwardenSetup asynchronously
                Task.Run(async () => 
                {
                    try 
                    {
                        await VerifyAndApplySettings();
                        await EnsureBitwardenSetup();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("Error during background initialization", ex);
                    }
                });

                Logger.Log("Plugin initialization completed", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Logger.LogError("Error during plugin initialization", ex);
            }
        }

        public async Task InitAsync(PluginInitContext context)
        {
            _context = context;
            _settings = context.API.LoadSettingJsonStorage<BitwardenFlowSettings>();
            Logger.Initialize(context.CurrentPluginMetadata.PluginDirectory, _settings);
            Logger.Log("Plugin initialization started", LogLevel.Info);

            // Verify and apply settings
            await VerifyAndApplySettings();

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

        private async Task<bool> VerifyAndApplySettings()
        {
            Logger.Log("Verifying and applying settings", LogLevel.Info);

            if (string.IsNullOrEmpty(_settings.ClientId))
            {
                Logger.Log("Client ID is not set", LogLevel.Warning);
                _needsInitialSetup = true;
                return false;
            }

            var clientSecret = SecureCredentialManager.RetrieveCredential(_settings.ClientId);
            if (clientSecret == null || clientSecret.Length == 0)
            {
                Logger.Log("Client Secret is not set", LogLevel.Warning);
                _needsInitialSetup = true;
                return false;
            }

            _needsInitialSetup = false;
            _clientSecret = clientSecret;

            // Verify API key asynchronously
            var apiKeyValid = await VerifyApiKey();
            if (!apiKeyValid)
            {
                Logger.Log("API key verification failed, attempting login", LogLevel.Warning);
                apiKeyValid = await LoginWithApiKey();
                if (!apiKeyValid)
                {
                    Logger.Log("Login attempt failed", LogLevel.Error);
                    _context.API.ShowMsg("API Key Invalid", "The provided API key is invalid or login failed. Please check your settings.");
                    _needsInitialSetup = true;
                    return false;
                }
                else
                {
                    Logger.Log("Login successful", LogLevel.Info);
                }
            }
            else
            {
                Logger.Log("API key verification successful", LogLevel.Info);
            }

            Logger.Log("Settings verified and applied successfully", LogLevel.Info);
            return true;
        }

        private void SetupAutoLockTimer()
        {
            if (_autoLockTimer != null)
            {
                _autoLockTimer.Dispose();
            }

            if (!_settings.KeepUnlocked && _settings.LockTime > 0)
            {
                _autoLockTimer = new Timer(AutoLockVault, null, TimeSpan.FromMinutes(_settings.LockTime), TimeSpan.FromMinutes(_settings.LockTime));
            }
        }

        private void AutoLockVault(object? state)
        {
            if (!_isLocked)
            {
                _settings.SessionKey = string.Empty;
                _context.API.SaveSettingJsonStorage<BitwardenFlowSettings>();
                UpdateHttpClientAuthorization();
                _isLocked = true;
                Logger.Log("Vault auto-locked due to inactivity", LogLevel.Info);
            }
        }

        private void ResetAutoLockTimer()
        {
            if (_autoLockTimer != null && !_settings.KeepUnlocked && _settings.LockTime > 0)
            {
                _autoLockTimer.Change(TimeSpan.FromMinutes(_settings.LockTime), TimeSpan.FromMinutes(_settings.LockTime));
            }
        }

        private async Task<bool> IsLoggedIn()
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "bw",
                    Arguments = "login --check",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return output.Trim().Equals("You are logged in!", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<bool> LoginWithApiKey()
        {
            try
            {
                var clientId = _settings.ClientId;
                var clientSecret = SecureCredentialManager.RetrieveCredential(_settings.ClientId);

                if (string.IsNullOrEmpty(clientId) || clientSecret == null || clientSecret.Length == 0)
                {
                    Logger.Log("API key information is missing", LogLevel.Error);
                    return false;
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                
                // First, check if already logged in
                var checkLoginProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "bw",
                        Arguments = "login --check",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                checkLoginProcess.Start();
                string checkOutput = await checkLoginProcess.StandardOutput.ReadToEndAsync();
                await checkLoginProcess.WaitForExitAsync(cts.Token);

                if (checkOutput.Trim().Equals("You are logged in!", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log("Already logged in to Bitwarden CLI", LogLevel.Info);
                    return true;
                }

                // If not logged in, proceed with login
                var startInfo = new ProcessStartInfo
                {
                    FileName = "bw",
                    Arguments = "login --apikey",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                startInfo.EnvironmentVariables["BW_CLIENTID"] = clientId;
                startInfo.EnvironmentVariables["BW_CLIENTSECRET"] = new System.Net.NetworkCredential(string.Empty, clientSecret).Password;

                using var loginProcess = new Process { StartInfo = startInfo };

                Logger.Log("Starting Bitwarden CLI login process", LogLevel.Debug);
                loginProcess.Start();

                string output = await loginProcess.StandardOutput.ReadToEndAsync();
                string error = await loginProcess.StandardError.ReadToEndAsync();

                await loginProcess.WaitForExitAsync(cts.Token);

                Logger.Log("Login process completed", LogLevel.Debug);
                // Do not log output or error as they might contain sensitive information

                if (!string.IsNullOrEmpty(error))
                {
                    if (error.Contains("You are already logged in"))
                    {
                        Logger.Log("Already logged in to Bitwarden CLI", LogLevel.Info);
                        return true;
                    }
                    if (error.Contains("TypeError: Cannot read properties of null (reading 'profile')"))
                    {
                        Logger.Log("Bitwarden CLI state error detected. Please reinstall the Bitwarden CLI.", LogLevel.Error);
                        _context.API.ShowMsg("Bitwarden CLI Error", "Please reinstall the Bitwarden CLI and try again.");
                        return false;
                    }
                    Logger.Log("Error occurred during login", LogLevel.Error);
                    return false;
                }

                bool loginSuccessful = output.Contains("You are logged in!");
                Logger.Log(loginSuccessful ? "Login successful" : "Login failed", LogLevel.Info);
                return loginSuccessful;
            }
            catch (OperationCanceledException)
            {
                Logger.Log("Login operation timed out", LogLevel.Error);
                return false;
            }
            catch (Exception ex)
            {
                Logger.LogError("Exception during login process", ex);
                return false;
            }
        }

        private async Task<bool> VerifyApiKey()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "bw",
                        Arguments = "login --check",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                if (await Task.WhenAny(process.WaitForExitAsync(cts.Token), Task.Delay(10000, cts.Token)) == Task.Delay(10000, cts.Token))
                {
                    Logger.Log("VerifyApiKey process timed out", LogLevel.Error);
                    process.Kill();
                    return false;
                }

                string output = await outputTask;
                string error = await errorTask;

                Logger.Log($"VerifyApiKey output: {output}", LogLevel.Debug);
                Logger.Log($"VerifyApiKey error: {error}", LogLevel.Debug);

                if (!string.IsNullOrEmpty(error))
                {
                    Logger.Log($"Error checking login status: {error}", LogLevel.Error);
                    return false;
                }

                return output.Trim().Equals("You are logged in!", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Logger.LogError("Exception during API key verification", ex);
                return false;
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
            if (!IsBitwardenCliInstalled())
            {
                Logger.Log("Bitwarden CLI not installed or not functioning properly. Setup cannot proceed.", LogLevel.Error);
                _context.API.ShowMsg("Bitwarden CLI Error", "Please ensure Bitwarden CLI is properly installed and functioning.");
                _needsInitialSetup = true;
                return;
            }

            try
            {
                Logger.Log("Starting Bitwarden setup");
                
                var isLoggedIn = await IsLoggedIn();
                if (!isLoggedIn)
                {
                    var loginSuccess = await LoginWithApiKey();
                    if (!loginSuccess)
                    {
                        Logger.Log("Failed to log in", LogLevel.Error);
                        _needsInitialSetup = true;
                        return;
                    }
                }
                else
                {
                    Logger.Log("Already logged in to Bitwarden CLI", LogLevel.Info);
                }
                
                _isInitialized = true;
                _needsInitialSetup = false;
                _isLocked = await IsVaultLocked();
                Logger.Log($"Bitwarden setup completed successfully. Vault is {(_isLocked ? "locked" : "unlocked")}.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Logger.LogError("Error during Bitwarden setup", ex);
                _isInitialized = false;
                _needsInitialSetup = true;
                _isLocked = true;
            }
        }

        public bool IsBitwardenCliInstalled()
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
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                
                if (!string.IsNullOrEmpty(output))
                {
                    Logger.Log($"Bitwarden CLI found. Version: {output.Trim()}", LogLevel.Info);
                    return true;
                }
                else if (!string.IsNullOrEmpty(error))
                {
                    Logger.Log($"Bitwarden CLI error: {error.Trim()}", LogLevel.Warning);
                    return false;
                }
                else
                {
                    Logger.Log("Bitwarden CLI not found or returned empty output.", LogLevel.Warning);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error checking for Bitwarden CLI: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private bool IsBitwardenCliRunning()
        {
            var processes = Process.GetProcessesByName("bw");
            return processes.Length > 0;
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
            if (!IsBitwardenCliInstalled())
            {
                Logger.Log("Bitwarden CLI not installed. Cannot start server.", LogLevel.Error);
                throw new Exception("Bitwarden CLI not installed");
            }

            if (_serveProcess != null && !_serveProcess.HasExited)
            {
                Logger.Log("Bitwarden server already running", LogLevel.Debug);
                return;
            }

            Logger.Log("Checking if Bitwarden CLI is already running", LogLevel.Debug);
            if (IsBitwardenCliRunning())
            {
                Logger.Log("Bitwarden CLI is already running. Attempting to kill the process.", LogLevel.Warning);
                foreach (var process in Process.GetProcessesByName("bw"))
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                        Logger.Log($"Killed Bitwarden CLI process with PID {process.Id}", LogLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Failed to kill Bitwarden CLI process with PID {process.Id}", ex);
                    }
                }
            }

            Logger.Log("Checking if port 8087 is in use", LogLevel.Debug);
            if (IsPortInUse(8087))
            {
                Logger.Log("Port 8087 is already in use. Attempting to free it up.", LogLevel.Warning);
                KillProcessUsingPort(8087);
                
                // Wait a bit for the port to be released
                await Task.Delay(2000);
                
                if (IsPortInUse(8087))
                {
                    Logger.Log("Failed to free up port 8087. Cannot start Bitwarden server.", LogLevel.Error);
                    throw new Exception("Failed to free up port 8087 for Bitwarden server");
                }
            }

            Logger.Log("Starting Bitwarden server", LogLevel.Info);
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

            try
            {
                Logger.Log("Starting Bitwarden server process", LogLevel.Debug);
                _serveProcess.Start();
                _serveProcess.BeginOutputReadLine();
                _serveProcess.BeginErrorReadLine();

                Logger.Log("Waiting for Bitwarden server to initialize", LogLevel.Debug);
                await Task.Delay(5000);

                // Check if the server is responsive
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        var response = await _httpClient.GetAsync($"{ApiBaseUrl}/status");
                        if (response.IsSuccessStatusCode)
                        {
                            Logger.Log("Bitwarden server started successfully", LogLevel.Info);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error checking server status: {ex.Message}", LogLevel.Warning);
                    }

                    await Task.Delay(1000);
                }

                Logger.Log("Failed to start Bitwarden server", LogLevel.Error);
                throw new Exception("Failed to start Bitwarden server");
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to start or connect to Bitwarden server", ex);
                throw;
            }
        }

        private bool IsPortInUse(int port)
        {
            bool inUse = false;
            using (var tcpClient = new TcpClient())
            {
                try
                {
                    tcpClient.Connect("127.0.0.1", port);
                    inUse = true;
                }
                catch (SocketException)
                {
                    // Port is not in use
                }
            }
            return inUse;
        }

        private async Task<bool> IsExistingServerValid()
        {
            try
            {
                using var client = new HttpClient();
                var response = await client.GetAsync($"{ApiBaseUrl}/status");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private void KillProcessUsingPort(int port)
        {
            try
            {
                var processInfo = new ProcessStartInfo("cmd", $"/c netstat -ano | findstr :{port}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                };

                using (var process = Process.Start(processInfo))
                {
                    if (process == null)
                    {
                        Logger.Log($"Failed to start process to find PID for port {port}", LogLevel.Error);
                        return;
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    string[] lines = output.Split('\n');
                    foreach (string line in lines)
                    {
                        string[] parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 4)
                        {
                            string pidString = parts[4];
                            if (int.TryParse(pidString, out int pid))
                            {
                                try
                                {
                                    Process.GetProcessById(pid).Kill();
                                    Logger.Log($"Killed process with PID {pid} using port {port}", LogLevel.Info);
                                    return;
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogError($"Failed to kill process with PID {pid}", ex);
                                }
                            }
                        }
                    }
                }

                Logger.Log($"No process found using port {port}", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error occurred while trying to kill process using port {port}", ex);
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
                Logger.Log("Authorization cleared due to missing session key", LogLevel.Info);
            }
        }

        private bool _lastKnownLockState = true;
        private DateTime _lastLockCheckTime = DateTime.MinValue;
        private static readonly TimeSpan LockCheckCooldown = TimeSpan.FromSeconds(5);

        private async Task<bool> IsVaultLocked()
        {
            if (DateTime.Now - _lastLockCheckTime < LockCheckCooldown)
            {
                return _lastKnownLockState;
            }

            try
            {
                if (string.IsNullOrEmpty(_settings.SessionKey))
                {
                    _lastKnownLockState = true;
                    _lastLockCheckTime = DateTime.Now;
                    return true;
                }

                var response = await _httpClient.GetAsync($"{ApiBaseUrl}/status");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var status = JsonConvert.DeserializeObject<JObject>(content);
                    
                    if (status?["data"]?["template"]?["status"] is JToken statusToken)
                    {
                        var vaultStatus = statusToken.ToString();
                        _lastKnownLockState = vaultStatus.ToLower() != "unlocked";
                        _lastLockCheckTime = DateTime.Now;
                        return _lastKnownLockState;
                    }
                }
                
                _lastKnownLockState = true;
                _lastLockCheckTime = DateTime.Now;
                return true;
            }
            catch (Exception)
            {
                _lastKnownLockState = true;
                _lastLockCheckTime = DateTime.Now;
                return true;
            }
        }

        public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
        {
            Logger.Log($"QueryAsync called with ActionKeyword: {query.ActionKeyword}, Search: {Logger.SanitizeQuery(query.Search)}", LogLevel.Debug);

            if (!IsBitwardenCliInstalled())
            {
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Bitwarden CLI not installed",
                        SubTitle = "Click here to learn how to install the Bitwarden CLI",
                        IcoPath = "Images/bitwarden.png",
                        Action = _ => 
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "https://bitwarden.com/help/cli/#download-and-install",
                                UseShellExecute = true
                            });
                            return true;
                        }
                    }
                };
            }

            if (_needsInitialSetup || !_isInitialized)
            {
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Bitwarden plugin is initializing...",
                        SubTitle = "Please wait a moment and try again. If this persists, check your settings.",
                        IcoPath = "Images/bitwarden.png",
                        Action = _ => 
                        {
                            _context.API.OpenSettingDialog();
                            return true;
                        }
                    }
                };
            }

            if (query.ActionKeyword.ToLower() == "bw")
            {
                Logger.Log("Executing HandleBitwardenSearch", LogLevel.Debug);
                ResetAutoLockTimer();
                return await HandleBitwardenSearch(query, token);
            }

            Logger.Log($"No matching action keyword found for: {query.ActionKeyword}", LogLevel.Warning);
            return new List<Result>();
        }

        private async Task<List<Result>> HandleBitwardenSearch(Query query, CancellationToken token)
        {
            Logger.Log($"HandleBitwardenSearch called with query: {Logger.SanitizeQuery(query.Search)}", LogLevel.Debug);

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
                                    if (unlockResults)
                                    {
                                        _context.API.ShowMsg("Vault Unlocked", "Your Bitwarden vault has been successfully unlocked.");
                                    }
                                    else
                                    {
                                        _context.API.ShowMsg("Unlock Failed", "Failed to unlock the vault. Please check your master password and try again.");
                                    }
                                    _context.API.ChangeQuery(""); // Clear the query after unlocking
                                });
                                return true;
                            }
                        }
                    };
                }
            }

            bool isLocked = await IsVaultLocked();
            if (isLocked)
            {
                Logger.Log("Vault is considered locked", LogLevel.Warning);
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

            // Ensure the server is running before performing a search
            await EnsureServerRunning();

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

            // New functionality: Copy search, clear, and paste after unlock
            if (_isFirstSearchAfterUnlock)
            {
                _isFirstSearchAfterUnlock = false;
                string originalSearch = query.Search;

                // Copy the search to clipboard
                CopyToClipboard(originalSearch, "Search");

                // Clear the search string
                _context.API.ChangeQuery(_context.CurrentPluginMetadata.ActionKeyword + " ");

                // Paste the previous search
                await Task.Delay(100); // Short delay to ensure the query is cleared
                _context.API.ChangeQuery(_context.CurrentPluginMetadata.ActionKeyword + " " + originalSearch);

                // Allow the pasted query to be processed
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Processing your search...",
                        SubTitle = "Please wait a moment",
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

                items = await SearchBitwardenAsync(query.Search, token);
                
                var results = new List<Result>();
                _currentResults.Clear();

                for (int index = 0; index < items.Count; index++)
                {
                    var item = items[index];
                    var resultId = item.id;
                    Logger.Log($"Creating result for item: {item.name} with ID: {resultId} at index: {index}", LogLevel.Debug);
                    var result = new Result
                    {
                        Title = item.name, // Removed the ID from the title
                        SubTitle = BuildSubTitle(item),
                        IcoPath = "Images/bitwarden.png",
                        ActionKeywordAssigned = resultId,
                        Score = items.Count - index,
                        Action = context => 
                        {
                            _selectedItemId = resultId;
                            Logger.Log($"Action triggered for item: {item.name} with ID: {resultId}", LogLevel.Debug);
                            return HandleItemActionWrapper(context, resultId, ActionType.Default);
                        }
                    };

                    _currentResults[resultId] = (result, item);
                    results.Add(result);

                    // Start favicon download asynchronously
                    _ = Task.Run(() => UpdateFaviconAsync(item, result, resultId));
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

        private bool HandleItemActionWrapper(ActionContext context, string resultId, ActionType actionType)
        {
            Logger.Log($"HandleItemActionWrapper called with resultId: {resultId}", LogLevel.Debug);
            Logger.Log($"Selected item ID: {_selectedItemId}", LogLevel.Debug);
            Logger.Log($"Context SpecialKeyState: Ctrl={context.SpecialKeyState.CtrlPressed}, Shift={context.SpecialKeyState.ShiftPressed}, Alt={context.SpecialKeyState.AltPressed}, Win={context.SpecialKeyState.WinPressed}", LogLevel.Debug);
            
            if (_currentResults.TryGetValue(_selectedItemId, out var resultTuple))
            {
                var (result, item) = resultTuple;
                Logger.Log($"HandleItemActionWrapper processing selected item: {item.name} with ID: {item.id}", LogLevel.Debug);
                
                bool isTKeyPressed = false;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    isTKeyPressed = Keyboard.IsKeyDown(Key.T);
                });
                Logger.Log($"T key pressed: {isTKeyPressed}", LogLevel.Debug);

                var extendedContext = new ExtendedActionContext(context, isTKeyPressed);

                Task.Run(async () =>
                {
                    bool success = await HandleItemAction(extendedContext, item, actionType);
                    if (!success)
                    {
                        _context.API.ShowMsg("Action Failed", $"Failed to perform {actionType} action for {item.name}.");
                    }
                });
                return true;
            }
            else
            {
                Logger.Log($"Failed to find selected item with ID: {_selectedItemId}", LogLevel.Error);
                return false;
            }
        }

        private async Task EnsureServerRunning()
        {
            if (_serveProcess == null || _serveProcess.HasExited)
            {
                await StartBitwardenServer();
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

        private async Task<bool> UnlockVault(string masterPassword)
        {
            Logger.Log("Starting UnlockVault method", LogLevel.Debug);

            // Try unlocking with different quoting methods
            if (await TryUnlock(masterPassword, QuoteMethod.None) ||
                await TryUnlock(masterPassword, QuoteMethod.Single) ||
                await TryUnlock(masterPassword, QuoteMethod.Double))
            {
                UpdateHttpClientAuthorization();
                await StartBitwardenServer(); // Start the server immediately after unlocking
                
                // Wait a bit for the server to fully initialize
                await Task.Delay(2000);
                
                // Check if the vault is actually unlocked
                if (!await IsVaultLocked())
                {
                    Logger.Log("Vault successfully unlocked", LogLevel.Info);
                    _isFirstSearchAfterUnlock = true;
                    return true;
                }
                else
                {
                    Logger.Log("Unlock process completed, but vault is still reported as locked", LogLevel.Warning);
                    return false;
                }
            }

            Logger.Log("Failed to unlock the vault with all quoting methods", LogLevel.Error);
            return false;
        }

        private enum QuoteMethod
        {
            None,
            Single,
            Double
        }

        private async Task<bool> TryUnlock(string masterPassword, QuoteMethod quoteMethod)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

                string arguments = quoteMethod switch
                {
                    QuoteMethod.None => $"unlock {masterPassword} --raw",
                    QuoteMethod.Single => $"unlock '{masterPassword}' --raw",
                    QuoteMethod.Double => $"unlock \"{masterPassword}\" --raw",
                    _ => throw new ArgumentOutOfRangeException(nameof(quoteMethod))
                };

                var unlockProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "bw",
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                Logger.Log($"Starting Bitwarden CLI unlock process with {quoteMethod} quoting", LogLevel.Debug);
                unlockProcess.Start();

                string unlockOutput = await unlockProcess.StandardOutput.ReadToEndAsync();
                string unlockError = await unlockProcess.StandardError.ReadToEndAsync();

                await unlockProcess.WaitForExitAsync(cts.Token);

                // Log completion without revealing sensitive information
                Logger.Log($"Unlock process completed for {quoteMethod} quoting", LogLevel.Debug);

                if (!string.IsNullOrEmpty(unlockError))
                {
                    Logger.Log($"Error during unlock with {quoteMethod} quoting: {SanitizeErrorMessage(unlockError, masterPassword)}", LogLevel.Error);
                    return false;
                }

                if (!string.IsNullOrEmpty(unlockOutput))
                {
                    _settings.SessionKey = unlockOutput.Trim();
                    _context.API.SaveSettingJsonStorage<BitwardenFlowSettings>();
                    Logger.Log("Session key extracted and saved", LogLevel.Info);
                    _isLocked = false;
                    return true;
                }
                else
                {
                    Logger.Log($"Failed to extract session key with {quoteMethod} quoting", LogLevel.Error);
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Log($"Unlock operation timed out with {quoteMethod} quoting", LogLevel.Error);
                return false;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Exception during unlock process with {quoteMethod} quoting", ex);
                return false;
            }
        }

        private string SanitizeErrorMessage(string errorMessage, string sensitiveData)
        {
            // Remove any potential password information from the error message
            return errorMessage.Replace(sensitiveData, "[REDACTED]");
        }

        private string SanitizeExceptionMessage(Exception ex, string sensitiveData)
        {
            // Remove any potential password information from the exception message
            return ex.ToString().Replace(sensitiveData, "[REDACTED]");
        }
        
        private string ExtractSessionKey(string output)
        {
            var match = System.Text.RegularExpressions.Regex.Match(output, @"BW_SESSION=""(.+?)""");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private enum ActionType
        {
            Default,
            CopyTotp
        }

        private async Task<bool> HandleItemAction(ExtendedActionContext context, BitwardenItem item, ActionType actionType)
        {
            Logger.Log($"HandleItemAction called with actionType: {actionType} for item: {item.name}", LogLevel.Debug);
            Logger.Log($"Item details - Name: {item.name}, ID: {item.id}, HasTotp: {item.hasTotp}", LogLevel.Debug);
            
            try
            {
                if (context.SpecialKeyState.CtrlPressed)
                {
                    if (context.SpecialKeyState.ShiftPressed)
                    {
                        Logger.Log($"Showing URI list for item: {item.name}", LogLevel.Debug);
                        return ShowUriListPopup(item);
                    }
                    else if (context.IsTKeyPressed)
                    {
                        Logger.Log($"TOTP copy triggered for item: {item.name}", LogLevel.Debug);
                        return await CopyTotpCode(item);
                    }
                    else
                    {
                        Logger.Log($"Copying username for item: {item.name}", LogLevel.Debug);
                        CopyToClipboard(item.login?.username, "Username");
                        return true;
                    }
                }
                else
                {
                    Logger.Log($"Copying password for item: {item.name}", LogLevel.Debug);
                    CopyToClipboard(item.login?.password, "Password");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in HandleItemAction for {actionType} on item {item.name}", ex);
                _context.API.ShowMsg("Error", $"An error occurred while performing the action for {item.name}. Check logs for details.");
                return false;
            }
        }

        private async Task UpdateFaviconAsync(BitwardenItem item, Result result, string resultId)
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
                        // Update the existing entry in _currentResults
                        if (_currentResults.TryGetValue(resultId, out var existingTuple))
                        {
                            _currentResults[resultId] = (result, existingTuple.Item);
                        }
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
                parts.Add("TOTP Available (Ctrl+T+Enter)");
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
                cts.CancelAfter(TimeSpan.FromSeconds(10)); // Increase timeout to 10 seconds

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
                try
                {
                    System.Windows.Clipboard.SetText(content);
                    Logger.Log($"Copied {itemType} to clipboard", LogLevel.Debug);
                    
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
                    }

                    if (shouldNotify)
                    {
                        _context.API.ShowMsg($"{itemType} Copied", $"{itemType} has been copied to clipboard", string.Empty);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to copy {itemType} to clipboard", ex);
                }
            });
        }

        public Control CreateSettingPanel()
        {
            return new BitwardenFlowSettingPanel(_settings, updatedSettings =>
            {
                _settings = updatedSettings;
                _context.API.SaveSettingJsonStorage<BitwardenFlowSettings>();
                Task.Run(async () =>
                {
                    await VerifyAndApplySettings();
                    return "Settings verified and applied successfully.";
                }).ContinueWith(task =>
                {
                    string message = task.Exception != null 
                        ? "Error verifying settings. Please check logs." 
                        : task.Result;
                    
                    if (task.Exception != null)
                    {
                        Logger.LogError("Error during settings verification", task.Exception);
                    }

                    // Use Dispatcher to update UI on the correct thread
                    Application.Current.Dispatcher.Invoke(() => 
                    {
                        if (Application.Current.MainWindow.Content is BitwardenFlowSettingPanel panel)
                        {
                            panel.SetVerificationStatus(message);
                        }
                    });
                }, TaskScheduler.FromCurrentSynchronizationContext());
            });
        }

        private void SetVerificationStatus(BitwardenFlowSettingPanel? panel, string status)
        {
            if (panel != null)
            {
                panel.SetVerificationStatus(status);
            }
            else
            {
                Logger.Log($"Unable to set verification status: {status}", LogLevel.Warning);
            }
        }

        private void UpdateSettings(BitwardenFlowSettings newSettings)
        {
            _settings = newSettings;
            _context.API.SaveSettingJsonStorage<BitwardenFlowSettings>();
            Logger.Log("Settings updated", LogLevel.Info);
            SetupAutoLockTimer();

            // Attempt to initialize and log in immediately after settings are updated
            Task.Run(async () => 
            {
                await VerifyAndApplySettings();
                await EnsureBitwardenSetup();
                _isInitialized = true;
                _needsInitialSetup = false;
                Logger.Log("Plugin reinitialized after settings update", LogLevel.Info);
            }).ContinueWith(task => 
            {
                if (task.IsFaulted)
                {
                    if (task.Exception != null)
                    {
                        Logger.LogError("Failed to reinitialize plugin after settings update", task.Exception);
                    }
                    else
                    {
                        Logger.Log("Failed to reinitialize plugin after settings update, but no exception was thrown", LogLevel.Error);
                    }
                    _isInitialized = false;
                    _needsInitialSetup = true;
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private async Task AttemptLogin()
        {
            try
            {
                Logger.Log("Attempting to log in after settings update", LogLevel.Info);
                await VerifyAndApplySettings();
                await EnsureBitwardenSetup();
                _isInitialized = true;
                _needsInitialSetup = false;
                Logger.Log("Login attempt after settings update completed", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Logger.LogError("Error during login attempt after settings update", ex);
                _isInitialized = false;
                _needsInitialSetup = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _httpClient.Dispose();
        
                // Dispose of the Bitwarden server process
                if (_serveProcess != null)
                {
                    try
                    {
                        if (!_serveProcess.HasExited)
                        {
                            _serveProcess.Kill();
                            _serveProcess.WaitForExit(5000); // Wait up to 5 seconds for the process to exit
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("Error while killing Bitwarden server process", ex);
                    }
                    finally
                    {
                        _serveProcess.Dispose();
                        _serveProcess = null;
                    }
                }

                _initializationLock.Dispose();
                _autoLockTimer?.Dispose();
                if (_clientSecret != null)
                {
                    _clientSecret.Dispose();
                }
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

        private async Task<bool> CopyTotpCode(BitwardenItem item)
        {
            Logger.Log($"Starting TOTP copy process for item: {item.name} with ID: {item.id}", LogLevel.Debug);
            try
            {
                var totpCode = await GetTotpCodeAsync(item.id);
                Logger.Log($"TOTP code retrieved for item: {item.name}. Code exists: {!string.IsNullOrEmpty(totpCode)}", LogLevel.Debug);
                if (!string.IsNullOrEmpty(totpCode))
                {
                    CopyToClipboard(totpCode, "TOTP Code");
                    Logger.Log($"TOTP code copied for item: {item.name}", LogLevel.Debug);
                    
                    // Only show notification if the setting is enabled
                    if (_settings.NotifyOnTotpCopy)
                    {
                        _context.API.ShowMsg("TOTP Copied", $"TOTP code for {item.name} has been copied to clipboard.");
                    }
                    return true;
                }
                else
                {
                    Logger.Log($"No TOTP code found for item: {item.name}", LogLevel.Warning);
                    _context.API.ShowMsg("No TOTP Code", $"Item {item.name} does not have a TOTP code associated with it.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error fetching TOTP code for item: {item.name}", ex);
                _context.API.ShowMsg("Error", $"Failed to fetch TOTP code for {item.name}. Check logs for details.");
                return false;
            }
        }

        private async Task<string> GetTotpCodeAsync(string itemId)
        {
            var url = $"{ApiBaseUrl}/object/totp/{itemId}";
            Logger.Log($"Fetching TOTP code for item {itemId}", LogLevel.Debug);
            
            var response = await _httpClient.GetAsync(url);
            
            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var jObject = JObject.Parse(responseContent);
                var totpCode = jObject["data"]?["data"]?.ToString() ?? string.Empty;
                Logger.Log($"TOTP code fetched successfully for item {itemId}", LogLevel.Debug);
                return totpCode;
            }
            
            Logger.Log($"Failed to fetch TOTP code for item {itemId}. Status code: {response.StatusCode}", LogLevel.Error);
            return string.Empty;
        }

        ~Main()
        {
            Dispose(false);
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
