using System.Collections.Concurrent;
using VelocityDM.Core.Models;

namespace VelocityDM.Core.Services;

public sealed record AddDownloadOptions(
    string Url,
    string? CustomFileName = null,
    string? CustomDirectory = null,
    FileCategory? CategoryOverride = null,
    int? Connections = null,
    string? ExpectedSha256 = null,
    bool StartImmediately = true,
    string? Referrer = null,
    string? CookieHeader = null,
    string? UserAgentOverride = null);

/// <summary>
/// Orchestrates queue scheduling, concurrent download limits, pause/resume/cancel, persistence, and settings.
/// </summary>
public sealed class DownloadManagerService : IAsyncDisposable
{
    private readonly JsonStateStore _store;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly HttpProbeService _probeService;
    private readonly HttpDownloadRunner _runner;
    private readonly TokenBucketRateLimiter _rateLimiter;

    private readonly object _stateLock = new();
    private readonly List<DownloadTask> _tasks = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeCts = new();
    private readonly ConcurrentDictionary<Guid, Task> _activeRuns = new();

    private AppSettings _settings = new();
    private int _nextQueueOrder = 1;
    private bool _initialized;
    private bool _disposed;

    public DownloadManagerService(JsonStateStore? store = null, HttpClient? httpClient = null)
    {
        _store = store ?? new JsonStateStore();
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? CreateDefaultHttpClient();
        _rateLimiter = new TokenBucketRateLimiter(0);
        _probeService = new HttpProbeService(_httpClient);
        _runner = new HttpDownloadRunner(_httpClient, _probeService, _rateLimiter);
    }

    public event Action<DownloadTask>? TaskAdded;
    public event Action<DownloadTask>? TaskUpdated;
    public event Action<Guid>? TaskRemoved;
    public event Action<AppSettings>? SettingsUpdated;

    public AppSettings Settings
    {
        get
        {
            lock (_stateLock)
            {
                return _settings.Clone();
            }
        }
    }

    public IReadOnlyList<DownloadTask> GetTasksSnapshot()
    {
        lock (_stateLock)
        {
            return _tasks.Select(t => t.CloneSnapshot()).ToList();
        }
    }

    public DownloadTask? GetTaskSnapshot(Guid id)
    {
        lock (_stateLock)
        {
            return _tasks.FirstOrDefault(t => t.Id == id)?.CloneSnapshot();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        _settings = await _store.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        _rateLimiter.UpdateLimit(_settings.GlobalSpeedLimitBytesPerSec);
        _httpClient.Timeout = TimeSpan.FromSeconds(_settings.RequestTimeoutSeconds);

        var loaded = await _store.LoadDownloadsAsync(cancellationToken).ConfigureAwait(false);
        lock (_stateLock)
        {
            _tasks.Clear();
            _tasks.AddRange(loaded);
            _nextQueueOrder = _tasks.Count == 0 ? 1 : _tasks.Max(t => t.QueueOrder) + 1;
            _initialized = true;
        }

        await PersistTasksAsync().ConfigureAwait(false);
        if (_settings.AutoStartQueuedDownloads)
        {
            PumpQueue();
        }
    }

    public Task<ProbeResult> ProbeUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        return _probeService.ProbeAsync(url, cancellationToken);
    }

    public async Task<DownloadTask> AddDownloadAsync(AddDownloadOptions options, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(options.Url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Please enter a valid HTTP or HTTPS URL.", nameof(options));
        }

        AppSettings currentSettings = Settings;
        string initialName = string.IsNullOrWhiteSpace(options.CustomFileName)
            ? GuessInitialFileName(uri)
            : FileCategoryClassifier.SanitizeFileName(options.CustomFileName);

        FileCategory category = options.CategoryOverride ?? FileCategoryClassifier.Classify(initialName);
        string saveDir = !string.IsNullOrWhiteSpace(options.CustomDirectory)
            ? options.CustomDirectory.Trim()
            : FileCategoryClassifier.ResolveSaveDirectory(
                currentSettings.DefaultDownloadDirectory,
                category,
                currentSettings.CategorizeIntoSubfolders);

        DownloadTask task;
        lock (_stateLock)
        {
            var reserved = GetReservedPathsLocked();
            var targetPath = FileCategoryClassifier.GetUniqueFilePath(saveDir, initialName, reserved);

            task = new DownloadTask
            {
                Url = uri.AbsoluteUri,
                FileName = Path.GetFileName(targetPath),
                HasCustomFileName = !string.IsNullOrWhiteSpace(options.CustomFileName),
                SaveDirectory = saveDir,
                HasCustomSaveDirectory = !string.IsNullOrWhiteSpace(options.CustomDirectory),
                TargetFilePath = targetPath,
                TempFilePath = targetPath + ".vdm.part",
                Category = category,
                HasCustomCategory = options.CategoryOverride.HasValue,
                MaxConnections = Math.Clamp(options.Connections ?? currentSettings.DefaultConnectionsPerDownload, 1, 32),
                ExpectedSha256 = HashService.NormalizeHex(options.ExpectedSha256),
                Referrer = options.Referrer,
                CookieHeader = options.CookieHeader,
                UserAgentOverride = options.UserAgentOverride,
                Status = options.StartImmediately ? DownloadStatus.Queued : DownloadStatus.Paused,
                CreatedAt = DateTimeOffset.UtcNow,
                QueueOrder = _nextQueueOrder++
            };

            _tasks.Add(task);
        }

        await PersistTasksAsync().ConfigureAwait(false);
        var snap = task.CloneSnapshot();
        TaskAdded?.Invoke(snap);

        if (options.StartImmediately)
        {
            PumpQueue();
        }

        return snap;
    }

    public async Task<IReadOnlyList<DownloadTask>> AddBatchDownloadsAsync(
        IEnumerable<string> urls,
        string? customDirectory = null,
        int? connections = null,
        bool startImmediately = true)
    {
        var added = new List<DownloadTask>();
        foreach (var url in urls)
        {
            var item = await AddDownloadAsync(new AddDownloadOptions(
                Url: url,
                CustomDirectory: customDirectory,
                Connections: connections,
                StartImmediately: false)).ConfigureAwait(false);
            added.Add(item);
        }

        if (startImmediately && added.Count > 0)
        {
            lock (_stateLock)
            {
                foreach (var created in added)
                {
                    var real = _tasks.FirstOrDefault(t => t.Id == created.Id);
                    if (real != null && real.Status == DownloadStatus.Paused)
                    {
                        real.Status = DownloadStatus.Queued;
                    }
                }
            }
            await PersistTasksAsync().ConfigureAwait(false);
            foreach (var created in added)
            {
                var updated = GetTaskSnapshot(created.Id);
                if (updated != null) TaskUpdated?.Invoke(updated);
            }
            PumpQueue();
        }

        return added;
    }

    public async Task StartOrResumeAsync(Guid taskId, bool forceStart = false)
    {
        DownloadTask? target;
        lock (_stateLock)
        {
            target = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (target == null)
                return;

            if (target.Status is DownloadStatus.Downloading or DownloadStatus.Probing or DownloadStatus.Completed)
                return;

            if (_activeRuns.ContainsKey(taskId))
                return;

            target.ErrorMessage = null;
            target.Status = forceStart ? DownloadStatus.Probing : DownloadStatus.Queued;
        }

        await PersistTasksAsync().ConfigureAwait(false);
        TaskUpdated?.Invoke(target.CloneSnapshot());

        if (forceStart)
        {
            LaunchTask(target);
        }
        else
        {
            PumpQueue();
        }
    }

    public async Task PauseAsync(Guid taskId)
    {
        if (_activeCts.TryGetValue(taskId, out var cts))
        {
            cts.Cancel();
            if (_activeRuns.TryGetValue(taskId, out var running))
            {
                try
                {
                    await running.ConfigureAwait(false);
                }
                catch
                {
                    // Expected cancellation or error cleanup handled by runner wrapper
                }
            }
        }

        DownloadTask? snapshot = null;
        lock (_stateLock)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (task != null && task.Status != DownloadStatus.Completed)
            {
                task.Status = DownloadStatus.Paused;
                task.SpeedBytesPerSec = 0;
                foreach (var seg in task.Segments)
                {
                    if (seg.Status == SegmentStatus.Active)
                        seg.Status = SegmentStatus.Paused;
                    seg.SpeedBytesPerSec = 0;
                }
                snapshot = task.CloneSnapshot();
            }
        }

        await PersistTasksAsync().ConfigureAwait(false);
        if (snapshot != null)
        {
            TaskUpdated?.Invoke(snapshot);
        }

        PumpQueue();
    }

    public async Task PauseAllAsync()
    {
        List<Guid> ids;
        lock (_stateLock)
        {
            ids = _tasks
                .Where(t => t.Status is DownloadStatus.Downloading or DownloadStatus.Probing or DownloadStatus.Queued)
                .Select(t => t.Id)
                .ToList();
        }

        await Task.WhenAll(ids.Select(PauseAsync)).ConfigureAwait(false);
    }

    public async Task ResumeAllAsync()
    {
        List<Guid> ids;
        lock (_stateLock)
        {
            ids = _tasks
                .Where(t => t.Status is DownloadStatus.Paused or DownloadStatus.Error)
                .OrderBy(t => t.QueueOrder)
                .Select(t => t.Id)
                .ToList();

            foreach (var task in _tasks.Where(t => ids.Contains(t.Id)))
            {
                task.Status = DownloadStatus.Queued;
                task.ErrorMessage = null;
            }
        }

        await PersistTasksAsync().ConfigureAwait(false);
        foreach (var id in ids)
        {
            var snap = GetTaskSnapshot(id);
            if (snap != null) TaskUpdated?.Invoke(snap);
        }

        PumpQueue();
    }

    public async Task RemoveAsync(Guid taskId, bool deleteFiles = false)
    {
        if (_activeCts.TryGetValue(taskId, out var cts))
        {
            cts.Cancel();
            if (_activeRuns.TryGetValue(taskId, out var running))
            {
                try
                {
                    await running.ConfigureAwait(false);
                }
                catch
                {
                    // Handled
                }
            }
        }

        DownloadTask? removed = null;
        lock (_stateLock)
        {
            int idx = _tasks.FindIndex(t => t.Id == taskId);
            if (idx >= 0)
            {
                removed = _tasks[idx];
                _tasks.RemoveAt(idx);
            }
        }

        if (removed != null)
        {
            if (deleteFiles)
            {
                TryDeleteFile(removed.TempFilePath);
                TryDeleteFile(removed.TargetFilePath);
            }
            else if (removed.Status != DownloadStatus.Completed)
            {
                // Clean up orphaned partial temp file on cancel/remove
                TryDeleteFile(removed.TempFilePath);
            }

            await PersistTasksAsync().ConfigureAwait(false);
            TaskRemoved?.Invoke(taskId);
            PumpQueue();
        }
    }

    public async Task MoveInQueueAsync(Guid taskId, int delta)
    {
        List<DownloadTask> changed = new();
        lock (_stateLock)
        {
            var ordered = _tasks.OrderBy(t => t.QueueOrder).ToList();
            int index = ordered.FindIndex(t => t.Id == taskId);
            if (index < 0)
                return;

            int newIndex = Math.Clamp(index + delta, 0, ordered.Count - 1);
            if (newIndex == index)
                return;

            var item = ordered[index];
            ordered.RemoveAt(index);
            ordered.Insert(newIndex, item);

            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].QueueOrder = i + 1;
                changed.Add(ordered[i].CloneSnapshot());
            }
        }

        await PersistTasksAsync().ConfigureAwait(false);
        foreach (var snap in changed)
        {
            TaskUpdated?.Invoke(snap);
        }
    }

    public async Task<string?> ComputeAndVerifySha256Async(Guid taskId, string? expectedSha256 = null, CancellationToken cancellationToken = default)
    {
        DownloadTask? task;
        lock (_stateLock)
        {
            task = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null || task.Status != DownloadStatus.Completed || !File.Exists(task.TargetFilePath))
                return null;
        }

        string computed = await HashService.ComputeSha256Async(task.TargetFilePath, cancellationToken).ConfigureAwait(false);

        DownloadTask snapshot;
        lock (_stateLock)
        {
            task.ActualSha256 = computed;
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                task.ExpectedSha256 = HashService.NormalizeHex(expectedSha256);
            }

            task.HashVerified = !string.IsNullOrWhiteSpace(task.ExpectedSha256)
                ? HashService.AreEqual(computed, task.ExpectedSha256)
                : null;
            snapshot = task.CloneSnapshot();
        }

        await PersistTasksAsync().ConfigureAwait(false);
        TaskUpdated?.Invoke(snapshot);
        return computed;
    }

    public async Task UpdateSettingsAsync(AppSettings newSettings)
    {
        var normalized = newSettings.Clone();
        normalized.Normalize();

        lock (_stateLock)
        {
            _settings = normalized;
        }

        _rateLimiter.UpdateLimit(normalized.GlobalSpeedLimitBytesPerSec);
        await _store.SaveSettingsAsync(normalized).ConfigureAwait(false);
        SettingsUpdated?.Invoke(normalized.Clone());

        PumpQueue();
    }

    public void UpdateGlobalSpeedLimit(long bytesPerSecond)
    {
        var updated = Settings;
        updated.GlobalSpeedLimitBytesPerSec = Math.Max(0, bytesPerSecond);
        _ = UpdateSettingsAsync(updated);
    }

    public void PumpQueue()
    {
        if (_disposed)
            return;

        List<DownloadTask> toStart = new();
        lock (_stateLock)
        {
            int activeCount = _tasks.Count(t => t.Status is DownloadStatus.Downloading or DownloadStatus.Probing);
            int availableSlots = Math.Max(0, _settings.MaxConcurrentDownloads - activeCount);
            if (availableSlots == 0)
                return;

            var queued = _tasks
                .Where(t => t.Status == DownloadStatus.Queued && !_activeRuns.ContainsKey(t.Id))
                .OrderBy(t => t.QueueOrder)
                .Take(availableSlots)
                .ToList();

            foreach (var item in queued)
            {
                item.Status = DownloadStatus.Probing;
                toStart.Add(item);
            }
        }

        foreach (var item in toStart)
        {
            LaunchTask(item);
        }
    }

    private void LaunchTask(DownloadTask task)
    {
        if (_disposed)
            return;

        var cts = new CancellationTokenSource();
        if (!_activeCts.TryAdd(task.Id, cts))
        {
            cts.Dispose();
            return;
        }

        var settingsSnapshot = Settings;
        var runTask = Task.Run(async () =>
        {
            try
            {
                await _runner.RunAsync(
                    task,
                    settingsSnapshot,
                    PersistTasksAsync,
                    updated => TaskUpdated?.Invoke(updated.CloneSnapshot()),
                    dir =>
                    {
                        lock (_stateLock)
                        {
                            var reserved = GetReservedPathsLocked(excludeTaskId: task.Id);
                            return FileCategoryClassifier.GetUniqueFilePath(dir, task.FileName, reserved);
                        }
                    },
                    cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_stateLock)
                {
                    if (task.Status != DownloadStatus.Completed)
                    {
                        task.Status = DownloadStatus.Paused;
                        task.SpeedBytesPerSec = 0;
                        foreach (var seg in task.Segments)
                        {
                            if (seg.Status == SegmentStatus.Active)
                                seg.Status = SegmentStatus.Paused;
                            seg.SpeedBytesPerSec = 0;
                        }
                    }
                }
                await PersistTasksAsync().ConfigureAwait(false);
                TaskUpdated?.Invoke(task.CloneSnapshot());
            }
            catch (Exception ex)
            {
                lock (_stateLock)
                {
                    task.Status = DownloadStatus.Error;
                    task.SpeedBytesPerSec = 0;
                    task.ErrorMessage = ex.Message;
                    foreach (var seg in task.Segments)
                    {
                        if (seg.Status == SegmentStatus.Active)
                            seg.Status = SegmentStatus.Error;
                        seg.SpeedBytesPerSec = 0;
                    }
                }
                await PersistTasksAsync().ConfigureAwait(false);
                TaskUpdated?.Invoke(task.CloneSnapshot());
            }
            finally
            {
                _activeCts.TryRemove(task.Id, out _);
                _activeRuns.TryRemove(task.Id, out _);
                cts.Dispose();
                PumpQueue();
            }
        });

        _activeRuns[task.Id] = runTask;
    }

    private HashSet<string> GetReservedPathsLocked(Guid? excludeTaskId = null)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _tasks)
        {
            if (excludeTaskId.HasValue && t.Id == excludeTaskId.Value)
                continue;
            if (!string.IsNullOrWhiteSpace(t.TargetFilePath))
                reserved.Add(t.TargetFilePath);
            if (!string.IsNullOrWhiteSpace(t.TempFilePath))
                reserved.Add(t.TempFilePath);
        }
        return reserved;
    }

    private Task PersistTasksAsync()
    {
        List<DownloadTask> snapshot;
        lock (_stateLock)
        {
            snapshot = _tasks.Select(t => t.CloneSnapshot()).ToList();
        }
        return _store.SaveDownloadsAsync(snapshot);
    }

    private static string GuessInitialFileName(Uri uri)
    {
        var name = Path.GetFileName(uri.AbsolutePath);
        if (!string.IsNullOrWhiteSpace(name) && name != "/")
        {
            return FileCategoryClassifier.SanitizeFileName(Uri.UnescapeDataString(name));
        }
        return FileCategoryClassifier.SanitizeFileName($"{uri.Host}_download.bin", "download.bin");
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Ignore locked/already deleted cleanup failure
        }
    }

    public static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(3),
            AutomaticDecompression = System.Net.DecompressionMethods.None
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VelocityDM/1.0 (Windows NT; Multi-Segment)");
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var cts in _activeCts.Values)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // Ignore
            }
        }

        try
        {
            await Task.WhenAll(_activeRuns.Values).ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown cancellations
        }

        await PersistTasksAsync().ConfigureAwait(false);

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
