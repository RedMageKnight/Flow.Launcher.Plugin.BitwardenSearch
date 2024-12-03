using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

public class PasswordInputDialog : Window
{
    private readonly PasswordBox _passwordBox;
    public string Password => _passwordBox.Password;

    public PasswordInputDialog()
    {
        Title = "Unlock Bitwarden Vault";
        Width = 400;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        
        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = "Enter your master password:",
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(label, 0);

        _passwordBox = new PasswordBox
        {
            Margin = new Thickness(0, 0, 0, 20)
        };
        Grid.SetRow(_passwordBox, 1);

        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetRow(buttonsPanel, 2);

        var okButton = new Button
        {
            Content = "Unlock",
            Padding = new Thickness(20, 5, 20, 5),
            Margin = new Thickness(0, 0, 10, 0)
        };
        okButton.Click += (s, e) => { DialogResult = true; Close(); };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(20, 5, 20, 5)
        };
        cancelButton.Click += (s, e) => { DialogResult = false; Close(); };

        buttonsPanel.Children.Add(okButton);
        buttonsPanel.Children.Add(cancelButton);

        grid.Children.Add(label);
        grid.Children.Add(_passwordBox);
        grid.Children.Add(buttonsPanel);

        Content = grid;

        _passwordBox.Focus();
        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                DialogResult = true;
                Close();
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        };
    }
}