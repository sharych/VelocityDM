using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using VelocityDM.App.Helpers;
using VelocityDM.Core.Services;
using VelocityDM.Mac.Helpers;
using VelocityDM.Mac.ViewModels;
using VelocityDM.Mac.Views;

namespace VelocityDM.Mac;

public partial class App : Application
{
    private static MacSingleInstanceIpc? _ipc;
    private static bool _startMinimized;
    private static string? _initialAddUrl;
    private static string? _dataDirOverride;
    private static int? _portOverride;

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private DownloadManagerService? _downloadManager;
    private MacMainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private BrowserBridgeServer? _bridgeServer;
    private TrayIcon? _trayIcon;
    private DispatcherTimer? _trayTooltipTimer;
    private readonly GitHubUpdateService _updateService = new();

    private readonly ExternalDownloadBurstGuard _externalBurstGuard = new();
    private readonly SemaphoreSlim _externalPopupGate = new(1, 1);
    private readonly CancellationTokenSource _exitCts = new();
    private bool _isExiting;

    public static void Configure(
        MacSingleInstanceIpc ipc,
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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        _desktop = desktop;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.Exit += OnDesktopExit;

        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, args) =>
            {
                if (args.Kind == ActivationKind.Reopen)
                {
                    Dispatcher.UIThread.Post(() => _mainWindow?.RestoreFromTray());
                }
            };
        }

        try
        {
            string stateDir = string.IsNullOrWhiteSpace(_dataDirOverride) ? MacPaths.AppSupportDir : _dataDirOverride;
            var store = new JsonStateStore(stateDir);
            _downloadManager = new DownloadManagerService(store);
            _mainViewModel = new MacMainViewModel(_downloadManager);
            _mainWindow = new MainWindow(_mainViewModel);
            _mainWindow.ConfigureUpdater(_updateService, ExplicitExit);

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

            _downloadManager.SettingsUpdated += updated =>
            {
                Dispatcher.UIThread.Post(() => _bridgeServer?.Start(updated.BrowserBridgePort));
            };

            // Extract bundled Chrome extension & ensure macOS Chrome Native Messaging Host is registered
            MacBrowserRegistrationService.ExtractExtensionFiles();
            MacBrowserRegistrationService.RegisterNativeMessagingHosts();

            bool hideOnLaunch = _startMinimized || settings.StartMinimizedToTray;
            if (!hideOnLaunch)
            {
                desktop.MainWindow = _mainWindow;
                _mainWindow.Show();
            }

            if (!string.IsNullOrWhiteSpace(_initialAddUrl))
            {
                await HandleExternalCommandAsync(new BridgeCommand { Action = "download", Url = _initialAddUrl });
            }

            _trayTooltipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _trayTooltipTimer.Tick += (_, _) => UpdateTrayTooltip();
            _trayTooltipTimer.Start();

            if (string.IsNullOrWhiteSpace(_dataDirOverride))
            {
                _ = _updateService.MonitorForUpdatesAsync(
                    () => _downloadManager.Settings,
                    result => Dispatcher.UIThread.Post(async () =>
                    {
                        if (_isExiting || _mainWindow == null) return;
                        _mainWindow.SetAvailableUpdateBadge(result.Release);
                        await _mainWindow.ShowUpdateDialogAsync(result, bringToFront: true);
                    }),
                    _exitCts.Token);
            }
        }
        catch (Exception ex)
        {
            LogFatalError("Startup error", ex);
            Environment.ExitCode = 1;
            desktop.Shutdown(1);
        }
    }

    private void SetupTrayIcon()
    {
        WindowIcon? icon = null;
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://VelocityDM/Assets/VelocityDM.png"));
            icon = new WindowIcon(stream);
        }
        catch
        {
            // Fallback without custom tray icon
        }

        var openItem = new NativeMenuItem("Open VelocityDM");
        openItem.Click += (_, _) => Dispatcher.UIThread.Post(() => _mainWindow?.RestoreFromTray());

        var addUrlItem = new NativeMenuItem("+ Add Download URL...");
        addUrlItem.Click += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            if (_mainWindow == null) return;
            _mainWindow.RestoreFromTray();
            await _mainWindow.ShowAddUrlDialogAsync();
        });

        var resumeAllItem = new NativeMenuItem("Resume All Downloads");
        resumeAllItem.Click += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            if (_downloadManager != null) await _downloadManager.ResumeAllAsync();
        });

        var pauseAllItem = new NativeMenuItem("Pause All Downloads");
        pauseAllItem.Click += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            if (_downloadManager != null) await _downloadManager.PauseAllAsync();
        });

        var chromeExtItem = new NativeMenuItem("Chrome Extension Setup...");
        chromeExtItem.Click += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            if (_mainWindow == null) return;
            _mainWindow.RestoreFromTray();
            await _mainWindow.ShowChromeExtensionDialogAsync();
        });

        var updatesItem = new NativeMenuItem("Check for Updates...");
        updatesItem.Click += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            if (_mainWindow == null) return;
            _mainWindow.RestoreFromTray();
            await _mainWindow.ShowUpdateDialogAsync();
        });

        var exitItem = new NativeMenuItem("Exit VelocityDM");
        exitItem.Click += (_, _) => Dispatcher.UIThread.Post(ExplicitExit);

        var menu = new NativeMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(addUrlItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(resumeAllItem);
        menu.Items.Add(pauseAllItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(chromeExtItem);
        menu.Items.Add(updatesItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "VelocityDM — Background Download Manager",
            IsVisible = true,
            Menu = menu
        };
        _trayIcon.Clicked += (_, _) => Dispatcher.UIThread.Post(() => _mainWindow?.RestoreFromTray());
    }

    private void DispatchExternalCommand(BridgeCommand cmd)
    {
        Dispatcher.UIThread.Post(async () =>
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
        if (_isExiting || _mainWindow == null || _downloadManager == null)
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
            return;

        bool isTorrentOrMagnet = url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);

        if (isTorrentOrMagnet)
        {
            if (_mainViewModel != null)
            {
                _mainViewModel.StatusMessage = "BitTorrent (.torrent / magnet) requires Windows native libtorrent; HTTP downloads are active on macOS.";
            }
            return;
        }

        bool startNow = cmd.StartImmediately ?? _downloadManager.Settings.AutoStartBrowserDownloads;
        if (startNow)
        {
            await _downloadManager.AddDownloadAsync(new AddDownloadOptions(
                Url: url,
                CustomFileName: string.IsNullOrWhiteSpace(cmd.Filename) ? null : cmd.Filename,
                StartImmediately: true,
                Referrer: cmd.Referrer,
                CookieHeader: cmd.Cookies,
                UserAgentOverride: cmd.UserAgent));
            return;
        }

        if (!_externalBurstGuard.TryAcquirePopupSlot())
        {
            if (_mainViewModel != null)
            {
                _mainViewModel.StatusMessage = "Ignored excess external download popups.";
            }
            return;
        }

        bool gateAcquired = false;
        try
        {
            await _externalPopupGate.WaitAsync(_exitCts.Token);
            gateAcquired = true;

            if (_isExiting)
                return;

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
        _trayIcon.ToolTipText = active > 0
            ? $"VelocityDM — {active} active ({ByteFormatter.FormatSpeed(speed)})"
            : "VelocityDM — Running in background";
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

        _desktop?.Shutdown(0);
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _isExiting = true;
        _exitCts.Cancel();
        _trayTooltipTimer?.Stop();
        _trayIcon?.Dispose();
        _bridgeServer?.Stop();
        _updateService.Dispose();

        if (_downloadManager != null)
        {
            try { Task.Run(() => _downloadManager.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(4)); } catch { }
        }
    }

    private static void LogFatalError(string context, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(MacPaths.AppSupportDir);
            File.AppendAllText(
                Path.Combine(MacPaths.AppSupportDir, "error.log"),
                $"[{DateTimeOffset.Now:O}] {context}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Ignore
        }
    }
}
