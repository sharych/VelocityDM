using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using VelocityDM.App.Helpers;
using VelocityDM.Core.Services;

namespace VelocityDM.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--register-browser", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                BrowserRegistrationService.ExtractExtensionFiles();
                BrowserRegistrationService.RegisterNativeMessagingHost();
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

        using var ipc = new SingleInstanceIpc(dataDirArg);
        if (!ipc.TryAcquirePrimary())
        {
            var cmd = !string.IsNullOrWhiteSpace(addUrlArg)
                ? new BridgeCommand { Action = "download", Url = addUrlArg }
                : new BridgeCommand { Action = "show" };

            ipc.TrySendCommandAsync(cmd, 2500).GetAwaiter().GetResult();
            return 0;
        }

        var app = new App();
        app.Configure(ipc, startMinimized, addUrlArg, dataDirArg, portArg);
        app.InitializeComponent();
        return app.Run();
    }

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

            var command = JsonSerializer.Deserialize<BridgeCommand>(json);
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

            bool sent = await Task.Run(() => EnsurePrimaryAndSendCommand(command)).ConfigureAwait(false);
            object responsePayload;
            if (sent)
            {
                var settings = await new JsonStateStore().LoadSettingsAsync().ConfigureAwait(false);
                responsePayload = BridgeAckFactory.Build(command, settings, ok: true);
            }
            else
            {
                responsePayload = new { ok = false };
            }

            await WriteNativeResponseAsync(stdout, responsePayload).ConfigureAwait(false);
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

    private static bool EnsurePrimaryAndSendCommand(BridgeCommand command)
    {
        if (SingleInstanceIpc.TrySendCommandToDefaultAsync(command, 800).GetAwaiter().GetResult())
            return true;

        string userScope = SingleInstanceIpc.SanitizeIdentifier(Environment.UserName);
        using var launchMutex = new Mutex(initiallyOwned: false, $@"Local\VelocityDM_NativeHost_Launch_{userScope}");
        bool acquired = false;

        try
        {
            try
            {
                acquired = launchMutex.WaitOne(TimeSpan.FromSeconds(8));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (SingleInstanceIpc.TrySendCommandToDefaultAsync(command, 600).GetAwaiter().GetResult())
                return true;

            if (!acquired)
                return false;

            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--minimized",
                UseShellExecute = false
            });

            for (int attempt = 0; attempt < 25; attempt++)
            {
                Thread.Sleep(200);
                if (SingleInstanceIpc.TrySendCommandToDefaultAsync(command, 600).GetAwaiter().GetResult())
                    return true;
            }

            return false;
        }
        finally
        {
            if (acquired)
            {
                try { launchMutex.ReleaseMutex(); } catch { }
            }
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
