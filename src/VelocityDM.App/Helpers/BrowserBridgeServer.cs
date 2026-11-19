using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VelocityDM.Core.Services;

namespace VelocityDM.App.Helpers;

/// <summary>
/// Loopback-only HTTP server allowing the Chrome extension to talk directly to a running VelocityDM instance.
/// </summary>
public sealed class BrowserBridgeServer : IAsyncDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private int _port;

    public event Action<BridgeCommand>? CommandReceived;
    public Func<BridgeCommand, object?>? CommandResponder { get; set; }

    public bool IsRunning => _listener != null;
    public int Port => _port;

    public void Start(int port)
    {
        if (_listener != null && _port == port)
            return;

        Stop();
        _port = port;
        _cts = new CancellationTokenSource();

        try
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }
        catch
        {
            _listener = null;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            var rawHeader = new List<byte>(2048);
            var b = new byte[1];
            while (!ct.IsCancellationRequested && rawHeader.Count < 65536)
            {
                int n = await stream.ReadAsync(b, ct).ConfigureAwait(false);
                if (n == 0) return;
                rawHeader.Add(b[0]);
                int c = rawHeader.Count;
                if (c >= 4 &&
                    rawHeader[c - 4] == '\r' &&
                    rawHeader[c - 3] == '\n' &&
                    rawHeader[c - 2] == '\r' &&
                    rawHeader[c - 1] == '\n')
                {
                    break;
                }
            }

            string headerText = Encoding.ASCII.GetString(rawHeader.ToArray());
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;

            var reqParts = lines[0].Split(' ');
            if (reqParts.Length < 2) return;

            string method = reqParts[0].ToUpperInvariant();
            string path = reqParts[1].Split('?')[0];

            int contentLength = 0;
            foreach (var line in lines.Skip(1))
            {
                int idx = line.IndexOf(':');
                if (idx > 0 && line[..idx].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line[(idx + 1)..].Trim(), out contentLength);
                }
            }

            if (method == "OPTIONS")
            {
                await WriteJsonResponseAsync(stream, "204 No Content", string.Empty, ct).ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == "/ping")
            {
                await WriteJsonResponseAsync(stream, "200 OK", "{\"ok\":true,\"app\":\"VelocityDM\",\"version\":\"2.0\"}", ct).ConfigureAwait(false);
                return;
            }

            if (method == "POST" && path == "/download")
            {
                string bodyJson = string.Empty;
                if (contentLength > 0 && contentLength <= 1_048_576)
                {
                    var bodyBytes = new byte[contentLength];
                    int totalRead = 0;
                    while (totalRead < contentLength)
                    {
                        int r = await stream.ReadAsync(bodyBytes.AsMemory(totalRead, contentLength - totalRead), ct).ConfigureAwait(false);
                        if (r == 0) break;
                        totalRead += r;
                    }
                    bodyJson = Encoding.UTF8.GetString(bodyBytes, 0, totalRead);
                }

                BridgeCommand? cmd = null;
                if (!string.IsNullOrWhiteSpace(bodyJson))
                {
                    try
                    {
                        cmd = JsonSerializer.Deserialize<BridgeCommand>(bodyJson);
                    }
                    catch (JsonException)
                    {
                        cmd = null;
                    }
                }

                if (cmd == null)
                {
                    await WriteJsonResponseAsync(stream, "400 Bad Request", "{\"ok\":false,\"error\":\"Invalid payload\"}", ct).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(cmd.Action, "download", StringComparison.OrdinalIgnoreCase) &&
                    !ExternalDownloadBurstGuard.TryNormalizeSupportedUrl(cmd.Url, out _))
                {
                    await WriteJsonResponseAsync(stream, "400 Bad Request", "{\"ok\":false,\"error\":\"Invalid or missing URL\"}", ct).ConfigureAwait(false);
                    return;
                }

                object? ack = CommandResponder?.Invoke(cmd);
                CommandReceived?.Invoke(cmd);
                string ackJson = ack != null ? JsonSerializer.Serialize(ack) : "{\"ok\":true}";
                await WriteJsonResponseAsync(stream, "200 OK", ackJson, ct).ConfigureAwait(false);
                return;
            }

            await WriteJsonResponseAsync(stream, "404 Not Found", "{\"ok\":false}", ct).ConfigureAwait(false);
        }
    }

    private static async Task WriteJsonResponseAsync(NetworkStream stream, string status, string json, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status}\r\n");
        sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
        sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
        sb.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
