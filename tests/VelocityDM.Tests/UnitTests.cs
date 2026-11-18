using VelocityDM.Core.Models;
using VelocityDM.Core.Services;
using Xunit;

namespace VelocityDM.Tests;

public sealed class UnitTests
{
    [Fact]
    public void SegmentPlanner_SplitsLargeRangeFileEvenlyWithoutGaps()
    {
        long totalSize = 10_000_005;
        var segments = SegmentPlanner.CreateSegments(totalSize, supportsRanges: true, requestedConnections: 8);

        Assert.Equal(8, segments.Count);
        Assert.Equal(0, segments[0].StartOffset);
        Assert.Equal(totalSize - 1, segments[^1].EndOffset);

        long covered = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            Assert.Equal(i, segments[i].Index);
            if (i > 0)
            {
                Assert.Equal(segments[i - 1].EndOffset + 1, segments[i].StartOffset);
            }
            covered += segments[i].TotalBytes;
        }
        Assert.Equal(totalSize, covered);
    }

    [Fact]
    public void SegmentPlanner_UsesSingleSegmentForSmallOrNonRangeFiles()
    {
        var small = SegmentPlanner.CreateSegments(100_000, supportsRanges: true, requestedConnections: 16);
        Assert.Single(small);
        Assert.Equal(0, small[0].StartOffset);
        Assert.Equal(99_999, small[0].EndOffset);

        var noRange = SegmentPlanner.CreateSegments(50_000_000, supportsRanges: false, requestedConnections: 8);
        Assert.Single(noRange);

        var unknown = SegmentPlanner.CreateSegments(null, supportsRanges: false, requestedConnections: 8);
        Assert.Single(unknown);
        Assert.Equal(-1, unknown[0].EndOffset);
    }

    [Fact]
    public void BatchLinkParser_ExpandsZeroPaddedNumericAndAlphaPatternsAndDeduplicates()
    {
        string input = """
            https://cdn.example.com/archive_part[01-03].zip
            https://cdn.example.com/archive_part02.zip
            http://cdn.example.com/slice_[a-c].bin
            not-a-valid-url
            """;

        var urls = BatchLinkParser.ParseAndExpand(input);

        Assert.Equal(
            new[]
            {
                "https://cdn.example.com/archive_part01.zip",
                "https://cdn.example.com/archive_part02.zip",
                "https://cdn.example.com/archive_part03.zip",
                "http://cdn.example.com/slice_a.bin",
                "http://cdn.example.com/slice_b.bin",
                "http://cdn.example.com/slice_c.bin"
            },
            urls);
    }

    [Fact]
    public void FileCategoryClassifier_ClassifiesExtensionsAndDeduplicatesCollisions()
    {
        Assert.Equal(FileCategory.Compressed, FileCategoryClassifier.Classify("ubuntu.iso"));
        Assert.Equal(FileCategory.Programs, FileCategoryClassifier.Classify("installer.exe"));
        Assert.Equal(FileCategory.Video, FileCategoryClassifier.Classify("clip.mkv"));
        Assert.Equal(FileCategory.Music, FileCategoryClassifier.Classify("track.flac"));
        Assert.Equal(FileCategory.Documents, FileCategoryClassifier.Classify("manual.pdf"));
        Assert.Equal(FileCategory.Images, FileCategoryClassifier.Classify("photo.webp"));

        var tempDir = Path.Combine(Path.GetTempPath(), "vdm_cls_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var first = FileCategoryClassifier.GetUniqueFilePath(tempDir, "test?.zip");
            Assert.Equal("test_.zip", Path.GetFileName(first));
            File.WriteAllText(first, "1");

            var second = FileCategoryClassifier.GetUniqueFilePath(tempDir, "test?.zip");
            Assert.Equal("test_ (1).zip", Path.GetFileName(second));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void JsonStateStore_RecoverInterruptedTasksMarksActiveAsPaused()
    {
        var tasks = new List<DownloadTask>
        {
            new()
            {
                Status = DownloadStatus.Downloading,
                Segments = new List<SegmentState>
                {
                    new() { Index = 0, StartOffset = 0, EndOffset = 1000, DownloadedBytes = 500, Status = SegmentStatus.Active },
                    new() { Index = 1, StartOffset = 1001, EndOffset = 2000, DownloadedBytes = 1000, Status = SegmentStatus.Completed }
                }
            },
            new() { Status = DownloadStatus.Completed }
        };

        JsonStateStore.RecoverInterruptedTasks(tasks);

        Assert.Equal(DownloadStatus.Paused, tasks[0].Status);
        Assert.Equal(SegmentStatus.Paused, tasks[0].Segments[0].Status);
        Assert.Equal(SegmentStatus.Completed, tasks[0].Segments[1].Status);
        Assert.Equal(DownloadStatus.Completed, tasks[1].Status);
    }

    [Fact]
    public void ExternalDownloadBurstGuard_DeduplicatesUrlsAndCapsPendingPopups()
    {
        var currentTime = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        var guard = new ExternalDownloadBurstGuard(
            maxPendingPopups: 3,
            duplicateCooldown: TimeSpan.FromSeconds(3),
            utcNow: () => currentTime);

        Assert.False(guard.TryAcceptUrl("", out _));
        Assert.False(guard.TryAcceptUrl("ftp://example.com/file.zip", out _));

        Assert.True(guard.TryAcceptUrl("https://example.com/file.zip", out var normalized));
        Assert.Equal("https://example.com/file.zip", normalized);
        Assert.False(guard.TryAcceptUrl("HTTPS://example.com/file.zip", out _));

        currentTime = currentTime.AddSeconds(4);
        Assert.True(guard.TryAcceptUrl("https://example.com/file.zip", out _));

        Assert.True(guard.TryAcquirePopupSlot());
        Assert.True(guard.TryAcquirePopupSlot());
        Assert.True(guard.TryAcquirePopupSlot());
        Assert.False(guard.TryAcquirePopupSlot());
        Assert.Equal(3, guard.PendingPopupCount);

        guard.ReleasePopupSlot();
        Assert.Equal(2, guard.PendingPopupCount);
        Assert.True(guard.TryAcquirePopupSlot());
    }
}
