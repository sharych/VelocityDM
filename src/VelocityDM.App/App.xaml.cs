using System.IO;
using System.Windows;
using System.Windows.Threading;
using VelocityDM.App.Helpers;
using VelocityDM.App.ViewModels;
using VelocityDM.App.Views;
using VelocityDM.Core.Services;

namespace VelocityDM.App;

public partial class App : Application
{
    private SingleInstanceIpc? _ipc;
    private bool _startMinimized;
    private string? _initialAddUrl;
    private string? _dataDirOverride;
    private int? _portOverride;

    private DownloadManagerService? _downloadManager;
    private TorrentManagerService? _torrentManager;
    private MainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private TrayIconService? _trayIcon;
    private BrowserBridgeServer? _bridgeServer;
    private DispatcherTimer? _trayTooltipTimer;

    private readonly ExternalDownloadBurstGuard _externalBurstGuard = new();
    private readonly SemaphoreSlim _externalPopupGate = new(1, 1);
    private readonly CancellationTokenSource _exitCts = new();
    private bool _isExiting;

    public App()
    {
    }

    public void Configure(
        SingleInstanceIpc ipc,
        bool startMinimized = false,
        string? initialAddUrl = null,
        string? dataDirOverride = null,
        int? portOverride = null)
    {
        _ipc = ipc;
        _startMinimized = startMinimized;
        _initialAddUrl = initialAddUrl;
        _dataDirOverride = dataDirOverride;
        _portOverride = portOverride;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            var store = new JsonStateStore(_dataDirOverride);
            _downloadManager = new DownloadManagerService(store);
            _torrentManager = new TorrentManagerService(store, useEphemeralPorts: !string.IsNullOrWhiteSpace(_dataDirOverride));
            _mainViewModel = new MainViewModel(_downloadManager, _torrentManager);
            _mainWindow = new MainWindow(_mainViewModel);
            MainWindow = _mainWindow;

            SetupTrayIcon();

            if (_ipc != null)
            {
                _ipc.CommandReceived += DispatchExternalCommand;
                _ipc.StartListening();
            }

            _bridgeServer = new BrowserBridgeServer
            {
                CommandResponder = cmd => _downloadManager != null
                    ? BridgeAckFactory.Build(cmd, _downloadManager.Settings)
                    : new { ok = true, app = "VelocityDM" }
            };
            _bridgeServer.CommandReceived += DispatchExternalCommand;

            await _mainViewModel.InitializeAsync();
            var settings = _downloadManager.Settings;
            _bridgeServer.Start(_portOverride ?? settings.BrowserBridgePort);
            _trayIcon?.UpdateClipboardChecked(settings.ClipboardMonitoringEnabled);

            _downloadManager.SettingsUpdated += updated =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _trayIcon?.UpdateClipboardChecked(updated.ClipboardMonitoringEnabled);
                    _bridgeServer?.Start(updated.BrowserBridgePort);
                });
            };

            // Extract bundled Chrome extension & ensure Native Host path stays current if already registered
            BrowserRegistrationService.ExtractExtensionFiles();
            if (BrowserRegistrationService.IsNativeHostRegistered())
            {
                BrowserRegistrationService.RegisterNativeMessagingHost();
            }

            bool hideOnLaunch = _startMinimized || settings.StartMinimizedToTray;
            if (!hideOnLaunch)
            {
                _mainWindow.Show();
            }

            if (!string.IsNullOrWhiteSpace(_initialAddUrl))
            {
                await HandleExternalCommandAsync(new BridgeCommand { Action = "download", Url = _initialAddUrl });
            }

            _trayTooltipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _trayTooltipTimer.Tick += (_, _) => UpdateTrayTooltip();
            _trayTooltipTimer.Start();
        }
        catch (Exception ex)
        {
            LogFatalError("Startup error", ex);
            MessageBox.Show($"Failed to start VelocityDM:\n{ex.Message}", "VelocityDM Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new TrayIconService();
        _trayIcon.ShowRequested += () => Dispatcher.BeginInvoke(() => _mainWindow?.RestoreFromTray());
        _trayIcon.AddUrlRequested += () => Dispatcher.BeginInvoke(async () =>
        {
            _mainWindow?.RestoreFromTray();
            if (_mainWindow != null) await _mainWindow.ShowAddUrlDialogAsync();
        });
        _trayIcon.AddTorrentRequested += () => Dispatcher.BeginInvoke(async () =>
        {
            _mainWindow?.RestoreFromTray();
            if (_mainWindow != null) await _mainWindow.ShowAddTorrentDialogAsync();
        });
        _trayIcon.ResumeAllRequested += () => Dispatcher.BeginInvoke(async () =>
        {
            if (_downloadManager != null) await _downloadManager.ResumeAllAsync();
        });
        _trayIcon.PauseAllRequested += () => Dispatcher.BeginInvoke(async () =>
        {
            if (_downloadManager != null) await _downloadManager.PauseAllAsync();
        });
        _trayIcon.ClipboardToggleRequested += enabled => Dispatcher.BeginInvoke(() =>
        {
            if (_mainViewModel != null) _mainViewModel.ClipboardMonitorEnabled = enabled;
        });
        _trayIcon.ExitRequested += () => Dispatcher.BeginInvoke(ExplicitExit);
    }

    private void DispatchExternalCommand(BridgeCommand cmd)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await HandleExternalCommandAsync(cmd);
            }
            catch (Exception ex)
            {
                LogFatalError("External command error", ex);
            }
        });
    }

    private async Task HandleExternalCommandAsync(BridgeCommand cmd)
    {
        if (_isExiting || _mainWindow == null || _downloadManager == null || _torrentManager == null)
            return;

        string action = (cmd.Action ?? "show").ToLowerInvariant();
        if (action == "show")
        {
            _mainWindow.RestoreFromTray();
            return;
        }

        if (action == "exit")
        {
            ExplicitExit();
            return;
        }

        if (!_externalBurstGuard.TryAcceptUrl(cmd.Url, out string url))
        {
            return;
        }

        bool isTorrentFile = File.Exists(url) && url.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
        bool isMagnet = url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase);
        bool startNow = !isTorrentFile && (cmd.StartImmediately ?? _downloadManager.Settings.AutoStartBrowserDownloads);

        if (startNow)
        {
            if (isMagnet)
            {
                await _torrentManager.AddMagnetAsync(url);
                _trayIcon?.ShowBalloon("VelocityDM", "Torrent added from browser.");
            }
            else
            {
                var created = await _downloadManager.AddDownloadAsync(new AddDownloadOptions(
                    Url: url,
                    CustomFileName: string.IsNullOrWhiteSpace(cmd.Filename) ? null : cmd.Filename,
                    StartImmediately: true,
                    Referrer: cmd.Referrer,
                    CookieHeader: cmd.Cookies,
                    UserAgentOverride: cmd.UserAgent));
                _trayIcon?.ShowBalloon("VelocityDM", $"Started downloading {created.FileName}");
            }
            return;
        }

        if (!_externalBurstGuard.TryAcquirePopupSlot())
        {
            _trayIcon?.ShowBalloon("VelocityDM", "Ignored excess external download popups.");
            return;
        }

        bool gateAcquired = false;
        try
        {
            await _externalPopupGate.WaitAsync(_exitCts.Token);
            gateAcquired = true;

            if (_isExiting)
                return;

            if (isTorrentFile || isMagnet)
            {
                _mainWindow.RestoreFromTray();
                await _mainWindow.ShowAddTorrentDialogAsync(url);
                return;
            }

            await _mainWindow.ShowAddUrlDialogAsync(
                prefillUrl: url,
                customFileName: string.IsNullOrWhiteSpace(cmd.Filename) ? null : cmd.Filename,
                referrer: cmd.Referrer,
                cookies: cmd.Cookies,
                userAgent: cmd.UserAgent,
                bringToFront: true);
        }
        catch (OperationCanceledException)
        {
            // App is shutting down
        }
        finally
        {
            if (gateAcquired)
            {
                _externalPopupGate.Release();
            }
            _externalBurstGuard.ReleasePopupSlot();
        }
    }

    private void UpdateTrayTooltip()
    {
        if (_trayIcon == null || _mainViewModel == null)
            return;

        var (active, speed) = _mainViewModel.GetBackgroundSummary();
        string tooltip = active > 0
            ? $"VelocityDM — {active} active ({ByteFormatter.FormatSpeed(speed)})"
            : "VelocityDM — Running in background";
        _trayIcon.UpdateTooltip(tooltip);
    }

    private void ExplicitExit()
    {
        _isExiting = true;
        _exitCts.Cancel();
        _trayTooltipTimer?.Stop();
        if (_mainWindow != null)
        {
            _mainWindow.AllowExplicitClose = true;
            _mainWindow.Close();
        }
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isExiting = true;
        _exitCts.Cancel();
        _trayTooltipTimer?.Stop();
        _trayIcon?.Dispose();
        _bridgeServer?.Stop();

        if (_torrentManager != null)
        {
            try { Task.Run(() => _torrentManager.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(4)); } catch { }
        }
        if (_downloadManager != null)
        {
            try { Task.Run(() => _downloadManager.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(4)); } catch { }
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogFatalError("Unhandled UI exception", e.Exception);
        MessageBox.Show($"Unexpected error:\n{e.Exception.Message}", "VelocityDM", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void LogFatalError(string context, Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VelocityDM");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "error.log"),
                $"[{DateTimeOffset.Now:O}] {context}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Ignore
        }
    }
}
