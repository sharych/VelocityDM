using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia;
using VelocityDM.App.Helpers;
using VelocityDM.Core.Services;
using VelocityDM.Mac.Helpers;

namespace VelocityDM.Mac;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--register-browser", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                MacBrowserRegistrationService.ExtractExtensionFiles();
                MacBrowserRegistrationService.RegisterNativeMessagingHosts();
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        bool isNativeHost = args.Any(a =>
            a.Equals("--native-host", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase));

        if (isNativeHost)
        {
            return RunNativeMessagingHostAsync().GetAwaiter().GetResult();
        }

        bool startMinimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        string? addUrlArg = ExtractArgValue(args, "--add-url");
        string? dataDirArg = ExtractArgValue(args, "--data-dir");
        int? portArg = int.TryParse(ExtractArgValue(args, "--port"), out int p) ? p : null;

        if (string.IsNullOrWhiteSpace(addUrlArg) && args.Length == 1 && !args[0].StartsWith("--"))
        {
            addUrlArg = args[0];
        }

        using var ipc = new MacSingleInstanceIpc(dataDirArg);
        if (!ipc.TryAcquirePrimary())
        {
            var cmd = !string.IsNullOrWhiteSpace(addUrlArg)
                ? new BridgeCommand { Action = "download", Url = addUrlArg }
                : new BridgeCommand { Action = "show" };

            ipc.TrySendCommandAsync(cmd, 2500).GetAwaiter().GetResult();
            return 0;
        }

        App.Configure(ipc, startMinimized, addUrlArg, dataDirArg, portArg);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions
            {
                DisableDefaultApplicationMenuItems = false,
                ShowInDock = true
            })
            .LogToTrace();

    private static async Task<int> RunNativeMessagingHostAsync()
    {
        try
        {
            using Stream stdin = Console.OpenStandardInput();
            using Stream stdout = Console.OpenStandardOutput();

            var lengthBytes = new byte[4];
            int readLen = await ReadExactAsync(stdin, lengthBytes, 4).ConfigureAwait(false);
            if (readLen < 4)
            {
                await WriteNativeResponseAsync(stdout, new { ok = false, error = "Empty stdin" }).ConfigureAwait(false);
                return 0;
            }

            int msgLen = BitConverter.ToInt32(lengthBytes, 0);
            if (msgLen <= 0 || msgLen > 4 * 1024 * 1024)
            {
                await WriteNativeResponseAsync(stdout, new { ok = false, error = "Invalid message length" }).ConfigureAwait(false);
                return 0;
            }

            var payloadBytes = new byte[msgLen];
            await ReadExactAsync(stdin, payloadBytes, msgLen).ConfigureAwait(false);
            string json = Encoding.UTF8.GetString(payloadBytes);

            BridgeCommand? command;
            try
            {
                command = JsonSerializer.Deserialize<BridgeCommand>(json);
            }
            catch (JsonException)
            {
                command = null;
            }

            if (command == null)
            {
                await WriteNativeResponseAsync(stdout, new { ok = false, error = "Invalid JSON" }).ConfigureAwait(false);
                return 0;
            }

            if (string.Equals(command.Action, "download", StringComparison.OrdinalIgnoreCase) &&
                !ExternalDownloadBurstGuard.TryNormalizeSupportedUrl(command.Url, out _))
            {
                await WriteNativeResponseAsync(stdout, new { ok = false, error = "Invalid or missing URL" }).ConfigureAwait(false);
                return 0;
            }

            bool sent = await EnsurePrimaryAndSendCommandAsync(command).ConfigureAwait(false);
            await WriteNativeResponseAsync(stdout, new { ok = sent }).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                using Stream stdout = Console.OpenStandardOutput();
                await WriteNativeResponseAsync(stdout, new { ok = false, error = ex.Message }).ConfigureAwait(false);
            }
            catch
            {
                // Ignore
            }
            return 1;
        }
    }

    private static async Task<bool> EnsurePrimaryAndSendCommandAsync(BridgeCommand command)
    {
        if (await MacSingleInstanceIpc.TrySendCommandToDefaultAsync(command, 800).ConfigureAwait(false))
            return true;

        Directory.CreateDirectory(MacPaths.AppSupportDir);
        string launchLockPath = Path.Combine(MacPaths.AppSupportDir, "native-launch.lock");
        FileStream? lockStream = null;

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    lockStream = new FileStream(launchLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    break;
                }
                catch (IOException)
                {
                    if (await MacSingleInstanceIpc.TrySendCommandToDefaultAsync(command, 500).ConfigureAwait(false))
                        return true;
                    await Task.Delay(120).ConfigureAwait(false);
                }
            }

            if (await MacSingleInstanceIpc.TrySendCommandToDefaultAsync(command, 600).ConfigureAwait(false))
                return true;

            if (lockStream == null)
                return false;

            LaunchAppInBackground();

            for (int attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(200).ConfigureAwait(false);
                if (await MacSingleInstanceIpc.TrySendCommandToDefaultAsync(command, 600).ConfigureAwait(false))
                    return true;
            }

            return false;
        }
        finally
        {
            lockStream?.Dispose();
        }
    }

    private static void LaunchAppInBackground()
    {
        string? bundlePath = MacBrowserRegistrationService.TryGetAppBundlePath();
        if (!string.IsNullOrWhiteSpace(bundlePath) && Directory.Exists(bundlePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                ArgumentList = { "-g", "-a", bundlePath, "--args", "--minimized" },
                UseShellExecute = false
            });
            return;
        }

        string? exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                ArgumentList = { "--minimized" },
                UseShellExecute = false
            });
        }
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, count - offset)).ConfigureAwait(false);
            if (n == 0) break;
            offset += n;
        }
        return offset;
    }

    private static async Task WriteNativeResponseAsync(Stream stdout, object payload)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        byte[] lenPrefix = BitConverter.GetBytes(jsonBytes.Length);
        await stdout.WriteAsync(lenPrefix).ConfigureAwait(false);
        await stdout.WriteAsync(jsonBytes).ConfigureAwait(false);
        await stdout.FlushAsync().ConfigureAwait(false);
    }

    private static string? ExtractArgValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
