using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Security;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.Win32;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.IO.Compression;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Threading;
using System.Collections.Generic;
using Flow.Launcher.Plugin.BitwardenSearch;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public partial class BitwardenFlowSettingPanel : UserControl
    {
        private BitwardenFlowSettings? _settings;
        private Action<BitwardenFlowSettings>? _updateSettings;
        private bool _isClientSecretModified = false;
        private DispatcherTimer? _resetButtonTimer;
        private const string BitwardenCliApiUrl = "https://api.github.com/repos/bitwarden/clients/releases";

        public BitwardenFlowSettingPanel()
        {
            InitializeComponent();
        }

        public BitwardenFlowSettingPanel(BitwardenFlowSettings settings, Action<BitwardenFlowSettings> updateSettings)
        {
            InitializeComponent();
            _settings = settings;
            _updateSettings = updateSettings;
            
            BwExecutablePathTextBox.Text = _settings.BwExecutablePath;
            UpdatePathStatus();
            
            ClientIdTextBox.Text = _settings.ClientId;
            
            // We'll set the Password property of ClientSecretBox to a placeholder value
            // The actual secret will be retrieved only when needed
            ClientSecretBox.Password = SecureCredentialManager.RetrieveCredential(_settings.ClientId) != null 
                ? "********" // Placeholder for when a secret exists
                : string.Empty;
            
            SaveClientSecretButton.IsEnabled = false;
            // Initialize the timer
            _resetButtonTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _resetButtonTimer.Tick += ResetButtonStyle;
            
            LogTraceCheckBox.IsChecked = _settings.LogTrace;
            LogDebugCheckBox.IsChecked = _settings.LogDebug;
            LogInfoCheckBox.IsChecked = _settings.LogInfo;
            LogWarningCheckBox.IsChecked = _settings.LogWarning;
            LogErrorCheckBox.IsChecked = _settings.LogError;
            NotifySyncStartCheckBox.IsChecked = _settings.NotifyOnSyncStart;
            NotifyIconCacheStartCheckBox.IsChecked = _settings.NotifyOnIconCacheStart;
            NotifySyncCompleteCheckBox.IsChecked = _settings.NotifyOnSyncComplete;
            AutoLockComboBox.SelectedIndex = GetIndexFromDuration(_settings.AutoLockDuration);
            NotifyAutoLockCheckBox.IsChecked = _settings.NotifyOnAutoLock;
            NotifyPasswordCopyCheckBox.IsChecked = _settings.NotifyOnPasswordCopy;
            NotifyUsernameCopyCheckBox.IsChecked = _settings.NotifyOnUsernameCopy;
            NotifyUriCopyCheckBox.IsChecked = _settings.NotifyOnUriCopy;
            NotifyTotpCopyCheckBox.IsChecked = _settings.NotifyOnTotpCopy;

            // Add event handlers for the new functionality
            ClientSecretBox.PasswordChanged += ClientSecretBox_PasswordChanged;
            SaveClientSecretButton.Click += SaveClientSecretButton_Click;

            // Initialize the clipboard clear combo box
            ClipboardClearComboBox.SelectedIndex = GetIndexFromSeconds(_settings.ClipboardClearSeconds);

            InitializeServerConfig();
            InitializeOfficialServerSelection();
        }

        private void AutoLockComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings != null && sender is ComboBox comboBox)
            {
                var selectedItem = comboBox.SelectedItem as ComboBoxItem;
                if (selectedItem != null && int.TryParse(selectedItem.Tag.ToString(), out int duration))
                {
                    var oldDuration = _settings.AutoLockDuration;
                    _settings.AutoLockDuration = duration;
                    _updateSettings?.Invoke(_settings);  // Make sure this line is present
                    Logger.Log($"Auto-lock duration changed from {oldDuration} seconds to {duration} seconds", LogLevel.Debug);
                }
            }
        }

        private static int GetIndexFromDuration(int duration)
        {
            return duration switch
            {
                0 => 0,
                60 => 1,
                300 => 2,
                900 => 3,
                1800 => 4,
                3600 => 5,
                14400 => 6,
                _ => 0
            };
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Executable files (*.exe)|*.exe",
                Title = "Select Bitwarden CLI executable"
            };

            if (openFileDialog.ShowDialog() == true && _settings != null)
            {
                _settings.BwExecutablePath = openFileDialog.FileName;
                BwExecutablePathTextBox.Text = _settings.BwExecutablePath;
                _updateSettings?.Invoke(_settings);
                UpdatePathStatus();
            }
        }

        private void UpdatePathStatus()
        {
            if (_settings == null)
            {
                PathStatusTextBlock.Text = "Settings not initialized.";
                PathStatusTextBlock.Foreground = Brushes.Red;
                return;
            }

            if (string.IsNullOrEmpty(_settings.BwExecutablePath))
            {
                PathStatusTextBlock.Text = "Please select the Bitwarden CLI executable.";
                PathStatusTextBlock.Foreground = Brushes.Gray;
                return;
            }

            if (!File.Exists(_settings.BwExecutablePath))
            {
                PathStatusTextBlock.Text = "The specified file does not exist.";
                PathStatusTextBlock.Foreground = Brushes.Red;
                return;
            }

            var directoryPath = Path.GetDirectoryName(_settings.BwExecutablePath);
            if (string.IsNullOrEmpty(directoryPath))
            {
                PathStatusTextBlock.Text = "Invalid file path.";
                PathStatusTextBlock.Foreground = Brushes.Red;
                return;
            }

            bool isInUserPath = IsPathInEnvironmentVariable(directoryPath, EnvironmentVariableTarget.User);
            bool isInSystemPath = IsPathInEnvironmentVariable(directoryPath, EnvironmentVariableTarget.Machine);

            if (isInUserPath || isInSystemPath)
            {
                PathStatusTextBlock.Text = $"Bitwarden CLI path is correctly set in the {(isInUserPath ? "User" : "System")} PATH environment variable.";
                PathStatusTextBlock.Foreground = Brushes.Green;
                _settings.IsPathEnvironmentValid = true;
            }
            else
            {
                PathStatusTextBlock.Text = "Bitwarden CLI path is not in the PATH environment variable. Click to add to User PATH.";
                PathStatusTextBlock.Foreground = Brushes.Orange;
                PathStatusTextBlock.Cursor = Cursors.Hand;
                PathStatusTextBlock.MouseDown += PathStatusTextBlock_MouseDown;
                _settings.IsPathEnvironmentValid = false;
            }

            _updateSettings?.Invoke(_settings);
        }

        private static bool IsPathInEnvironmentVariable(string path, EnvironmentVariableTarget target)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH", target);
            if (string.IsNullOrEmpty(pathEnv)) return false;

            var paths = pathEnv.Split(Path.PathSeparator);
            return paths.Contains(path, StringComparer.OrdinalIgnoreCase);
        }

        private void PathStatusTextBlock_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_settings != null && !_settings.IsPathEnvironmentValid)
            {
                AddToPath();
            }
        }

        private void AddToPath()
        {
            if (_settings == null) return;

            var directoryPath = Path.GetDirectoryName(_settings.BwExecutablePath);
            if (string.IsNullOrEmpty(directoryPath))
            {
                MessageBox.Show("Invalid file path.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
                var newPath = pathEnv + Path.PathSeparator + directoryPath;
                Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.User);

                MessageBox.Show("Bitwarden CLI path has been added to the User PATH environment variable.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                UpdatePathStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to update PATH environment variable: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task<string> GetLatestCliVersionUrl()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "Flow.Launcher.Plugin.BitwardenSearch");

                    var releases = await GitHubApiHandler.GetWithRateLimit<JArray>(BitwardenCliApiUrl, client);
                    
                    // Find the first release that contains a CLI asset
                    foreach (var release in releases)
                    {
                        var assets = release["assets"] as JArray;
                        if (assets == null) continue;

                        var cliAsset = assets.FirstOrDefault(a => 
                            a["name"]?.ToString().StartsWith("bw-windows-") == true && 
                            a["name"]?.ToString().EndsWith(".zip") == true);

                        if (cliAsset != null)
                        {
                            var downloadUrl = cliAsset["browser_download_url"]?.ToString();
                            if (!string.IsNullOrEmpty(downloadUrl))
                            {
                                var version = release["tag_name"]?.ToString() ?? "unknown";
                                Logger.Log($"Found latest CLI version: {version} at {downloadUrl}", LogLevel.Info);
                                return downloadUrl;
                            }
                        }
                    }

                    throw new Exception("No valid CLI download URL found in the releases");
                }
            }
            catch (HttpRequestException ex)
            {
                Logger.LogError("Error accessing GitHub API", ex);
                throw new Exception("Failed to access GitHub API. Please check your internet connection.", ex);
            }
            catch (JsonException ex)
            {
                Logger.LogError("Error parsing GitHub API response", ex);
                throw new Exception("Failed to parse version information from GitHub.", ex);
            }
            catch (Exception ex)
            {
                Logger.LogError("Unexpected error fetching latest CLI version", ex);
                throw new Exception("An unexpected error occurred while checking for the latest version.", ex);
            }
        }
        
        private async void DownloadCliLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Show save file dialog for choosing where to save the CLI
                var saveFileDialog = new SaveFileDialog
                {
                    FileName = "bw.exe",
                    Filter = "Executable files (*.exe)|*.exe",
                    Title = "Select where to save Bitwarden CLI"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    var targetPath = saveFileDialog.FileName;
                    var targetDirectory = Path.GetDirectoryName(targetPath);

                    if (string.IsNullOrEmpty(targetDirectory))
                    {
                        MessageBox.Show("Invalid save location selected.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    // Create a temporary directory for extraction
                    var tempDir = Path.Combine(Path.GetTempPath(), "BitwardenCLI_" + Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempDir);
                    var zipPath = Path.Combine(tempDir, "bw.zip");

                    try
                    {
                        // Show progress dialog
                        var progressDialog = new Window
                        {
                            Title = "Downloading Bitwarden CLI",
                            Width = 300,
                            Height = 150,
                            WindowStartupLocation = WindowStartupLocation.CenterScreen,
                            ResizeMode = ResizeMode.NoResize,
                            WindowStyle = WindowStyle.ToolWindow
                        };

                        var stackPanel = new StackPanel { Margin = new Thickness(10) };
                        var statusText = new TextBlock 
                        { 
                            Text = "Checking for latest version...",
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 0, 0, 10)
                        };
                        var progressBar = new ProgressBar
                        {
                            Height = 20,
                            IsIndeterminate = true
                        };

                        stackPanel.Children.Add(statusText);
                        stackPanel.Children.Add(progressBar);
                        progressDialog.Content = stackPanel;
                        progressDialog.Show();

                        // Get the latest version URL
                        statusText.Text = "Fetching latest version information...";
                        var downloadUrl = await GetLatestCliVersionUrl();

                        // Download the ZIP file
                        statusText.Text = "Downloading CLI...";
                        using (var client = new HttpClient())
                        {
                            var response = await client.GetAsync(downloadUrl);
                            response.EnsureSuccessStatusCode();

                            using (var fs = new FileStream(zipPath, FileMode.Create))
                            {
                                await response.Content.CopyToAsync(fs);
                            }
                        }

                        // Extract the ZIP file
                        statusText.Text = "Extracting files...";
                        ZipFile.ExtractToDirectory(zipPath, tempDir);

                        // Find the bw.exe file
                        var bwExePath = Directory.GetFiles(tempDir, "bw.exe", SearchOption.AllDirectories).FirstOrDefault();

                        if (string.IsNullOrEmpty(bwExePath))
                        {
                            throw new FileNotFoundException("bw.exe not found in the downloaded package.");
                        }

                        // Copy to the target location
                        statusText.Text = "Installing...";
                        File.Copy(bwExePath, targetPath, true);

                        // Update the settings
                        if (_settings != null)
                        {
                            _settings.BwExecutablePath = targetPath;
                            BwExecutablePathTextBox.Text = targetPath;
                            _updateSettings?.Invoke(_settings);
                            
                            // Automatically add to PATH
                            UpdatePathStatus();
                            if (!_settings.IsPathEnvironmentValid)
                            {
                                AddToPath();
                            }
                        }

                        progressDialog.Close();

                        MessageBox.Show("Bitwarden CLI has been successfully downloaded, installed, and added to your PATH.", 
                            "Installation Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    finally
                    {
                        // Clean up temporary files
                        try
                        {
                            if (Directory.Exists(tempDir))
                            {
                                Directory.Delete(tempDir, true);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Error cleaning up temporary files: {ex.Message}", LogLevel.Warning);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error downloading and installing Bitwarden CLI: {ex.Message}", 
                    "Installation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Logger.LogError("Error during CLI download and installation", ex);
            }
        }

        private void LogLevelCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings != null && sender is CheckBox checkBox)
            {
                switch (checkBox.Content.ToString())
                {
                    case "Trace":
                        _settings.LogTrace = checkBox.IsChecked ?? false;
                        break;
                    case "Debug":
                        _settings.LogDebug = checkBox.IsChecked ?? false;
                        break;
                    case "Info":
                        _settings.LogInfo = checkBox.IsChecked ?? false;
                        break;
                    case "Warning":
                        _settings.LogWarning = checkBox.IsChecked ?? false;
                        break;
                    case "Error":
                        _settings.LogError = checkBox.IsChecked ?? false;
                        break;
                }
                _updateSettings?.Invoke(_settings);
            }
        }

        private void SyncNotificationCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings != null && sender is CheckBox checkBox)
            {
                switch (checkBox.Name)
                {
                    case "NotifySyncStartCheckBox":
                        _settings.NotifyOnSyncStart = checkBox.IsChecked ?? false;
                        break;
                    case "NotifyIconCacheStartCheckBox":
                        _settings.NotifyOnIconCacheStart = checkBox.IsChecked ?? false;
                        break;
                    case "NotifySyncCompleteCheckBox":
                        _settings.NotifyOnSyncComplete = checkBox.IsChecked ?? false;
                        break;
                }
                _updateSettings?.Invoke(_settings);
            }
        }

        private void NotificationCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings != null && sender is CheckBox checkBox)
            {
                switch (checkBox.Name)
                {
                    case "NotifyPasswordCopyCheckBox":
                        _settings.NotifyOnPasswordCopy = checkBox.IsChecked ?? false;
                        break;
                    case "NotifyUsernameCopyCheckBox":
                        _settings.NotifyOnUsernameCopy = checkBox.IsChecked ?? false;
                        break;
                    case "NotifyUriCopyCheckBox":
                        _settings.NotifyOnUriCopy = checkBox.IsChecked ?? false;
                        break;
                    case "NotifyTotpCopyCheckBox":
                        _settings.NotifyOnTotpCopy = checkBox.IsChecked ?? false;
                        break;
                    case "NotifyAutoLockCheckBox":
                        _settings.NotifyOnAutoLock = checkBox.IsChecked ?? false;
                        break;
                }
                _updateSettings?.Invoke(_settings);
            }
        }

        private void ClientIdTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_settings != null && _settings.ClientId != ClientIdTextBox.Text)
            {
                _settings.ClientId = ClientIdTextBox.Text;
                _updateSettings?.Invoke(_settings);
                
                // Enable save button only if both fields have content
                SaveClientSecretButton.IsEnabled = !string.IsNullOrEmpty(ClientIdTextBox.Text) && 
                                                !string.IsNullOrEmpty(ClientSecretBox.Password) &&
                                                ClientSecretBox.Password != "********";
            }
        }

        private void ClientSecretBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            _isClientSecretModified = true;
            
            // Enable save button only if both fields have content
            SaveClientSecretButton.IsEnabled = !string.IsNullOrEmpty(ClientIdTextBox.Text) && 
                                            !string.IsNullOrEmpty(ClientSecretBox.Password);
        }

        private async void SaveClientSecretButton_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            if (string.IsNullOrEmpty(ClientIdTextBox.Text) || string.IsNullOrEmpty(ClientSecretBox.Password))
            {
                if (ServerConfigStatusTextBlock != null)
                {
                    ServerConfigStatusTextBlock.Text = "Both Client ID and Client Secret are required.";
                    ServerConfigStatusTextBlock.Foreground = Brushes.Red;
                }
                return;
            }

            SaveClientSecret();

            // Now attempt to verify the credentials
            try 
            {
                var secureSecret = SecureCredentialManager.RetrieveCredential(_settings.ClientId);
                if (secureSecret == null)
                {
                    if (ServerConfigStatusTextBlock != null)
                    {
                        ServerConfigStatusTextBlock.Text = "Failed to retrieve saved credentials.";
                        ServerConfigStatusTextBlock.Foreground = Brushes.Red;
                    }
                    return;
                }

                var loginSuccess = await BitwardenAuthService.LoginWithApiKey(_settings.ClientId, secureSecret);
                if (loginSuccess)
                {
                    if (ServerConfigStatusTextBlock != null)
                    {
                        ServerConfigStatusTextBlock.Text = "API credentials verified successfully.";
                        ServerConfigStatusTextBlock.Foreground = Brushes.Green;
                    }
                }
                else
                {
                    if (ServerConfigStatusTextBlock != null)
                    {
                        ServerConfigStatusTextBlock.Text = "Failed to verify API credentials. Please check your Client ID and Secret.";
                        ServerConfigStatusTextBlock.Foreground = Brushes.Red;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Error verifying API credentials", ex);
                if (ServerConfigStatusTextBlock != null)
                {
                    ServerConfigStatusTextBlock.Text = $"Error verifying API credentials: {ex.Message}";
                    ServerConfigStatusTextBlock.Foreground = Brushes.Red;
                }
            }
        }

        private void SaveClientSecret()
        {
            if (_settings != null && _isClientSecretModified)
            {
                var securePassword = new SecureString();
                foreach (char c in ClientSecretBox.Password)
                {
                    securePassword.AppendChar(c);
                }
                SecureCredentialManager.SaveCredential(_settings.ClientId, securePassword);
                ClientSecretBox.Password = "********";
                _isClientSecretModified = false;
                SaveClientSecretButton.IsEnabled = false;
                _updateSettings?.Invoke(_settings);
                
                // Change button appearance to indicate success
                SaveClientSecretButton.Content = "✓";
                SaveClientSecretButton.Background = new SolidColorBrush(Colors.Green);
                
                // Start the timer to reset the button
                _resetButtonTimer?.Start();
            }
        }

        private void ResetButtonStyle(object? sender, EventArgs e)
        {
            SaveClientSecretButton.Content = "Save Secret";
            SaveClientSecretButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007bff"));
            _resetButtonTimer?.Stop();
        }

        public void SetVerificationStatus(string status)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetVerificationStatus(status));
                return;
            }

            VerificationStatusTextBlock.Text = status;

            if (status.Contains("Error"))
            {
                VerificationStatusTextBlock.Foreground = Brushes.Red;
            }
            else if (status.Contains("success"))
            {
                VerificationStatusTextBlock.Foreground = Brushes.Green;
            }
            else
            {
                VerificationStatusTextBlock.Foreground = Brushes.Gray;
            }
        }

        private void ClipboardClearComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings != null && sender is ComboBox comboBox)
            {
                var selectedItem = comboBox.SelectedItem as ComboBoxItem;
                if (selectedItem != null && int.TryParse(selectedItem.Tag.ToString(), out int seconds))
                {
                    _settings.ClipboardClearSeconds = seconds;
                    _updateSettings?.Invoke(_settings);
                }
            }
        }

        private static int GetIndexFromSeconds(int seconds)
        {
            return seconds switch
            {
                0 => 0,
                10 => 1,
                20 => 2,
                30 => 3,
                60 => 4,
                120 => 5,
                300 => 6,
                _ => 0
            };
        }

        private async void UseCustomServerCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _updateSettings == null) return;

            _settings.UseCustomServer = UseCustomServerCheckBox?.IsChecked ?? false;
            if (ServerConfigPanel != null)
            {
                ServerConfigPanel.Visibility = _settings.UseCustomServer ? Visibility.Visible : Visibility.Collapsed;
            }

            // Enable/disable official server selection
            if (OfficialServerComboBox != null)
            {
                OfficialServerComboBox.IsEnabled = !_settings.UseCustomServer;
            }

            // Clear credentials when switching servers
            if (ClientIdTextBox != null)
            {
                ClientIdTextBox.Text = string.Empty;
                _settings.ClientId = string.Empty;
            }
            
            if (ClientSecretBox != null)
            {
                ClientSecretBox.Password = string.Empty;
            }

            // Clear any stored credentials
            try
            {
                SecureCredentialManager.DeleteCredential();
                Logger.Log("Cleared stored credentials", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Logger.LogError("Error clearing stored credentials", ex);
            }

            if (_settings.UseCustomServer)
            {
                if (ServerConfigStatusTextBlock != null)
                {
                    ServerConfigStatusTextBlock.Text = "Note: You will need to provide new API credentials for this server.";
                    ServerConfigStatusTextBlock.Foreground = Brushes.Gray;
                }

                // Try to load existing server configuration
                var currentServerUrl = await BitwardenCliConfigManager.GetCurrentServerUrl();
                if (!string.IsNullOrEmpty(currentServerUrl) && currentServerUrl != BitwardenConstants.DEFAULT_SERVER_URL && currentServerUrl != BitwardenConstants.EU_SERVER_URL)
                {
                    if (BaseUrlTextBox != null) BaseUrlTextBox.Text = currentServerUrl;
                    if (ServerConfigStatusTextBlock != null)
                    {
                        ServerConfigStatusTextBlock.Text = "Existing server configuration loaded. Please provide API credentials for this server.";
                        ServerConfigStatusTextBlock.Foreground = Brushes.Green;
                    }
                }
            }
            else
            {
                if (ServerConfigStatusTextBlock != null)
                {
                    ServerConfigStatusTextBlock.Text = "Switching to selected official server...";
                    ServerConfigStatusTextBlock.Foreground = Brushes.Gray;
                }

                // Use the currently selected official server
                var success = await BitwardenCliConfigManager.ConfigureOfficialServer(_settings.OfficialServerRegion);
                
                if (success)
                {
                    ClearServerConfigFields();
                    if (ServerConfigStatusTextBlock != null)
                    {
                        ServerConfigStatusTextBlock.Text = $"Switched to {(_settings.OfficialServerRegion == "eu" ? "EU" : "US")} server. You will need to log in again.";
                        ServerConfigStatusTextBlock.Foreground = Brushes.Green;
                    }
                }
                else
                {
                    if (ServerConfigStatusTextBlock != null)
                    {
                        ServerConfigStatusTextBlock.Text = "Failed to switch to official server. Please try logging out manually.";
                        ServerConfigStatusTextBlock.Foreground = Brushes.Red;
                    }
                }
            }

            _updateSettings.Invoke(_settings);
        }

        private void ServerConfig_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_settings == null || _updateSettings == null) return;

            if (BaseUrlTextBox != null) _settings.CustomServerUrl = BaseUrlTextBox.Text;
            if (IdentityUrlTextBox != null) _settings.CustomIdentityUrl = IdentityUrlTextBox.Text;
            if (ApiUrlTextBox != null) _settings.CustomApiUrl = ApiUrlTextBox.Text;
            if (NotificationsUrlTextBox != null) _settings.CustomNotificationsUrl = NotificationsUrlTextBox.Text;
            if (WebVaultUrlTextBox != null) _settings.CustomWebVaultUrl = WebVaultUrlTextBox.Text;
            if (IconsUrlTextBox != null) _settings.CustomIconsUrl = IconsUrlTextBox.Text;
            if (KeysUrlTextBox != null) _settings.CustomKeysUrl = KeysUrlTextBox.Text;

            _updateSettings.Invoke(_settings);
            
            // Enable apply button only if base URL is provided
            if (ApplyServerConfigButton != null && BaseUrlTextBox != null)
            {
                ApplyServerConfigButton.IsEnabled = !string.IsNullOrWhiteSpace(BaseUrlTextBox.Text);
            }
        }

        private async void ApplyServerConfigButton_Click(object sender, RoutedEventArgs e)
        {
            if (BaseUrlTextBox == null || ServerConfigStatusTextBlock == null || ApplyServerConfigButton == null) return;

            if (string.IsNullOrWhiteSpace(BaseUrlTextBox.Text))
            {
                ServerConfigStatusTextBlock.Text = "Base URL is required";
                ServerConfigStatusTextBlock.Foreground = Brushes.Red;
                return;
            }

            var config = new BitwardenCliConfig
            {
                BaseUrl = BaseUrlTextBox.Text,
                IdentityUrl = IdentityUrlTextBox?.Text,
                ApiUrl = ApiUrlTextBox?.Text,
                NotificationsUrl = NotificationsUrlTextBox?.Text,
                WebVaultUrl = WebVaultUrlTextBox?.Text,
                IconsUrl = IconsUrlTextBox?.Text,
                KeysUrl = KeysUrlTextBox?.Text
            };

            ApplyServerConfigButton.IsEnabled = false;
            ServerConfigStatusTextBlock.Text = "Applying server configuration...";
            ServerConfigStatusTextBlock.Foreground = Brushes.Gray;

            try
            {
                bool success = await BitwardenCliConfigManager.ConfigureServer(config);
                if (success)
                {
                    ServerConfigStatusTextBlock.Text = "Server configuration applied successfully";
                    ServerConfigStatusTextBlock.Foreground = Brushes.Green;
                }
                else
                {
                    ServerConfigStatusTextBlock.Text = "Failed to apply server configuration";
                    ServerConfigStatusTextBlock.Foreground = Brushes.Red;
                }
            }
            catch (Exception ex)
            {
                ServerConfigStatusTextBlock.Text = $"Error: {ex.Message}";
                ServerConfigStatusTextBlock.Foreground = Brushes.Red;
                Logger.LogError("Failed to apply server configuration", ex);
            }
            finally
            {
                ApplyServerConfigButton.IsEnabled = true;
            }
        }

        private void ClearServerConfigFields()
        {
            if (BaseUrlTextBox != null) BaseUrlTextBox.Text = string.Empty;
            if (IdentityUrlTextBox != null) IdentityUrlTextBox.Text = string.Empty;
            if (ApiUrlTextBox != null) ApiUrlTextBox.Text = string.Empty;
            if (NotificationsUrlTextBox != null) NotificationsUrlTextBox.Text = string.Empty;
            if (WebVaultUrlTextBox != null) WebVaultUrlTextBox.Text = string.Empty;
            if (IconsUrlTextBox != null) IconsUrlTextBox.Text = string.Empty;
            if (KeysUrlTextBox != null) KeysUrlTextBox.Text = string.Empty;
            if (ServerConfigStatusTextBlock != null) ServerConfigStatusTextBlock.Text = string.Empty;
        }

        private void InitializeOfficialServerSelection()
        {
            if (_settings == null || OfficialServerComboBox == null) return;

            // Set the current selection based on settings
            if (_settings.OfficialServerRegion == "eu")
            {
                OfficialServerComboBox.SelectedIndex = 1; // EU server
            }
            else
            {
                OfficialServerComboBox.SelectedIndex = 0; // US server (default)
            }

            // Disable if using custom server
            OfficialServerComboBox.IsEnabled = !_settings.UseCustomServer;
        }

        private async void OfficialServerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings == null || _updateSettings == null || OfficialServerComboBox == null) return;
            
            var selectedItem = OfficialServerComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem == null) return;

            var newRegion = selectedItem.Tag?.ToString() ?? "com";
            
            // Only proceed if the region actually changed
            if (_settings.OfficialServerRegion == newRegion) return;

            _settings.OfficialServerRegion = newRegion;

            // Don't apply server configuration if using custom server
            if (_settings.UseCustomServer) 
            {
                _updateSettings.Invoke(_settings);
                return;
            }

            // Clear credentials when switching servers
            if (ClientIdTextBox != null)
            {
                ClientIdTextBox.Text = string.Empty;
                _settings.ClientId = string.Empty;
            }
            
            if (ClientSecretBox != null)
            {
                ClientSecretBox.Password = string.Empty;
            }

            // Clear any stored credentials
            try
            {
                SecureCredentialManager.DeleteCredential();
                Logger.Log("Cleared stored credentials when switching official server", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Logger.LogError("Error clearing stored credentials", ex);
            }

            if (ServerConfigStatusTextBlock != null)
            {
                ServerConfigStatusTextBlock.Text = $"Switching to {(newRegion == "eu" ? "EU" : "US")} server...";
                ServerConfigStatusTextBlock.Foreground = Brushes.Gray;
            }

            try
            {
                var success = await BitwardenCliConfigManager.ConfigureOfficialServer(newRegion);
                
                if (success)
                {
                    if (ServerConfigStatusTextBlock != null)
                    {
                        ServerConfigStatusTextBlock.Text = $"Successfully switched to {(newRegion == "eu" ? "EU" : "US")} server. Please provide your API credentials.";
                        ServerConfigStatusTextBlock.Foreground = Brushes.Green;
                    }
                }
                else
                {
                    if (ServerConfigStatusTextBlock != null)
                    {
                        ServerConfigStatusTextBlock.Text = "Failed to switch server. Please try again.";
                        ServerConfigStatusTextBlock.Foreground = Brushes.Red;
                    }
                }
            }
            catch (Exception ex)
            {
                if (ServerConfigStatusTextBlock != null)
                {
                    ServerConfigStatusTextBlock.Text = $"Error switching server: {ex.Message}";
                    ServerConfigStatusTextBlock.Foreground = Brushes.Red;
                }
                Logger.LogError("Failed to switch official server", ex);
            }

            _updateSettings.Invoke(_settings);
        }

        private void InitializeServerConfig()
        {
            if (_settings == null || UseCustomServerCheckBox == null || ServerConfigPanel == null) return;

            UseCustomServerCheckBox.IsChecked = _settings.UseCustomServer;
            if (_settings.UseCustomServer)
            {
                ServerConfigPanel.Visibility = Visibility.Visible;

                if (BaseUrlTextBox != null)
                    BaseUrlTextBox.Text = _settings.CustomServerUrl;
                    
                if (IdentityUrlTextBox != null)
                    IdentityUrlTextBox.Text = _settings.CustomIdentityUrl;
                    
                if (ApiUrlTextBox != null)
                    ApiUrlTextBox.Text = _settings.CustomApiUrl;
                    
                if (NotificationsUrlTextBox != null)
                    NotificationsUrlTextBox.Text = _settings.CustomNotificationsUrl;
                    
                if (WebVaultUrlTextBox != null)
                    WebVaultUrlTextBox.Text = _settings.CustomWebVaultUrl;
                    
                if (IconsUrlTextBox != null)
                    IconsUrlTextBox.Text = _settings.CustomIconsUrl;
                    
                if (KeysUrlTextBox != null)
                    KeysUrlTextBox.Text = _settings.CustomKeysUrl;
            }
        }
    }
}