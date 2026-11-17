using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using VelocityDM.App.Helpers;

namespace VelocityDM.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool isNativeHost = args.Any(a =>
            a.Equals("--native-host", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase));

        if (isNativeHost)
        {
            return RunNativeMessagingHostAsync().GetAwaiter().GetResult();
        }

        bool startMinimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        string? addUrlArg = ExtractArgValue(args, "--add-url");

        using var ipc = new SingleInstanceIpc();
        if (!ipc.TryAcquirePrimary())
        {
            var cmd = !string.IsNullOrWhiteSpace(addUrlArg)
                ? new BridgeCommand { Action = "download", Url = addUrlArg }
                : new BridgeCommand { Action = "show" };

            SingleInstanceIpc.TrySendCommandAsync(cmd, 2500).GetAwaiter().GetResult();
            return 0;
        }

        var app = new App();
        app.Configure(ipc, startMinimized, addUrlArg);
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

            bool sent = await SingleInstanceIpc.TrySendCommandAsync(command, 800).ConfigureAwait(false);
            if (!sent)
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "--minimized",
                        UseShellExecute = false
                    });

                    for (int attempt = 0; attempt < 25; attempt++)
                    {
                        await Task.Delay(200).ConfigureAwait(false);
                        sent = await SingleInstanceIpc.TrySendCommandAsync(command, 600).ConfigureAwait(false);
                        if (sent) break;
                    }
                }
            }

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
