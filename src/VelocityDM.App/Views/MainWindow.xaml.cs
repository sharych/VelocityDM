using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VelocityDM.App.Helpers;
using VelocityDM.App.ViewModels;
using VelocityDM.Core.Models;

namespace VelocityDM.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ClipboardMonitor _clipboardMonitor;

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

        Closed += (_, _) => _clipboardMonitor.Dispose();
    }

    private async void AddUrl_Click(object sender, RoutedEventArgs e)
    {
        await ShowAddDialogAsync(prefillUrl: null);
    }

    private async Task ShowAddDialogAsync(string? prefillUrl)
    {
        var dialog = new AddDownloadWindow(_viewModel.Manager, prefillUrl) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ResultOptions != null)
        {
            await _viewModel.Manager.AddDownloadAsync(dialog.ResultOptions);
        }
    }

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
        if (_viewModel.SelectedItem == null)
            return;

        var result = MessageBox.Show(
            this,
            $"Remove '{_viewModel.SelectedItem.FileName}' from list?\n\nChoose YES to also delete the file from disk, NO to keep the file and remove only the entry.",
            "Remove Download",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel)
            return;

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

    private async void VerifySha256_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.VerifySelectedSha256Async();

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_viewModel.Manager.Settings) { Owner = this };
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
        if (!string.IsNullOrWhiteSpace(url))
        {
            await ShowAddDialogAsync(url);
        }
    }

    private void ClipboardDismiss_Click(object sender, RoutedEventArgs e) =>
        _viewModel.PendingClipboardUrl = null;

    private async void DownloadsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedItem == null)
            return;

        if (_viewModel.SelectedItem.Status == DownloadStatus.Completed)
        {
            _viewModel.OpenSelectedFile();
        }
        else if (_viewModel.SelectedItem.Status is DownloadStatus.Paused or DownloadStatus.Error)
        {
            await _viewModel.ResumeSelectedAsync();
        }
        else if (_viewModel.SelectedItem.Status == DownloadStatus.Downloading)
        {
            await _viewModel.PauseSelectedAsync();
        }
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
