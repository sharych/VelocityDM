using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace VelocityDM.App.Helpers;

/// <summary>
/// Enforces single-instance background execution and forwards commands between launches via Named Pipes.
/// </summary>
public sealed class SingleInstanceIpc : IDisposable
{
    private static readonly string SafeUser = SanitizeIdentifier(Environment.UserName);
    public static readonly string MutexName = $@"Local\VelocityDM_SingleInstance_v2_{SafeUser}";
    public static readonly string PipeName = $"VelocityDM_IPC_v2_{SafeUser}";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public event Action<BridgeCommand>? CommandReceived;

    public bool TryAcquirePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
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

    public static async Task<bool> TrySendCommandAsync(BridgeCommand command, int timeoutMs = 2000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
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
                    PipeName,
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

    private static string SanitizeIdentifier(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return chars.Length == 0 ? "user" : new string(chars);
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
