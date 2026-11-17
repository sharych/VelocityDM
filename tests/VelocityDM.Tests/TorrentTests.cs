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
}
