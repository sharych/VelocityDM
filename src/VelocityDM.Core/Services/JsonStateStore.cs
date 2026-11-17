using System.Text.Json;
using System.Text.Json.Serialization;
using VelocityDM.Core.Models;

namespace VelocityDM.Core.Services;

/// <summary>
/// Persists download queue state and application settings atomically to JSON files.
/// </summary>
public sealed class JsonStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly string _downloadsFilePath;
    private readonly string _settingsFilePath;

    public JsonStateStore(string? storageDirectory = null)
    {
        var baseDir = string.IsNullOrWhiteSpace(storageDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VelocityDM")
            : storageDirectory;

        Directory.CreateDirectory(baseDir);
        StorageDirectory = baseDir;
        _downloadsFilePath = Path.Combine(baseDir, "downloads.json");
        _settingsFilePath = Path.Combine(baseDir, "settings.json");
    }

    public string StorageDirectory { get; }

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                var defaults = new AppSettings();
                defaults.Normalize();
                return defaults;
            }

            await using var stream = File.OpenRead(_settingsFilePath);
            var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? new AppSettings();
            loaded.Normalize();
            return loaded;
        }
        catch
        {
            var fallback = new AppSettings();
            fallback.Normalize();
            return fallback;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Normalize();
        await WriteAtomicJsonAsync(_settingsFilePath, settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DownloadTask>> LoadDownloadsAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_downloadsFilePath))
                return new List<DownloadTask>();

            await using var stream = File.OpenRead(_downloadsFilePath);
            var tasks = await JsonSerializer.DeserializeAsync<List<DownloadTask>>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? new List<DownloadTask>();

            RecoverInterruptedTasks(tasks);
            return tasks;
        }
        catch
        {
            return new List<DownloadTask>();
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task SaveDownloadsAsync(IEnumerable<DownloadTask> tasks, CancellationToken cancellationToken = default)
    {
        var snapshot = tasks.Select(t => t.CloneSnapshot()).ToList();
        await WriteAtomicJsonAsync(_downloadsFilePath, snapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Converts any in-flight statuses from a crashed or closed session into safe Paused states for resumption.
    /// </summary>
    public static void RecoverInterruptedTasks(IList<DownloadTask> tasks)
    {
        foreach (var task in tasks)
        {
            if (task.Status is DownloadStatus.Downloading or DownloadStatus.Probing)
            {
                task.Status = DownloadStatus.Paused;
                task.SpeedBytesPerSec = 0;
                foreach (var seg in task.Segments)
                {
                    if (seg.Status == SegmentStatus.Active)
                    {
                        seg.Status = SegmentStatus.Paused;
                    }
                    seg.SpeedBytesPerSec = 0;
                }
            }
        }
    }

    private async Task WriteAtomicJsonAsync<T>(string targetPath, T value, CancellationToken cancellationToken)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            _ioLock.Release();
        }
    }
}
