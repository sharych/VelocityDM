using System.Numerics;
using System.Text;
using LibtorrentSharp;
using LibtorrentSharp.Enums;
using VelocityDM.Core.Models;

namespace VelocityDM.Core.Services;

/// <summary>
/// Unified wrapper around either a LibtorrentSharp <see cref="TorrentHandle"/> or <see cref="MagnetHandle"/>
/// so TorrentManagerService treats .torrent, magnet, and resume-restored handles uniformly.
/// </summary>
internal sealed class ManagedTorrent
{
    public TorrentHandle? TorrentHandle { get; }
    public MagnetHandle? MagnetHandle { get; }
    public TorrentInfo? TorrentInfo { get; }
    public string SaveDirectory { get; }

    public ManagedTorrent(TorrentHandle torrentHandle, TorrentInfo? torrentInfo, string saveDirectory)
    {
        TorrentHandle = torrentHandle;
        TorrentInfo = torrentInfo;
        SaveDirectory = saveDirectory;
    }

    public ManagedTorrent(MagnetHandle magnetHandle, string saveDirectory)
    {
        MagnetHandle = magnetHandle;
        SaveDirectory = saveDirectory;
    }

    public bool IsValid => TorrentHandle != null || (MagnetHandle?.IsValid ?? false);

    public bool HasMetadata
    {
        get
        {
            if (TorrentInfo != null || TorrentHandle != null)
                return TotalSize > 0 || GetFileCount() > 0;
            return MagnetHandle != null && (MagnetHandle.TotalSize > 0 || MagnetHandle.NumPieces > 0);
        }
    }

    public long TotalSize
    {
        get
        {
            if (TorrentInfo != null)
                return TorrentInfo.Metadata.TotalSize;
            if (TorrentHandle != null)
                return TorrentHandle.Info.Metadata.TotalSize;
            return MagnetHandle?.TotalSize ?? 0;
        }
    }

    public int NumPieces
    {
        get
        {
            if (TorrentInfo != null)
                return TorrentInfo.NumPieces;
            if (TorrentHandle != null)
                return TorrentHandle.Info.NumPieces;
            return MagnetHandle?.NumPieces ?? 0;
        }
    }

    public TorrentStatus GetStatus() =>
        TorrentHandle != null ? TorrentHandle.GetCurrentStatus() : MagnetHandle!.GetCurrentStatus();

    public void Pause()
    {
        if (TorrentHandle != null)
        {
            TorrentHandle.UnsetFlags(TorrentFlags.AutoManaged);
            TorrentHandle.SetFlags(TorrentFlags.Paused);
        }
        else if (MagnetHandle != null)
        {
            MagnetHandle.UnsetFlags(TorrentFlags.AutoManaged);
            MagnetHandle.SetFlags(TorrentFlags.Paused);
        }
    }

    public void Resume()
    {
        if (TorrentHandle != null)
        {
            TorrentHandle.ClearError();
            TorrentHandle.Resume();
            TorrentHandle.SetForceStart(true);
        }
        else if (MagnetHandle != null)
        {
            MagnetHandle.ClearError();
            MagnetHandle.Resume();
            MagnetHandle.SetForceStart(true);
        }
    }

    public void Stop() => Pause();

    public void ForceRecheck()
    {
        if (TorrentHandle != null)
            TorrentHandle.ForceRecheck();
        else
            MagnetHandle?.ForceRecheck();
    }

    public void SetSequential(bool sequential)
    {
        if (TorrentHandle != null)
            TorrentHandle.SetSequentialDownload(sequential);
        else
            MagnetHandle?.SetSequentialDownload(sequential);
    }

    public void SetRateLimits(int downloadBytesPerSec, int uploadBytesPerSec)
    {
        if (TorrentHandle != null)
        {
            TorrentHandle.DownloadRateLimit = downloadBytesPerSec;
            TorrentHandle.UploadRateLimit = uploadBytesPerSec;
        }
        else if (MagnetHandle != null)
        {
            MagnetHandle.DownloadRateLimit = downloadBytesPerSec;
            MagnetHandle.UploadRateLimit = uploadBytesPerSec;
        }
    }

    public void RequestResumeData(LibtorrentSession session)
    {
        if (TorrentHandle != null)
            session.RequestResumeData(TorrentHandle);
        else if (MagnetHandle != null)
            session.RequestResumeData(MagnetHandle);
    }

    public void Detach(LibtorrentSession session, bool deleteDownloadedData)
    {
        if (TorrentHandle != null)
        {
            var flags = deleteDownloadedData
                ? (RemoveFlags.DeleteFiles | RemoveFlags.DeletePartfile)
                : RemoveFlags.None;
            session.DetachTorrent(TorrentHandle, flags);
            return;
        }

        if (MagnetHandle == null)
            return;

        IReadOnlyList<string> filesToDelete = deleteDownloadedData
            ? GetKnownDiskFilePaths()
            : Array.Empty<string>();

        session.DetachMagnet(MagnetHandle);

        if (deleteDownloadedData && filesToDelete.Count > 0)
        {
            DeleteDetachedFilesWithRetry(filesToDelete, SaveDirectory);
        }
    }

    public int GetTrackerCount()
    {
        try
        {
            return TorrentHandle != null
                ? TorrentHandle.GetTrackers().Count
                : (MagnetHandle?.GetTrackers().Count ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    public void AddTracker(string url, int tier)
    {
        if (TorrentHandle != null)
            TorrentHandle.AddTracker(url, tier);
        else
            MagnetHandle?.AddTracker(url, tier);
    }

    public void ReannounceAll()
    {
        try
        {
            if (TorrentHandle != null)
                TorrentHandle.ReannounceAllTrackers(TimeSpan.Zero, true);
            else
                MagnetHandle?.ReannounceAllTrackers(TimeSpan.Zero, true);
        }
        catch
        {
            // Ignore transient announce errors
        }
    }

    public string? TryResolveDisplayName()
    {
        if (TorrentInfo != null && !string.IsNullOrWhiteSpace(TorrentInfo.Metadata.Name))
            return TorrentInfo.Metadata.Name;

        if (TorrentHandle != null && !string.IsNullOrWhiteSpace(TorrentHandle.Info.Metadata.Name))
            return TorrentHandle.Info.Metadata.Name;

        if (MagnetHandle == null || !HasMetadata)
            return null;

        try
        {
            var files = MagnetHandle.GetFiles();
            if (files.Count == 0)
                return null;

            string firstRel = NormalizeRelPath(files[0].Info.Path);
            if (string.IsNullOrWhiteSpace(firstRel))
                return files[0].Info.Name;

            int sep = firstRel.IndexOf(Path.DirectorySeparatorChar);
            if (files.Count == 1 && sep < 0)
                return string.IsNullOrWhiteSpace(files[0].Info.Name) ? firstRel : files[0].Info.Name;

            return sep > 0 ? firstRel[..sep] : firstRel;
        }
        catch
        {
            return null;
        }
    }

    public bool SetFilePriority(string filePath, TorrentPriority priority)
    {
        var nativePrio = ToNativePriority(priority);
        if (TorrentHandle != null)
        {
            foreach (var f in TorrentHandle.Files)
            {
                if (MatchesFilePath(f.Info.Path, f.Path, filePath))
                {
                    f.Priority = nativePrio;
                    return true;
                }
            }
            return false;
        }

        if (MagnetHandle != null)
        {
            foreach (var f in MagnetHandle.GetFiles())
            {
                if (MatchesFilePath(f.Info.Path, f.Path, filePath))
                {
                    f.Priority = nativePrio;
                    return true;
                }
            }
        }

        return false;
    }

    public IReadOnlyList<TorrentFileSnapshot> GetFileSnapshots(double overallProgress)
    {
        if (!HasMetadata)
            return Array.Empty<TorrentFileSnapshot>();

        try
        {
            if (TorrentHandle != null)
            {
                var files = TorrentHandle.Files;
                long[] progressBytes = TorrentHandle.GetFileProgress();
                var list = new List<TorrentFileSnapshot>(files.Count);
                for (int i = 0; i < files.Count; i++)
                {
                    var f = files[i];
                    if (f.Info.IsPadFile)
                        continue;

                    long done = i < progressBytes.Length ? progressBytes[i] : 0;
                    double pct = f.Info.FileSize > 0
                        ? Math.Clamp((double)done * 100.0 / f.Info.FileSize, 0.0, 100.0)
                        : 100.0;

                    string full = ResolveFullPath(f.Path, f.Info.Path);
                    list.Add(new TorrentFileSnapshot
                    {
                        Path = f.Info.Path,
                        FullPath = full,
                        Length = f.Info.FileSize,
                        ProgressPercent = pct,
                        Priority = FromNativePriority(f.Priority)
                    });
                }
                return list;
            }

            if (MagnetHandle != null)
            {
                var files = MagnetHandle.GetFiles();
                if (files.Count == 0)
                    return Array.Empty<TorrentFileSnapshot>();

                int numPieces = MagnetHandle.NumPieces;
                long totalSize = MagnetHandle.TotalSize;
                bool[]? bitfield = numPieces > 0 ? MagnetHandle.GetPieceBitfield(numPieces) : null;
                long pieceLength = (bitfield != null && totalSize > 0 && numPieces > 0)
                    ? ComputePieceLength(totalSize, numPieces)
                    : 0;

                var list = new List<TorrentFileSnapshot>(files.Count);
                foreach (var f in files)
                {
                    if (f.Info.IsPadFile)
                        continue;

                    double pct = ComputeFileProgressFromPieces(
                        f.Info.Offset,
                        f.Info.FileSize,
                        pieceLength,
                        bitfield,
                        files.Count == 1 ? overallProgress : -1);

                    string full = ResolveFullPath(f.Path, f.Info.Path);
                    list.Add(new TorrentFileSnapshot
                    {
                        Path = f.Info.Path,
                        FullPath = full,
                        Length = f.Info.FileSize,
                        ProgressPercent = pct,
                        Priority = FromNativePriority(f.Priority)
                    });
                }
                return list;
            }
        }
        catch
        {
            // Ignore transient metadata race
        }

        return Array.Empty<TorrentFileSnapshot>();
    }

    private int GetFileCount()
    {
        if (TorrentHandle != null)
            return TorrentHandle.Files.Count;
        return MagnetHandle?.GetFiles().Count ?? 0;
    }

    private IReadOnlyList<string> GetKnownDiskFilePaths()
    {
        var paths = new List<string>();
        try
        {
            if (MagnetHandle != null)
            {
                foreach (var f in MagnetHandle.GetFiles())
                {
                    if (!f.Info.IsPadFile)
                        paths.Add(ResolveFullPath(f.Path, f.Info.Path));
                }
            }
        }
        catch
        {
            // Best effort before detach
        }
        return paths;
    }

    private string ResolveFullPath(string handleFilePath, string infoRelPath)
    {
        if (!string.IsNullOrWhiteSpace(handleFilePath))
            return Path.IsPathRooted(handleFilePath) ? handleFilePath : Path.Combine(SaveDirectory, handleFilePath);

        return Path.Combine(SaveDirectory, NormalizeRelPath(infoRelPath));
    }

    private static bool MatchesFilePath(string relPath, string handlePath, string requestedPath) =>
        string.Equals(relPath, requestedPath, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(NormalizeRelPath(relPath), NormalizeRelPath(requestedPath), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(handlePath, requestedPath, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelPath(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

    private static long ComputePieceLength(long totalSize, int numPieces)
    {
        if (numPieces <= 1)
            return totalSize;

        long minLen = (totalSize + numPieces - 1) / numPieces;
        long pow2 = 1L << (64 - BitOperations.LeadingZeroCount((ulong)Math.Max(1, minLen - 1)));
        return pow2 * (numPieces - 1) < totalSize ? pow2 : minLen;
    }

    private static double ComputeFileProgressFromPieces(
        long fileOffset,
        long fileSize,
        long pieceLength,
        bool[]? bitfield,
        double singleFileFallbackProgress)
    {
        if (fileSize <= 0)
            return 100.0;

        if (bitfield == null || bitfield.Length == 0 || pieceLength <= 0)
            return singleFileFallbackProgress >= 0 ? Math.Clamp(singleFileFallbackProgress * 100.0, 0.0, 100.0) : 0.0;

        int firstPiece = (int)(fileOffset / pieceLength);
        int lastPiece = (int)Math.Min(bitfield.Length - 1, (fileOffset + fileSize - 1) / pieceLength);
        long verifiedBytes = 0;

        for (int p = firstPiece; p <= lastPiece; p++)
        {
            if (!bitfield[p])
                continue;

            long pieceStart = (long)p * pieceLength;
            long pieceEnd = pieceStart + pieceLength;
            long overlapStart = Math.Max(pieceStart, fileOffset);
            long overlapEnd = Math.Min(pieceEnd, fileOffset + fileSize);
            if (overlapEnd > overlapStart)
                verifiedBytes += overlapEnd - overlapStart;
        }

        return Math.Clamp((double)verifiedBytes * 100.0 / fileSize, 0.0, 100.0);
    }

    private static void DeleteDetachedFilesWithRetry(IReadOnlyList<string> filePaths, string saveRoot)
    {
        _ = Task.Run(async () =>
        {
            string normalizedRoot = Path.GetFullPath(saveRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parentDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawPath in filePaths)
            {
                if (string.IsNullOrWhiteSpace(rawPath))
                    continue;

                string full = Path.GetFullPath(rawPath);
                if (!full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    continue;

                string? parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrWhiteSpace(parent) && !string.Equals(parent, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    parentDirs.Add(parent);

                for (int attempt = 0; attempt < 6; attempt++)
                {
                    try
                    {
                        if (File.Exists(full))
                            File.Delete(full);
                        break;
                    }
                    catch (IOException)
                    {
                        await Task.Delay(150).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }
                }
            }

            foreach (var dir in parentDirs.OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch
                {
                    // Non-empty or locked dir
                }
            }
        });
    }

    public static TorrentDownloadState MapState(TorrentStatus status, bool isPaused)
    {
        if (!string.IsNullOrWhiteSpace(status.ErrorMessage) || status.State == LibtorrentSharp.Enums.TorrentState.Errored)
            return TorrentDownloadState.Error;

        if (isPaused || status.Flags.HasFlag(TorrentFlags.Paused))
            return TorrentDownloadState.Paused;

        return status.State switch
        {
            LibtorrentSharp.Enums.TorrentState.CheckingFiles or LibtorrentSharp.Enums.TorrentState.CheckingResume => TorrentDownloadState.Hashing,
            LibtorrentSharp.Enums.TorrentState.DownloadingMetadata => TorrentDownloadState.Metadata,
            LibtorrentSharp.Enums.TorrentState.Downloading => TorrentDownloadState.Downloading,
            LibtorrentSharp.Enums.TorrentState.Seeding or LibtorrentSharp.Enums.TorrentState.Finished => TorrentDownloadState.Seeding,
            _ => TorrentDownloadState.Stopped
        };
    }

    public static FileDownloadPriority ToNativePriority(TorrentPriority priority) => priority switch
    {
        TorrentPriority.DoNotDownload => FileDownloadPriority.DoNotDownload,
        TorrentPriority.Low => FileDownloadPriority.Low,
        TorrentPriority.High => FileDownloadPriority.High,
        _ => FileDownloadPriority.Normal
    };

    public static TorrentPriority FromNativePriority(FileDownloadPriority priority) => priority switch
    {
        FileDownloadPriority.DoNotDownload => TorrentPriority.DoNotDownload,
        FileDownloadPriority.Low => TorrentPriority.Low,
        FileDownloadPriority.High => TorrentPriority.High,
        _ => TorrentPriority.Normal
    };

    public static bool IsPrivateTorrentBytes(byte[] torrentBytes)
    {
        // Bencoded private torrent flag inside info dictionary is literally "7:privatei1e".
        ReadOnlySpan<byte> marker = "7:privatei1e"u8;
        return torrentBytes.AsSpan().IndexOf(marker) >= 0;
    }

    public static (string InfoHashHex, string? DisplayName, string CleanMagnetUri, List<string> Trackers) ParseMagnetComponents(string magnetUri)
    {
        string trimmed = magnetUri.Trim();
        if (!trimmed.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid magnet URI.", nameof(magnetUri));

        if (!MagnetUri.TryGetInfoHash(trimmed, out var sha1Hash) || sha1Hash.IsZero)
            throw new ArgumentException("Magnet URI does not contain a valid BTIH info hash.", nameof(magnetUri));

        string infoHashHex = sha1Hash.ToString().ToUpperInvariant();
        string? displayName = MagnetUri.TryGetDisplayName(trimmed);
        var trackers = new List<string>();
        var seenTrackers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nonTrackerParams = new List<string>();

        string query = trimmed.Substring("magnet:?".Length);
        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
                continue;

            string key = part[..eq];
            string rawVal = part[(eq + 1)..];
            if (string.Equals(key, "tr", StringComparison.OrdinalIgnoreCase))
            {
                string decoded = Uri.UnescapeDataString(rawVal).Trim();
                if (!string.IsNullOrEmpty(decoded) && seenTrackers.Add(decoded))
                    trackers.Add(decoded);
            }
            else
            {
                nonTrackerParams.Add(part);
                if (string.IsNullOrWhiteSpace(displayName) && string.Equals(key, "dn", StringComparison.OrdinalIgnoreCase))
                    displayName = Uri.UnescapeDataString(rawVal.Replace('+', ' ')).Trim();
            }
        }

        string cleanMagnet = "magnet:?" + (nonTrackerParams.Count > 0
            ? string.Join("&", nonTrackerParams)
            : $"xt=urn:btih:{infoHashHex}");

        return (infoHashHex, string.IsNullOrWhiteSpace(displayName) ? null : displayName, cleanMagnet, trackers);
    }

    public static string? ExtractBencodedName(byte[] bytes)
    {
        // Simple safe scan for "4:name<len>:<utf8>" inside bencoded metadata/resume buffer if needed.
        ReadOnlySpan<byte> needle = "4:name"u8;
        int idx = bytes.AsSpan().IndexOf(needle);
        if (idx < 0)
            return null;

        int pos = idx + needle.Length;
        int colon = Array.IndexOf(bytes, (byte)':', pos, Math.Min(12, bytes.Length - pos));
        if (colon <= pos)
            return null;

        string lenStr = Encoding.ASCII.GetString(bytes, pos, colon - pos);
        if (!int.TryParse(lenStr, out int strLen) || strLen <= 0 || colon + 1 + strLen > bytes.Length)
            return null;

        return Encoding.UTF8.GetString(bytes, colon + 1, strLen);
    }
}
