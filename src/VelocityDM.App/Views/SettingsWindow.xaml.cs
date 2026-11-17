using System.IO;
using System.Windows;
using Microsoft.Win32;
using VelocityDM.App.Helpers;
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

        MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTrayOnClose;
        StartMinimizedCheckBox.IsChecked = _settings.StartMinimizedToTray;
        LaunchAtStartupCheckBox.IsChecked = _settings.LaunchAtWindowsStartup;
        AutoStartQueueCheckBox.IsChecked = _settings.AutoStartQueuedDownloads;

        ClipboardEnabledCheckBox.IsChecked = _settings.ClipboardMonitoringEnabled;
        ClipboardKnownExtCheckBox.IsChecked = _settings.ClipboardOnlyKnownFileExtensions;
        AutoStartBrowserCheckBox.IsChecked = _settings.AutoStartBrowserDownloads;
        BridgePortTextBox.Text = _settings.BrowserBridgePort.ToString();

        TorrentDlLimitTextBox.Text = (_settings.TorrentDownloadSpeedLimitBytesPerSec / 1024).ToString();
        TorrentUlLimitTextBox.Text = (_settings.TorrentUploadSpeedLimitBytesPerSec / 1024).ToString();
        TorrentMaxConnTextBox.Text = _settings.TorrentMaxConnections.ToString();
        StopSeedingCheckBox.IsChecked = _settings.StopSeedingWhenComplete;
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

    private void ChromeSetup_Click(object sender, RoutedEventArgs e)
    {
        var win = new ChromeExtensionWindow { Owner = this };
        win.ShowDialog();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(SpeedLimitKbTextBox.Text.Trim(), out long kbPerSec) || kbPerSec < 0)
        {
            MessageBox.Show(this, "HTTP Speed Limit must be 0 or a positive integer (in KB/s).", "Invalid Setting", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(MaxRetriesTextBox.Text.Trim(), out int retries) || retries < 0)
        {
            MessageBox.Show(this, "Max Retries must be 0 or a positive integer.", "Invalid Setting", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(BridgePortTextBox.Text.Trim(), out int port) || port < 1024 || port > 65535)
        {
            MessageBox.Show(this, "Browser Bridge Port must be between 1024 and 65535.", "Invalid Setting", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!long.TryParse(TorrentDlLimitTextBox.Text.Trim(), out long tDlKb) || tDlKb < 0 ||
            !long.TryParse(TorrentUlLimitTextBox.Text.Trim(), out long tUlKb) || tUlKb < 0 ||
            !int.TryParse(TorrentMaxConnTextBox.Text.Trim(), out int tConn) || tConn < 10)
        {
            MessageBox.Show(this, "Please enter valid Torrent speed limits (>= 0) and Max Connections (>= 10).", "Invalid Setting", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.DefaultDownloadDirectory = DefaultFolderTextBox.Text.Trim();
        _settings.CategorizeIntoSubfolders = CategorizeCheckBox.IsChecked == true;
        _settings.DefaultConnectionsPerDownload = (int)Math.Round(ConnectionsSlider.Value);
        _settings.MaxConcurrentDownloads = (int)Math.Round(ConcurrentSlider.Value);
        _settings.GlobalSpeedLimitBytesPerSec = kbPerSec * 1024L;
        _settings.MaxRetriesPerSegment = retries;

        _settings.MinimizeToTrayOnClose = MinimizeToTrayCheckBox.IsChecked == true;
        _settings.StartMinimizedToTray = StartMinimizedCheckBox.IsChecked == true;
        _settings.LaunchAtWindowsStartup = LaunchAtStartupCheckBox.IsChecked == true;
        _settings.AutoStartQueuedDownloads = AutoStartQueueCheckBox.IsChecked == true;

        _settings.ClipboardMonitoringEnabled = ClipboardEnabledCheckBox.IsChecked == true;
        _settings.ClipboardOnlyKnownFileExtensions = ClipboardKnownExtCheckBox.IsChecked == true;
        _settings.AutoStartBrowserDownloads = AutoStartBrowserCheckBox.IsChecked == true;
        _settings.BrowserBridgePort = port;

        _settings.TorrentDownloadSpeedLimitBytesPerSec = tDlKb * 1024L;
        _settings.TorrentUploadSpeedLimitBytesPerSec = tUlKb * 1024L;
        _settings.TorrentMaxConnections = tConn;
        _settings.StopSeedingWhenComplete = StopSeedingCheckBox.IsChecked == true;

        _settings.Normalize();
        BrowserRegistrationService.SetWindowsStartup(_settings.LaunchAtWindowsStartup);

        UpdatedSettings = _settings;
        DialogResult = true;
        Close();
    }
}
