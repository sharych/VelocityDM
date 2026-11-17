using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VelocityDM.Tests;

/// <summary>
/// Lightweight deterministic HTTP/1.1 test server over TcpListener supporting Range requests,
/// throttled streaming, 405 HEAD rejection, lying Accept-Ranges, redirects, and dynamic ETag changes.
/// </summary>
public sealed class LocalHttpTestServer : IAsyncDisposable
{
    public sealed class RouteConfig
    {
        public required byte[] Payload { get; set; }
        public string ContentType { get; set; } = "application/octet-stream";
        public string? ContentDispositionFileName { get; set; }
        public bool SupportRanges { get; set; } = true;
        public bool RejectHead { get; set; }
        public bool AdvertiseRangeOnHeadButIgnoreOnGet { get; set; }
        public bool OmitContentLength { get; set; }
        public string ETag { get; set; } = "\"v1\"";
        public int DelayPerChunkMs { get; set; }
        public int StreamChunkSize { get; set; } = 16 * 1024;
        public string? RedirectToPath { get; set; }
        public int ObservedRangeGetCount;
        public int ObservedTotalGetCount;
        public string? LastCookieHeader;
        public string? LastRefererHeader;
    }

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, RouteConfig> _routes = new(StringComparer.Ordinal);
    private readonly Task _acceptLoop;

    public LocalHttpTestServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{Port}";
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }
    public string BaseUrl { get; }

    public void Map(string path, RouteConfig config) => _routes[path] = config;

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, _cts.Token));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (_cts.IsCancellationRequested) break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            var headerBytes = new List<byte>(1024);
            var singleByte = new byte[1];
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(singleByte, ct).ConfigureAwait(false);
                if (n == 0) return;
                headerBytes.Add(singleByte[0]);
                int count = headerBytes.Count;
                if (count >= 4 &&
                    headerBytes[count - 4] == '\r' &&
                    headerBytes[count - 3] == '\n' &&
                    headerBytes[count - 2] == '\r' &&
                    headerBytes[count - 1] == '\n')
                {
                    break;
                }
            }

            string rawRequest = Encoding.ASCII.GetString(headerBytes.ToArray());
            var lines = rawRequest.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;

            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return;

            string method = parts[0].ToUpperInvariant();
            string path = parts[1];

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
                }
            }

            if (!_routes.TryGetValue(path, out var route))
            {
                await WriteTextResponseAsync(stream, "404 Not Found", "Not found", ct).ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrEmpty(route.RedirectToPath))
            {
                var redirectHdr = new StringBuilder();
                redirectHdr.Append("HTTP/1.1 302 Found\r\n");
                redirectHdr.Append($"Location: {BaseUrl}{route.RedirectToPath}\r\n");
                redirectHdr.Append("Content-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(redirectHdr.ToString()), ct).ConfigureAwait(false);
                return;
            }

            if (method == "HEAD" && route.RejectHead)
            {
                await WriteTextResponseAsync(stream, "405 Method Not Allowed", "HEAD disabled", ct).ConfigureAwait(false);
                return;
            }

            long totalLength = route.Payload.Length;
            bool hasRange = headers.TryGetValue("Range", out var rangeValue);
            if (headers.TryGetValue("Cookie", out var cookieVal)) route.LastCookieHeader = cookieVal;
            if (headers.TryGetValue("Referer", out var refVal)) route.LastRefererHeader = refVal;

            if (method == "GET")
            {
                Interlocked.Increment(ref route.ObservedTotalGetCount);
                if (hasRange)
                {
                    Interlocked.Increment(ref route.ObservedRangeGetCount);
                }
            }

            if (method == "GET" && hasRange && route.SupportRanges && !route.AdvertiseRangeOnHeadButIgnoreOnGet)
            {
                if (!TryParseRange(rangeValue!, totalLength, out long start, out long end))
                {
                    var hdr = $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{totalLength}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(hdr), ct).ConfigureAwait(false);
                    return;
                }

                long sliceLength = end - start + 1;
                var sb = new StringBuilder();
                sb.Append("HTTP/1.1 206 Partial Content\r\n");
                sb.Append($"Content-Type: {route.ContentType}\r\n");
                sb.Append($"Content-Length: {sliceLength}\r\n");
                sb.Append($"Content-Range: bytes {start}-{end}/{totalLength}\r\n");
                sb.Append("Accept-Ranges: bytes\r\n");
                sb.Append($"ETag: {route.ETag}\r\n");
                if (!string.IsNullOrEmpty(route.ContentDispositionFileName))
                    sb.Append($"Content-Disposition: attachment; filename=\"{route.ContentDispositionFileName}\"\r\n");
                sb.Append("Connection: close\r\n\r\n");

                await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
                await WritePayloadSliceAsync(stream, route.Payload, (int)start, (int)sliceLength, route, ct).ConfigureAwait(false);
                return;
            }

            var okHdr = new StringBuilder();
            okHdr.Append("HTTP/1.1 200 OK\r\n");
            okHdr.Append($"Content-Type: {route.ContentType}\r\n");
            if (!route.OmitContentLength)
                okHdr.Append($"Content-Length: {totalLength}\r\n");
            if (route.SupportRanges || (method == "HEAD" && route.AdvertiseRangeOnHeadButIgnoreOnGet))
                okHdr.Append("Accept-Ranges: bytes\r\n");
            else
                okHdr.Append("Accept-Ranges: none\r\n");
            okHdr.Append($"ETag: {route.ETag}\r\n");
            if (!string.IsNullOrEmpty(route.ContentDispositionFileName))
                okHdr.Append($"Content-Disposition: attachment; filename=\"{route.ContentDispositionFileName}\"\r\n");
            okHdr.Append("Connection: close\r\n\r\n");

            await stream.WriteAsync(Encoding.ASCII.GetBytes(okHdr.ToString()), ct).ConfigureAwait(false);

            if (method == "GET")
            {
                await WritePayloadSliceAsync(stream, route.Payload, 0, route.Payload.Length, route, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task WritePayloadSliceAsync(
        NetworkStream stream,
        byte[] payload,
        int offset,
        int length,
        RouteConfig route,
        CancellationToken ct)
    {
        int written = 0;
        while (written < length)
        {
            int chunk = Math.Min(route.StreamChunkSize, length - written);
            await stream.WriteAsync(payload.AsMemory(offset + written, chunk), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            written += chunk;

            if (route.DelayPerChunkMs > 0 && written < length)
            {
                await Task.Delay(route.DelayPerChunkMs, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteTextResponseAsync(NetworkStream stream, string status, string body, CancellationToken ct)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var hdr = $"HTTP/1.1 {status}\r\nContent-Type: text/plain\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(hdr), ct).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
    }

    private static bool TryParseRange(string header, long totalLength, out long start, out long end)
    {
        start = 0;
        end = totalLength - 1;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return false;

        var rangePart = header["bytes=".Length..].Trim();
        var dash = rangePart.IndexOf('-');
        if (dash < 0) return false;

        var startStr = rangePart[..dash];
        var endStr = rangePart[(dash + 1)..];

        if (!long.TryParse(startStr, out start) || start < 0 || start >= totalLength)
            return false;

        if (!string.IsNullOrEmpty(endStr))
        {
            if (!long.TryParse(endStr, out end) || end < start)
                return false;
            end = Math.Min(end, totalLength - 1);
        }
        else
        {
            end = totalLength - 1;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
