using MonoTorrent;
using MonoTorrent.Client;

namespace VelocityDM.Core.Models;

/// <summary>
/// Persisted torrent entry stored in torrents.json across restarts.
/// </summary>
public sealed class TorrentRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string InfoHashHex { get; set; } = string.Empty;
    public string? MagnetUri { get; set; }
    public string? CachedTorrentFilePath { get; set; }
    public string SaveDirectory { get; set; } = string.Empty;
    public bool Sequential { get; set; }
    public bool IsPaused { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TorrentFileSnapshot
{
    public required string Path { get; init; }
    public required string FullPath { get; init; }
    public long Length { get; init; }
    public double ProgressPercent { get; init; }
    public Priority Priority { get; init; }
}

public sealed class TorrentStatusSnapshot
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string InfoHashHex { get; init; } = string.Empty;
    public string? MagnetUri { get; init; }
    public string SaveDirectory { get; init; } = string.Empty;
    public TorrentState State { get; init; }
    public bool IsPaused { get; init; }
    public bool Sequential { get; init; }
    public double ProgressPercent { get; init; }
    public long TotalBytes { get; init; }
    public long DownloadedBytes { get; init; }
    public long TotalUploadedBytes { get; init; }
    public long DownloadSpeedBytesPerSec { get; init; }
    public long UploadSpeedBytesPerSec { get; init; }
    public int ConnectedPeers { get; init; }
    public int Seeds { get; init; }
    public int Leechers { get; init; }
    public bool HasMetadata { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyList<TorrentFileSnapshot> Files { get; init; } = Array.Empty<TorrentFileSnapshot>();
}
