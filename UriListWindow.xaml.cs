using System;
using System.Windows;
using System.Collections.Generic;
using System.Windows.Input;
using Flow.Launcher.Plugin;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public partial class UriListWindow : Window
    {
        private readonly IPublicAPI _api;

        public string? SelectedUri { get; private set; }

        public UriListWindow(string title, IEnumerable<string> uris, IPublicAPI api)
        {
            InitializeComponent();
            _api = api;
            TitleTextBlock.Text = title;
            UriListBox.ItemsSource = uris;
            
            if (UriListBox.Items.Count > 0)
            {
                UriListBox.SelectedIndex = 0;
                UriListBox.Focus();
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        }

        private void UriListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SelectUri();
            }
        }

        private void UriListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            SelectUri();
        }

        private void SelectUri()
        {
            if (UriListBox.SelectedItem is string selectedUri)
            {
                SelectedUri = selectedUri;
                DialogResult = true;
                Close();
            }
        }
    }
}