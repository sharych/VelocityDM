using System.Diagnostics;
using System.Security.Cryptography;
using VelocityDM.Core.Models;
using VelocityDM.Core.Services;
using Xunit;

namespace VelocityDM.Tests;

public sealed class DownloadIntegrationTests
{
    private static byte[] CreateDeterministicPayload(int length, int seed = 42)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static string ComputeHexSha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static async Task<DownloadTask> WaitForStatusAsync(
        DownloadManagerService manager,
        Guid taskId,
        Func<DownloadTask, bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            var snap = manager.GetTaskSnapshot(taskId);
            if (snap != null && predicate(snap))
                return snap;

            await Task.Delay(50);
        }

        var last = manager.GetTaskSnapshot(taskId);
        throw new TimeoutException($"Timed out waiting for task {taskId}. Last status={last?.Status}, error={last?.ErrorMessage}");
    }

    [Fact]
    public async Task ParallelMultiSegmentDownload_CompletesWithExactBytesAndVerifiedSha256()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "vdm_int_" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(tempRoot, "state");
        var downloadDir = Path.Combine(tempRoot, "downloads");

        await using var server = new LocalHttpTestServer();
        var payload = CreateDeterministicPayload(2 * 1024 * 1024 + 137);
        var expectedHash = ComputeHexSha256(payload);

        var route = new LocalHttpTestServer.RouteConfig
        {
            Payload = payload,
            SupportRanges = true,
            ContentDispositionFileName = "release-archive.zip"
        };
        server.Map("/redirect", new LocalHttpTestServer.RouteConfig { Payload = Array.Empty<byte>(), RedirectToPath = "/archive" });
        server.Map("/archive", route);

        var store = new JsonStateStore(stateDir);
        await store.SaveSettingsAsync(new AppSettings
        {
            DefaultDownloadDirectory = downloadDir,
            CategorizeIntoSubfolders = true,
            DefaultConnectionsPerDownload = 8,
            MaxConcurrentDownloads = 2
        });

        await using var manager = new DownloadManagerService(store);
        await manager.InitializeAsync();

        var created = await manager.AddDownloadAsync(new AddDownloadOptions(
            Url: $"{server.BaseUrl}/redirect",
            Connections: 8,
            ExpectedSha256: expectedHash,
            StartImmediately: true));

        var done = await WaitForStatusAsync(manager, created.Id, t => t.Status == DownloadStatus.Completed);

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(8, done.Segments.Count);
        Assert.True(route.ObservedRangeGetCount >= 8);
        Assert.Equal("release-archive.zip", done.FileName);
        Assert.Equal(FileCategory.Compressed, done.Category);
        Assert.True(done.HashVerified);
        Assert.Equal(expectedHash, done.ActualSha256);
        Assert.Equal(payload, await File.ReadAllBytesAsync(done.TargetFilePath));
        Assert.False(File.Exists(done.TempFilePath));

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task PauseResumeAndCrashRecovery_PreservesProgressAndCompletesExactFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "vdm_resume_" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(tempRoot, "state");
        var downloadDir = Path.Combine(tempRoot, "downloads");

        await using var server = new LocalHttpTestServer();
        var payload = CreateDeterministicPayload(1_500_000, seed: 99);
        var expectedHash = ComputeHexSha256(payload);

        server.Map("/slow.bin", new LocalHttpTestServer.RouteConfig
        {
            Payload = payload,
            SupportRanges = true,
            StreamChunkSize = 16 * 1024,
            DelayPerChunkMs = 20
        });

        var store = new JsonStateStore(stateDir);
        await store.SaveSettingsAsync(new AppSettings
        {
            DefaultDownloadDirectory = downloadDir,
            CategorizeIntoSubfolders = false,
            DefaultConnectionsPerDownload = 4,
            AutoStartQueuedDownloads = false
        });

        Guid taskId;
        await using (var manager1 = new DownloadManagerService(store))
        {
            await manager1.InitializeAsync();
            var task = await manager1.AddDownloadAsync(new AddDownloadOptions(
                Url: $"{server.BaseUrl}/slow.bin",
                Connections: 4,
                ExpectedSha256: expectedHash));
            taskId = task.Id;

            await WaitForStatusAsync(manager1, taskId, t => t.DownloadedBytes > 120_000);
            await manager1.PauseAsync(taskId);

            var paused = manager1.GetTaskSnapshot(taskId);
            Assert.NotNull(paused);
            Assert.Equal(DownloadStatus.Paused, paused!.Status);
            Assert.True(paused.DownloadedBytes > 0 && paused.DownloadedBytes < payload.Length);
        }

        // Simulate fresh app restart reading persisted state from disk
        var store2 = new JsonStateStore(stateDir);
        await using var manager2 = new DownloadManagerService(store2);
        await manager2.InitializeAsync();

        var reloaded = manager2.GetTaskSnapshot(taskId);
        Assert.NotNull(reloaded);
        Assert.Equal(DownloadStatus.Paused, reloaded!.Status);
        Assert.True(reloaded.DownloadedBytes > 0);

        await manager2.StartOrResumeAsync(taskId);
        var completed = await WaitForStatusAsync(manager2, taskId, t => t.Status == DownloadStatus.Completed);

        Assert.True(completed.HashVerified);
        Assert.Equal(payload, await File.ReadAllBytesAsync(completed.TargetFilePath));

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task ServerRejectingHeadAndLyingAboutRange_FallsBackGracefullyWithoutCorruption()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "vdm_fallback_" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(tempRoot, "state");
        var downloadDir = Path.Combine(tempRoot, "downloads");

        await using var server = new LocalHttpTestServer();
        var payloadA = CreateDeterministicPayload(600_000, seed: 7);
        var payloadB = CreateDeterministicPayload(450_000, seed: 13);

        // Server rejects HEAD (405) but honors GET Range: bytes=0-0 and multi-segment GETs
        server.Map("/nohead.zip", new LocalHttpTestServer.RouteConfig
        {
            Payload = payloadA,
            SupportRanges = true,
            RejectHead = true
        });

        // Server advertises Accept-Ranges: bytes on HEAD, then ignores Range and returns 200 OK on GET
        server.Map("/liar.dat", new LocalHttpTestServer.RouteConfig
        {
            Payload = payloadB,
            SupportRanges = false,
            AdvertiseRangeOnHeadButIgnoreOnGet = true
        });

        var store = new JsonStateStore(stateDir);
        await store.SaveSettingsAsync(new AppSettings
        {
            DefaultDownloadDirectory = downloadDir,
            CategorizeIntoSubfolders = false,
            DefaultConnectionsPerDownload = 4,
            MaxConcurrentDownloads = 2
        });

        await using var manager = new DownloadManagerService(store);
        await manager.InitializeAsync();

        var t1 = await manager.AddDownloadAsync(new AddDownloadOptions($"{server.BaseUrl}/nohead.zip"));
        var t2 = await manager.AddDownloadAsync(new AddDownloadOptions($"{server.BaseUrl}/liar.dat"));

        var done1 = await WaitForStatusAsync(manager, t1.Id, t => t.Status == DownloadStatus.Completed);
        var done2 = await WaitForStatusAsync(manager, t2.Id, t => t.Status == DownloadStatus.Completed);

        Assert.True(done1.Segments.Count > 1);
        Assert.Equal(payloadA, await File.ReadAllBytesAsync(done1.TargetFilePath));

        Assert.Single(done2.Segments);
        Assert.False(done2.SupportsRanges);
        Assert.Equal(payloadB, await File.ReadAllBytesAsync(done2.TargetFilePath));

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task ConcurrentQueueLimitAndSpeedThrottler_RespectCaps()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "vdm_queue_" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(tempRoot, "state");
        var downloadDir = Path.Combine(tempRoot, "downloads");

        await using var server = new LocalHttpTestServer();
        var payload = CreateDeterministicPayload(300_000, seed: 55);
        for (int i = 1; i <= 4; i++)
        {
            server.Map($"/f{i}.bin", new LocalHttpTestServer.RouteConfig
            {
                Payload = payload,
                SupportRanges = true,
                StreamChunkSize = 16 * 1024
            });
        }

        var store = new JsonStateStore(stateDir);
        await store.SaveSettingsAsync(new AppSettings
        {
            DefaultDownloadDirectory = downloadDir,
            CategorizeIntoSubfolders = false,
            DefaultConnectionsPerDownload = 2,
            MaxConcurrentDownloads = 2,
            GlobalSpeedLimitBytesPerSec = 400_000 // ~400 KB/s cap
        });

        await using var manager = new DownloadManagerService(store);
        await manager.InitializeAsync();

        int maxObservedActive = 0;
        manager.TaskUpdated += _ =>
        {
            int active = manager.GetTasksSnapshot().Count(t => t.Status is DownloadStatus.Downloading or DownloadStatus.Probing);
            if (active > maxObservedActive) maxObservedActive = active;
        };

        var sw = Stopwatch.StartNew();
        var tasks = await manager.AddBatchDownloadsAsync(new[]
        {
            $"{server.BaseUrl}/f1.bin",
            $"{server.BaseUrl}/f2.bin",
            $"{server.BaseUrl}/f3.bin",
            $"{server.BaseUrl}/f4.bin"
        });

        foreach (var t in tasks)
        {
            var done = await WaitForStatusAsync(manager, t.Id, x => x.Status == DownloadStatus.Completed, TimeSpan.FromSeconds(30));
            Assert.Equal(payload, await File.ReadAllBytesAsync(done.TargetFilePath));
        }
        sw.Stop();

        Assert.InRange(maxObservedActive, 1, 2);
        // 4 * 300 KB = 1.2 MB at 400 KB/s cap should take at least ~1.8s including initial token burst
        Assert.True(sw.Elapsed.TotalSeconds >= 1.5, $"Expected throttled elapsed >= 1.5s, got {sw.Elapsed.TotalSeconds:0.00}s");

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task Sha256Mismatch_MarksDownloadAsError()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "vdm_hashfail_" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(tempRoot, "state");
        var downloadDir = Path.Combine(tempRoot, "downloads");

        await using var server = new LocalHttpTestServer();
        server.Map("/badhash.bin", new LocalHttpTestServer.RouteConfig
        {
            Payload = CreateDeterministicPayload(64 * 1024, seed: 3),
            SupportRanges = true
        });

        var store = new JsonStateStore(stateDir);
        await store.SaveSettingsAsync(new AppSettings
        {
            DefaultDownloadDirectory = downloadDir,
            CategorizeIntoSubfolders = false
        });

        await using var manager = new DownloadManagerService(store);
        await manager.InitializeAsync();

        var task = await manager.AddDownloadAsync(new AddDownloadOptions(
            Url: $"{server.BaseUrl}/badhash.bin",
            ExpectedSha256: new string('0', 64)));

        var failed = await WaitForStatusAsync(manager, task.Id, t => t.Status == DownloadStatus.Error);
        Assert.False(failed.HashVerified);
        Assert.Contains("SHA-256 mismatch", failed.ErrorMessage);

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task UnknownContentLength_DownloadsViaSingleStreamWithoutCorruption()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "vdm_unknownlen_" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(tempRoot, "state");
        var downloadDir = Path.Combine(tempRoot, "downloads");

        await using var server = new LocalHttpTestServer();
        var payload = CreateDeterministicPayload(320_000, seed: 101);
        server.Map("/stream.bin", new LocalHttpTestServer.RouteConfig
        {
            Payload = payload,
            SupportRanges = false,
            OmitContentLength = true
        });

        var store = new JsonStateStore(stateDir);
        await store.SaveSettingsAsync(new AppSettings
        {
            DefaultDownloadDirectory = downloadDir,
            CategorizeIntoSubfolders = false,
            DefaultConnectionsPerDownload = 8
        });

        await using var manager = new DownloadManagerService(store);
        await manager.InitializeAsync();

        var task = await manager.AddDownloadAsync(new AddDownloadOptions($"{server.BaseUrl}/stream.bin"));
        var done = await WaitForStatusAsync(manager, task.Id, t => t.Status == DownloadStatus.Completed);

        Assert.Single(done.Segments);
        Assert.Equal(payload.Length, done.TotalBytes);
        Assert.Equal(payload, await File.ReadAllBytesAsync(done.TargetFilePath));

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task ResumeWhenServerETagChanged_RestartsCleanlyWithNewPayload()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "vdm_etag_" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(tempRoot, "state");
        var downloadDir = Path.Combine(tempRoot, "downloads");

        await using var server = new LocalHttpTestServer();
        var oldPayload = CreateDeterministicPayload(800_000, seed: 11);
        var newPayload = CreateDeterministicPayload(800_000, seed: 22);

        var route = new LocalHttpTestServer.RouteConfig
        {
            Payload = oldPayload,
            SupportRanges = true,
            ETag = "\"v1\"",
            StreamChunkSize = 16 * 1024,
            DelayPerChunkMs = 20
        };
        server.Map("/mutable.bin", route);

        var store = new JsonStateStore(stateDir);
        await store.SaveSettingsAsync(new AppSettings
        {
            DefaultDownloadDirectory = downloadDir,
            CategorizeIntoSubfolders = false,
            DefaultConnectionsPerDownload = 4
        });

        await using var manager = new DownloadManagerService(store);
        await manager.InitializeAsync();

        var task = await manager.AddDownloadAsync(new AddDownloadOptions($"{server.BaseUrl}/mutable.bin"));
        await WaitForStatusAsync(manager, task.Id, t => t.DownloadedBytes > 80_000);
        await manager.PauseAsync(task.Id);

        // Simulate remote file updating while paused
        route.Payload = newPayload;
        route.ETag = "\"v2\"";
        route.DelayPerChunkMs = 0;

        await manager.StartOrResumeAsync(task.Id);
        var done = await WaitForStatusAsync(manager, task.Id, t => t.Status == DownloadStatus.Completed);

        Assert.Equal(newPayload, await File.ReadAllBytesAsync(done.TargetFilePath));

        Directory.Delete(tempRoot, recursive: true);
    }
}
