using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VelocityDM.App.Helpers;
using VelocityDM.Core.Services;
using VelocityDM.Mac.Helpers;

namespace VelocityDM.Mac.Views;

public partial class UpdateWindow : Window
{
    private readonly GitHubUpdateService _updateService;
    private readonly DownloadManagerService _downloadManager;
    private readonly Action _initiateRestartExit;
    private readonly UpdateCheckResult? _initialResult;
    private readonly bool _bringToFront;

    private UpdateCheckResult? _currentResult;
    private CancellationTokenSource? _activeCts;
    private bool _isDownloadingOrApplying;

    public UpdateWindow()
        : this(new GitHubUpdateService(), new DownloadManagerService(), () => { })
    {
    }

    public UpdateWindow(
        GitHubUpdateService updateService,
        DownloadManagerService downloadManager,
        Action initiateRestartExit,
        UpdateCheckResult? initialResult = null,
        bool bringToFront = false)
    {
        _updateService = updateService;
        _downloadManager = downloadManager;
        _initiateRestartExit = initiateRestartExit;
        _initialResult = initialResult;
        _bringToFront = bringToFront;

        InitializeComponent();

        Opened += async (_, _) =>
        {
            if (_bringToFront)
            {
                Activate();
                Topmost = true;
                Topmost = false;
            }

            if (_initialResult != null)
            {
                ApplyCheckResult(_initialResult);
            }
            else
            {
                await CheckNowAsync();
            }
        };

        Closed += (_, _) =>
        {
            _activeCts?.Cancel();
            _activeCts?.Dispose();
            _activeCts = null;
        };
    }

    private async Task CheckNowAsync()
    {
        _activeCts?.Cancel();
        _activeCts?.Dispose();
        _activeCts = new CancellationTokenSource();

        HeadlineText.Text = "Checking for VelocityDM updates...";
        SubheadlineText.Text = $"Installed version: v{GitHubUpdateService.FormatShortVersion(GitHubUpdateService.GetCurrentVersion())}";
        PackageTitleText.Text = "GitHub Releases";
        PackageDetailsText.Text = "Checking https://github.com/sharych/VelocityDM/releases/latest...";
        ChromeHintText.IsVisible = false;
        ReleaseNotesTextBox.Text = string.Empty;

        ProgressPanel.IsVisible = true;
        UpdateProgressBar.IsIndeterminate = true;
        UpdateProgressBar.Value = 0;
        StatusText.Text = "Contacting GitHub...";

        InstallUpdateButton.IsVisible = false;
        SkipVersionButton.IsVisible = false;
        OpenGitHubButton.IsVisible = false;
        RecheckButton.IsVisible = false;
        CloseOrCancelButton.Content = "Close";

        try
        {
            var result = await _updateService.CheckForUpdateAsync(cancellationToken: _activeCts.Token);
            ApplyCheckResult(result);
        }
        catch (OperationCanceledException)
        {
            // Dialog closed or canceled
        }
        catch (Exception ex)
        {
            HeadlineText.Text = "Update check failed";
            PackageTitleText.Text = "Could not reach GitHub Releases";
            PackageDetailsText.Text = ex.Message;
            UpdateProgressBar.IsIndeterminate = false;
            ProgressPanel.IsVisible = false;
            RecheckButton.IsVisible = true;
            OpenGitHubButton.IsVisible = true;
        }
    }

    private void ApplyCheckResult(UpdateCheckResult result)
    {
        _currentResult = result;
        ProgressPanel.IsVisible = false;
        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = 0;

        string currentStr = $"v{GitHubUpdateService.FormatShortVersion(result.CurrentVersion)}";
        if (result.Release == null)
        {
            HeadlineText.Text = "No compatible update found";
            SubheadlineText.Text = $"Installed version: {currentStr}";
            PackageTitleText.Text = "Release Status";
            PackageDetailsText.Text = result.Message;
            ReleaseNotesTextBox.Text = string.Empty;
            ChromeHintText.IsVisible = false;
            InstallUpdateButton.IsVisible = false;
            SkipVersionButton.IsVisible = false;
            RecheckButton.IsVisible = true;
            OpenGitHubButton.IsVisible = true;
            CloseOrCancelButton.Content = "Close";
            return;
        }

        var rel = result.Release;
        string published = rel.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "Unknown date";
        ReleaseNotesTextBox.Text = string.IsNullOrWhiteSpace(rel.Body)
            ? $"Release {rel.Name} ({rel.TagName}) published {published}."
            : rel.Body;

        OpenGitHubButton.IsVisible = true;

        if (result.UpdateAvailable)
        {
            HeadlineText.Text = $"Update Available: {rel.Name}";
            SubheadlineText.Text = $"Installed: {currentStr}   →   New: {rel.TagName} (published {published})";
            PackageTitleText.Text = $"Package: {rel.SelectedAsset.Name} ({ByteFormatter.FormatBytes(rel.SelectedAsset.SizeBytes)})";
            PackageDetailsText.Text = "Verified via SHA-256 checksum before installing into VelocityDM.app.";
            ChromeHintText.IsVisible = true;

            InstallUpdateButton.IsVisible = true;
            InstallUpdateButton.IsEnabled = true;
            SkipVersionButton.IsVisible = true;
            SkipVersionButton.IsEnabled = true;
            RecheckButton.IsVisible = false;
            CloseOrCancelButton.Content = "Later";
        }
        else
        {
            HeadlineText.Text = "VelocityDM is up to date";
            SubheadlineText.Text = $"Installed: {currentStr}   •   Latest GitHub release: {rel.TagName} ({published})";
            PackageTitleText.Text = "Up to date";
            PackageDetailsText.Text = "You are already running the latest release of VelocityDM.";
            ChromeHintText.IsVisible = false;

            InstallUpdateButton.IsVisible = false;
            SkipVersionButton.IsVisible = false;
            RecheckButton.IsVisible = true;
            CloseOrCancelButton.Content = "Close";
        }
    }

    private async void InstallUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (_isDownloadingOrApplying || _currentResult?.Release == null)
            return;

        var rel = _currentResult.Release;
        _isDownloadingOrApplying = true;
        _activeCts?.Cancel();
        _activeCts?.Dispose();
        _activeCts = new CancellationTokenSource();

        InstallUpdateButton.IsEnabled = false;
        SkipVersionButton.IsEnabled = false;
        OpenGitHubButton.IsEnabled = false;
        CloseOrCancelButton.Content = "Cancel Download";

        ProgressPanel.IsVisible = true;
        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = 0;
        StatusText.Text = $"Preparing download for {rel.SelectedAsset.Name}...";

        try
        {
            var progress = new Progress<UpdateProgress>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (p.Stage == "Verifying")
                    {
                        UpdateProgressBar.IsIndeterminate = true;
                        StatusText.Text = p.Message;
                        return;
                    }

                    UpdateProgressBar.IsIndeterminate = false;
                    if (p.TotalBytes is > 0)
                    {
                        double pct = Math.Clamp(p.BytesReceived * 100.0 / p.TotalBytes.Value, 0, 100);
                        UpdateProgressBar.Value = pct;
                        StatusText.Text = $"{p.Message}  {pct:0.#}% ({ByteFormatter.FormatBytes(p.BytesReceived)} / {ByteFormatter.FormatBytes(p.TotalBytes.Value)})";
                    }
                    else
                    {
                        StatusText.Text = $"{p.Message}  ({ByteFormatter.FormatBytes(p.BytesReceived)})";
                    }
                });
            });

            var prepared = await _updateService.DownloadAndVerifyUpdateAsync(rel, progress, _activeCts.Token);

            StatusText.Text = $"SHA-256 verified ({prepared.ActualSha256[..12]}...). Applying update and restarting...";
            UpdateProgressBar.IsIndeterminate = true;

            string? appBundle = MacBrowserRegistrationService.TryGetAppBundlePath();
            await SelfUpdateLauncher.ApplyUpdateAndScheduleRestartAsync(
                prepared,
                macAppBundlePath: appBundle,
                cancellationToken: _activeCts.Token);

            // Clear any prior skip preference once user actively updates
            if (!string.IsNullOrWhiteSpace(_downloadManager.Settings.SkippedUpdateVersion))
            {
                var updated = _downloadManager.Settings.Clone();
                updated.SkippedUpdateVersion = null;
                await _downloadManager.UpdateSettingsAsync(updated);
            }

            Close();
            Dispatcher.UIThread.Post(_initiateRestartExit);
        }
        catch (OperationCanceledException)
        {
            _isDownloadingOrApplying = false;
            ResetButtonsAfterFailure("Update download cancelled.");
        }
        catch (Exception ex)
        {
            _isDownloadingOrApplying = false;
            ResetButtonsAfterFailure($"Update failed: {ex.Message}");
        }
    }

    private void ResetButtonsAfterFailure(string message)
    {
        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = 0;
        StatusText.Text = message;
        InstallUpdateButton.IsEnabled = true;
        SkipVersionButton.IsEnabled = true;
        OpenGitHubButton.IsEnabled = true;
        CloseOrCancelButton.Content = "Later";
    }

    private async void SkipVersion_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentResult?.Release != null)
        {
            var updated = _downloadManager.Settings.Clone();
            updated.SkippedUpdateVersion = _currentResult.Release.TagName;
            await _downloadManager.UpdateSettingsAsync(updated);
        }
        Close();
    }

    private async void Recheck_Click(object? sender, RoutedEventArgs e) =>
        await CheckNowAsync();

    private void OpenGitHub_Click(object? sender, RoutedEventArgs e)
    {
        string url = _currentResult?.Release?.HtmlUrl
            ?? $"https://github.com/{GitHubUpdateService.DefaultOwner}/{GitHubUpdateService.DefaultRepo}/releases";
        if (!GitHubUpdateService.IsAllowedGitHubDownloadUrl(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo("open", url) { CreateNoWindow = true });
        }
        catch
        {
            // Ignore browser open error
        }
    }

    private void CloseOrCancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_isDownloadingOrApplying)
        {
            _activeCts?.Cancel();
            return;
        }
        Close();
    }
}
