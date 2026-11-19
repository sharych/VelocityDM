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

    [Fact]
    public async Task AppSettings_DefaultsTo32ConnectionsAndMigratesLegacyEightConnectionDefault()
    {
        var fresh = new AppSettings();
        Assert.Equal(32, fresh.DefaultConnectionsPerDownload);
        Assert.True(fresh.RememberCustomDownloadLocation);

        var tempDir = Path.Combine(Path.GetTempPath(), "vdm_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Legacy v1 settings file with default 8 and no SettingsSchemaVersion
            string legacyPath = Path.Combine(tempDir, "settings.json");
            await File.WriteAllTextAsync(legacyPath, """
                {
                  "DefaultConnectionsPerDownload": 8,
                  "CategorizeIntoSubfolders": true
                }
                """);

            var store = new JsonStateStore(tempDir);
            var migrated = await store.LoadSettingsAsync();
            Assert.Equal(32, migrated.DefaultConnectionsPerDownload);
            Assert.Equal(AppSettings.CurrentSchemaVersion, migrated.SettingsSchemaVersion);

            // User-customized connection count (e.g. 16) must stay untouched on migration
            await File.WriteAllTextAsync(legacyPath, """
                {
                  "DefaultConnectionsPerDownload": 16,
                  "CategorizeIntoSubfolders": false
                }
                """);
            var custom = await store.LoadSettingsAsync();
            Assert.Equal(16, custom.DefaultConnectionsPerDownload);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void FileCategoryClassifier_CreateCategoryHierarchyCreatesAllSubfoldersAndResolvesDestination()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vdm_hier_" + Guid.NewGuid().ToString("N"));
        try
        {
            var folders = FileCategoryClassifier.CreateCategoryHierarchy(tempDir);
            string[] expected = ["Programs", "Compressed", "Video", "Music", "Documents", "Images", "Other", "Torrents"];
            foreach (var name in expected)
            {
                string sub = Path.Combine(tempDir, name);
                Assert.True(Directory.Exists(sub), $"Missing expected folder: {sub}");
                Assert.Contains(sub, folders);
            }

            var settings = new AppSettings
            {
                DefaultDownloadDirectory = tempDir,
                CategorizeIntoSubfolders = true
            };
            var dest = FileCategoryClassifier.ResolveSuggestedDestination(settings, "https://cdn.example.com/movie.mp4");
            Assert.Equal("movie.mp4", dest.FileName);
            Assert.Equal(FileCategory.Video, dest.Category);
            Assert.Equal(Path.Combine(tempDir, "Video"), dest.SaveDirectory);

            string nested = Path.Combine(tempDir, "Other", "Compressed", "Compressed", "Other", "Other");
            Assert.Equal(tempDir, FileCategoryClassifier.SanitizeCorruptedCategoryNesting(nested));
            Assert.Equal(Path.Combine(tempDir, "MyVideos"), FileCategoryClassifier.SanitizeCorruptedCategoryNesting(Path.Combine(tempDir, "MyVideos")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task JsonStateStore_RepairsCorruptedNestedDefaultDirectoryFromV2()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vdm_v3mig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string baseRoot = Path.Combine(tempDir, "VelocityDM");
            string corrupted = Path.Combine(baseRoot, "Other", "Compressed", "Compressed", "Other", "Other");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "settings.json"), $$"""
                {
                  "SettingsSchemaVersion": 2,
                  "DefaultDownloadDirectory": {{System.Text.Json.JsonSerializer.Serialize(corrupted)}},
                  "CategorizeIntoSubfolders": true,
                  "DefaultConnectionsPerDownload": 32
                }
                """);

            var store = new JsonStateStore(tempDir);
            var loaded = await store.LoadSettingsAsync();
            Assert.Equal(baseRoot, loaded.DefaultDownloadDirectory);
            Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SettingsSchemaVersion);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
