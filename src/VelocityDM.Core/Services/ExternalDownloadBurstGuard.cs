namespace VelocityDM.Core.Services;

/// <summary>
/// Validates external browser/IPC download requests, suppresses rapid duplicates,
/// and caps queued confirmation dialogs to prevent modal popup storms.
/// </summary>
public sealed class ExternalDownloadBurstGuard
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _recentUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxPendingPopups;
    private readonly TimeSpan _duplicateCooldown;
    private readonly Func<DateTimeOffset> _utcNow;
    private int _pendingPopups;

    public ExternalDownloadBurstGuard(
        int maxPendingPopups = 5,
        TimeSpan? duplicateCooldown = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        if (maxPendingPopups < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPendingPopups));

        _maxPendingPopups = maxPendingPopups;
        _duplicateCooldown = duplicateCooldown ?? TimeSpan.FromSeconds(3);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public int PendingPopupCount
    {
        get
        {
            lock (_lock)
            {
                return _pendingPopups;
            }
        }
    }

    public static bool TryNormalizeSupportedUrl(string? rawUrl, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(rawUrl))
            return false;

        string trimmed = rawUrl.Trim();
        if (trimmed.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            normalizedUrl = trimmed;
            return true;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            normalizedUrl = uri.AbsoluteUri;
            return true;
        }

        if (trimmed.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(trimmed))
        {
            normalizedUrl = trimmed;
            return true;
        }

        return false;
    }

    public bool TryAcceptUrl(string? rawUrl, out string normalizedUrl)
    {
        if (!TryNormalizeSupportedUrl(rawUrl, out normalizedUrl))
            return false;

        DateTimeOffset now = _utcNow();
        lock (_lock)
        {
            PruneExpiredLocked(now);
            if (_recentUrls.ContainsKey(normalizedUrl))
                return false;

            _recentUrls[normalizedUrl] = now;
            return true;
        }
    }

    public bool TryAcquirePopupSlot()
    {
        lock (_lock)
        {
            if (_pendingPopups >= _maxPendingPopups)
                return false;

            _pendingPopups++;
            return true;
        }
    }

    public void ReleasePopupSlot()
    {
        lock (_lock)
        {
            if (_pendingPopups > 0)
                _pendingPopups--;
        }
    }

    private void PruneExpiredLocked(DateTimeOffset now)
    {
        if (_recentUrls.Count == 0)
            return;

        List<string>? expired = null;
        foreach (var pair in _recentUrls)
        {
            if (now - pair.Value >= _duplicateCooldown)
            {
                expired ??= new List<string>();
                expired.Add(pair.Key);
            }
        }

        if (expired == null)
            return;

        foreach (var key in expired)
        {
            _recentUrls.Remove(key);
        }
    }
}
