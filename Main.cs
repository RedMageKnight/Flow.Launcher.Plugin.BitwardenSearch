using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Linq;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using System.Security;
using System.Net.Sockets;
using System.Windows.Input;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public class Main : IAsyncPlugin, ISettingProvider, IDisposable, IContextMenu
    {
        private readonly HttpClient _httpClient;
        private const string ApiBaseUrl = "http://localhost:8087"; // Bitwarden CLI server URL
        private Process? _serveProcess;
        private bool _isInitialized = false;
        private readonly SemaphoreSlim _initializationLock = new SemaphoreSlim(1, 1);
        private string? _faviconCacheDir;
        private Dictionary<string, (Result Result, BitwardenItem Item)> _currentResults = new Dictionary<string, (Result, BitwardenItem)>();
        private const int DebounceDelay = 300; // milliseconds
        private CancellationTokenSource _debounceTokenSource;
        private PluginInitContext _context = null!;
        private BitwardenFlowSettings _settings = null!;
        private bool _isLocked = false;
        private bool _needsInitialSetup = false;
        private SecureString? _clientSecret;
        private string _selectedItemId = string.Empty;
        private IconCacheManager? _iconCacheManager;
        private DispatcherTimer? _clipboardClearTimer;
        private Timer? _autoLockTimer;
        private DateTime _lastActivityTime;
        private bool _lastKnownLockState = true;
        private DateTime _lastLockCheckTime = DateTime.MinValue;
        private static readonly TimeSpan LockCheckCooldown = TimeSpan.FromSeconds(5);
        private SemaphoreSlim _iconCacheThrottler = new SemaphoreSlim(5);
        private VaultItemCache? _vaultItemCache;

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
                    _iconCacheManager = new IconCacheManager(_faviconCacheDir, _httpClient);
                    Logger.Log($"Favicon cache directory created: {_faviconCacheDir}", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to create favicon cache directory: {ex.Message}", LogLevel.Error);
                    _faviconCacheDir = null;
                }
                var vaultCacheDir = Path.Combine(assemblyDirectory, "VaultItemCache");
                try
                {
                    Directory.CreateDirectory(vaultCacheDir);
                    _vaultItemCache = new VaultItemCache(vaultCacheDir);
                    Logger.Log($"Vault item cache directory created: {vaultCacheDir}", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to create vault item cache directory: {ex.Message}", LogLevel.Error);
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
                    SetupAutoLockTimer();
                }
                catch (Exception ex)
                {
                    Logger.LogError("Error during background initialization", ex);
                }
            });
            SetupAutoLockTimer();
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
        
        private static bool IsBitwardenCliAccessible()
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

                return !string.IsNullOrEmpty(output) && string.IsNullOrEmpty(error);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsBitwardenCliInstalled()
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

        private static bool IsBitwardenCliRunning()
        {
            var processes = Process.GetProcessesByName("bw");
            return processes.Length > 0;
        }
        
        private async Task EnsureServerRunning()
        {
            if (_serveProcess == null || _serveProcess.HasExited)
            {
                await StartBitwardenServer();
            }
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

            if (!string.IsNullOrEmpty(_settings.BwExecutablePath))
            {
                var directoryPath = Path.GetDirectoryName(_settings.BwExecutablePath);
                if (!string.IsNullOrEmpty(directoryPath))
                {
                    var currentPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? string.Empty;
                    Environment.SetEnvironmentVariable("PATH", currentPath + Path.PathSeparator + directoryPath, EnvironmentVariableTarget.Process);
                }
            }

            if (!IsBitwardenCliAccessible())
            {
                Logger.Log("Bitwarden CLI is not accessible. Please check the PATH or the executable location in settings.", LogLevel.Warning);
                _needsInitialSetup = true;
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
                _autoLockTimer = null;
            }

            if (_settings.AutoLockDuration > 0)
            {
                _lastActivityTime = DateTime.Now;
                _autoLockTimer = new Timer(CheckAutoLock, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
                Logger.Log($"Auto-lock timer set for {_settings.AutoLockDuration} seconds", LogLevel.Debug);
            }
            else
            {
                Logger.Log("Auto-lock timer disabled", LogLevel.Debug);
            }
        }

        private void CheckAutoLock(object? state)
        {
            if (!_isLocked && _settings.AutoLockDuration > 0)
            {
                var elapsedTime = (DateTime.Now - _lastActivityTime).TotalSeconds;
                if (elapsedTime >= _settings.AutoLockDuration)
                {
                    AutoLockVault();
                }
            }
        }

        private void AutoLockVault()
        {
            if (!_isLocked)
            {
                _settings.SessionKey = string.Empty;
                ClearEnvironmentSessionKey();
                _context.API.SaveSettingJsonStorage<BitwardenFlowSettings>();
                UpdateHttpClientAuthorization();
                _isLocked = true;
                Logger.Log("Vault auto-locked due to inactivity", LogLevel.Info);
                
                if (_settings.NotifyOnAutoLock)
                {
                    _context.API.ShowMsg("Vault Locked", "Your vault has been automatically locked due to inactivity");
                }
            }
        }

        private void ResetAutoLockTimer()
        {
            _lastActivityTime = DateTime.Now;
            Logger.Log("Auto-lock timer reset", LogLevel.Debug);
        }
        
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
                        Logger.Log($"Vault status check: {vaultStatus}", LogLevel.Debug);
                        return _lastKnownLockState;
                    }
                }
                
                Logger.Log("Failed to get a valid status response", LogLevel.Warning);
                _lastKnownLockState = true;
                _lastLockCheckTime = DateTime.Now;
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("Error checking vault lock status", ex);
                _lastKnownLockState = true;
                _lastLockCheckTime = DateTime.Now;
                return true;
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

            // Try unlocking with different quoting methods, prioritizing single quotes
            if (await TryUnlock(masterPassword, QuoteMethod.Single) ||
                await TryUnlock(masterPassword, QuoteMethod.Double) ||
                await TryUnlock(masterPassword, QuoteMethod.None))
            {
                UpdateHttpClientAuthorization();
                SetEnvironmentSessionKey(_settings.SessionKey);
                await StartBitwardenServer();
                
                await Task.Delay(5000);
                
                for (int i = 0; i < 5; i++)
                {
                    if (!await IsVaultLocked())
                    {
                        Logger.Log("Vault successfully unlocked", LogLevel.Info);
                        SetupAutoLockTimer();
                        ResetAutoLockTimer();
                        await SyncVaultAndIcons();
                        return true;
                    }
                    Logger.Log($"Vault still reported as locked. Attempt {i + 1} of 5", LogLevel.Warning);
                    await Task.Delay(1000);
                }

                Logger.Log("Unlock process completed, but vault is still reported as locked after multiple checks", LogLevel.Error);
                return false;
            }

            Logger.Log("Failed to unlock the vault with all quoting methods", LogLevel.Error);
            return false;
        }

        private void SetEnvironmentSessionKey(string sessionKey)
        {
            if (!string.IsNullOrEmpty(sessionKey))
            {
                Environment.SetEnvironmentVariable("BW_SESSION", sessionKey, EnvironmentVariableTarget.Process);
                Logger.Log("BW_SESSION environment variable set", LogLevel.Debug);
            }
        }

        private void ClearEnvironmentSessionKey()
        {
            Environment.SetEnvironmentVariable("BW_SESSION", null, EnvironmentVariableTarget.Process);
            Logger.Log("BW_SESSION environment variable cleared", LogLevel.Debug);
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

        private static async Task<bool> VerifyApiKey()
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

        private static async Task<bool> IsLoggedIn()
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

            // Verify we have a session key before starting the server
            if (string.IsNullOrEmpty(_settings.SessionKey))
            {
                Logger.Log("No valid session key available. Cannot start server.", LogLevel.Error);
                throw new Exception("No valid session key available");
            }

            Logger.Log("Starting Bitwarden server", LogLevel.Info);
            _serveProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "bw",
                    Arguments = $"serve --session \"{_settings.SessionKey}\"", // Added quotes around session key
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            _serveProcess.OutputDataReceived += (sender, e) => 
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Logger.Log($"Bitwarden server output: {e.Data}", LogLevel.Debug);
            };
            _serveProcess.ErrorDataReceived += (sender, e) => 
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Logger.Log($"Bitwarden server error: {e.Data}", LogLevel.Error);
            };

            try
            {
                Logger.Log("Starting Bitwarden server process", LogLevel.Debug);
                _serveProcess.Start();
                _serveProcess.BeginOutputReadLine();
                _serveProcess.BeginErrorReadLine();

                // Give the server a moment to start
                await Task.Delay(2000);

                // Check if the server is responsive
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        using var testClient = new HttpClient();
                        var response = await testClient.GetAsync($"{ApiBaseUrl}/status", cts.Token);
                        if (response.IsSuccessStatusCode)
                        {
                            Logger.Log("Bitwarden server started successfully", LogLevel.Info);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Attempt {i + 1}/5: Error checking server status: {ex.Message}", LogLevel.Warning);
                    }

                    if (i < 4) // Don't delay on the last attempt
                        await Task.Delay(1000, cts.Token);
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

        private static bool IsPortInUse(int port)
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

        private static void KillProcessUsingPort(int port)
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

            bool isLocked = await IsVaultLocked();

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

            if (isLocked)
            {
                Logger.Log("Vault is considered locked", LogLevel.Warning);
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Bitwarden vault is locked",
                        SubTitle = "Click here or press Enter to enter your password",
                        IcoPath = "Images/bitwarden.png",
                        Action = _ => 
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                var passwordDialog = new PasswordInputDialog();
                                
                                if (passwordDialog.ShowDialog() == true)
                                {
                                    string masterPassword = passwordDialog.Password;
                                    Task.Run(async () =>
                                    {
                                        var unlockResults = await UnlockVault(masterPassword);
                                        if (unlockResults)
                                        {
                                            _context.API.ShowMsg("Vault Unlocked", "Your Bitwarden vault has been successfully unlocked.");
                                        }
                                        else
                                        {
                                            _context.API.ShowMsg("Unlock Failed", "Failed to unlock the vault. Please check your master password and try again.");
                                        }
                                        _context.API.ChangeQuery(""); // Clear the query after unlocking attempt
                                    });
                                }
                                else
                                {
                                    _context.API.ChangeQuery(""); // Clear the query if canceled
                                }
                            });
                            return true;
                        }
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
                        SubTitle = "Type to search your vault or right-click for more options",
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
                _currentResults.Clear();

                for (int index = 0; index < items.Count; index++)
                {
                    var item = items[index];
                    var resultId = item.id;
                    Logger.Log($"Creating result for item: {item.name} with ID: {resultId} at index: {index}", LogLevel.Debug);
                    Logger.Log($"Creating result for {item.name} - HasTotp: {item.hasTotp}, URIs count: {item.login?.uris?.Count ?? 0}", LogLevel.Debug);
                    var result = new Result
                    {
                        Title = item.name,
                        SubTitle = BuildSubTitle(item),
                        IcoPath = "Images/bitwarden.png",
                        ActionKeywordAssigned = resultId,
                        ContextData = item,
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

                    // Use cached favicon
                    UpdateFaviconAsync(item, result, resultId);
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

        private async Task SyncVaultAndIcons()
        {
            try
            {
                if (_settings.NotifyOnSyncStart)
                {
                    _context.API.ShowMsg("Sync Started", "Beginning full sync of vault and icons. This may take a while.");
                }

                // Clear the vault cache before syncing
                _vaultItemCache?.ClearCache();

                // Step 1: Sync the vault
                Logger.Log("Starting vault synchronization", LogLevel.Info);
                var syncResponse = await _httpClient.PostAsync($"{ApiBaseUrl}/sync", null);
                
                if (!syncResponse.IsSuccessStatusCode)
                {
                    Logger.Log($"Failed to synchronize vault. Status code: {syncResponse.StatusCode}", LogLevel.Error);
                    _context.API.ShowMsg("Sync Failed", "Failed to synchronize your vault. Please try again later.");
                    return;
                }

                Logger.Log("Vault synchronization completed successfully", LogLevel.Info);

                // Step 2: Fetch all items
                var itemsResponse = await _httpClient.GetAsync($"{ApiBaseUrl}/list/object/items");
                
                if (!itemsResponse.IsSuccessStatusCode)
                {
                    Logger.Log($"Failed to retrieve items after sync. Status code: {itemsResponse.StatusCode}", LogLevel.Error);
                    _context.API.ShowMsg("Icon Sync Failed", "Vault synced successfully, but failed to retrieve items for icon caching.");
                    return;
                }

                var responseContent = await itemsResponse.Content.ReadAsStringAsync();
                var jObject = JObject.Parse(responseContent);
                var dataArray = jObject["data"]?["data"] as JArray;

                if (dataArray == null)
                {
                    Logger.Log("No data array found in response for icon syncing", LogLevel.Warning);
                    _context.API.ShowMsg("Icon Sync Failed", "Vault synced successfully, but no items found for icon caching.");
                    return;
                }

                var items = dataArray
                    .Select(item => item.ToObject<BitwardenItem>())
                    .Where(item => item != null)
                    .Cast<BitwardenItem>()
                    .ToList();

                // Step 3: Cache icons
                if (_iconCacheManager != null)
                {
                    var progress = new Progress<int>(percent =>
                    {
                        Logger.Log($"Icon caching progress: {percent}%", LogLevel.Debug);
                    });

                    if (_settings.NotifyOnIconCacheStart)
                    {
                        _context.API.ShowMsg("Sync In Progress", "Caching icons. This may take a few minutes.");
                    }

                    await _iconCacheManager.CacheAllIconsAsync(items, progress);
                    
                    Logger.Log("Icon caching completed successfully", LogLevel.Info);
                    if (_settings.NotifyOnSyncComplete)
                    {
                        _context.API.ShowMsg("Sync Complete", "Your vault has been synchronized and all icons have been cached.");
                    }
                }
                else
                {
                    Logger.Log("IconCacheManager is not initialized", LogLevel.Warning);
                    _context.API.ShowMsg("Icon Sync Incomplete", "Vault synced successfully, but icon caching could not be performed.");
                }

                // Step 4: Update vault cache
                if (_vaultItemCache != null && items.Any())
                {
                    _vaultItemCache.UpdateCache(items);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Error during vault and icon synchronization", ex);
                _context.API.ShowMsg("Sync Error", "An error occurred during synchronization. Please check the logs for details.");
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

        private static string SanitizeErrorMessage(string errorMessage, string sensitiveData)
        {
            // Remove any potential password information from the error message
            return errorMessage.Replace(sensitiveData, "[REDACTED]");
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
                        return await Application.Current.Dispatcher.InvokeAsync(() => ShowUriListPopup(item));
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
                    // For password copying, we need to fetch the full item from the CLI
                    Logger.Log($"Fetching full item for password copy: {item.name}", LogLevel.Debug);
                    var fullItem = await GetFullItemFromCli(item.id);
                    if (fullItem != null && fullItem.login?.password != null)
                    {
                        Logger.Log($"Copying password for item: {item.name}", LogLevel.Debug);
                        CopyToClipboard(fullItem.login.password, "Password");
                        return true;
                    }
                    else
                    {
                        Logger.Log($"Failed to fetch password for item: {item.name}", LogLevel.Error);
                        _context.API.ShowMsg("Error", "Failed to fetch password from vault.");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in HandleItemAction for {actionType} on item {item.name}", ex);
                _context.API.ShowMsg("Error", $"An error occurred while performing the action for {item.name}. Check logs for details.");
                return false;
            }
        }

        private async Task<BitwardenItem?> GetFullItemFromCli(string itemId)
        {
            try
            {
                if (_serveProcess == null || _serveProcess.HasExited)
                {
                    Logger.Log("Bitwarden server is not running. Attempting to start...", LogLevel.Warning);
                    await StartBitwardenServer();
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                var response = await _httpClient.GetAsync($"{ApiBaseUrl}/object/item/{itemId}");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var result = JsonConvert.DeserializeObject<JObject>(content);
                    var itemData = result?["data"]?.ToString();
                    
                    if (!string.IsNullOrEmpty(itemData))
                    {
                        return JsonConvert.DeserializeObject<BitwardenItem>(itemData);
                    }
                }
                
                Logger.Log($"Failed to fetch full item. Status code: {response.StatusCode}", LogLevel.Error);
                return null;
            }
            catch (Exception ex)
            {
                Logger.LogError("Error fetching full item from CLI", ex);
                return null;
            }
        }

        private void UpdateFaviconAsync(BitwardenItem item, Result result, string resultId)
        {
            if (_iconCacheManager != null && item.login?.uris != null && item.login.uris.Any())
            {
                var webUri = item.login.uris
                    .Select(u => u.uri)
                    .FirstOrDefault(u => u.StartsWith("http://") || u.StartsWith("https://"));

                if (!string.IsNullOrEmpty(webUri))
                {
                    result.IcoPath = _iconCacheManager.GetCachedIconPath(webUri);
                    // Update the existing entry in _currentResults
                    if (_currentResults.TryGetValue(resultId, out var existingTuple))
                    {
                        _currentResults[resultId] = (result, existingTuple.Item);
                    }
                }
            }
        }

        private static string BuildSubTitle(BitwardenItem item)
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

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                var sessionKey = Environment.GetEnvironmentVariable("BW_SESSION");
                if (string.IsNullOrEmpty(sessionKey))
                {
                    Logger.Log("No valid session key found", LogLevel.Warning);
                    return new List<BitwardenItem>();
                }

                // First try to search the cache
                if (_vaultItemCache != null && _vaultItemCache.IsCacheValid())
                {
                    var cachedResults = _vaultItemCache.SearchCache(searchTerm);
                    if (cachedResults.Any())
                    {
                        Logger.Log($"Found {cachedResults.Count} results in cache for search term: {searchTerm}", LogLevel.Debug);
                        return cachedResults;
                    }
                }

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "bw",
                        Arguments = $"list items --session {sessionKey} --search \"{searchTerm}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                Logger.Log($"Executing search with command: bw list items --session [REDACTED] --search \"{searchTerm}\"", LogLevel.Debug);
                process.Start();
                
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                
                await process.WaitForExitAsync(cts.Token);

                if (!string.IsNullOrEmpty(error))
                {
                    Logger.Log($"Error during search: {error}", LogLevel.Error);
                    return new List<BitwardenItem>();
                }

                var items = JsonConvert.DeserializeObject<List<BitwardenItem>>(output) ?? new List<BitwardenItem>();
                foreach (var item in items)
                {
                    Logger.Log($"Deserialized item {item.name} - TOTP status: hasTotp={item.hasTotp}, " +
                            $"login null={item.login == null}", LogLevel.Debug);
                }

                // Update cache with new results
                if (_vaultItemCache != null && items.Any())
                {
                    _vaultItemCache.UpdateCache(items);
                }

                return items;
            }
            catch (Exception ex)
            {
                Logger.LogError("Error during search", ex);
                return new List<BitwardenItem>();
            }
        }

        public Control CreateSettingPanel()
        {
            return new BitwardenFlowSettingPanel(_settings, updatedSettings =>
            {
                _settings = updatedSettings;
                _context.API.SaveSettingJsonStorage<BitwardenFlowSettings>();
                SetupAutoLockTimer(); // Add this line to ensure the timer is updated when settings change
                Task.Run(async () =>
                {
                    await VerifyAndApplySettings();
                    return "Settings verified and applied successfully.";
                }).ContinueWith(task =>
                {
                    // ... (existing code)
                }, TaskScheduler.FromCurrentSynchronizationContext());
            });
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
                        case "totp":
                            shouldNotify = _settings.NotifyOnTotpCopy;
                            break;
                    }

                    if (shouldNotify)
                    {
                        _context.API.ShowMsg($"{itemType} Copied", $"{itemType} has been copied to clipboard", string.Empty);
                    }

                    SetupClipboardClearTimer();
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to copy {itemType} to clipboard", ex);
                }
            });
        }
        
        private void SetupClipboardClearTimer()
        {
            if (_clipboardClearTimer != null)
            {
                _clipboardClearTimer.Stop();
                _clipboardClearTimer = null;
            }

            if (_settings.ClipboardClearSeconds > 0)
            {
                _clipboardClearTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(_settings.ClipboardClearSeconds)
                };
                _clipboardClearTimer.Tick += ClearClipboard;
                _clipboardClearTimer.Start();
            }
        }
        
        private void ClearClipboard(object? sender, EventArgs e)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    System.Windows.Clipboard.Clear();
                    Logger.Log("Clipboard cleared", LogLevel.Debug);
                }
                catch (Exception ex)
                {
                    Logger.LogError("Failed to clear clipboard", ex);
                }
            });

            if (_clipboardClearTimer != null)
            {
                _clipboardClearTimer.Stop();
                _clipboardClearTimer = null;
            }
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
                _iconCacheThrottler.Dispose();
                if (_clipboardClearTimer != null)
                {
                    _clipboardClearTimer.Stop();
                    _clipboardClearTimer = null;
                }
                if (_clientSecret != null)
                {
                    _clientSecret.Dispose();
                }
                if (_autoLockTimer != null)
                {
                    _autoLockTimer.Dispose();
                    _autoLockTimer = null;
                }
            }
        }

        private async Task RestartBitwardenServer()
        {
            try
            {
                if (_serveProcess != null)
                {
                    if (!_serveProcess.HasExited)
                    {
                        _serveProcess.Kill();
                        await _serveProcess.WaitForExitAsync();
                    }
                    _serveProcess.Dispose();
                    _serveProcess = null;
                }

                // Clear the session key when restarting server
                _settings.SessionKey = string.Empty;
                _context.API.SaveSettingJsonStorage<BitwardenFlowSettings>();
                _isLocked = true;
                UpdateHttpClientAuthorization();

                // Wait a moment before attempting to start the server again
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                Logger.LogError("Error during Bitwarden server restart", ex);
            }
        }

        public List<Result> LoadContextMenus(Result selectedResult)
        {
            Logger.Log($"LoadContextMenus called for result: {selectedResult.Title}", LogLevel.Debug);

            // Context menu for main Bitwarden entry
            if (selectedResult.Title == "Search Bitwarden" && 
                selectedResult.SubTitle == "Type to search your vault or right-click for more options")
            {
                Logger.Log("Showing main Bitwarden context menu", LogLevel.Debug);
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Lock Vault",
                        SubTitle = "Lock your Bitwarden vault",
                        IcoPath = "Images/bitwarden.png",
                        Action = _ => 
                        {
                            var lockResults = LockVault();
                            _context.API.ChangeQuery(""); // Clear the query after locking
                            return true;
                        }
                    },
                    new Result
                    {
                        Title = "Sync Vault and Icons",
                        SubTitle = "Synchronize your vault and cache icons for all items",
                        IcoPath = "Images/bitwarden.png",
                        Action = _ => 
                        {
                            Task.Run(async () =>
                            {
                                await SyncVaultAndIcons();
                                _context.API.ShowMsg("Sync Complete", "Your vault has been synchronized and all icons have been cached.");
                            });
                            return true;
                        }
                    }
                };
            }

            // Context menu for individual vault items
            if (selectedResult.ContextData is BitwardenItem item)
            {
                Logger.Log($"Found vault item: {item.name}", LogLevel.Debug);
                Logger.Log($"Item details - HasTotp: {item.hasTotp}, Login null: {item.login == null}, URIs count: {item.login?.uris?.Count ?? 0}", LogLevel.Debug);
                
                var contextMenuItems = new List<Result>();

                // Password option
                contextMenuItems.Add(new Result
                {
                    Title = "Copy Password",
                    SubTitle = "Copy the password to clipboard",
                    IcoPath = "Images/bitwarden.png",
                    Action = _ =>
                    {
                        // Start the async operation but don't wait for it
                        Task.Run(async () =>
                        {
                            try
                            {
                                var fullItem = await GetFullItemFromCli(item.id);
                                if (fullItem != null && fullItem.login?.password != null)
                                {
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        CopyToClipboard(fullItem.login.password, "Password");
                                    });
                                }
                                else
                                {
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        _context.API.ShowMsg("Error", "Failed to fetch password from vault.");
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.LogError("Error copying password from context menu", ex);
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    _context.API.ShowMsg("Error", "Failed to copy password. Check logs for details.");
                                });
                            }
                        });

                        return true;
                    }
                });

                // Username option (if available)
                if (!string.IsNullOrEmpty(item.login?.username))
                {
                    contextMenuItems.Add(new Result
                    {
                        Title = "Copy Username",
                        SubTitle = $"Copy the username ({item.login.username}) to clipboard",
                        IcoPath = "Images/bitwarden.png",
                        Action = _ =>
                        {
                            CopyToClipboard(item.login.username, "Username");
                            return true;
                        }
                    });
                }

                // TOTP option (if available)
                Logger.Log($"Checking TOTP availability for {item.name}: {item.hasTotp}", LogLevel.Debug);
                if (item.hasTotp)
                {
                    Logger.Log($"Adding TOTP option for {item.name}", LogLevel.Debug);
                    contextMenuItems.Add(new Result
                    {
                        Title = "Copy TOTP Code",
                        SubTitle = "Copy the current TOTP code to clipboard",
                        IcoPath = "Images/totp.png",
                        Action = _ =>
                        {
                            Task.Run(() => CopyTotpCode(item));
                            return true;
                        }
                    });
                }

                // URIs option (if available)
                if (item.login?.uris != null && item.login.uris.Any())
                {
                    Logger.Log($"Adding URIs option for {item.name} ({item.login.uris.Count} URIs)", LogLevel.Debug);
                    contextMenuItems.Add(new Result
                    {
                        Title = "Show URIs",
                        SubTitle = $"Show and copy available URIs ({item.login.uris.Count} available)",
                        IcoPath = "Images/bitwarden.png",
                        Action = _ =>
                        {
                            Application.Current.Dispatcher.Invoke(() => ShowUriListPopup(item));
                            return true;
                        }
                    });
                }

                Logger.Log($"Returning {contextMenuItems.Count} context menu items for vault item", LogLevel.Debug);
                return contextMenuItems;
            }

            Logger.Log("No context menu items found for this result", LogLevel.Debug);
            return new List<Result>();
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        private enum QuoteMethod
        {
            Single,
            Double,
            None
        }

        private enum ActionType
        {
            Default,
            CopyTotp
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
        
        [JsonProperty("hasTotp")]
        private bool? _rawHasTotp;

        [JsonIgnore]
        public bool hasTotp 
        {
            get
            {
                // Check both the raw property and the login.totp field
                if (_rawHasTotp.HasValue)
                    return _rawHasTotp.Value;
                
                // If login exists and has a non-null totp field, consider it as having TOTP
                if (login?.totp != null)
                    return true;

                return false;
            }
            set => _rawHasTotp = value;
        }
    }

    public class BitwardenLogin
    {
        public string? username { get; set; }
        public string? password { get; set; }
        public List<BitwardenUri>? uris { get; set; }
        public string? totp { get; set; } 
    }

    public class BitwardenUri
    {
        public string uri { get; set; } = string.Empty;
        public int? match { get; set; }
    }
}
