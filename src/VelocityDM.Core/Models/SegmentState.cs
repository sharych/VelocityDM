using System.Text.Json.Serialization;

namespace VelocityDM.Core.Models;

/// <summary>
/// Represents a single contiguous byte-range slice of a file downloaded by one connection.
/// </summary>
public sealed class SegmentState
{
    public int Index { get; set; }

    /// <summary>Inclusive zero-based start byte offset.</summary>
    public long StartOffset { get; set; }

    /// <summary>Inclusive end byte offset, or -1 when total file size is unknown (single stream).</summary>
    public long EndOffset { get; set; }

    /// <summary>Number of bytes persisted to disk for this segment so far.</summary>
    public long DownloadedBytes { get; set; }

    public SegmentStatus Status { get; set; } = SegmentStatus.Pending;

    public string? LastError { get; set; }

    [JsonIgnore]
    public double SpeedBytesPerSec { get; set; }

    [JsonIgnore]
    public long TotalBytes => EndOffset >= StartOffset ? (EndOffset - StartOffset + 1) : -1;

    [JsonIgnore]
    public long NextWriteOffset => StartOffset + DownloadedBytes;

    [JsonIgnore]
    public long RemainingBytes => EndOffset >= StartOffset ? Math.Max(0, TotalBytes - DownloadedBytes) : -1;

    [JsonIgnore]
    public bool IsFinished => EndOffset >= StartOffset && DownloadedBytes >= TotalBytes;

    [JsonIgnore]
    public double ProgressPercent => TotalBytes > 0
        ? Math.Clamp((double)DownloadedBytes / TotalBytes * 100.0, 0.0, 100.0)
        : (Status == SegmentStatus.Completed ? 100.0 : 0.0);

    public SegmentState Clone() => new()
    {
        Index = Index,
        StartOffset = StartOffset,
        EndOffset = EndOffset,
        DownloadedBytes = DownloadedBytes,
        Status = Status,
        LastError = LastError,
        SpeedBytesPerSec = SpeedBytesPerSec
    };
}
