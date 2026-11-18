using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelocityDM.Core.Models;
using VelocityDM.Mac.Helpers;

namespace VelocityDM.Mac.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public AppSettings? UpdatedSettings { get; private set; }

    public SettingsWindow() : this(new AppSettings())
    {
    }

    public SettingsWindow(AppSettings currentSettings)
    {
        InitializeComponent();
        _settings = currentSettings.Clone();

        DefaultFolderTextBox.Text = _settings.DefaultDownloadDirectory;
        CategorizeCheckBox.IsChecked = _settings.CategorizeIntoSubfolders;
        ConnectionsSlider.Value = _settings.DefaultConnectionsPerDownload;
        ConcurrentSlider.Value = _settings.MaxConcurrentDownloads;
        ConnectionsLabel.Text = $"{_settings.DefaultConnectionsPerDownload}";
        ConcurrentLabel.Text = $"{_settings.MaxConcurrentDownloads}";
        SpeedLimitKbTextBox.Text = (_settings.GlobalSpeedLimitBytesPerSec / 1024).ToString();
        MaxRetriesTextBox.Text = _settings.MaxRetriesPerSegment.ToString();

        MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTrayOnClose;
        StartMinimizedCheckBox.IsChecked = _settings.StartMinimizedToTray;
        LaunchAtStartupCheckBox.IsChecked = _settings.LaunchAtWindowsStartup;
        AutoStartQueueCheckBox.IsChecked = _settings.AutoStartQueuedDownloads;

        AutoStartBrowserCheckBox.IsChecked = _settings.AutoStartBrowserDownloads;
        BridgePortTextBox.Text = _settings.BrowserBridgePort.ToString();
    }

    private void ConnectionsSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (ConnectionsLabel != null)
            ConnectionsLabel.Text = $"{(int)Math.Round(e.NewValue)}";
    }

    private void ConcurrentSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (ConcurrentLabel != null)
            ConcurrentLabel.Text = $"{(int)Math.Round(e.NewValue)}";
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose Default Download Folder",
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].Path.LocalPath is { Length: > 0 } localPath)
        {
            DefaultFolderTextBox.Text = localPath;
        }
    }

    private async void ChromeSetup_Click(object? sender, RoutedEventArgs e)
    {
        var win = new ChromeExtensionWindow();
        await win.ShowDialog<bool>(this);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!long.TryParse((SpeedLimitKbTextBox.Text ?? string.Empty).Trim(), out long kbPerSec) || kbPerSec < 0)
        {
            ValidationErrorText.Text = "HTTP Speed Limit must be 0 or a positive integer (in KB/s).";
            return;
        }

        if (!int.TryParse((MaxRetriesTextBox.Text ?? string.Empty).Trim(), out int retries) || retries < 0)
        {
            ValidationErrorText.Text = "Max Retries must be 0 or a positive integer.";
            return;
        }

        if (!int.TryParse((BridgePortTextBox.Text ?? string.Empty).Trim(), out int port) || port < 1024 || port > 65535)
        {
            ValidationErrorText.Text = "Browser Bridge Port must be between 1024 and 65535.";
            return;
        }

        _settings.DefaultDownloadDirectory = (DefaultFolderTextBox.Text ?? string.Empty).Trim();
        _settings.CategorizeIntoSubfolders = CategorizeCheckBox.IsChecked == true;
        _settings.DefaultConnectionsPerDownload = (int)Math.Round(ConnectionsSlider.Value);
        _settings.MaxConcurrentDownloads = (int)Math.Round(ConcurrentSlider.Value);
        _settings.GlobalSpeedLimitBytesPerSec = kbPerSec * 1024L;
        _settings.MaxRetriesPerSegment = retries;

        _settings.MinimizeToTrayOnClose = MinimizeToTrayCheckBox.IsChecked == true;
        _settings.StartMinimizedToTray = StartMinimizedCheckBox.IsChecked == true;
        _settings.LaunchAtWindowsStartup = LaunchAtStartupCheckBox.IsChecked == true;
        _settings.AutoStartQueuedDownloads = AutoStartQueueCheckBox.IsChecked == true;

        _settings.AutoStartBrowserDownloads = AutoStartBrowserCheckBox.IsChecked == true;
        _settings.BrowserBridgePort = port;

        _settings.Normalize();
        MacBrowserRegistrationService.SetMacLoginStartup(_settings.LaunchAtWindowsStartup);

        UpdatedSettings = _settings;
        Close(true);
    }
}
