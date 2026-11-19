namespace VelocityDM.Core.Models;

/// <summary>
/// Persistent application settings for VelocityDM.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 3;

    public int SettingsSchemaVersion { get; set; } = 0;

    public string DefaultDownloadDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "VelocityDM");

    public bool CategorizeIntoSubfolders { get; set; } = true;

    public bool RememberCustomDownloadLocation { get; set; } = true;

    public int DefaultConnectionsPerDownload { get; set; } = 32;

    public int MaxConcurrentDownloads { get; set; } = 3;

    /// <summary>
    /// Global download bandwidth cap in bytes/sec. Zero means unlimited.
    /// </summary>
    public long GlobalSpeedLimitBytesPerSec { get; set; } = 0;

    public bool ClipboardMonitoringEnabled { get; set; } = true;

    /// <summary>
    /// When true, clipboard watcher prompts on known downloadable file extensions;
    /// when false, prompts on any HTTP/HTTPS URL copied to clipboard.
    /// </summary>
    public bool ClipboardOnlyKnownFileExtensions { get; set; } = true;

    public bool AutoStartQueuedDownloads { get; set; } = true;

    public int MaxRetriesPerSegment { get; set; } = 5;

    public int BufferSizeBytes { get; set; } = 65536;

    public int RequestTimeoutSeconds { get; set; } = 30;

    public string UserAgent { get; set; } = "VelocityDM/2.0 (Windows NT; Multi-Segment)";

    public bool MinimizeToTrayOnClose { get; set; } = true;

    public bool StartMinimizedToTray { get; set; } = false;

    public bool LaunchAtWindowsStartup { get; set; } = false;

    public int BrowserBridgePort { get; set; } = 17890;

    public bool AutoStartBrowserDownloads { get; set; } = false;

    public long TorrentDownloadSpeedLimitBytesPerSec { get; set; } = 0;

    public long TorrentUploadSpeedLimitBytesPerSec { get; set; } = 0;

    public int TorrentMaxConnections { get; set; } = 300;

    public bool StopSeedingWhenComplete { get; set; } = false;

    public AppSettings Clone() => new()
    {
        SettingsSchemaVersion = SettingsSchemaVersion,
        DefaultDownloadDirectory = DefaultDownloadDirectory,
        CategorizeIntoSubfolders = CategorizeIntoSubfolders,
        RememberCustomDownloadLocation = RememberCustomDownloadLocation,
        DefaultConnectionsPerDownload = DefaultConnectionsPerDownload,
        MaxConcurrentDownloads = MaxConcurrentDownloads,
        GlobalSpeedLimitBytesPerSec = GlobalSpeedLimitBytesPerSec,
        ClipboardMonitoringEnabled = ClipboardMonitoringEnabled,
        ClipboardOnlyKnownFileExtensions = ClipboardOnlyKnownFileExtensions,
        AutoStartQueuedDownloads = AutoStartQueuedDownloads,
        MaxRetriesPerSegment = MaxRetriesPerSegment,
        BufferSizeBytes = BufferSizeBytes,
        RequestTimeoutSeconds = RequestTimeoutSeconds,
        UserAgent = UserAgent,
        MinimizeToTrayOnClose = MinimizeToTrayOnClose,
        StartMinimizedToTray = StartMinimizedToTray,
        LaunchAtWindowsStartup = LaunchAtWindowsStartup,
        BrowserBridgePort = BrowserBridgePort,
        AutoStartBrowserDownloads = AutoStartBrowserDownloads,
        TorrentDownloadSpeedLimitBytesPerSec = TorrentDownloadSpeedLimitBytesPerSec,
        TorrentUploadSpeedLimitBytesPerSec = TorrentUploadSpeedLimitBytesPerSec,
        TorrentMaxConnections = TorrentMaxConnections,
        StopSeedingWhenComplete = StopSeedingWhenComplete
    };

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(DefaultDownloadDirectory))
        {
            DefaultDownloadDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "VelocityDM");
        }

        DefaultConnectionsPerDownload = Math.Clamp(DefaultConnectionsPerDownload, 1, 32);
        MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 16);
        GlobalSpeedLimitBytesPerSec = Math.Max(0, GlobalSpeedLimitBytesPerSec);
        MaxRetriesPerSegment = Math.Clamp(MaxRetriesPerSegment, 0, 20);
        BufferSizeBytes = Math.Clamp(BufferSizeBytes, 8192, 1024 * 1024);
        RequestTimeoutSeconds = Math.Clamp(RequestTimeoutSeconds, 5, 300);
        BrowserBridgePort = Math.Clamp(BrowserBridgePort, 1024, 65535);
        TorrentDownloadSpeedLimitBytesPerSec = Math.Max(0, TorrentDownloadSpeedLimitBytesPerSec);
        TorrentUploadSpeedLimitBytesPerSec = Math.Max(0, TorrentUploadSpeedLimitBytesPerSec);
        TorrentMaxConnections = Math.Clamp(TorrentMaxConnections, 10, 1000);
    }

    /// <summary>
    /// Upgrades legacy defaults and repairs corrupted nested category paths from earlier builds.
    /// </summary>
    public bool ApplyMigrations()
    {
        bool changed = false;
        if (SettingsSchemaVersion < 2)
        {
            if (DefaultConnectionsPerDownload == 8)
            {
                DefaultConnectionsPerDownload = 32;
            }
            changed = true;
        }

        if (SettingsSchemaVersion < 3)
        {
            string cleaned = Services.FileCategoryClassifier.SanitizeCorruptedCategoryNesting(DefaultDownloadDirectory);
            if (!string.Equals(cleaned, DefaultDownloadDirectory, StringComparison.Ordinal))
            {
                DefaultDownloadDirectory = cleaned;
            }
            SettingsSchemaVersion = CurrentSchemaVersion;
            changed = true;
        }

        return changed;
    }
}
