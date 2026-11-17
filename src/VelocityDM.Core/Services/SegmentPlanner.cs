using VelocityDM.Core.Models;

namespace VelocityDM.Core.Services;

/// <summary>
/// Plans contiguous byte-range segments for multi-connection downloading.
/// </summary>
public static class SegmentPlanner
{
    public const long DefaultMinSegmentBytes = 128 * 1024; // 128 KB

    public static List<SegmentState> CreateSegments(
        long? totalBytes,
        bool supportsRanges,
        int requestedConnections,
        long minSegmentBytes = DefaultMinSegmentBytes)
    {
        requestedConnections = Math.Clamp(requestedConnections, 1, 32);
        minSegmentBytes = Math.Max(1, minSegmentBytes);

        if (totalBytes is null or <= 0)
        {
            return new List<SegmentState>
            {
                new()
                {
                    Index = 0,
                    StartOffset = 0,
                    EndOffset = -1,
                    DownloadedBytes = 0,
                    Status = SegmentStatus.Pending
                }
            };
        }

        long size = totalBytes.Value;
        if (!supportsRanges || requestedConnections <= 1 || size < minSegmentBytes * 2)
        {
            return new List<SegmentState>
            {
                new()
                {
                    Index = 0,
                    StartOffset = 0,
                    EndOffset = size - 1,
                    DownloadedBytes = 0,
                    Status = SegmentStatus.Pending
                }
            };
        }

        int maxBySize = (int)Math.Min(requestedConnections, size / minSegmentBytes);
        int segmentCount = Math.Clamp(maxBySize, 1, requestedConnections);

        long baseSize = size / segmentCount;
        long remainder = size % segmentCount;

        var segments = new List<SegmentState>(segmentCount);
        long cursor = 0;

        for (int i = 0; i < segmentCount; i++)
        {
            long chunkSize = baseSize + (i < remainder ? 1 : 0);
            long endOffset = cursor + chunkSize - 1;

            segments.Add(new SegmentState
            {
                Index = i,
                StartOffset = cursor,
                EndOffset = endOffset,
                DownloadedBytes = 0,
                Status = SegmentStatus.Pending
            });

            cursor = endOffset + 1;
        }

        return segments;
    }
}
