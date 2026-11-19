using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

    [Fact]
    public void GitHubUpdateService_SelectsPlatformAssetsAndDetectsNewerVersion()
    {
        const string releaseJson = """
            {
              "tag_name": "v2.4.0",
              "name": "VelocityDM v2.4.0",
              "body": "Added automatic GitHub updates.",
              "html_url": "https://github.com/sharych/VelocityDM/releases/tag/v2.4.0",
              "published_at": "2026-11-19T12:00:00Z",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "name": "VelocityDM-Setup-v2.4.0.exe",
                  "browser_download_url": "https://github.com/sharych/VelocityDM/releases/download/v2.4.0/VelocityDM-Setup-v2.4.0.exe",
                  "size": 4921274,
                  "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                },
                {
                  "name": "VelocityDM-v2.4.0-win-x64-portable.zip",
                  "browser_download_url": "https://github.com/sharych/VelocityDM/releases/download/v2.4.0/VelocityDM-v2.4.0-win-x64-portable.zip",
                  "size": 76265909
                },
                {
                  "name": "VelocityDM-v2.4.0-osx-arm64.dmg",
                  "browser_download_url": "https://github.com/sharych/VelocityDM/releases/download/v2.4.0/VelocityDM-v2.4.0-osx-arm64.dmg",
                  "size": 48038765
                },
                {
                  "name": "VelocityDM-v2.4.0-osx-x64.dmg",
                  "browser_download_url": "https://github.com/sharych/VelocityDM/releases/download/v2.4.0/VelocityDM-v2.4.0-osx-x64.dmg",
                  "size": 49241223
                },
                {
                  "name": "SHA256SUMS.txt",
                  "browser_download_url": "https://github.com/sharych/VelocityDM/releases/download/v2.4.0/SHA256SUMS.txt",
                  "size": 393
                },
                {
                  "name": "evil.exe",
                  "browser_download_url": "http://evil.example.com/evil.exe",
                  "size": 100
                }
              ]
            }
            """;

        var currentOlder = new Version(2, 3, 2, 0);

        var winInstaller = GitHubUpdateService.ParseReleaseJson(
            releaseJson, currentOlder, OSPlatform.Windows, Architecture.X64, isWindowsInstaller: true, isWindowsPortable: false);
        Assert.True(winInstaller.UpdateAvailable);
        Assert.Equal("VelocityDM-Setup-v2.4.0.exe", winInstaller.Release!.SelectedAsset.Name);
        Assert.Equal(UpdatePackageKind.WindowsInstallerExe, winInstaller.Release.SelectedAsset.PackageKind);

        var winPortable = GitHubUpdateService.ParseReleaseJson(
            releaseJson, currentOlder, OSPlatform.Windows, Architecture.X64, isWindowsInstaller: false, isWindowsPortable: true);
        Assert.True(winPortable.UpdateAvailable);
        Assert.Equal("VelocityDM-v2.4.0-win-x64-portable.zip", winPortable.Release!.SelectedAsset.Name);
        Assert.Equal(UpdatePackageKind.WindowsPortableZip, winPortable.Release.SelectedAsset.PackageKind);

        var macArm = GitHubUpdateService.ParseReleaseJson(
            releaseJson, currentOlder, OSPlatform.OSX, Architecture.Arm64);
        Assert.True(macArm.UpdateAvailable);
        Assert.Equal("VelocityDM-v2.4.0-osx-arm64.dmg", macArm.Release!.SelectedAsset.Name);
        Assert.Equal(UpdatePackageKind.MacOsDmg, macArm.Release.SelectedAsset.PackageKind);

        var macX64 = GitHubUpdateService.ParseReleaseJson(
            releaseJson, new Version(2, 4, 0), OSPlatform.OSX, Architecture.X64);
        Assert.False(macX64.UpdateAvailable);
        Assert.Equal("VelocityDM-v2.4.0-osx-x64.dmg", macX64.Release!.SelectedAsset.Name);

        var settings = new AppSettings { AutoCheckForUpdates = true, SkippedUpdateVersion = "v2.4.0" };
        Assert.False(GitHubUpdateService.ShouldAutoPrompt(settings, macArm.Release));
        settings.SkippedUpdateVersion = "v2.3.2";
        Assert.True(GitHubUpdateService.ShouldAutoPrompt(settings, macArm.Release));

        Assert.True(GitHubUpdateService.IsAllowedGitHubDownloadUrl("https://objects.githubusercontent.com/file"));
        Assert.False(GitHubUpdateService.IsAllowedGitHubDownloadUrl("http://github.com/file"));
        Assert.False(GitHubUpdateService.IsAllowedGitHubDownloadUrl("https://evil.com/VelocityDM.exe"));
    }

    [Fact]
    public async Task GitHubUpdateService_VerifiesSha256AndRejectsCorruptedPayload()
    {
        byte[] payload = "VelocityDM-installer-binary-payload-v2.4.0"u8.ToArray();
        string goodSha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        string wrongSha = new string('f', 64);

        string checksums = $"{goodSha}  VelocityDM-Setup-v2.4.0.exe\n{wrongSha}  VelocityDM-v2.4.0-osx-arm64.dmg\n";
        string assetUrl = "https://github.com/sharych/VelocityDM/releases/download/v2.4.0/VelocityDM-Setup-v2.4.0.exe";
        string sumsUrl = "https://github.com/sharych/VelocityDM/releases/download/v2.4.0/SHA256SUMS.txt";

        using var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.ToString() == assetUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            if (req.RequestUri.ToString() == sumsUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(checksums) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        using var updater = new GitHubUpdateService(http);

        var goodRelease = new GitHubReleaseInfo(
            "v2.4.0",
            new Version(2, 4, 0, 0),
            "VelocityDM v2.4.0",
            "Notes",
            "https://github.com/sharych/VelocityDM/releases/tag/v2.4.0",
            DateTimeOffset.UtcNow,
            new UpdateAssetInfo("VelocityDM-Setup-v2.4.0.exe", assetUrl, payload.Length, null, UpdatePackageKind.WindowsInstallerExe),
            sumsUrl);

        PreparedUpdate? prepared = null;
        try
        {
            prepared = await updater.DownloadAndVerifyUpdateAsync(goodRelease);
            Assert.Equal(goodSha, prepared.ActualSha256);
            Assert.True(File.Exists(prepared.PackageFilePath));
        }
        finally
        {
            if (prepared != null && Directory.Exists(prepared.StagingDirectory))
                Directory.Delete(prepared.StagingDirectory, recursive: true);
        }

        // Now confirm tampered / wrong digest throws and cleans up staging folder
        string badAssetUrl = "https://github.com/sharych/VelocityDM/releases/download/v2.4.0/VelocityDM-v2.4.0-osx-arm64.dmg";
        using var badHandler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.ToString() == badAssetUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            if (req.RequestUri.ToString() == sumsUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(checksums) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var badHttp = new HttpClient(badHandler);
        using var badUpdater = new GitHubUpdateService(badHttp);

        var tamperedRelease = new GitHubReleaseInfo(
            "v2.4.0",
            new Version(2, 4, 0, 0),
            "VelocityDM v2.4.0",
            "Notes",
            "https://github.com/sharych/VelocityDM/releases/tag/v2.4.0",
            DateTimeOffset.UtcNow,
            new UpdateAssetInfo("VelocityDM-v2.4.0-osx-arm64.dmg", badAssetUrl, payload.Length, null, UpdatePackageKind.MacOsDmg),
            sumsUrl);

        await Assert.ThrowsAsync<InvalidOperationException>(() => badUpdater.DownloadAndVerifyUpdateAsync(tamperedRelease));
    }

    [Fact]
    public void SelfUpdateLauncher_GeneratesPlatformUpdateScripts()
    {
        string winSetupScript = SelfUpdateLauncher.BuildWindowsInstallerScript(
            1234,
            @"C:\Temp\VelocityDM-Setup-v2.4.0.exe",
            @"C:\Users\Test\AppData\Local\Programs\VelocityDM\VelocityDM.exe",
            @"C:\Temp\stage");
        Assert.Contains("Wait-Process -Id $oldPid", winSetupScript);
        Assert.Contains("'/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CLOSEAPPLICATIONS'", winSetupScript);

        string winPortableScript = SelfUpdateLauncher.BuildWindowsPortableScript(
            1234,
            @"C:\Temp\stage\extracted",
            @"D:\Apps\VelocityDM",
            @"D:\Apps\VelocityDM\VelocityDM.exe",
            @"C:\Temp\stage",
            registerBrowser: true);
        Assert.Contains("Copy-Item", winPortableScript);
        Assert.Contains("--register-browser", winPortableScript);

        string macScript = SelfUpdateLauncher.BuildMacDmgScript(
            5678,
            "/tmp/stage/VelocityDM-v2.4.0-osx-arm64.dmg",
            "/Applications/VelocityDM.app",
            "/tmp/stage",
            "/tmp/update.sh");
        Assert.Contains("hdiutil attach -nobrowse -readonly -mountpoint", macScript);
        Assert.Contains("ditto \"$SOURCE_APP\" \"$TARGET_APP\"", macScript);
        Assert.Contains("--register-browser", macScript);
        Assert.Contains("open \"$TARGET_APP\"", macScript);
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }
}
