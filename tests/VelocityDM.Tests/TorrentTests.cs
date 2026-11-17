using LibtorrentSharp;
using VelocityDM.Core.Models;
using VelocityDM.Core.Services;
using Xunit;

namespace VelocityDM.Tests;

public sealed class TorrentTests
{
    [Fact]
    public async Task TorrentManagerService_PersistsMagnetPauseSequentialAndRemovalAcrossRestarts()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "vdm_tor_" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(tempRoot, "state");
        var dlDir = Path.Combine(tempRoot, "downloads");

        var settings = new AppSettings
        {
            DefaultDownloadDirectory = dlDir,
            AutoStartQueuedDownloads = true
        };

        const string sampleMagnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=VelocityDM-Sample-ISO";

        Guid torrentId;
        await using (var svc1 = new TorrentManagerService(new JsonStateStore(stateDir), useEphemeralPorts: true))
        {
            await svc1.InitializeAsync(settings);

            var added = await svc1.AddMagnetAsync(sampleMagnet, startImmediately: false);
            torrentId = added.Id;

            Assert.Equal("VelocityDM-Sample-ISO", added.Name);
            Assert.True(added.IsPaused);
            Assert.False(added.Sequential);

            await svc1.SetSequentialAsync(torrentId, true);
            var updated = svc1.GetSnapshots().Single(t => t.Id == torrentId);
            Assert.True(updated.Sequential);
        }

        // Reload in a brand new TorrentManagerService instance to verify persistence
        await using (var svc2 = new TorrentManagerService(new JsonStateStore(stateDir), useEphemeralPorts: true))
        {
            await svc2.InitializeAsync(settings);

            var reloaded = svc2.GetSnapshots();
            Assert.Single(reloaded);
            Assert.Equal(torrentId, reloaded[0].Id);
            Assert.Equal("VelocityDM-Sample-ISO", reloaded[0].Name);
            Assert.True(reloaded[0].Sequential);
            Assert.True(reloaded[0].IsPaused);

            await svc2.RemoveAsync(torrentId, deleteDownloadedData: false);
            Assert.Empty(svc2.GetSnapshots());
        }

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task TorrentManagerService_SplitsMagnetTrackersIntoParallelTiersAndAddsFallbacksWhenEmpty()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "vdm_tiers_" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(tempRoot, "state");
        var settings = new AppSettings { DefaultDownloadDirectory = Path.Combine(tempRoot, "downloads") };

        await using var svc = new TorrentManagerService(new JsonStateStore(stateDir), useEphemeralPorts: true);
        await svc.InitializeAsync(settings);

        string multiTrackerMagnet =
            "magnet:?xt=urn:btih:1111111111111111111111111111111111111111&dn=Multi" +
            "&tr=udp%3A%2F%2Ftracker.example1.org%3A1337%2Fannounce" +
            "&tr=udp%3A%2F%2Ftracker.example2.org%3A6969%2Fannounce" +
            "&tr=udp%3A%2F%2Ftracker.example1.org%3A1337%2Fannounce" +
            "&tr=https%3A%2F%2Ftracker.example3.org%2Fannounce" +
            "&tr=http%3A%2F%2Ftracker.example4.org%3A80%2Fannounce";

        var t1 = await svc.AddMagnetAsync(multiTrackerMagnet, startImmediately: false);
        Assert.Equal(4, svc.GetTrackerTierCount(t1.Id));

        string trackerlessMagnet = "magnet:?xt=urn:btih:2222222222222222222222222222222222222222&dn=Trackerless";
        var t2 = await svc.AddMagnetAsync(trackerlessMagnet, startImmediately: false);
        Assert.True(svc.GetTrackerTierCount(t2.Id) >= 5);

        await svc.RemoveAsync(t1.Id, false);
        await svc.RemoveAsync(t2.Id, false);
        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task TorrentManagerService_HashChecksExistingBinaryAndResumesVerifiedPieces()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "vdm_recheck_" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(tempRoot, "state");
        var sourceDir = Path.Combine(tempRoot, "src");
        var dlDir = Path.Combine(tempRoot, "dl");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(dlDir);

        // Create a deterministic 512 KiB binary payload and generate its .torrent metadata (16 KiB pieces).
        const int totalBytes = 512 * 1024;
        byte[] payload = new byte[totalBytes];
        new Random(42).NextBytes(payload);

        string sourceFile = Path.Combine(sourceDir, "payload.bin");
        await File.WriteAllBytesAsync(sourceFile, payload);

        string torrentFile = Path.Combine(tempRoot, "payload.torrent");
        await TorrentCreator.CreateAsync(new CreateTorrentParams
        {
            SourcePath = sourceFile,
            OutputPath = torrentFile,
            PieceSize = 16 * 1024
        });

        // Pre-seed target download folder with only the valid first half (256 KiB) + corrupted second half.
        byte[] partial = new byte[totalBytes];
        Array.Copy(payload, 0, partial, 0, totalBytes / 2);
        await File.WriteAllBytesAsync(Path.Combine(dlDir, "payload.bin"), partial);

        var settings = new AppSettings { DefaultDownloadDirectory = dlDir };
        await using var svc = new TorrentManagerService(new JsonStateStore(stateDir), useEphemeralPorts: true);
        await svc.InitializeAsync(settings);

        var added = await svc.AddTorrentFileAsync(torrentFile, customSaveDirectory: dlDir, startImmediately: true);

        // Wait up to 8s for libtorrent CheckingFiles pass to verify existing pieces on disk.
        TorrentStatusSnapshot? checkedSnap = null;
        for (int i = 0; i < 40; i++)
        {
            checkedSnap = svc.GetSnapshots().Single(t => t.Id == added.Id);
            if (checkedSnap.State != TorrentDownloadState.Hashing && checkedSnap.ProgressPercent > 40.0)
                break;
            await Task.Delay(200);
        }

        Assert.NotNull(checkedSnap);
        Assert.InRange(checkedSnap!.ProgressPercent, 48.0, 52.0);
        Assert.InRange(checkedSnap.DownloadedBytes, (totalBytes / 2) - 16384, (totalBytes / 2) + 16384);
        Assert.Single(checkedSnap.Files);

        await svc.SetFilePriorityAsync(added.Id, checkedSnap.Files[0].Path, TorrentPriority.High);
        var afterPrio = svc.GetSnapshots().Single(t => t.Id == added.Id);
        Assert.Equal(TorrentPriority.High, afterPrio.Files[0].Priority);

        await svc.ForceRecheckAsync(added.Id);
        await Task.Delay(600);
        var afterRecheck = svc.GetSnapshots().Single(t => t.Id == added.Id);
        Assert.InRange(afterRecheck.ProgressPercent, 48.0, 52.0);

        await svc.RemoveAsync(added.Id, deleteDownloadedData: true);
        Assert.Empty(svc.GetSnapshots());

        await Task.Delay(250);
        Directory.Delete(tempRoot, recursive: true);
    }
}
