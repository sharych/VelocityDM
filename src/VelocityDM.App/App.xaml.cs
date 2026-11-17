using System.IO;
using System.Windows;
using System.Windows.Threading;
using VelocityDM.App.ViewModels;
using VelocityDM.App.Views;
using VelocityDM.Core.Services;

namespace VelocityDM.App;

public partial class App : Application
{
    private DownloadManagerService? _downloadManager;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            _downloadManager = new DownloadManagerService();
            var mainVm = new MainViewModel(_downloadManager);
            var window = new MainWindow(mainVm);
            MainWindow = window;
            window.Show();

            await mainVm.InitializeAsync();
        }
        catch (Exception ex)
        {
            LogFatalError("Startup error", ex);
            MessageBox.Show($"Failed to start VelocityDM:\n{ex.Message}", "VelocityDM Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_downloadManager != null)
        {
            try
            {
                Task.Run(() => _downloadManager.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Best-effort flush on exit
            }
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
            // Ignore logging failures
        }
    }
}
