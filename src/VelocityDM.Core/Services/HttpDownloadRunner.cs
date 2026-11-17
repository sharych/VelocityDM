using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;
using VelocityDM.Core.Models;

namespace VelocityDM.Core.Services;

/// <summary>
/// Executes a single DownloadTask using parallel HTTP byte-range segments and direct random-access disk I/O.
/// </summary>
public sealed class HttpDownloadRunner
{
    private readonly HttpClient _httpClient;
    private readonly HttpProbeService _probeService;
    private readonly TokenBucketRateLimiter _rateLimiter;

    public HttpDownloadRunner(
        HttpClient httpClient,
        HttpProbeService probeService,
        TokenBucketRateLimiter rateLimiter)
    {
        _httpClient = httpClient;
        _probeService = probeService;
        _rateLimiter = rateLimiter;
    }

    public async Task RunAsync(
        DownloadTask task,
        AppSettings settings,
        Func<Task> persistStateAsync,
        Action<DownloadTask> onProgress,
        Func<string, string>? reserveUniqueTargetPath,
        CancellationToken cancellationToken)
    {
        bool isResumeAttempt = task.Segments.Count > 0 && task.DownloadedBytes > 0 && File.Exists(task.TempFilePath);

        task.Status = DownloadStatus.Probing;
        task.ErrorMessage = null;
        task.SpeedBytesPerSec = 0;
        onProgress(task);

        ProbeResult probe = await _probeService.ProbeAsync(task.Url, cancellationToken).ConfigureAwait(false);

        bool canResumeExisting = isResumeAttempt
            && task.SupportsRanges
            && probe.SupportsRanges
            && task.TotalBytes == probe.TotalBytes
            && !HasValidatorChanged(task.ETag, probe.ETag)
            && !HasValidatorChanged(task.LastModified, probe.LastModified);

        task.FinalUrl = probe.FinalUrl;
        task.ContentType = probe.ContentType;
        task.ETag = probe.ETag;
        task.LastModified = probe.LastModified;

        if (!canResumeExisting)
        {
            if (!task.HasCustomFileName || string.IsNullOrWhiteSpace(task.FileName))
            {
                task.FileName = probe.FileName;
            }

            if (!task.HasCustomCategory)
            {
                task.Category = FileCategoryClassifier.Classify(task.FileName, task.ContentType);
            }

            if (!task.HasCustomSaveDirectory || string.IsNullOrWhiteSpace(task.SaveDirectory))
            {
                task.SaveDirectory = FileCategoryClassifier.ResolveSaveDirectory(
                    settings.DefaultDownloadDirectory,
                    task.Category,
                    settings.CategorizeIntoSubfolders);
            }

            Directory.CreateDirectory(task.SaveDirectory);
            task.TargetFilePath = reserveUniqueTargetPath != null
                ? reserveUniqueTargetPath(task.SaveDirectory)
                : FileCategoryClassifier.GetUniqueFilePath(task.SaveDirectory, task.FileName);
            task.FileName = Path.GetFileName(task.TargetFilePath);
            task.TempFilePath = task.TargetFilePath + ".vdm.part";
        }

        if (!canResumeExisting)
        {
            task.TotalBytes = probe.TotalBytes;
            task.SupportsRanges = probe.SupportsRanges;
            task.DownloadedBytes = 0;
            task.ActualSha256 = null;
            task.HashVerified = null;
            task.Segments = SegmentPlanner.CreateSegments(
                task.TotalBytes,
                task.SupportsRanges,
                task.MaxConnections);

            PrepareFreshTempFile(task.TempFilePath, task.TotalBytes);
        }
        else
        {
            foreach (var seg in task.Segments)
            {
                if (!seg.IsFinished && seg.Status != SegmentStatus.Completed)
                {
                    seg.Status = SegmentStatus.Pending;
                    seg.LastError = null;
                }
            }
            task.DownloadedBytes = task.Segments.Sum(s => s.DownloadedBytes);
        }

        task.Status = DownloadStatus.Downloading;
        await persistStateAsync().ConfigureAwait(false);
        onProgress(task);

        try
        {
            await ExecuteSegmentsAsync(task, settings, persistStateAsync, onProgress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RangeNotHonoredException)
        {
            // Server advertised range support on probe, but returned 200 OK on parallel GET.
            // Cleanly restart in single-stream mode.
            task.SupportsRanges = false;
            task.DownloadedBytes = 0;
            task.Segments = SegmentPlanner.CreateSegments(task.TotalBytes, supportsRanges: false, requestedConnections: 1);
            PrepareFreshTempFile(task.TempFilePath, task.TotalBytes);
            await persistStateAsync().ConfigureAwait(false);
            onProgress(task);

            await ExecuteSegmentsAsync(task, settings, persistStateAsync, onProgress, cancellationToken)
                .ConfigureAwait(false);
        }

        FinalizeDownloadedFile(task, reserveUniqueTargetPath);

        if (!string.IsNullOrWhiteSpace(task.ExpectedSha256))
        {
            task.ActualSha256 = await HashService.ComputeSha256Async(task.TargetFilePath, cancellationToken)
                .ConfigureAwait(false);
            task.HashVerified = HashService.AreEqual(task.ActualSha256, task.ExpectedSha256);

            if (task.HashVerified == false)
            {
                task.Status = DownloadStatus.Error;
                task.SpeedBytesPerSec = 0;
                task.ErrorMessage = $"SHA-256 mismatch. Expected {HashService.NormalizeHex(task.ExpectedSha256)}, got {task.ActualSha256}.";
                await persistStateAsync().ConfigureAwait(false);
                onProgress(task);
                return;
            }
        }

        task.Status = DownloadStatus.Completed;
        task.CompletedAt = DateTimeOffset.UtcNow;
        task.SpeedBytesPerSec = 0;
        foreach (var seg in task.Segments)
        {
            seg.Status = SegmentStatus.Completed;
            seg.SpeedBytesPerSec = 0;
        }

        await persistStateAsync().ConfigureAwait(false);
        onProgress(task);
    }

    private async Task ExecuteSegmentsAsync(
        DownloadTask task,
        AppSettings settings,
        Func<Task> persistStateAsync,
        Action<DownloadTask> onProgress,
        CancellationToken cancellationToken)
    {
        using var fallbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using SafeFileHandle fileHandle = File.OpenHandle(
            task.TempFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            FileOptions.Asynchronous);

        var activeSegments = task.Segments.Where(s => !s.IsFinished && s.Status != SegmentStatus.Completed).ToList();
        var segmentTasks = activeSegments
            .Select(seg => RunSegmentWithRetriesAsync(task, seg, fileHandle, settings, fallbackCts, cancellationToken))
            .ToList();

        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task monitorTask = MonitorProgressAsync(task, persistStateAsync, onProgress, monitorCts.Token);

        try
        {
            await Task.WhenAll(segmentTasks).ConfigureAwait(false);
        }
        finally
        {
            monitorCts.Cancel();
            try
            {
                await monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on monitor stop
            }

            // Recompute exact total written bytes after all segment tasks exit
            Thread.MemoryBarrier();
            task.DownloadedBytes = task.Segments.Sum(s => s.DownloadedBytes);
            task.SpeedBytesPerSec = 0;
            foreach (var seg in task.Segments)
            {
                seg.SpeedBytesPerSec = 0;
                if (seg.Status == SegmentStatus.Active)
                {
                    seg.Status = seg.IsFinished ? SegmentStatus.Completed : SegmentStatus.Paused;
                }
            }
        }
    }

    private async Task RunSegmentWithRetriesAsync(
        DownloadTask task,
        SegmentState segment,
        SafeFileHandle fileHandle,
        AppSettings settings,
        CancellationTokenSource fallbackCts,
        CancellationToken externalCancellationToken)
    {
        var token = fallbackCts.Token;
        int attempt = 0;
        var buffer = new byte[settings.BufferSizeBytes];
        string effectiveUrl = task.FinalUrl ?? task.Url;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                segment.Status = SegmentStatus.Active;
                segment.LastError = null;

                using var request = new HttpRequestMessage(HttpMethod.Get, effectiveUrl);
                if (segment.EndOffset >= 0)
                {
                    long rangeStart = segment.NextWriteOffset;
                    if (rangeStart > segment.EndOffset)
                    {
                        segment.Status = SegmentStatus.Completed;
                        segment.SpeedBytesPerSec = 0;
                        return;
                    }
                    request.Headers.Range = new RangeHeaderValue(rangeStart, segment.EndOffset);
                }

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    token).ConfigureAwait(false);

                if (segment.EndOffset >= 0 && response.StatusCode == HttpStatusCode.OK)
                {
                    if (task.Segments.Count > 1)
                    {
                        fallbackCts.Cancel();
                        throw new RangeNotHonoredException();
                    }

                    // Single segment server ignored Range and returned 200 OK; write from offset 0 cleanly.
                    if (segment.DownloadedBytes > 0)
                    {
                        segment.DownloadedBytes = 0;
                    }
                    if (response.Content.Headers.ContentLength is long actualLen && actualLen > 0)
                    {
                        task.TotalBytes = actualLen;
                        segment.EndOffset = actualLen - 1;
                    }
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                }

                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

                long bytesWindow = 0;
                long windowStart = Stopwatch.GetTimestamp();

                while (true)
                {
                    int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    int toWrite = read;
                    if (segment.EndOffset >= 0)
                    {
                        long remaining = segment.RemainingBytes;
                        if (remaining <= 0)
                            break;
                        toWrite = (int)Math.Min(read, remaining);
                    }

                    await _rateLimiter.ThrottleAsync(toWrite, token).ConfigureAwait(false);

                    long writeOffset = segment.NextWriteOffset;
                    await RandomAccess.WriteAsync(
                        fileHandle,
                        buffer.AsMemory(0, toWrite),
                        writeOffset,
                        token).ConfigureAwait(false);

                    segment.DownloadedBytes += toWrite;
                    bytesWindow += toWrite;

                    long now = Stopwatch.GetTimestamp();
                    double elapsed = (double)(now - windowStart) / Stopwatch.Frequency;
                    if (elapsed >= 0.35)
                    {
                        double instantSpeed = bytesWindow / elapsed;
                        segment.SpeedBytesPerSec = segment.SpeedBytesPerSec <= 0
                            ? instantSpeed
                            : (segment.SpeedBytesPerSec * 0.45) + (instantSpeed * 0.55);
                        bytesWindow = 0;
                        windowStart = now;
                    }

                    if (segment.EndOffset >= 0 && segment.IsFinished)
                        break;
                }

                if (segment.EndOffset >= 0 && !segment.IsFinished)
                {
                    throw new IOException($"Segment {segment.Index} closed prematurely at {segment.DownloadedBytes}/{segment.TotalBytes} bytes.");
                }

                segment.Status = SegmentStatus.Completed;
                segment.SpeedBytesPerSec = 0;
                return;
            }
            catch (RangeNotHonoredException)
            {
                throw;
            }
            catch (OperationCanceledException) when (externalCancellationToken.IsCancellationRequested)
            {
                segment.Status = SegmentStatus.Paused;
                segment.SpeedBytesPerSec = 0;
                throw;
            }
            catch (OperationCanceledException) when (fallbackCts.IsCancellationRequested)
            {
                segment.Status = SegmentStatus.Paused;
                segment.SpeedBytesPerSec = 0;
                throw new RangeNotHonoredException();
            }
            catch (Exception ex) when (attempt < settings.MaxRetriesPerSegment && IsTransient(ex))
            {
                attempt++;
                segment.LastError = $"Retry {attempt}/{settings.MaxRetriesPerSegment}: {ex.Message}";
                segment.SpeedBytesPerSec = 0;
                int backoffMs = Math.Min(10_000, 400 * (1 << Math.Min(attempt, 5)));
                await Task.Delay(backoffMs, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                segment.Status = SegmentStatus.Error;
                segment.SpeedBytesPerSec = 0;
                segment.LastError = ex.Message;
                throw;
            }
        }
    }

    private async Task MonitorProgressAsync(
        DownloadTask task,
        Func<Task> persistStateAsync,
        Action<DownloadTask> onProgress,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        long lastBytes = task.Segments.Sum(s => s.DownloadedBytes);
        long lastStamp = Stopwatch.GetTimestamp();
        int persistTicks = 0;

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            long currentBytes = task.Segments.Sum(s => s.DownloadedBytes);
            long now = Stopwatch.GetTimestamp();
            double elapsed = (double)(now - lastStamp) / Stopwatch.Frequency;

            task.DownloadedBytes = currentBytes;
            if (elapsed > 0.05)
            {
                double instantTotalSpeed = Math.Max(0, currentBytes - lastBytes) / elapsed;
                task.SpeedBytesPerSec = task.SpeedBytesPerSec <= 0
                    ? instantTotalSpeed
                    : (task.SpeedBytesPerSec * 0.4) + (instantTotalSpeed * 0.6);
                lastBytes = currentBytes;
                lastStamp = now;
            }

            onProgress(task);

            persistTicks++;
            if (persistTicks >= 6) // ~1.5s
            {
                persistTicks = 0;
                try
                {
                    await persistStateAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Non-fatal periodic checkpoint failure
                }
            }
        }
    }

    private static void PrepareFreshTempFile(string tempPath, long? totalBytes)
    {
        var dir = Path.GetDirectoryName(tempPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (totalBytes is > 0)
        {
            fs.SetLength(totalBytes.Value);
        }
    }

    private static void FinalizeDownloadedFile(DownloadTask task, Func<string, string>? reserveUniqueTargetPath)
    {
        if (task.TotalBytes is null)
        {
            var info = new FileInfo(task.TempFilePath);
            task.TotalBytes = info.Length;
            task.DownloadedBytes = info.Length;
            if (task.Segments.Count == 1)
            {
                task.Segments[0].EndOffset = Math.Max(-1, info.Length - 1);
                task.Segments[0].DownloadedBytes = info.Length;
            }
        }

        // Ensure target path hasn't been claimed externally while download was active
        if (File.Exists(task.TargetFilePath))
        {
            var dir = Path.GetDirectoryName(task.TargetFilePath) ?? task.SaveDirectory;
            task.TargetFilePath = reserveUniqueTargetPath != null
                ? reserveUniqueTargetPath(dir)
                : FileCategoryClassifier.GetUniqueFilePath(dir, task.FileName);
            task.FileName = Path.GetFileName(task.TargetFilePath);
        }

        File.Move(task.TempFilePath, task.TargetFilePath, overwrite: true);
    }

    private static bool HasValidatorChanged(string? previous, string? current)
    {
        if (string.IsNullOrWhiteSpace(previous) || string.IsNullOrWhiteSpace(current))
            return false;

        return !string.Equals(previous, current, StringComparison.Ordinal);
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is IOException or TimeoutException)
            return true;

        if (ex is HttpRequestException hre)
        {
            if (hre.StatusCode is null)
                return true;

            int code = (int)hre.StatusCode.Value;
            return code == 408 || code == 429 || code >= 500;
        }

        return false;
    }

    private sealed class RangeNotHonoredException : Exception;
}
