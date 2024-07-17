using System;
using System.Windows;
using System.Windows.Controls;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public partial class BitwardenFlowSettingPanel : UserControl
    {
        private BitwardenFlowSettings? _settings;
        private Action<BitwardenFlowSettings>? _updateSettings;

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
            ClientSecretBox.Password = _settings.ClientSecret;
            MasterPasswordBox.Password = _settings.MasterPassword;
            
            LogDebugCheckBox.IsChecked = _settings.LogDebug;
            LogInfoCheckBox.IsChecked = _settings.LogInfo;
            LogWarningCheckBox.IsChecked = _settings.LogWarning;
            LogErrorCheckBox.IsChecked = _settings.LogError;
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
            if (_settings != null && _settings.ClientSecret != ClientSecretBox.Password)
            {
                _settings.ClientSecret = ClientSecretBox.Password;
                _updateSettings?.Invoke(_settings);
            }
        }

        private void MasterPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_settings != null && _settings.MasterPassword != MasterPasswordBox.Password)
            {
                _settings.MasterPassword = MasterPasswordBox.Password;
                _updateSettings?.Invoke(_settings);
            }
        }

        private void LogLevelCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings != null && sender is CheckBox checkBox)
            {
                switch (checkBox.Content.ToString())
                {
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
    }
}