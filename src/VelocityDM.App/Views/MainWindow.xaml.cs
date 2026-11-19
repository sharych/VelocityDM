using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VelocityDM.App.Helpers;
using VelocityDM.App.ViewModels;
using VelocityDM.Core.Models;
using VelocityDM.Core.Services;

namespace VelocityDM.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ClipboardMonitor _clipboardMonitor;
    private GitHubUpdateService? _updateService;
    private Action? _initiateRestartExit;
    private bool _isUpdateDialogOpen;

    public bool AllowExplicitClose { get; set; }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _clipboardMonitor = new ClipboardMonitor();
        _clipboardMonitor.UrlDetected += url =>
        {
            Dispatcher.BeginInvoke(() => _viewModel.PendingClipboardUrl = url);
        };

        _viewModel.Manager.SettingsUpdated += settings =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _clipboardMonitor.IsEnabled = settings.ClipboardMonitoringEnabled;
                _clipboardMonitor.OnlyKnownFileExtensions = settings.ClipboardOnlyKnownFileExtensions;
            });
        };

        SourceInitialized += (_, _) =>
        {
            var settings = _viewModel.Manager.Settings;
            _clipboardMonitor.IsEnabled = settings.ClipboardMonitoringEnabled;
            _clipboardMonitor.OnlyKnownFileExtensions = settings.ClipboardOnlyKnownFileExtensions;
            _clipboardMonitor.Attach(this);
        };

        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.ActiveFilter))
            {
                Dispatcher.BeginInvoke(SyncSidebarSelection);
            }
        };

        Closed += (_, _) => _clipboardMonitor.Dispose();
    }

    public void ConfigureUpdater(GitHubUpdateService updateService, Action initiateRestartExit)
    {
        _updateService = updateService;
        _initiateRestartExit = initiateRestartExit;
    }

    public void SetAvailableUpdateBadge(GitHubReleaseInfo? release)
    {
        if (release != null)
        {
            UpdatesButton.Content = $"Update {release.TagName}";
            if (TryFindResource("PrimaryButtonStyle") is Style primaryStyle)
                UpdatesButton.Style = primaryStyle;
        }
        else
        {
            UpdatesButton.Content = "Updates";
            if (TryFindResource("ToolbarButtonStyle") is Style toolbarStyle)
                UpdatesButton.Style = toolbarStyle;
        }
    }

    public void ShowUpdateDialog(
        UpdateCheckResult? preloadedResult = null,
        bool bringToFront = false,
        Window? ownerOverride = null)
    {
        if (_isUpdateDialogOpen)
            return;

        _isUpdateDialogOpen = true;
        try
        {
            var service = _updateService ??= new GitHubUpdateService();
            Action exitAction = _initiateRestartExit ?? Close;
            var dialog = new UpdateWindow(service, _viewModel.Manager, exitAction, preloadedResult, bringToFront);

            Window? effectiveOwner = ownerOverride ?? (IsVisible ? this : null);
            if (effectiveOwner != null)
            {
                dialog.Owner = effectiveOwner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                dialog.ShowDialog();
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.ShowInTaskbar = true;
                dialog.ShowDialog();
            }
        }
        finally
        {
            _isUpdateDialogOpen = false;
        }
    }

    private void SyncSidebarSelection()
    {
        string targetTag = _viewModel.ActiveFilter.ToString();
        foreach (var child in SidebarStack.Children)
        {
            if (child is RadioButton rb && rb.Tag is string tag &&
                string.Equals(tag, targetTag, StringComparison.Ordinal))
            {
                if (rb.IsChecked != true) rb.IsChecked = true;
                break;
            }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowExplicitClose && _viewModel.Manager.Settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    public void RestoreFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public async Task ShowAddUrlDialogAsync(
        string? prefillUrl = null,
        string? customFileName = null,
        string? referrer = null,
        string? cookies = null,
        string? userAgent = null,
        bool bringToFront = false)
    {
        var dialog = new AddDownloadWindow(_viewModel.Manager, prefillUrl, bringToFront);
        if (IsVisible)
        {
            dialog.Owner = this;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;
        }

        if (dialog.ShowDialog() == true && dialog.ResultOptions != null)
        {
            if (dialog.UpdatedSettingsToPersist != null)
            {
                await _viewModel.Manager.UpdateSettingsAsync(dialog.UpdatedSettingsToPersist);
            }

            var opts = dialog.ResultOptions with
            {
                CustomFileName = dialog.ResultOptions.CustomFileName ?? (dialog.ProbeResolvedFileName ? null : customFileName),
                Referrer = referrer,
                CookieHeader = cookies,
                UserAgentOverride = userAgent
            };
            await _viewModel.Manager.AddDownloadAsync(opts);
        }
    }

    public async Task ShowAddTorrentDialogAsync(string? prefillSource = null)
    {
        var dialog = new AddTorrentWindow(_viewModel.Manager.Settings, prefillSource);
        if (IsVisible) dialog.Owner = this;

        if (dialog.ShowDialog() == true)
        {
            try
            {
                if (dialog.IsTorrentFile)
                {
                    await _viewModel.TorrentManager.AddTorrentFileAsync(
                        dialog.Source,
                        dialog.SaveDirectory,
                        dialog.Sequential,
                        dialog.StartImmediately);
                }
                else
                {
                    await _viewModel.TorrentManager.AddMagnetAsync(
                        dialog.Source,
                        dialog.SaveDirectory,
                        dialog.Sequential,
                        dialog.StartImmediately);
                }

                _viewModel.ActiveFilter = SidebarFilter.TorrentsAll;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Add Torrent Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async void AddUrl_Click(object sender, RoutedEventArgs e) =>
        await ShowAddUrlDialogAsync();

    private async void AddTorrent_Click(object sender, RoutedEventArgs e) =>
        await ShowAddTorrentDialogAsync();

    private async void BatchImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BatchImportWindow(_viewModel.Manager) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedUrls.Count > 0)
        {
            await _viewModel.Manager.AddBatchDownloadsAsync(
                dialog.SelectedUrls,
                dialog.CustomDirectory,
                dialog.Connections,
                dialog.StartImmediately);
        }
    }

    private async void ResumeSelected_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.ResumeSelectedAsync(forceStart: false);

    private async void ForceStartSelected_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.ResumeSelectedAsync(forceStart: true);

    private async void PauseSelected_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.PauseSelectedAsync();

    private async void ResumeAll_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.Manager.ResumeAllAsync();

    private async void PauseAll_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.Manager.PauseAllAsync();

    private async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        string? name = _viewModel.IsTorrentView ? _viewModel.SelectedTorrent?.Name : _viewModel.SelectedItem?.FileName;
        if (string.IsNullOrEmpty(name)) return;

        var result = MessageBox.Show(
            this,
            $"Remove '{name}' from list?\n\nChoose YES to also delete downloaded data from disk, NO to keep downloaded files and remove only the entry.",
            "Remove Item",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel) return;
        await _viewModel.RemoveSelectedAsync(deleteFiles: result == MessageBoxResult.Yes);
    }

    private async void RemoveKeepFile_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.RemoveSelectedAsync(deleteFiles: false);

    private async void RemoveDeleteFile_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.RemoveSelectedAsync(deleteFiles: true);

    private async void MoveUp_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.MoveSelectedAsync(-1);

    private async void MoveDown_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.MoveSelectedAsync(1);

    private void OpenFile_Click(object sender, RoutedEventArgs e) =>
        _viewModel.OpenSelectedFile();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        _viewModel.OpenSelectedFolder();

    private void OpenDefaultFolder_Click(object sender, RoutedEventArgs e) =>
        _viewModel.OpenDefaultDownloadFolder();

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem != null)
        {
            _clipboardMonitor.SuppressUrlTemporarily(_viewModel.SelectedItem.Url);
            Clipboard.SetText(_viewModel.SelectedItem.Url);
            _viewModel.StatusMessage = "Download URL copied to clipboard.";
        }
    }

    private void CopyMagnet_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTorrent?.MagnetUri != null)
        {
            _clipboardMonitor.SuppressUrlTemporarily(_viewModel.SelectedTorrent.MagnetUri);
            Clipboard.SetText(_viewModel.SelectedTorrent.MagnetUri);
            _viewModel.StatusMessage = "Magnet link copied to clipboard.";
        }
    }

    private async void ToggleTorrentSequential_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTorrent == null) return;
        await _viewModel.TorrentManager.SetSequentialAsync(
            _viewModel.SelectedTorrent.Id,
            !_viewModel.SelectedTorrent.Sequential);
    }

    private async void ForceRecheckTorrent_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ForceRecheckSelectedTorrentAsync();
    }

    private async void SetTorrentFilePriority_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTorrent == null ||
            TorrentFilesGrid.SelectedItem is not TorrentFileViewModel fileVm ||
            sender is not MenuItem mi ||
            mi.Tag is not string tag ||
            !Enum.TryParse<TorrentPriority>(tag, out var priority))
        {
            return;
        }

        await _viewModel.TorrentManager.SetFilePriorityAsync(_viewModel.SelectedTorrent.Id, fileVm.Path, priority);
    }

    private async void VerifySha256_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.VerifySelectedSha256Async();

    private void ChromeExtension_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ChromeExtensionWindow { Owner = this };
        dialog.ShowDialog();
    }

    private void Updates_Click(object sender, RoutedEventArgs e)
    {
        ShowUpdateDialog();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        SettingsWindow? dialog = null;
        dialog = new SettingsWindow(
            _viewModel.Manager.Settings,
            openUpdatesAction: () => ShowUpdateDialog(ownerOverride: dialog))
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && dialog.UpdatedSettings != null)
        {
            await _viewModel.Manager.UpdateSettingsAsync(dialog.UpdatedSettings);
        }
    }

    private async void ClipboardDownloadNow_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.QuickDownloadClipboardUrlAsync();

    private async void ClipboardCustomize_Click(object sender, RoutedEventArgs e)
    {
        var url = _viewModel.PendingClipboardUrl;
        _viewModel.PendingClipboardUrl = null;
        if (string.IsNullOrWhiteSpace(url)) return;

        if (url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            await ShowAddTorrentDialogAsync(url);
        }
        else
        {
            await ShowAddUrlDialogAsync(url);
        }
    }

    private void ClipboardDismiss_Click(object sender, RoutedEventArgs e) =>
        _viewModel.PendingClipboardUrl = null;

    private async void DownloadsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedItem == null) return;

        if (_viewModel.SelectedItem.Status == DownloadStatus.Completed)
            _viewModel.OpenSelectedFile();
        else if (_viewModel.SelectedItem.Status is DownloadStatus.Paused or DownloadStatus.Error)
            await _viewModel.ResumeSelectedAsync();
        else if (_viewModel.SelectedItem.Status == DownloadStatus.Downloading)
            await _viewModel.PauseSelectedAsync();
    }

    private void SidebarFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null || sender is not RadioButton rb || rb.Tag is not string tag)
            return;

        if (Enum.TryParse<SidebarFilter>(tag, out var parsed))
        {
            _viewModel.ActiveFilter = parsed;
        }
    }
}
