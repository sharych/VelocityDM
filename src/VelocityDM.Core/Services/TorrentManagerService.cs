using System.Collections.Concurrent;
using LibtorrentSharp;
using LibtorrentSharp.Alerts;
using LibtorrentSharp.Enums;
using VelocityDM.Core.Models;

namespace VelocityDM.Core.Services;

/// <summary>
/// Manages BitTorrent downloads (.torrent files and magnet links) using libtorrent-rasterbar via LibtorrentSharp.
/// Matches qBittorrent's protocol behavior: DHT, uTP, PeX, LSD, parallel multi-tracker announces,
/// automatic piece-hash verification/resume on existing binary files, and fast-resume persistence.
/// </summary>
public sealed class TorrentManagerService : IAsyncDisposable
{
    private static readonly string[] FallbackPublicTrackers =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.demonii.com:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://tracker.dler.org:6969/announce",
        "udp://exodus.desync.com:6969/announce"
    ];

    private sealed class Entry
    {
        public required TorrentRecord Record { get; init; }
        public ManagedTorrent? Managed { get; set; }
        public string? LoadError { get; set; }
        public DateTimeOffset LastResumeSaveUtc { get; set; } = DateTimeOffset.MinValue;
    }

    private readonly JsonStateStore _store;
    private readonly bool _useEphemeralPorts;
    private readonly object _sync = new();
    private readonly List<Entry> _entries = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingResumeSaves = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _torrentFilesDir;
    private readonly string _resumeDir;

    private LibtorrentSession? _session;
    private AppSettings _settings = new();
    private CancellationTokenSource? _backgroundCts;
    private Task? _alertTask;
    private Task? _monitorTask;
    private bool _disposed;

    public TorrentManagerService(JsonStateStore store, bool useEphemeralPorts = false)
    {
        _store = store;
        _useEphemeralPorts = useEphemeralPorts;
        _torrentFilesDir = Path.Combine(store.StorageDirectory, "torrent-files");
        _resumeDir = Path.Combine(store.StorageDirectory, "torrent-resume");
        Directory.CreateDirectory(_torrentFilesDir);
        Directory.CreateDirectory(_resumeDir);
    }

    public event Action<IReadOnlyList<TorrentStatusSnapshot>>? TorrentsUpdated;

    public Task InitializeAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        InitializeInternalAsync(settings, cancellationToken);

    private async Task InitializeInternalAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (_session != null)
            return;

        _settings = settings.Clone();
        _session = new LibtorrentSession(BuildSettingsPack(_settings));

        _backgroundCts = new CancellationTokenSource();
        _alertTask = Task.Run(() => AlertLoopAsync(_backgroundCts.Token));

        var saved = await _store.LoadTorrentsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in saved)
        {
            AttachSavedRecord(record);
        }

        _monitorTask = Task.Run(() => MonitorLoopAsync(_backgroundCts.Token));
        PublishSnapshots();
    }

    public Task UpdateSettingsAsync(AppSettings settings)
    {
        _settings = settings.Clone();
        if (_session != null)
        {
            _session.UpdateSettings(BuildSettingsPack(_settings));
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<TorrentStatusSnapshot> GetSnapshots()
    {
        lock (_sync)
        {
            return _entries.Select(CreateSnapshot).ToList();
        }
    }

    public int GetTrackerTierCount(Guid id)
    {
        var entry = FindEntry(id);
        return entry?.Managed?.GetTrackerCount() ?? 0;
    }

    public async Task<TorrentStatusSnapshot> AddMagnetAsync(
        string magnetUri,
        string? customSaveDirectory = null,
        bool sequential = false,
        bool startImmediately = true)
    {
        EnsureInitialized();
        var (infoHashHex, displayName, cleanMagnet, trackers) = ManagedTorrent.ParseMagnetComponents(magnetUri);

        EnsureNotDuplicate(infoHashHex, displayName ?? infoHashHex);

        string saveDir = ResolveSaveDir(customSaveDirectory);
        Directory.CreateDirectory(saveDir);

        var addResult = _session!.Add(new AddTorrentParams
        {
            MagnetUri = cleanMagnet,
            SavePath = saveDir
        });

        if (!addResult.IsValid || addResult.Magnet == null)
            throw new InvalidOperationException("libtorrent failed to attach magnet URI.");

        var managed = new ManagedTorrent(addResult.Magnet, saveDir);
        AttachParallelTrackers(managed, trackers, isPrivate: false);

        if (sequential)
            managed.SetSequential(true);

        if (startImmediately)
            managed.Resume();
        else
            managed.Pause();

        var record = new TorrentRecord
        {
            Id = Guid.NewGuid(),
            Name = displayName ?? infoHashHex,
            InfoHashHex = infoHashHex,
            MagnetUri = magnetUri.Trim(),
            SaveDirectory = saveDir,
            Sequential = sequential,
            IsPaused = !startImmediately,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var entry = new Entry { Record = record, Managed = managed };
        lock (_sync)
        {
            _entries.Add(entry);
        }

        await PersistAsync().ConfigureAwait(false);
        var snap = CreateSnapshot(entry);
        PublishSnapshots();
        return snap;
    }

    public async Task<TorrentStatusSnapshot> AddTorrentFileAsync(
        string torrentFilePath,
        string? customSaveDirectory = null,
        bool sequential = false,
        bool startImmediately = true)
    {
        EnsureInitialized();
        if (!File.Exists(torrentFilePath))
            throw new FileNotFoundException("Torrent file was not found.", torrentFilePath);

        byte[] rawBytes = await File.ReadAllBytesAsync(torrentFilePath).ConfigureAwait(false);
        var torrentInfo = new TorrentInfo(rawBytes);
        string infoHashHex = ExtractInfoHashHex(torrentInfo);
        string name = string.IsNullOrWhiteSpace(torrentInfo.Metadata.Name) ? infoHashHex : torrentInfo.Metadata.Name;

        EnsureNotDuplicate(infoHashHex, name);

        string saveDir = ResolveSaveDir(customSaveDirectory);
        Directory.CreateDirectory(saveDir);

        string cachedPath = Path.Combine(_torrentFilesDir, $"{infoHashHex}.torrent");
        await File.WriteAllBytesAsync(cachedPath, rawBytes).ConfigureAwait(false);

        var addResult = _session!.Add(new AddTorrentParams
        {
            TorrentInfo = torrentInfo,
            SavePath = saveDir
        });

        if (!addResult.IsValid || addResult.Torrent == null)
            throw new InvalidOperationException($"libtorrent failed to load '{name}'.");

        var managed = new ManagedTorrent(addResult.Torrent, torrentInfo, saveDir);
        bool isPrivate = ManagedTorrent.IsPrivateTorrentBytes(rawBytes);
        if (!isPrivate && managed.GetTrackerCount() < 3)
        {
            AttachParallelTrackers(managed, Array.Empty<string>(), isPrivate: false);
        }

        if (sequential)
            managed.SetSequential(true);

        if (startImmediately)
            managed.Resume();
        else
            managed.Pause();

        var record = new TorrentRecord
        {
            Id = Guid.NewGuid(),
            Name = name,
            InfoHashHex = infoHashHex,
            CachedTorrentFilePath = cachedPath,
            MagnetUri = $"magnet:?xt=urn:btih:{infoHashHex}&dn={Uri.EscapeDataString(name)}",
            SaveDirectory = saveDir,
            Sequential = sequential,
            IsPaused = !startImmediately,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var entry = new Entry { Record = record, Managed = managed };
        lock (_sync)
        {
            _entries.Add(entry);
        }

        await PersistAsync().ConfigureAwait(false);
        var snap = CreateSnapshot(entry);
        PublishSnapshots();
        return snap;
    }

    public async Task StartOrResumeAsync(Guid id)
    {
        var entry = FindEntry(id);
        if (entry?.Managed == null)
            return;

        entry.Record.IsPaused = false;
        entry.LoadError = null;
        entry.Managed.Resume();
        entry.Managed.ReannounceAll();
        await PersistAsync().ConfigureAwait(false);
        PublishSnapshots();
    }

    public async Task PauseAsync(Guid id)
    {
        var entry = FindEntry(id);
        if (entry?.Managed == null)
            return;

        entry.Record.IsPaused = true;
        entry.Managed.Pause();
        TryRequestResumeSave(entry);
        await PersistAsync().ConfigureAwait(false);
        PublishSnapshots();
    }

    public async Task StopAsync(Guid id) => await PauseAsync(id).ConfigureAwait(false);

    public async Task ForceRecheckAsync(Guid id)
    {
        var entry = FindEntry(id);
        if (entry?.Managed == null)
            return;

        entry.Managed.ForceRecheck();
        if (!entry.Record.IsPaused)
            entry.Managed.Resume();
        await PersistAsync().ConfigureAwait(false);
        PublishSnapshots();
    }

    public async Task SetSequentialAsync(Guid id, bool sequential)
    {
        var entry = FindEntry(id);
        if (entry?.Managed == null)
            return;

        entry.Record.Sequential = sequential;
        entry.Managed.SetSequential(sequential);
        await PersistAsync().ConfigureAwait(false);
        PublishSnapshots();
    }

    public Task SetFilePriorityAsync(Guid id, string filePath, TorrentPriority priority)
    {
        var entry = FindEntry(id);
        if (entry?.Managed == null)
            return Task.CompletedTask;

        if (entry.Managed.SetFilePriority(filePath, priority))
            PublishSnapshots();

        return Task.CompletedTask;
    }

    public async Task RemoveAsync(Guid id, bool deleteDownloadedData)
    {
        Entry? entry;
        lock (_sync)
        {
            int idx = _entries.FindIndex(e => e.Record.Id == id);
            if (idx < 0)
                return;
            entry = _entries[idx];
            _entries.RemoveAt(idx);
        }

        if (entry.Managed != null && _session != null)
        {
            try
            {
                entry.Managed.Pause();
                entry.Managed.Detach(_session, deleteDownloadedData);
            }
            catch
            {
                // Continue cleanup on detach error
            }
        }

        DeleteFileQuietly(entry.Record.CachedTorrentFilePath);
        DeleteFileQuietly(entry.Record.ResumeDataFilePath);

        await PersistAsync().ConfigureAwait(false);
        PublishSnapshots();
    }

    private void AttachSavedRecord(TorrentRecord record)
    {
        if (_session == null)
            return;

        try
        {
            Directory.CreateDirectory(record.SaveDirectory);

            ManagedTorrent? managed = null;
            byte[]? resumeBytes = !string.IsNullOrWhiteSpace(record.ResumeDataFilePath) && File.Exists(record.ResumeDataFilePath)
                ? File.ReadAllBytes(record.ResumeDataFilePath)
                : null;

            if (resumeBytes is { Length: > 0 })
            {
                var res = _session.Add(new AddTorrentParams { ResumeData = resumeBytes, SavePath = record.SaveDirectory });
                if (res.IsValid)
                {
                    if (res.Torrent != null)
                        managed = new ManagedTorrent(res.Torrent, null, record.SaveDirectory);
                    else if (res.Magnet != null)
                        managed = new ManagedTorrent(res.Magnet, record.SaveDirectory);
                }
            }

            if (managed == null && !string.IsNullOrWhiteSpace(record.CachedTorrentFilePath) && File.Exists(record.CachedTorrentFilePath))
            {
                byte[] torrentBytes = File.ReadAllBytes(record.CachedTorrentFilePath);
                var torrentInfo = new TorrentInfo(torrentBytes);
                var res = _session.Add(new AddTorrentParams { TorrentInfo = torrentInfo, SavePath = record.SaveDirectory });
                if (res.IsValid && res.Torrent != null)
                {
                    managed = new ManagedTorrent(res.Torrent, torrentInfo, record.SaveDirectory);
                    if (!ManagedTorrent.IsPrivateTorrentBytes(torrentBytes) && managed.GetTrackerCount() < 3)
                        AttachParallelTrackers(managed, Array.Empty<string>(), isPrivate: false);
                }
            }

            if (managed == null && !string.IsNullOrWhiteSpace(record.MagnetUri))
            {
                var (_, _, cleanMagnet, trackers) = ManagedTorrent.ParseMagnetComponents(record.MagnetUri);
                var res = _session.Add(new AddTorrentParams { MagnetUri = cleanMagnet, SavePath = record.SaveDirectory });
                if (res.IsValid && res.Magnet != null)
                {
                    managed = new ManagedTorrent(res.Magnet, record.SaveDirectory);
                    AttachParallelTrackers(managed, trackers, isPrivate: false);
                }
            }

            if (managed != null)
            {
                managed.SetSequential(record.Sequential);
                if (!record.IsPaused && _settings.AutoStartQueuedDownloads)
                    managed.Resume();
                else
                    managed.Pause();
            }

            lock (_sync)
            {
                _entries.Add(new Entry
                {
                    Record = record,
                    Managed = managed,
                    LoadError = managed == null ? "Failed to restore torrent session handle." : null
                });
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _entries.Add(new Entry
                {
                    Record = record,
                    Managed = null,
                    LoadError = $"Failed to restore torrent: {ex.Message}"
                });
            }
        }
    }

    private async Task AlertLoopAsync(CancellationToken ct)
    {
        if (_session == null)
            return;

        try
        {
            await foreach (var alert in _session.Alerts.WithCancellation(ct).ConfigureAwait(false))
            {
                try
                {
                    await HandleAlertAsync(alert).ConfigureAwait(false);
                }
                catch
                {
                    // Isolate individual alert exceptions so pump stays alive
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch
        {
            // Session disposed
        }
    }

    private async Task HandleAlertAsync(Alert alert)
    {
        switch (alert)
        {
            case MetadataReceivedAlert metaAlert:
            {
                string hash = metaAlert.InfoHash.ToString().ToUpperInvariant();
                Entry? entry = FindEntryByInfoHash(hash);
                if (entry?.Managed != null)
                {
                    TryUpdateRecordNameFromHandle(entry);
                    TryRequestResumeSave(entry);
                }
                break;
            }

            case ResumeDataReadyAlert resumeAlert:
            {
                string hash = resumeAlert.InfoHash.ToString().ToUpperInvariant();
                Entry? entry = FindEntryByInfoHash(hash);
                if (entry != null && resumeAlert.ResumeData is { Length: > 0 } data)
                {
                    string path = Path.Combine(_resumeDir, $"{entry.Record.Id}.resume");
                    await File.WriteAllBytesAsync(path, data).ConfigureAwait(false);
                    entry.Record.ResumeDataFilePath = path;
                    entry.LastResumeSaveUtc = DateTimeOffset.UtcNow;

                    string? bencodedName = ManagedTorrent.ExtractBencodedName(data);
                    if (!string.IsNullOrWhiteSpace(bencodedName) && LooksLikeRawHash(entry.Record.Name, entry.Record.InfoHashHex))
                        entry.Record.Name = bencodedName;

                    await PersistAsync().ConfigureAwait(false);
                }

                if (_pendingResumeSaves.TryRemove(hash, out var tcs))
                    tcs.TrySetResult(true);
                break;
            }

            case TorrentFinishedAlert finishedAlert:
            {
                string hash = finishedAlert.InfoHash.ToString().ToUpperInvariant();
                Entry? entry = FindEntryByInfoHash(hash);
                if (entry?.Managed != null)
                {
                    TryRequestResumeSave(entry);
                    if (_settings.StopSeedingWhenComplete && !entry.Record.IsPaused)
                    {
                        entry.Record.IsPaused = true;
                        entry.Managed.Pause();
                        await PersistAsync().ConfigureAwait(false);
                    }
                }
                break;
            }

            case SaveResumeDataFailedAlert failedAlert:
            {
                string hash = failedAlert.InfoHash.ToString().ToUpperInvariant();
                if (_pendingResumeSaves.TryRemove(hash, out var tcs))
                    tcs.TrySetResult(false);
                break;
            }
        }
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            List<Entry> copy;
            lock (_sync)
            {
                copy = _entries.ToList();
            }

            bool dirty = false;
            var now = DateTimeOffset.UtcNow;

            foreach (var entry in copy)
            {
                if (entry.Managed == null || !entry.Managed.IsValid)
                    continue;

                if (TryUpdateRecordNameFromHandle(entry))
                    dirty = true;

                TorrentStatus status;
                try
                {
                    status = entry.Managed.GetStatus();
                }
                catch
                {
                    continue;
                }

                if (_settings.StopSeedingWhenComplete &&
                    !entry.Record.IsPaused &&
                    status.State is LibtorrentSharp.Enums.TorrentState.Seeding or LibtorrentSharp.Enums.TorrentState.Finished)
                {
                    entry.Record.IsPaused = true;
                    entry.Managed.Pause();
                    TryRequestResumeSave(entry);
                    dirty = true;
                }
                else if (entry.Managed.HasMetadata &&
                         !entry.Record.IsPaused &&
                         (now - entry.LastResumeSaveUtc).TotalSeconds >= 30)
                {
                    entry.LastResumeSaveUtc = now;
                    TryRequestResumeSave(entry);
                }
            }

            if (dirty)
            {
                try { await PersistAsync().ConfigureAwait(false); } catch { }
            }

            PublishSnapshots();
        }
    }

    private void TryRequestResumeSave(Entry entry)
    {
        if (_session == null || entry.Managed == null || !entry.Managed.IsValid || !entry.Managed.HasMetadata)
            return;

        try
        {
            entry.Managed.RequestResumeData(_session);
        }
        catch
        {
            // Ignore transient request failures
        }
    }

    private bool TryUpdateRecordNameFromHandle(Entry entry)
    {
        if (entry.Managed == null || !entry.Managed.HasMetadata)
            return false;

        string? resolved = entry.Managed.TryResolveDisplayName();
        if (string.IsNullOrWhiteSpace(resolved) || string.Equals(entry.Record.Name, resolved, StringComparison.Ordinal))
            return false;

        if (LooksLikeRawHash(entry.Record.Name, entry.Record.InfoHashHex) || string.IsNullOrWhiteSpace(entry.Record.Name))
        {
            entry.Record.Name = resolved;
            return true;
        }

        return false;
    }

    private void PublishSnapshots() => TorrentsUpdated?.Invoke(GetSnapshots());

    private static TorrentStatusSnapshot CreateSnapshot(Entry entry)
    {
        var record = entry.Record;
        var managed = entry.Managed;

        if (managed == null || !managed.IsValid)
        {
            return new TorrentStatusSnapshot
            {
                Id = record.Id,
                Name = record.Name,
                InfoHashHex = record.InfoHashHex,
                MagnetUri = record.MagnetUri,
                SaveDirectory = record.SaveDirectory,
                State = TorrentDownloadState.Error,
                IsPaused = record.IsPaused,
                Sequential = record.Sequential,
                CreatedAt = record.CreatedAt,
                ErrorMessage = entry.LoadError ?? "Failed to initialize torrent handle."
            };
        }

        TorrentStatus status;
        try
        {
            status = managed.GetStatus();
        }
        catch (Exception ex)
        {
            return new TorrentStatusSnapshot
            {
                Id = record.Id,
                Name = record.Name,
                InfoHashHex = record.InfoHashHex,
                MagnetUri = record.MagnetUri,
                SaveDirectory = record.SaveDirectory,
                State = TorrentDownloadState.Error,
                IsPaused = record.IsPaused,
                Sequential = record.Sequential,
                CreatedAt = record.CreatedAt,
                ErrorMessage = ex.Message
            };
        }

        var state = ManagedTorrent.MapState(status, record.IsPaused);
        long totalBytes = managed.TotalSize;
        double progressPct = Math.Clamp(status.Progress * 100.0, 0.0, 100.0);

        long downloadedBytes = state == TorrentDownloadState.Hashing
            ? 0
            : (totalBytes > 0
                ? (long)Math.Clamp(Math.Round(status.Progress * totalBytes), 0, totalBytes)
                : Math.Max(status.AllTimeDownloaded, status.BytesDownloaded));

        long uploadedBytes = Math.Max(status.AllTimeUploaded, status.BytesUploaded);

        string displayName = !string.IsNullOrWhiteSpace(record.Name)
            ? record.Name
            : (managed.TryResolveDisplayName() ?? record.InfoHashHex);

        return new TorrentStatusSnapshot
        {
            Id = record.Id,
            Name = displayName,
            InfoHashHex = record.InfoHashHex,
            MagnetUri = record.MagnetUri,
            SaveDirectory = string.IsNullOrWhiteSpace(status.SavePath) ? record.SaveDirectory : status.SavePath,
            State = state,
            IsPaused = record.IsPaused || state == TorrentDownloadState.Paused,
            Sequential = record.Sequential,
            ProgressPercent = progressPct,
            TotalBytes = totalBytes,
            DownloadedBytes = downloadedBytes,
            TotalUploadedBytes = uploadedBytes,
            DownloadSpeedBytesPerSec = Math.Max(0, status.DownloadRate),
            UploadSpeedBytesPerSec = Math.Max(0, status.UploadRate),
            ConnectedPeers = status.PeerCount,
            Seeds = status.SeedCount,
            Leechers = Math.Max(0, status.PeerCount - status.SeedCount),
            HasMetadata = managed.HasMetadata,
            ErrorMessage = string.IsNullOrWhiteSpace(status.ErrorMessage) ? null : status.ErrorMessage,
            CreatedAt = record.CreatedAt,
            Files = managed.GetFileSnapshots(status.Progress)
        };
    }

    private Entry? FindEntry(Guid id)
    {
        lock (_sync)
        {
            return _entries.FirstOrDefault(e => e.Record.Id == id);
        }
    }

    private Entry? FindEntryByInfoHash(string infoHashHex)
    {
        lock (_sync)
        {
            return _entries.FirstOrDefault(e => string.Equals(e.Record.InfoHashHex, infoHashHex, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void EnsureNotDuplicate(string infoHashHex, string label)
    {
        lock (_sync)
        {
            if (_entries.Any(e => string.Equals(e.Record.InfoHashHex, infoHashHex, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"'{label}' is already present in VelocityDM.");
        }
    }

    private SettingsPack BuildSettingsPack(AppSettings settings)
    {
        var pack = new SettingsPack();
        pack.Set("user_agent", "qBittorrent/5.0.0");
        pack.Set("peer_fingerprint", "-qB5000-");
        pack.Set("listen_interfaces", _useEphemeralPorts ? "127.0.0.1:0" : "0.0.0.0:0");

        bool networkingEnabled = !_useEphemeralPorts;
        pack.Set("enable_dht", networkingEnabled);
        pack.Set("enable_lsd", networkingEnabled);
        pack.Set("enable_upnp", networkingEnabled);
        pack.Set("enable_natpmp", networkingEnabled);
        pack.Set("dht_bootstrap_nodes", "dht.libtorrent.org:25401,dht.transmissionbt.com:6881,router.bittorrent.com:6881,router.utorrent.com:6881");

        // qBittorrent / high-throughput tuning: parallel multi-tracker announces, fast peering, prefer TCP with uTP enabled
        pack.Set("announce_to_all_trackers", true);
        pack.Set("announce_to_all_tiers", true);
        pack.Set("connections_limit", Math.Clamp(settings.TorrentMaxConnections, 10, 1000));
        pack.Set("connection_speed", 50);
        pack.Set("mixed_mode_algorithm", 0); // prefer_tcp
        pack.Set("enable_outgoing_tcp", true);
        pack.Set("enable_incoming_tcp", true);
        pack.Set("enable_outgoing_utp", true);
        pack.Set("enable_incoming_utp", true);
        pack.Set("out_enc_policy", 1); // pe_enabled
        pack.Set("in_enc_policy", 1);
        pack.Set("allowed_enc_level", 3);
        pack.Set("max_out_request_queue", 1500);
        pack.Set("max_allowed_in_request_queue", 2000);
        pack.Set("request_queue_time", 3);
        pack.Set("aio_threads", 8);
        pack.Set("checking_mem_usage", 512);
        pack.Set("active_downloads", -1);
        pack.Set("active_seeds", -1);
        pack.Set("active_limit", -1);

        pack.Set("download_rate_limit", (int)Math.Clamp(settings.TorrentDownloadSpeedLimitBytesPerSec, 0, int.MaxValue));
        pack.Set("upload_rate_limit", (int)Math.Clamp(settings.TorrentUploadSpeedLimitBytesPerSec, 0, int.MaxValue));
        pack.Set("alert_mask", (int)(AlertCategories.Error | AlertCategories.Status | AlertCategories.Tracker | AlertCategories.Storage));

        return pack;
    }

    private static void AttachParallelTrackers(ManagedTorrent managed, IReadOnlyList<string> explicitTrackers, bool isPrivate)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int tier = 0;

        foreach (var raw in explicitTrackers)
        {
            string url = raw.Trim();
            if (!string.IsNullOrEmpty(url) && seen.Add(url))
                managed.AddTracker(url, tier++);
        }

        if (!isPrivate && seen.Count + managed.GetTrackerCount() < 3)
        {
            foreach (var fallback in FallbackPublicTrackers)
            {
                if (seen.Add(fallback))
                    managed.AddTracker(fallback, tier++);
            }
        }
    }

    private static string ExtractInfoHashHex(TorrentInfo info)
    {
        if (info.Metadata.Hashes is { } h)
        {
            if (!string.IsNullOrWhiteSpace(h.PreferredHex))
                return h.PreferredHex.ToUpperInvariant();
            if (h.V1 is { } v1)
                return v1.ToString().ToUpperInvariant();
        }
        return Guid.NewGuid().ToString("N").ToUpperInvariant();
    }

    private static bool LooksLikeRawHash(string? value, string infoHashHex) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value.Trim(), infoHashHex, StringComparison.OrdinalIgnoreCase) ||
        (value.Length == 40 && value.All(Uri.IsHexDigit));

    private string ResolveSaveDir(string? customDir)
    {
        if (!string.IsNullOrWhiteSpace(customDir))
            return customDir.Trim();

        return Path.Combine(_settings.DefaultDownloadDirectory, "Torrents");
    }

    private static void DeleteFileQuietly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try { File.Delete(path); } catch { }
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
        if (_session == null)
            throw new InvalidOperationException("TorrentManagerService has not been initialized.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_session != null)
        {
            var waitTasks = new List<Task>();
            List<Entry> snapshot;
            lock (_sync)
            {
                snapshot = _entries.ToList();
            }

            foreach (var entry in snapshot)
            {
                if (entry.Managed != null && entry.Managed.IsValid && entry.Managed.HasMetadata)
                {
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingResumeSaves[entry.Record.InfoHashHex] = tcs;
                    waitTasks.Add(tcs.Task);
                    TryRequestResumeSave(entry);
                }
            }

            if (waitTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAny(Task.WhenAll(waitTasks), Task.Delay(1500)).ConfigureAwait(false);
                }
                catch
                {
                    // Proceed with teardown on timeout
                }
            }
        }

        _backgroundCts?.Cancel();
        if (_monitorTask != null)
        {
            try { await _monitorTask.ConfigureAwait(false); } catch { }
        }
        if (_alertTask != null)
        {
            try { await _alertTask.ConfigureAwait(false); } catch { }
        }
        _backgroundCts?.Dispose();

        _session?.Dispose();
        _session = null;
    }
}
