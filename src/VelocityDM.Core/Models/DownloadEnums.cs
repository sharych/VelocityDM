namespace VelocityDM.Core.Models;

/// <summary>
/// Lifecycle status of a download task.
/// </summary>
public enum DownloadStatus
{
    Queued = 0,
    Probing = 1,
    Downloading = 2,
    Paused = 3,
    Completed = 4,
    Error = 5,
    Cancelled = 6
}

/// <summary>
/// Lifecycle status of an individual byte-range segment.
/// </summary>
public enum SegmentStatus
{
    Pending = 0,
    Active = 1,
    Paused = 2,
    Completed = 3,
    Error = 4
}

/// <summary>
/// Logical file categories similar to IDM's category tree.
/// </summary>
public enum FileCategory
{
    Other = 0,
    Programs = 1,
    Compressed = 2,
    Video = 3,
    Music = 4,
    Documents = 5,
    Images = 6
}
