using System.Text.Json.Serialization;

namespace VelocityDM.Core.Models;

/// <summary>
/// Represents a persistent download job, its segments, and runtime metrics.
/// </summary>
public sealed class DownloadTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Url { get; set; } = string.Empty;

    public string? FinalUrl { get; set; }

    public string FileName { get; set; } = string.Empty;

    public bool HasCustomFileName { get; set; }

    public string SaveDirectory { get; set; } = string.Empty;

    public bool HasCustomSaveDirectory { get; set; }

    public string TargetFilePath { get; set; } = string.Empty;

    public string TempFilePath { get; set; } = string.Empty;

    public FileCategory Category { get; set; } = FileCategory.Other;

    public bool HasCustomCategory { get; set; }

    public string? ContentType { get; set; }

    public long? TotalBytes { get; set; }

    public long DownloadedBytes { get; set; }

    public bool SupportsRanges { get; set; }

    public int MaxConnections { get; set; } = 32;

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;

    public string? ErrorMessage { get; set; }

    public string? ETag { get; set; }

    public string? LastModified { get; set; }

    public string? ExpectedSha256 { get; set; }

    public string? ActualSha256 { get; set; }

    /// <summary>
    /// Null when not verified against ExpectedSha256; true when matched; false on mismatch.
    /// </summary>
    public bool? HashVerified { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    public string? Referrer { get; set; }

    public string? CookieHeader { get; set; }

    public string? UserAgentOverride { get; set; }

    public int QueueOrder { get; set; }

    public List<SegmentState> Segments { get; set; } = new();

    [JsonIgnore]
    public double SpeedBytesPerSec { get; set; }

    [JsonIgnore]
    public double? EtaSeconds => TotalBytes is > 0 && SpeedBytesPerSec > 1.0 && DownloadedBytes < TotalBytes.Value
        ? (TotalBytes.Value - DownloadedBytes) / SpeedBytesPerSec
        : null;

    [JsonIgnore]
    public double ProgressPercent => TotalBytes is > 0
        ? Math.Clamp((double)DownloadedBytes / TotalBytes.Value * 100.0, 0.0, 100.0)
        : (Status == DownloadStatus.Completed ? 100.0 : 0.0);

    [JsonIgnore]
    public int ActiveSegmentCount => Segments.Count(s => s.Status == SegmentStatus.Active);

    public DownloadTask CloneSnapshot()
    {
        return new DownloadTask
        {
            Id = Id,
            Url = Url,
            FinalUrl = FinalUrl,
            FileName = FileName,
            HasCustomFileName = HasCustomFileName,
            SaveDirectory = SaveDirectory,
            HasCustomSaveDirectory = HasCustomSaveDirectory,
            TargetFilePath = TargetFilePath,
            TempFilePath = TempFilePath,
            Category = Category,
            HasCustomCategory = HasCustomCategory,
            ContentType = ContentType,
            TotalBytes = TotalBytes,
            DownloadedBytes = DownloadedBytes,
            SupportsRanges = SupportsRanges,
            MaxConnections = MaxConnections,
            Status = Status,
            ErrorMessage = ErrorMessage,
            ETag = ETag,
            LastModified = LastModified,
            ExpectedSha256 = ExpectedSha256,
            ActualSha256 = ActualSha256,
            HashVerified = HashVerified,
            CreatedAt = CreatedAt,
            CompletedAt = CompletedAt,
            Referrer = Referrer,
            CookieHeader = CookieHeader,
            UserAgentOverride = UserAgentOverride,
            QueueOrder = QueueOrder,
            SpeedBytesPerSec = SpeedBytesPerSec,
            Segments = Segments.Select(s => s.Clone()).ToList()
        };
    }
}
