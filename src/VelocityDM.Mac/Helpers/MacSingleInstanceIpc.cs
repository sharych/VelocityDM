using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VelocityDM.App.Helpers;

namespace VelocityDM.Mac.Helpers;

/// <summary>
/// Enforces single-instance execution on macOS via an exclusive OS file lock
/// and forwards external commands over a Unix Domain Socket.
/// </summary>
public sealed class MacSingleInstanceIpc : IDisposable
{
    private readonly string _lockPath;
    private readonly string _socketPath;
    private FileStream? _lockStream;
    private Socket? _serverSocket;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public MacSingleInstanceIpc(string? dataDir = null)
    {
        string baseDir = string.IsNullOrWhiteSpace(dataDir) ? MacPaths.AppSupportDir : dataDir;
        Directory.CreateDirectory(baseDir);

        _lockPath = Path.Combine(baseDir, "instance.lock");
        string candidateSocket = Path.Combine(baseDir, "ipc.sock");
        if (Encoding.UTF8.GetByteCount(candidateSocket) > 95)
        {
            candidateSocket = Path.Combine("/tmp", $"vdm_{ShortHash(baseDir)}.sock");
        }
        _socketPath = candidateSocket;
    }

    public event Action<BridgeCommand>? CommandReceived;

    public bool TryAcquirePrimary()
    {
        try
        {
            _lockStream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch
        {
            _lockStream?.Dispose();
            _lockStream = null;
            return false;
        }
    }

    public void StartListening()
    {
        if (_listenTask != null)
            return;

        if (File.Exists(_socketPath))
        {
            try { File.Delete(_socketPath); } catch { }
        }

        _cts = new CancellationTokenSource();
        _serverSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _serverSocket.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _serverSocket.Listen(32);

        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public Task<bool> TrySendCommandAsync(BridgeCommand command, int timeoutMs = 2000) =>
        TrySendToSocketAsync(_socketPath, command, timeoutMs);

    public static Task<bool> TrySendCommandToDefaultAsync(BridgeCommand command, int timeoutMs = 2000)
    {
        string defaultDir = MacPaths.AppSupportDir;
        string candidate = Path.Combine(defaultDir, "ipc.sock");
        if (Encoding.UTF8.GetByteCount(candidate) > 95)
        {
            candidate = Path.Combine("/tmp", $"vdm_{ShortHash(defaultDir)}.sock");
        }
        return TrySendToSocketAsync(candidate, command, timeoutMs);
    }

    private static async Task<bool> TrySendToSocketAsync(string socketPath, BridgeCommand command, int timeoutMs)
    {
        if (!File.Exists(socketPath))
            return false;

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var cts = new CancellationTokenSource(timeoutMs);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cts.Token).ConfigureAwait(false);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command) + "\n");
            await stream.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _serverSocket != null)
        {
            try
            {
                using var client = await _serverSocket.AcceptAsync(ct).ConfigureAwait(false);
                await using var stream = new NetworkStream(client, ownsSocket: false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
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
                if (ct.IsCancellationRequested) break;
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
    }

    private static string ShortHash(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _serverSocket?.Dispose(); } catch { }
        try { if (File.Exists(_socketPath)) File.Delete(_socketPath); } catch { }
        _cts?.Dispose();
        _lockStream?.Dispose();
        _lockStream = null;
    }
}
