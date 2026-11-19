using System.Text.RegularExpressions;
using VelocityDM.Core.Models;

namespace VelocityDM.Core.Services;

/// <summary>
/// Categorizes files by extension/MIME type, sanitizes filenames, and resolves non-colliding save paths.
/// </summary>
public static partial class FileCategoryClassifier
{
    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".dmg", ".pkg", ".deb", ".rpm", ".apk", ".bat", ".cmd", ".ps1", ".sh"
    };

    private static readonly HashSet<string> CompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab", ".iso", ".img"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpeg", ".mpg", ".ts"
    };

    private static readonly HashSet<string> MusicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".opus", ".wma", ".aiff"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".csv", ".md", ".epub", ".json", ".xml"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".ico", ".tiff", ".tif", ".avif", ".heic"
    };

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static FileCategory Classify(string? fileName, string? contentType = null)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(ext))
        {
            if (ProgramExtensions.Contains(ext)) return FileCategory.Programs;
            if (CompressedExtensions.Contains(ext)) return FileCategory.Compressed;
            if (VideoExtensions.Contains(ext)) return FileCategory.Video;
            if (MusicExtensions.Contains(ext)) return FileCategory.Music;
            if (DocumentExtensions.Contains(ext)) return FileCategory.Documents;
            if (ImageExtensions.Contains(ext)) return FileCategory.Images;
        }

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var mime = contentType.Split(';')[0].Trim().ToLowerInvariant();
            if (mime.StartsWith("video/")) return FileCategory.Video;
            if (mime.StartsWith("audio/")) return FileCategory.Music;
            if (mime.StartsWith("image/")) return FileCategory.Images;
            if (mime is "application/pdf" or "text/plain" or "text/csv" or "application/msword"
                || mime.StartsWith("application/vnd.openxmlformats-officedocument")
                || mime.StartsWith("application/vnd.ms-"))
            {
                return FileCategory.Documents;
            }
            if (mime is "application/zip" or "application/x-zip-compressed" or "application/x-rar-compressed"
                or "application/x-7z-compressed" or "application/gzip" or "application/x-tar" or "application/x-iso9660-image")
            {
                return FileCategory.Compressed;
            }
            if (mime is "application/x-msdownload" or "application/x-msi" or "application/vnd.android.package-archive")
            {
                return FileCategory.Programs;
            }
        }

        return FileCategory.Other;
    }

    public static bool IsKnownDownloadableExtension(string? urlOrFileName)
    {
        if (string.IsNullOrWhiteSpace(urlOrFileName))
            return false;

        var candidate = urlOrFileName;
        if (Uri.TryCreate(urlOrFileName, UriKind.Absolute, out var uri))
        {
            candidate = uri.AbsolutePath;
        }

        var ext = Path.GetExtension(candidate);
        if (string.IsNullOrWhiteSpace(ext))
            return false;

        return ProgramExtensions.Contains(ext)
            || CompressedExtensions.Contains(ext)
            || VideoExtensions.Contains(ext)
            || MusicExtensions.Contains(ext)
            || DocumentExtensions.Contains(ext)
            || ImageExtensions.Contains(ext);
    }

    public static string GetCategorySubfolder(FileCategory category) => category switch
    {
        FileCategory.Programs => "Programs",
        FileCategory.Compressed => "Compressed",
        FileCategory.Video => "Video",
        FileCategory.Music => "Music",
        FileCategory.Documents => "Documents",
        FileCategory.Images => "Images",
        _ => "Other"
    };

    public static string ResolveSaveDirectory(string baseDirectory, FileCategory category, bool categorizeIntoSubfolders)
    {
        return categorizeIntoSubfolders
            ? Path.Combine(baseDirectory, GetCategorySubfolder(category))
            : baseDirectory;
    }

    /// <summary>
    /// Creates the base folder and all standard VelocityDM category subfolders inside it.
    /// </summary>
    public static IReadOnlyList<string> CreateCategoryHierarchy(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("Base directory cannot be empty.", nameof(baseDirectory));

        string root = baseDirectory.Trim();
        Directory.CreateDirectory(root);

        var created = new List<string>();
        foreach (FileCategory category in Enum.GetValues<FileCategory>())
        {
            string sub = Path.Combine(root, GetCategorySubfolder(category));
            Directory.CreateDirectory(sub);
            created.Add(sub);
        }

        string torrentsDir = Path.Combine(root, "Torrents");
        Directory.CreateDirectory(torrentsDir);
        created.Add(torrentsDir);

        return created;
    }

    public static (string FileName, FileCategory Category, string SaveDirectory) ResolveSuggestedDestination(
        AppSettings settings,
        string url,
        string? customFileName = null)
    {
        string name = string.IsNullOrWhiteSpace(customFileName) ? string.Empty : SanitizeFileName(customFileName);
        if (string.IsNullOrWhiteSpace(name) && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            string seg = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(seg) && seg != "/")
            {
                name = SanitizeFileName(Uri.UnescapeDataString(seg));
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "download.bin";
        }

        FileCategory category = Classify(name);
        string saveDir = ResolveSaveDirectory(settings.DefaultDownloadDirectory, category, settings.CategorizeIntoSubfolders);
        return (name, category, saveDir);
    }

    public static string SanitizeFileName(string? rawName, string fallback = "download.bin")
    {
        var name = (rawName ?? string.Empty).Trim();
        name = Path.GetFileName(name);

        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            cleaned[i] = Array.IndexOf(invalidChars, name[i]) >= 0 ? '_' : name[i];
        }

        var result = new string(cleaned).Trim().TrimEnd('.');
        result = MultiSpaceRegex().Replace(result, " ");

        if (string.IsNullOrWhiteSpace(result))
            return fallback;

        var baseName = Path.GetFileNameWithoutExtension(result);
        if (ReservedWindowsNames.Contains(baseName))
        {
            result = "_" + result;
        }

        if (result.Length > 180)
        {
            var ext = Path.GetExtension(result);
            var maxBase = Math.Max(16, 180 - ext.Length);
            result = result[..Math.Min(result.Length, maxBase)] + ext;
        }

        return result;
    }

    public static string GetUniqueFilePath(string directory, string fileName, ISet<string>? reservedPaths = null)
    {
        Directory.CreateDirectory(directory);
        var sanitized = SanitizeFileName(fileName);
        var candidate = Path.Combine(directory, sanitized);

        if (!IsTaken(candidate, reservedPaths))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(sanitized);
        var ext = Path.GetExtension(sanitized);

        for (int index = 1; index < 10_000; index++)
        {
            var next = Path.Combine(directory, $"{stem} ({index}){ext}");
            if (!IsTaken(next, reservedPaths))
                return next;
        }

        return Path.Combine(directory, $"{stem}_{Guid.NewGuid():N}{ext}");
    }

    private static bool IsTaken(string path, ISet<string>? reservedPaths)
    {
        if (File.Exists(path) || File.Exists(path + ".vdm.part"))
            return true;

        return reservedPaths != null && (reservedPaths.Contains(path) || reservedPaths.Contains(path + ".vdm.part"));
    }

    public static string GuessExtensionFromMime(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return string.Empty;

        var mime = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return mime switch
        {
            "application/zip" or "application/x-zip-compressed" => ".zip",
            "application/pdf" => ".pdf",
            "application/x-msdownload" => ".exe",
            "application/x-msi" => ".msi",
            "application/x-7z-compressed" => ".7z",
            "application/x-rar-compressed" => ".rar",
            "application/gzip" => ".gz",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/x-matroska" => ".mkv",
            "audio/mpeg" => ".mp3",
            "audio/flac" => ".flac",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "text/plain" => ".txt",
            "application/json" => ".json",
            _ => string.Empty
        };
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();
}
