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

        private bool _hasShownSetupMessage = false;

        private async Task VerifyAndApplySettings()
        {
            bool settingsChanged = false;

            if (string.IsNullOrEmpty(_settings.ClientId))
            {
                _needsInitialSetup = true;
                Logger.Log("Client ID is not set", LogLevel.Warning);
            }

            var clientSecret = SecureCredentialManager.RetrieveCredential(_settings.ClientId);
            if (clientSecret == null || clientSecret.Length == 0)
            {
                _needsInitialSetup = true;
                Logger.Log("Client Secret is not set", LogLevel.Warning);
            }

            if (_needsInitialSetup && !_hasShownSetupMessage)
            {
                _context.API.ShowMsg("Bitwarden Setup Required", "Please set up your Client ID and Client Secret in the plugin settings.");
                _hasShownSetupMessage = true;
            }
            else if (!_needsInitialSetup)
            {
                _clientSecret = clientSecret;
                settingsChanged = true;

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
                        return; // Exit the method to prevent further initialization
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
            }

            if (settingsChanged)
            {
                _context.API.SaveSettingJsonStorage<BitwardenFlowSettings>();
                Logger.Log("Settings verified and applied", LogLevel.Info);
            }
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
                _settings.SessionKey = string.Empty;
                _context.API.SaveSettingJsonStorage<BitwardenFlowSettings>();
                UpdateHttpClientAuthorization();
                _isLocked = true;
                Logger.Log("Vault auto-locked due to inactivity", LogLevel.Info);
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
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = "bw",
                    Arguments = "login --apikey",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                // Set environment variables for API key
                startInfo.EnvironmentVariables["BW_CLIENTID"] = clientId;
                startInfo.EnvironmentVariables["BW_CLIENTSECRET"] = new System.Net.NetworkCredential(string.Empty, clientSecret).Password;

                using var loginProcess = new Process { StartInfo = startInfo };

                Logger.Log("Starting Bitwarden CLI login process", LogLevel.Debug);
                loginProcess.Start();

                string output = await loginProcess.StandardOutput.ReadToEndAsync();
                string error = await loginProcess.StandardError.ReadToEndAsync();

                await loginProcess.WaitForExitAsync(cts.Token);

                Logger.Log($"Login process output: {output}", LogLevel.Debug);
                Logger.Log($"Login process error: {error}", LogLevel.Debug);

                if (!string.IsNullOrEmpty(error))
                {
                    if (error.Contains("TypeError: Cannot read properties of null (reading 'profile')"))
                    {
                        Logger.Log("Bitwarden CLI state error detected. Please reinstall the Bitwarden CLI.", LogLevel.Error);
                        _context.API.ShowMsg("Bitwarden CLI Error", "Please reinstall the Bitwarden CLI and try again.");
                        return false;
                    }
                    Logger.Log($"Error during login: {error}", LogLevel.Error);
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
                return;
            }

            if (string.IsNullOrEmpty(_settings.ClientId) || _clientSecret == null || _clientSecret.Length == 0)
            {
                Logger.Log("Client ID or Client Secret not set. Initial setup required.", LogLevel.Info);
                _needsInitialSetup = true;
                return;
            }

            try
            {
                Logger.Log("Starting Bitwarden setup");
                
                var loginSuccess = await LoginWithApiKey();
                if (!loginSuccess)
                {
                    Logger.Log("Failed to log in", LogLevel.Error);
                    return;
                }
                
                _isInitialized = true;
                _isLocked = true; // The vault is locked by default after login
                Logger.Log("Bitwarden setup completed successfully. Vault is locked.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Logger.LogError("Error during Bitwarden setup", ex);
                _isInitialized = false;
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

                if (_serveProcess.HasExited)
                {
                    var exitCode = _serveProcess.ExitCode;
                    Logger.Log($"Bitwarden server process exited unexpectedly with code: {exitCode}", LogLevel.Error);
                    throw new Exception($"Bitwarden server process exited unexpectedly with code: {exitCode}");
                }

                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var response = await client.GetAsync($"{ApiBaseUrl}/status");
                response.EnsureSuccessStatusCode();
                Logger.Log("Bitwarden server started successfully", LogLevel.Info);
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

        private async Task<bool> IsVaultLocked()
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "bw",
                        Arguments = $"unlock --check --session {_settings.SessionKey}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (!string.IsNullOrEmpty(error))
                {
                    Logger.Log($"Error checking vault status: {error}", LogLevel.Error);
                    return true; // Assume locked if there's an error
                }

                return !output.Contains("Vault is unlocked!");
            }
            catch (Exception ex)
            {
                Logger.LogError("Exception while checking vault status", ex);
                return true; // Assume locked if there's an exception
            }
        }

        public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
        {
            Logger.Log($"QueryAsync called with ActionKeyword: {query.ActionKeyword}, Search: {query.Search}", LogLevel.Debug);

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
            Logger.Log($"HandleBitwardenSearch called with query: {query.Search}", LogLevel.Debug);

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

        private async Task<bool> UnlockVault(string masterPassword)
        {
            Logger.Log("Starting UnlockVault method", LogLevel.Debug);

            // Try unlocking with different quoting methods
            if (await TryUnlock(masterPassword, QuoteMethod.None) ||
                await TryUnlock(masterPassword, QuoteMethod.Single) ||
                await TryUnlock(masterPassword, QuoteMethod.Double))
            {
                return true;
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

                Logger.Log($"Unlock process output: {unlockOutput}", LogLevel.Debug);
                Logger.Log($"Unlock process error: {unlockError}", LogLevel.Debug);

                if (!string.IsNullOrEmpty(unlockError))
                {
                    Logger.Log($"Error during unlock with {quoteMethod} quoting: {unlockError}", LogLevel.Error);
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
            Logger.Log("Settings updated");
            SetupAutoLockTimer();
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