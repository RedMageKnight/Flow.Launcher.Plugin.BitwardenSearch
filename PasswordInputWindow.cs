using System.Windows;
using System.Windows.Controls;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public partial class PasswordInputWindow : Window
    {
        public string Password { get; private set; } = string.Empty;

        public PasswordInputWindow()
        {
            InitializeComponent();
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            Password = PasswordBox.Password;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}