using System.IO;
using System.Windows;
using Microsoft.Win32;
using VelocityDM.Core.Models;

namespace VelocityDM.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public AppSettings? UpdatedSettings { get; private set; }

    public SettingsWindow(AppSettings currentSettings)
    {
        InitializeComponent();
        _settings = currentSettings.Clone();

        DefaultFolderTextBox.Text = _settings.DefaultDownloadDirectory;
        CategorizeCheckBox.IsChecked = _settings.CategorizeIntoSubfolders;
        ConnectionsSlider.Value = _settings.DefaultConnectionsPerDownload;
        ConcurrentSlider.Value = _settings.MaxConcurrentDownloads;
        SpeedLimitKbTextBox.Text = (_settings.GlobalSpeedLimitBytesPerSec / 1024).ToString();
        MaxRetriesTextBox.Text = _settings.MaxRetriesPerSegment.ToString();
        ClipboardEnabledCheckBox.IsChecked = _settings.ClipboardMonitoringEnabled;
        ClipboardKnownExtCheckBox.IsChecked = _settings.ClipboardOnlyKnownFileExtensions;
        AutoStartQueueCheckBox.IsChecked = _settings.AutoStartQueuedDownloads;
    }

    private void ConnectionsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ConnectionsLabel != null)
            ConnectionsLabel.Text = $"{(int)Math.Round(e.NewValue)}";
    }

    private void ConcurrentSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ConcurrentLabel != null)
            ConcurrentLabel.Text = $"{(int)Math.Round(e.NewValue)}";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose Default Download Folder",
            InitialDirectory = Directory.Exists(DefaultFolderTextBox.Text)
                ? DefaultFolderTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) == true)
        {
            DefaultFolderTextBox.Text = dialog.FolderName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(SpeedLimitKbTextBox.Text.Trim(), out long kbPerSec) || kbPerSec < 0)
        {
            MessageBox.Show(this, "Global Speed Limit must be 0 or a positive integer (in KB/s).", "Invalid Setting", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(MaxRetriesTextBox.Text.Trim(), out int retries) || retries < 0)
        {
            MessageBox.Show(this, "Max Retries must be 0 or a positive integer.", "Invalid Setting", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.DefaultDownloadDirectory = DefaultFolderTextBox.Text.Trim();
        _settings.CategorizeIntoSubfolders = CategorizeCheckBox.IsChecked == true;
        _settings.DefaultConnectionsPerDownload = (int)Math.Round(ConnectionsSlider.Value);
        _settings.MaxConcurrentDownloads = (int)Math.Round(ConcurrentSlider.Value);
        _settings.GlobalSpeedLimitBytesPerSec = kbPerSec * 1024L;
        _settings.MaxRetriesPerSegment = retries;
        _settings.ClipboardMonitoringEnabled = ClipboardEnabledCheckBox.IsChecked == true;
        _settings.ClipboardOnlyKnownFileExtensions = ClipboardKnownExtCheckBox.IsChecked == true;
        _settings.AutoStartQueuedDownloads = AutoStartQueueCheckBox.IsChecked == true;
        _settings.Normalize();

        UpdatedSettings = _settings;
        DialogResult = true;
        Close();
    }
}
