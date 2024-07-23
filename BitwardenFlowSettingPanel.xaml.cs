using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Security;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public partial class BitwardenFlowSettingPanel : UserControl
    {
        private BitwardenFlowSettings? _settings;
        private Action<BitwardenFlowSettings>? _updateSettings;
        private bool _isClientSecretModified = false;
        private DispatcherTimer? _resetButtonTimer;

        public BitwardenFlowSettingPanel()
        {
            InitializeComponent();
        }

        public BitwardenFlowSettingPanel(BitwardenFlowSettings settings, Action<BitwardenFlowSettings> updateSettings)
        {
            InitializeComponent();
            _settings = settings;
            _updateSettings = updateSettings;
            
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

            KeepUnlockedCheckBox.IsChecked = _settings.KeepUnlocked;
            LockTimeTextBox.Text = _settings.LockTime.ToString();
            LockTimeTextBox.IsEnabled = !_settings.KeepUnlocked;

            NotifyPasswordCopyCheckBox.IsChecked = _settings.NotifyOnPasswordCopy;
            NotifyUsernameCopyCheckBox.IsChecked = _settings.NotifyOnUsernameCopy;
            NotifyUriCopyCheckBox.IsChecked = _settings.NotifyOnUriCopy;
            NotifyTotpCopyCheckBox.IsChecked = _settings.NotifyOnTotpCopy;

            // Add event handlers for the new functionality
            ClientSecretBox.PasswordChanged += ClientSecretBox_PasswordChanged;
            SaveClientSecretButton.Click += SaveClientSecretButton_Click;
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

        private void KeepUnlockedCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.KeepUnlocked = KeepUnlockedCheckBox.IsChecked ?? false;
                LockTimeTextBox.IsEnabled = !_settings.KeepUnlocked;
                _updateSettings?.Invoke(_settings);
            }
        }

        private void LockTimeTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_settings != null && int.TryParse(LockTimeTextBox.Text, out int lockTime))
            {
                _settings.LockTime = lockTime;
                _updateSettings?.Invoke(_settings);
            }
        }

        private void LockTimeTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _);
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
            }
        }

        private void ClientSecretBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            _isClientSecretModified = true;
            SaveClientSecretButton.IsEnabled = true;
            SaveClientSecretButton.Content = "Save Secret";
            SaveClientSecretButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007bff"));
        }

        private void SaveClientSecretButton_Click(object sender, RoutedEventArgs e)
        {
            SaveClientSecret();
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
    }
}