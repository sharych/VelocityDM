using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelocityDM.Core.Models;
using VelocityDM.Mac.Helpers;
using VelocityDM.Mac.ViewModels;

namespace VelocityDM.Mac.Views;

public partial class MainWindow : Window
{
    private readonly MacMainViewModel _viewModel;

    public bool AllowExplicitClose { get; set; }

    public MainWindow() : this(new MacMainViewModel(new VelocityDM.Core.Services.DownloadManagerService()))
    {
    }

    public MainWindow(MacMainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
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
        bool accepted;
        if (IsVisible)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            accepted = await dialog.ShowDialog<bool>(this);
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var tcs = new TaskCompletionSource<bool>();
            dialog.Closed += (_, _) => tcs.TrySetResult(dialog.Accepted);
            dialog.Show();
            accepted = await tcs.Task;
        }

        if (accepted && dialog.ResultOptions != null)
        {
            if (dialog.UpdatedSettingsToPersist != null)
            {
                await _viewModel.Manager.UpdateSettingsAsync(dialog.UpdatedSettingsToPersist);
            }

            var opts = dialog.ResultOptions with
            {
                CustomFileName = dialog.ResultOptions.CustomFileName ?? customFileName,
                Referrer = referrer,
                CookieHeader = cookies,
                UserAgentOverride = userAgent
            };
            await _viewModel.Manager.AddDownloadAsync(opts);
        }
    }

    public async Task ShowChromeExtensionDialogAsync()
    {
        var dialog = new ChromeExtensionWindow();
        await ShowOwnedDialogAsync(dialog);
    }

    private Task<bool> ShowOwnedDialogAsync(Window dialog)
    {
        if (!IsVisible)
            RestoreFromTray();

        return dialog.ShowDialog<bool>(this);
    }

    private async void AddUrl_Click(object? sender, RoutedEventArgs e) =>
        await ShowAddUrlDialogAsync();

    private async void BatchImport_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new BatchImportWindow(_viewModel.Manager);
        if (await ShowOwnedDialogAsync(dialog) && dialog.SelectedUrls.Count > 0)
        {
            await _viewModel.Manager.AddBatchDownloadsAsync(
                dialog.SelectedUrls,
                dialog.CustomDirectory,
                dialog.Connections,
                dialog.StartImmediately);
        }
    }

    private async void ResumeSelected_Click(object? sender, RoutedEventArgs e) =>
        await _viewModel.ResumeSelectedAsync(forceStart: false);

    private async void ForceStartSelected_Click(object? sender, RoutedEventArgs e) =>
        await _viewModel.ResumeSelectedAsync(forceStart: true);

    private async void PauseSelected_Click(object? sender, RoutedEventArgs e) =>
        await _viewModel.PauseSelectedAsync();

    private async void ResumeAll_Click(object? sender, RoutedEventArgs e) =>
        await _viewModel.Manager.ResumeAllAsync();

    private async void PauseAll_Click(object? sender, RoutedEventArgs e) =>
        await _viewModel.Manager.PauseAllAsync();

    private async void RemoveKeepFile_Click(object? sender, RoutedEventArgs e) =>
        await _viewModel.RemoveSelectedAsync(deleteFiles: false);

    private async void RemoveDeleteFile_Click(object? sender, RoutedEventArgs e) =>
        await _viewModel.RemoveSelectedAsync(deleteFiles: true);

    private async void MoveUp_Click(object? sender, RoutedEventArgs e) =>
        await _viewModel.MoveSelectedAsync(-1);

    private async void MoveDown_Click(object? sender, RoutedEventArgs e) =>
        await _viewModel.MoveSelectedAsync(1);

    private void OpenFile_Click(object? sender, RoutedEventArgs e) =>
        _viewModel.OpenSelectedFile();

    private void OpenFolder_Click(object? sender, RoutedEventArgs e) =>
        _viewModel.OpenSelectedFolder();

    private void CopyUrl_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem != null)
        {
            MacBrowserRegistrationService.CopyToMacClipboard(_viewModel.SelectedItem.Url);
            _viewModel.StatusMessage = "Download URL copied to clipboard.";
        }
    }

    private async void VerifySha256_Click(object? sender, RoutedEventArgs e) =>
        await _viewModel.VerifySelectedSha256Async();

    private async void ChromeExtension_Click(object? sender, RoutedEventArgs e) =>
        await ShowChromeExtensionDialogAsync();

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_viewModel.Manager.Settings);
        if (await ShowOwnedDialogAsync(dialog) && dialog.UpdatedSettings != null)
        {
            await _viewModel.Manager.UpdateSettingsAsync(dialog.UpdatedSettings);
        }
    }

    private async void DownloadsGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel.SelectedItem == null) return;

        if (_viewModel.SelectedItem.Status == DownloadStatus.Completed)
            _viewModel.OpenSelectedFile();
        else if (_viewModel.SelectedItem.Status is DownloadStatus.Paused or DownloadStatus.Error)
            await _viewModel.ResumeSelectedAsync();
        else if (_viewModel.SelectedItem.Status == DownloadStatus.Downloading)
            await _viewModel.PauseSelectedAsync();
    }

    private void SidebarFilter_Changed(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || sender is not RadioButton rb || rb.IsChecked != true || rb.Tag is not string tag)
            return;

        if (Enum.TryParse<MacSidebarFilter>(tag, out var parsed))
        {
            _viewModel.ActiveFilter = parsed;
        }
    }
}
