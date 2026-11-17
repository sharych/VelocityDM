using System.Net;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.PiecePicking;
using VelocityDM.Core.Models;

namespace VelocityDM.Core.Services;

/// <summary>
/// Manages BitTorrent downloads (.torrent files and magnet links) via MonoTorrent ClientEngine.
/// </summary>
public sealed class TorrentManagerService : IAsyncDisposable
{
    private readonly JsonStateStore _store;
    private readonly bool _useEphemeralPorts;
    private readonly object _sync = new();
    private readonly List<(TorrentRecord Record, TorrentManager? Manager)> _entries = new();
    private readonly string _torrentCacheDir;
    private readonly string _torrentFilesDir;

    private ClientEngine? _engine;
    private AppSettings _settings = new();
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _disposed;

    public TorrentManagerService(JsonStateStore store, bool useEphemeralPorts = false)
    {
        _store = store;
        _useEphemeralPorts = useEphemeralPorts;
        _torrentCacheDir = Path.Combine(store.StorageDirectory, "torrent-cache");
        _torrentFilesDir = Path.Combine(store.StorageDirectory, "torrent-files");
        Directory.CreateDirectory(_torrentCacheDir);
        Directory.CreateDirectory(_torrentFilesDir);
    }

    public event Action<IReadOnlyList<TorrentStatusSnapshot>>? TorrentsUpdated;

    public async Task InitializeAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (_engine != null)
            return;

        _settings = settings.Clone();
        _engine = new ClientEngine(BuildEngineSettings(_settings));

        var saved = await _store.LoadTorrentsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in saved)
        {
            await AttachSavedRecordAsync(record).ConfigureAwait(false);
        }

        _monitorCts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(_monitorCts.Token));
        PublishSnapshots();
    }

    public async Task UpdateSettingsAsync(AppSettings settings)
    {
        _settings = settings.Clone();
        if (_engine != null)
        {
            await _engine.UpdateSettingsAsync(BuildEngineSettings(_settings)).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<TorrentStatusSnapshot> GetSnapshots()
    {
        lock (_sync)
        {
            return _entries.Select(e => CreateSnapshot(e.Record, e.Manager)).ToList();
        }
    }

    public async Task<TorrentStatusSnapshot> AddMagnetAsync(
        string magnetUri,
        string? customSaveDirectory = null,
        bool sequential = false,
        bool startImmediately = true)
    {
        EnsureInitialized();
        if (!MagnetLink.TryParse(magnetUri.Trim(), out var magnet))
        {
            throw new ArgumentException("Invalid magnet URI.", nameof(magnetUri));
        }

        if (_engine!.Contains(magnet.InfoHashes))
        {
            throw new InvalidOperationException("This torrent is already present in VelocityDM.");
        }

        string saveDir = ResolveSaveDir(customSaveDirectory);
        Directory.CreateDirectory(saveDir);

        var torrentSettings = new TorrentSettingsBuilder
        {
            MaximumConnections = _settings.TorrentMaxConnections,
            CreateContainingDirectory = true
        }.ToSettings();

        TorrentManager manager = sequential
            ? await _engine.AddStreamingAsync(magnet, saveDir, torrentSettings).ConfigureAwait(false)
            : await _engine.AddAsync(magnet, saveDir, torrentSettings).ConfigureAwait(false);

        var record = new TorrentRecord
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(magnet.Name) ? magnet.InfoHashes.V1OrV2.ToHex() : magnet.Name,
            InfoHashHex = magnet.InfoHashes.V1OrV2.ToHex(),
            MagnetUri = magnet.ToV1String(),
            SaveDirectory = saveDir,
            Sequential = sequential,
            IsPaused = !startImmediately,
            CreatedAt = DateTimeOffset.UtcNow
        };

        lock (_sync)
        {
            _entries.Add((record, manager));
        }

        if (startImmediately)
        {
            await manager.StartAsync().ConfigureAwait(false);
        }

        await PersistAsync().ConfigureAwait(false);
        PublishSnapshots();
        return CreateSnapshot(record, manager);
    }

    public async Task<TorrentStatusSnapshot> AddTorrentFileAsync(
        string torrentFilePath,
        string? customSaveDirectory = null,
        bool sequential = false,
        bool startImmediately = true)
    {
        EnsureInitialized();
        if (!File.Exists(torrentFilePath))
        {
            throw new FileNotFoundException("Torrent file was not found.", torrentFilePath);
        }

        Torrent torrent = await Torrent.LoadAsync(torrentFilePath).ConfigureAwait(false);
        if (_engine!.Contains(torrent))
        {
            throw new InvalidOperationException($"'{torrent.Name}' is already present in VelocityDM.");
        }

        string saveDir = ResolveSaveDir(customSaveDirectory);
        Directory.CreateDirectory(saveDir);

        string infoHex = torrent.InfoHashes.V1OrV2.ToHex();
        string cachedPath = Path.Combine(_torrentFilesDir, $"{infoHex}.torrent");
        File.Copy(torrentFilePath, cachedPath, overwrite: true);

        var torrentSettings = new TorrentSettingsBuilder
        {
            MaximumConnections = _settings.TorrentMaxConnections,
            CreateContainingDirectory = true
        }.ToSettings();

        TorrentManager manager = sequential
            ? await _engine.AddStreamingAsync(torrent, saveDir, torrentSettings).ConfigureAwait(false)
            : await _engine.AddAsync(torrent, saveDir, torrentSettings).ConfigureAwait(false);

        var record = new TorrentRecord
        {
            Id = Guid.NewGuid(),
            Name = torrent.Name,
            InfoHashHex = infoHex,
            CachedTorrentFilePath = cachedPath,
            MagnetUri = new MagnetLink(torrent.InfoHashes, torrent.Name, torrent.AnnounceUrls.SelectMany(t => t).ToList(), null, torrent.Size).ToV1String(),
            SaveDirectory = saveDir,
            Sequential = sequential,
            IsPaused = !startImmediately,
            CreatedAt = DateTimeOffset.UtcNow
        };

        lock (_sync)
        {
            _entries.Add((record, manager));
        }

        if (startImmediately)
        {
            await manager.StartAsync().ConfigureAwait(false);
        }

        await PersistAsync().ConfigureAwait(false);
        PublishSnapshots();
        return CreateSnapshot(record, manager);
    }

    public async Task StartOrResumeAsync(Guid id)
    {
        var entry = FindEntry(id);
        if (entry.Manager == null)
            return;

        entry.Record.IsPaused = false;
        await entry.Manager.StartAsync().ConfigureAwait(false);
        await PersistAsync().ConfigureAwait(false);
        PublishSnapshots();
    }

    public async Task PauseAsync(Guid id)
    {
        var entry = FindEntry(id);
        if (entry.Manager == null)
            return;

        entry.Record.IsPaused = true;
        if (entry.Manager.State is TorrentState.Downloading or TorrentState.Seeding or TorrentState.Metadata or TorrentState.Hashing)
        {
            await entry.Manager.PauseAsync().ConfigureAwait(false);
        }
        await PersistAsync().ConfigureAwait(false);
        PublishSnapshots();
    }

    public async Task StopAsync(Guid id)
    {
        var entry = FindEntry(id);
        if (entry.Manager == null)
            return;

        entry.Record.IsPaused = true;
        await entry.Manager.StopAsync().ConfigureAwait(false);
        await PersistAsync().ConfigureAwait(false);
        PublishSnapshots();
    }

    public async Task SetSequentialAsync(Guid id, bool sequential)
    {
        var entry = FindEntry(id);
        if (entry.Manager == null)
            return;

        entry.Record.Sequential = sequential;
        IPieceRequester requester = sequential
            ? new StreamingPieceRequester()
            : new StandardPieceRequester(new PieceRequesterSettings(
                allowPrioritisation: true,
                allowRandomised: true,
                allowRarestFirst: true,
                maximumDuplicateRequests: 2));
        await entry.Manager.ChangePickerAsync(requester).ConfigureAwait(false);
        await PersistAsync().ConfigureAwait(false);
        PublishSnapshots();
    }

    public async Task SetFilePriorityAsync(Guid id, string filePath, Priority priority)
    {
        var entry = FindEntry(id);
        if (entry.Manager == null)
            return;

        var file = entry.Manager.Files.FirstOrDefault(f => string.Equals(f.Path, filePath, StringComparison.OrdinalIgnoreCase));
        if (file != null)
        {
            await entry.Manager.SetFilePriorityAsync(file, priority).ConfigureAwait(false);
            PublishSnapshots();
        }
    }

    public async Task RemoveAsync(Guid id, bool deleteDownloadedData)
    {
        (TorrentRecord Record, TorrentManager? Manager) entry;
        lock (_sync)
        {
            int idx = _entries.FindIndex(e => e.Record.Id == id);
            if (idx < 0)
                return;
            entry = _entries[idx];
            _entries.RemoveAt(idx);
        }

        if (entry.Manager != null && _engine != null)
        {
            try
            {
                await entry.Manager.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
                // Continue removal even if stop times out
            }

            var mode = deleteDownloadedData ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly;
            await _engine.RemoveAsync(entry.Manager, mode).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(entry.Record.CachedTorrentFilePath) && File.Exists(entry.Record.CachedTorrentFilePath))
        {
            try { File.Delete(entry.Record.CachedTorrentFilePath); } catch { }
        }

        await PersistAsync().ConfigureAwait(false);
        PublishSnapshots();
    }

    private async Task AttachSavedRecordAsync(TorrentRecord record)
    {
        if (_engine == null)
            return;

        try
        {
            Directory.CreateDirectory(record.SaveDirectory);
            TorrentManager? manager = null;

            var settings = new TorrentSettingsBuilder
            {
                MaximumConnections = _settings.TorrentMaxConnections,
                CreateContainingDirectory = true
            }.ToSettings();

            if (!string.IsNullOrWhiteSpace(record.CachedTorrentFilePath) && File.Exists(record.CachedTorrentFilePath))
            {
                var torrent = await Torrent.LoadAsync(record.CachedTorrentFilePath).ConfigureAwait(false);
                if (!_engine.Contains(torrent))
                {
                    manager = record.Sequential
                        ? await _engine.AddStreamingAsync(torrent, record.SaveDirectory, settings).ConfigureAwait(false)
                        : await _engine.AddAsync(torrent, record.SaveDirectory, settings).ConfigureAwait(false);
                }
            }
            else if (!string.IsNullOrWhiteSpace(record.MagnetUri) && MagnetLink.TryParse(record.MagnetUri, out var magnet))
            {
                if (!_engine.Contains(magnet.InfoHashes))
                {
                    manager = record.Sequential
                        ? await _engine.AddStreamingAsync(magnet, record.SaveDirectory, settings).ConfigureAwait(false)
                        : await _engine.AddAsync(magnet, record.SaveDirectory, settings).ConfigureAwait(false);
                }
            }

            lock (_sync)
            {
                _entries.Add((record, manager));
            }

            if (manager != null && !record.IsPaused && _settings.AutoStartQueuedDownloads)
            {
                await manager.StartAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_sync)
            {
                _entries.Add((record, null));
            }
        }
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            List<(TorrentRecord Record, TorrentManager? Manager)> copy;
            lock (_sync)
            {
                copy = _entries.ToList();
            }

            bool dirty = false;
            foreach (var item in copy)
            {
                if (item.Manager == null) continue;

                if (item.Manager.HasMetadata && !string.IsNullOrWhiteSpace(item.Manager.Name) &&
                    !string.Equals(item.Record.Name, item.Manager.Name, StringComparison.Ordinal))
                {
                    item.Record.Name = item.Manager.Name;
                    dirty = true;
                }

                if (_settings.StopSeedingWhenComplete && item.Manager.State == TorrentState.Seeding && !item.Record.IsPaused)
                {
                    item.Record.IsPaused = true;
                    dirty = true;
                    _ = Task.Run(async () =>
                    {
                        try { await item.Manager.StopAsync().ConfigureAwait(false); } catch { }
                    }, CancellationToken.None);
                }
            }

            if (dirty)
            {
                try { await PersistAsync().ConfigureAwait(false); } catch { }
            }

            PublishSnapshots();
        }
    }

    private void PublishSnapshots() => TorrentsUpdated?.Invoke(GetSnapshots());

    private static TorrentStatusSnapshot CreateSnapshot(TorrentRecord record, TorrentManager? manager)
    {
        if (manager == null)
        {
            return new TorrentStatusSnapshot
            {
                Id = record.Id,
                Name = record.Name,
                InfoHashHex = record.InfoHashHex,
                MagnetUri = record.MagnetUri,
                SaveDirectory = record.SaveDirectory,
                State = TorrentState.Error,
                IsPaused = record.IsPaused,
                Sequential = record.Sequential,
                CreatedAt = record.CreatedAt,
                ErrorMessage = "Failed to load torrent metadata on startup."
            };
        }

        long totalBytes = manager.Torrent?.Size ?? manager.MagnetLink?.Size ?? 0;
        long downloadedBytes = totalBytes > 0
            ? (long)Math.Clamp(totalBytes * (manager.Progress / 100.0), 0, totalBytes)
            : manager.Monitor.DataBytesReceived;

        var files = manager.Files
            .Select(f => new TorrentFileSnapshot
            {
                Path = f.Path,
                FullPath = f.FullPath,
                Length = f.Length,
                ProgressPercent = f.BitField.PercentComplete,
                Priority = f.Priority
            })
            .ToList();

        return new TorrentStatusSnapshot
        {
            Id = record.Id,
            Name = string.IsNullOrWhiteSpace(manager.Name) ? record.Name : manager.Name,
            InfoHashHex = record.InfoHashHex,
            MagnetUri = record.MagnetUri,
            SaveDirectory = string.IsNullOrWhiteSpace(manager.ContainingDirectory) ? record.SaveDirectory : manager.ContainingDirectory,
            State = manager.State,
            IsPaused = record.IsPaused || manager.State is TorrentState.Paused or TorrentState.Stopped,
            Sequential = record.Sequential,
            ProgressPercent = Math.Clamp(manager.Progress, 0, 100),
            TotalBytes = totalBytes,
            DownloadedBytes = downloadedBytes,
            TotalUploadedBytes = manager.Monitor.DataBytesSent,
            DownloadSpeedBytesPerSec = manager.Monitor.DownloadRate,
            UploadSpeedBytesPerSec = manager.Monitor.UploadRate,
            ConnectedPeers = manager.OpenConnections,
            Seeds = manager.Peers.Seeds,
            Leechers = manager.Peers.Leechs,
            HasMetadata = manager.HasMetadata,
            ErrorMessage = manager.Error?.Exception?.Message ?? manager.Error?.Reason.ToString(),
            CreatedAt = record.CreatedAt,
            Files = files
        };
    }

    private (TorrentRecord Record, TorrentManager? Manager) FindEntry(Guid id)
    {
        lock (_sync)
        {
            return _entries.FirstOrDefault(e => e.Record.Id == id);
        }
    }

    private EngineSettings BuildEngineSettings(AppSettings settings)
    {
        var builder = new EngineSettingsBuilder
        {
            CacheDirectory = _torrentCacheDir,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            AllowLocalPeerDiscovery = !_useEphemeralPorts,
            AllowPortForwarding = !_useEphemeralPorts,
            MaximumConnections = settings.TorrentMaxConnections,
            MaximumDownloadRate = (int)Math.Clamp(settings.TorrentDownloadSpeedLimitBytesPerSec, 0, int.MaxValue),
            MaximumUploadRate = (int)Math.Clamp(settings.TorrentUploadSpeedLimitBytesPerSec, 0, int.MaxValue)
        };

        if (_useEphemeralPorts)
        {
            builder.ListenEndPoints["ipv4"] = new IPEndPoint(IPAddress.Loopback, 0);
            builder.DhtEndPoint = null;
        }

        return builder.ToSettings();
    }

    private string ResolveSaveDir(string? customDir)
    {
        if (!string.IsNullOrWhiteSpace(customDir))
            return customDir.Trim();

        return Path.Combine(_settings.DefaultDownloadDirectory, "Torrents");
    }

    private Task PersistAsync()
    {
        List<TorrentRecord> records;
        lock (_sync)
        {
            records = _entries.Select(e => e.Record).ToList();
        }
        return _store.SaveTorrentsAsync(records);
    }

    private void EnsureInitialized()
    {
        if (_engine == null)
            throw new InvalidOperationException("TorrentManagerService has not been initialized.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _monitorCts?.Cancel();
        if (_monitorTask != null)
        {
            try { await _monitorTask.ConfigureAwait(false); } catch { }
        }
        _monitorCts?.Dispose();

        if (_engine != null)
        {
            try
            {
                await _engine.StopAllAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort engine stop
            }
            _engine.Dispose();
            _engine = null;
        }
    }
}
