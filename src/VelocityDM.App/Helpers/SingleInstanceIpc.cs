using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VelocityDM.App.Helpers;

/// <summary>
/// Enforces single-instance background execution and forwards commands between launches via Named Pipes.
/// </summary>
public sealed class SingleInstanceIpc : IDisposable
{
    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public SingleInstanceIpc(string? profileScope = null)
    {
        string userScope = SanitizeIdentifier(Environment.UserName);
        string scopeSuffix = string.IsNullOrWhiteSpace(profileScope)
            ? userScope
            : $"{userScope}_{ShortHash(profileScope)}";

        _mutexName = $@"Local\VelocityDM_SingleInstance_v2_{scopeSuffix}";
        _pipeName = $"VelocityDM_IPC_v2_{scopeSuffix}";
    }

    public event Action<BridgeCommand>? CommandReceived;

    public bool TryAcquirePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, _mutexName, out bool createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        return true;
    }

    public void StartListening()
    {
        if (_listenTask != null)
            return;

        _cts = new CancellationTokenSource();
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public Task<bool> TrySendCommandAsync(BridgeCommand command, int timeoutMs = 2000) =>
        TrySendCommandToPipeAsync(_pipeName, command, timeoutMs);

    public static async Task<bool> TrySendCommandToDefaultAsync(BridgeCommand command, int timeoutMs = 2000)
    {
        string defaultPipe = $"VelocityDM_IPC_v2_{SanitizeIdentifier(Environment.UserName)}";
        return await TrySendCommandToPipeAsync(defaultPipe, command, timeoutMs).ConfigureAwait(false);
    }

    private static async Task<bool> TrySendCommandToPipeAsync(string pipeName, BridgeCommand command, int timeoutMs)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeoutMs).ConfigureAwait(false);

            string json = JsonSerializer.Serialize(command);
            byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
            await client.WriteAsync(bytes).ConfigureAwait(false);
            await client.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var cmd = JsonSerializer.Deserialize<BridgeCommand>(line);
                    if (cmd != null)
                    {
                        CommandReceived?.Invoke(cmd);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
    }

    internal static string SanitizeIdentifier(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return chars.Length == 0 ? "user" : new string(chars);
    }

    private static string ShortHash(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(hash)[..10];
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
            _mutex = null;
        }
    }
}
