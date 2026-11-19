using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using VelocityDM.Core.Models;

namespace VelocityDM.Core.Services;

public enum UpdatePackageKind
{
    WindowsInstallerExe,
    WindowsPortableZip,
    MacOsDmg
}

public sealed record UpdateAssetInfo(
    string Name,
    string DownloadUrl,
    long SizeBytes,
    string? Digest,
    UpdatePackageKind PackageKind);

public sealed record GitHubReleaseInfo(
    string TagName,
    Version Version,
    string Name,
    string Body,
    string HtmlUrl,
    DateTimeOffset? PublishedAt,
    UpdateAssetInfo SelectedAsset,
    string? ChecksumsUrl);

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    Version CurrentVersion,
    GitHubReleaseInfo? Release,
    string Message);

public sealed record UpdateProgress(
    long BytesReceived,
    long? TotalBytes,
    string Stage,
    string Message);

public sealed record PreparedUpdate(
    GitHubReleaseInfo Release,
    string StagingDirectory,
    string PackageFilePath,
    string ExpectedSha256,
    string ActualSha256);

/// <summary>
/// Checks GitHub Releases for newer VelocityDM versions, downloads platform installers/DMGs,
/// and verifies SHA-256 checksums before applying self-updates.
/// </summary>
public sealed class GitHubUpdateService : IDisposable
{
    public const string DefaultOwner = "sharych";
    public const string DefaultRepo = "VelocityDM";

    private static readonly Regex ShaLineRegex = new(
        @"^(?<hash>[a-fA-F0-9]{64})\s+[\*\s]?(?<name>.+?)\s*$",
        RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _owner;
    private readonly string _repo;

    public GitHubUpdateService(
        HttpClient? httpClient = null,
        string owner = DefaultOwner,
        string repo = DefaultRepo)
    {
        _owner = owner;
        _repo = repo;
        if (httpClient != null)
        {
            _http = httpClient;
            _ownsHttp = false;
        }
        else
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            _ownsHttp = true;
        }
    }

    public string LatestReleaseApiUrl =>
        $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";

    public static Version GetCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(GitHubUpdateService).Assembly;
        var infoAttr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParseVersion(infoAttr, out var infoVersion) && infoVersion != null)
            return infoVersion;

        var raw = assembly.GetName().Version ?? new Version(2, 0, 0, 0);
        return NormalizeVersion(raw);
    }

    public static Version NormalizeVersion(Version v) =>
        new(
            Math.Max(0, v.Major),
            Math.Max(0, v.Minor),
            v.Build >= 0 ? v.Build : 0,
            v.Revision >= 0 ? v.Revision : 0);

    public static bool TryParseVersion(string? raw, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string cleaned = raw.Trim();
        if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[1..];

        int plusIdx = cleaned.IndexOf('+');
        if (plusIdx >= 0) cleaned = cleaned[..plusIdx];

        int dashIdx = cleaned.IndexOf('-');
        if (dashIdx >= 0) cleaned = cleaned[..dashIdx];

        if (!Version.TryParse(cleaned, out var parsed))
            return false;

        version = NormalizeVersion(parsed);
        return true;
    }

    public static bool ShouldAutoPrompt(AppSettings settings, GitHubReleaseInfo? release)
    {
        if (!settings.AutoCheckForUpdates || release == null)
            return false;

        if (!string.IsNullOrWhiteSpace(settings.SkippedUpdateVersion) &&
            TryParseVersion(settings.SkippedUpdateVersion, out var skippedVer) &&
            skippedVer != null &&
            release.Version <= skippedVer)
        {
            return false;
        }

        return true;
    }

    public static bool IsAllowedGitHubDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        string host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    public static (bool IsInstaller, bool IsPortable) DetectWindowsInstallMode(string? baseDirectory = null)
    {
        string dir = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        bool hasUninstaller = File.Exists(Path.Combine(dir, "unins000.exe"));
        bool hasPortableRuntime = File.Exists(Path.Combine(dir, "coreclr.dll"));
        return (hasUninstaller, !hasUninstaller && hasPortableRuntime);
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(
        Version? currentVersionOverride = null,
        CancellationToken cancellationToken = default)
    {
        Version current = currentVersionOverride != null
            ? NormalizeVersion(currentVersionOverride)
            : GetCurrentVersion();

        using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        ConfigureGitHubHeaders(req, current, acceptJson: true);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        OSPlatform platform = OperatingSystem.IsMacOS() ? OSPlatform.OSX
            : OperatingSystem.IsWindows() ? OSPlatform.Windows
            : OSPlatform.Linux;

        var (isInstaller, isPortable) = OperatingSystem.IsWindows()
            ? DetectWindowsInstallMode()
            : (false, false);

        return ParseReleaseJson(
            json,
            current,
            platform,
            RuntimeInformation.ProcessArchitecture,
            isInstaller,
            isPortable);
    }

    public static UpdateCheckResult ParseReleaseJson(
        string json,
        Version currentVersion,
        OSPlatform platform,
        Architecture architecture,
        bool isWindowsInstaller = true,
        bool isWindowsPortable = false)
    {
        Version normalizedCurrent = NormalizeVersion(currentVersion);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean())
        {
            return new UpdateCheckResult(false, normalizedCurrent, null, "Latest release is still a draft.");
        }

        if (root.TryGetProperty("prerelease", out var preProp) && preProp.GetBoolean())
        {
            return new UpdateCheckResult(false, normalizedCurrent, null, "Latest release is a prerelease.");
        }

        string tagName = root.TryGetProperty("tag_name", out var tagProp) ? (tagProp.GetString() ?? string.Empty) : string.Empty;
        if (!TryParseVersion(tagName, out var releaseVersion) || releaseVersion == null)
        {
            return new UpdateCheckResult(false, normalizedCurrent, null, "Could not parse GitHub release version.");
        }

        string releaseName = root.TryGetProperty("name", out var nameProp) && !string.IsNullOrWhiteSpace(nameProp.GetString())
            ? nameProp.GetString()!
            : tagName;
        string body = root.TryGetProperty("body", out var bodyProp) ? (bodyProp.GetString() ?? string.Empty) : string.Empty;
        string htmlUrl = root.TryGetProperty("html_url", out var htmlProp)
            ? (htmlProp.GetString() ?? $"https://github.com/{DefaultOwner}/{DefaultRepo}/releases")
            : $"https://github.com/{DefaultOwner}/{DefaultRepo}/releases";

        DateTimeOffset? publishedAt = null;
        if (root.TryGetProperty("published_at", out var pubProp) &&
            DateTimeOffset.TryParse(pubProp.GetString(), out var parsedDate))
        {
            publishedAt = parsedDate;
        }

        var parsedAssets = new List<UpdateAssetInfo>();
        string? checksumsUrl = null;

        if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var assetEl in assetsProp.EnumerateArray())
            {
                string? name = assetEl.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;
                string? url = assetEl.TryGetProperty("browser_download_url", out var uProp) ? uProp.GetString() : null;
                long size = assetEl.TryGetProperty("size", out var sProp) && sProp.TryGetInt64(out var sz) ? sz : 0;
                string? digest = assetEl.TryGetProperty("digest", out var dProp) ? dProp.GetString() : null;

                if (string.IsNullOrWhiteSpace(name) || !IsAllowedGitHubDownloadUrl(url))
                    continue;

                if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    checksumsUrl = url;
                    continue;
                }

                UpdatePackageKind? kind = ClassifyAssetKind(name);
                if (kind != null)
                {
                    parsedAssets.Add(new UpdateAssetInfo(name, url!, size, NormalizeSha256Digest(digest), kind.Value));
                }
            }
        }

        UpdateAssetInfo? selectedAsset = SelectBestAsset(parsedAssets, platform, architecture, isWindowsInstaller, isWindowsPortable);
        if (selectedAsset == null)
        {
            return new UpdateCheckResult(
                false,
                normalizedCurrent,
                null,
                $"Release {tagName} has no compatible package for {platform} ({architecture}).");
        }

        var releaseInfo = new GitHubReleaseInfo(
            tagName,
            releaseVersion,
            releaseName,
            body.Trim(),
            htmlUrl,
            publishedAt,
            selectedAsset,
            checksumsUrl);

        bool newer = releaseVersion > normalizedCurrent;
        string msg = newer
            ? $"VelocityDM {tagName} is available (installed: v{FormatShortVersion(normalizedCurrent)})."
            : $"You're up to date (v{FormatShortVersion(normalizedCurrent)}).";

        return new UpdateCheckResult(newer, normalizedCurrent, releaseInfo, msg);
    }

    public static string FormatShortVersion(Version v) =>
        v.Revision > 0 ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    private static UpdatePackageKind? ClassifyAssetKind(string fileName)
    {
        if (fileName.StartsWith("VelocityDM-Setup-", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePackageKind.WindowsInstallerExe;
        }

        if (fileName.EndsWith("-win-x64-portable.zip", StringComparison.OrdinalIgnoreCase) ||
            (fileName.Contains("win", StringComparison.OrdinalIgnoreCase) &&
             fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            return UpdatePackageKind.WindowsPortableZip;
        }

        if (fileName.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePackageKind.MacOsDmg;
        }

        return null;
    }

    public static UpdateAssetInfo? SelectBestAsset(
        IReadOnlyList<UpdateAssetInfo> assets,
        OSPlatform platform,
        Architecture architecture,
        bool isWindowsInstaller = true,
        bool isWindowsPortable = false)
    {
        if (platform == OSPlatform.Windows)
        {
            var setupExe = assets.FirstOrDefault(a => a.PackageKind == UpdatePackageKind.WindowsInstallerExe);
            var portableZip = assets.FirstOrDefault(a => a.PackageKind == UpdatePackageKind.WindowsPortableZip);

            if (isWindowsPortable && !isWindowsInstaller)
                return portableZip ?? setupExe;

            return setupExe ?? portableZip;
        }

        if (platform == OSPlatform.OSX)
        {
            var arm64Dmg = assets.FirstOrDefault(a =>
                a.PackageKind == UpdatePackageKind.MacOsDmg &&
                a.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase));
            var x64Dmg = assets.FirstOrDefault(a =>
                a.PackageKind == UpdatePackageKind.MacOsDmg &&
                a.Name.Contains("x64", StringComparison.OrdinalIgnoreCase));
            var anyDmg = assets.FirstOrDefault(a => a.PackageKind == UpdatePackageKind.MacOsDmg);

            return architecture == Architecture.Arm64
                ? arm64Dmg ?? x64Dmg ?? anyDmg
                : x64Dmg ?? arm64Dmg ?? anyDmg;
        }

        return null;
    }

    public static string? NormalizeSha256Digest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return null;

        string trimmed = digest.Trim();
        if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["sha256:".Length..];

        return trimmed.Length == 64 && trimmed.All(Uri.IsHexDigit)
            ? trimmed.ToLowerInvariant()
            : null;
    }

    public static string? ParseSha256Sums(string content, string assetFileName)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(assetFileName))
            return null;

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var match = ShaLineRegex.Match(line);
            if (!match.Success)
                continue;

            string entryName = Path.GetFileName(match.Groups["name"].Value.Trim());
            if (string.Equals(entryName, assetFileName, StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups["hash"].Value.ToLowerInvariant();
            }
        }

        return null;
    }

    public async Task<PreparedUpdate> DownloadAndVerifyUpdateAsync(
        GitHubReleaseInfo release,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAllowedGitHubDownloadUrl(release.SelectedAsset.DownloadUrl))
            throw new InvalidOperationException("Update asset URL is not an HTTPS GitHub URL.");

        string updatesRoot = Path.Combine(Path.GetTempPath(), "VelocityDM-Updates");
        CleanupOldStagingDirectories(updatesRoot);

        string safeTag = SanitizePathSegment(release.TagName);
        string stagingDir = Path.Combine(updatesRoot, $"{safeTag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        string safeFileName = Path.GetFileName(release.SelectedAsset.Name);
        string packagePath = Path.Combine(stagingDir, safeFileName);

        try
        {
            progress?.Report(new UpdateProgress(0, release.SelectedAsset.SizeBytes, "Downloading", $"Downloading {safeFileName}..."));

            await DownloadFileAsync(release.SelectedAsset.DownloadUrl, packagePath, release.SelectedAsset.SizeBytes, progress, cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new UpdateProgress(release.SelectedAsset.SizeBytes, release.SelectedAsset.SizeBytes, "Verifying", "Verifying SHA-256 checksum..."));

            string? expectedSha256 = null;
            if (IsAllowedGitHubDownloadUrl(release.ChecksumsUrl))
            {
                using var sumReq = new HttpRequestMessage(HttpMethod.Get, release.ChecksumsUrl);
                ConfigureGitHubHeaders(sumReq, GetCurrentVersion(), acceptJson: false);
                using var sumResp = await _http.SendAsync(sumReq, cancellationToken).ConfigureAwait(false);
                if (sumResp.IsSuccessStatusCode)
                {
                    string sumsText = await sumResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    expectedSha256 = ParseSha256Sums(sumsText, safeFileName);
                }
            }

            expectedSha256 ??= NormalizeSha256Digest(release.SelectedAsset.Digest);

            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                throw new InvalidOperationException(
                    $"Release {release.TagName} did not provide a SHA-256 checksum for {safeFileName}. Auto-update aborted for security.");
            }

            string actualSha256 = await ComputeSha256Async(packagePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SHA-256 mismatch for {safeFileName}. Expected {expectedSha256}, got {actualSha256}.");
            }

            return new PreparedUpdate(release, stagingDir, packagePath, expectedSha256, actualSha256);
        }
        catch
        {
            try
            {
                if (Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, recursive: true);
            }
            catch
            {
                // Ignore temp cleanup failures
            }
            throw;
        }
    }

    private async Task DownloadFileAsync(
        string url,
        string destinationPath,
        long fallbackTotalBytes,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ConfigureGitHubHeaders(req, GetCurrentVersion(), acceptJson: false);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long? totalBytes = resp.Content.Headers.ContentLength ?? (fallbackTotalBytes > 0 ? fallbackTotalBytes : null);
        long received = 0;

        await using var source = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var dest = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

        byte[] buffer = new byte[65536];
        DateTime lastReport = DateTime.MinValue;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;

            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalMilliseconds >= 100 || (totalBytes.HasValue && received >= totalBytes.Value))
            {
                lastReport = now;
                progress?.Report(new UpdateProgress(received, totalBytes, "Downloading", $"Downloading {Path.GetFileName(destinationPath)}..."));
            }
        }

        await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(fs, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ConfigureGitHubHeaders(HttpRequestMessage request, Version currentVersion, bool acceptJson)
    {
        request.Headers.UserAgent.ParseAdd($"VelocityDM/{FormatShortVersion(currentVersion)}");
        request.Headers.Accept.ParseAdd(acceptJson ? "application/vnd.github+json" : "application/octet-stream");
        if (acceptJson)
        {
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        }
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }

    private static void CleanupOldStagingDirectories(string updatesRoot)
    {
        try
        {
            if (!Directory.Exists(updatesRoot))
                return;

            DateTime cutoff = DateTime.UtcNow.AddDays(-2);
            foreach (string dir in Directory.EnumerateDirectories(updatesRoot))
            {
                if (Directory.GetCreationTimeUtc(dir) < cutoff)
                {
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    public async Task MonitorForUpdatesAsync(
        Func<AppSettings> getSettings,
        Action<UpdateCheckResult> onUpdateAvailable,
        CancellationToken cancellationToken,
        TimeSpan? initialDelay = null,
        TimeSpan? pollInterval = null)
    {
        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await Task.Delay(initialDelay ?? TimeSpan.FromSeconds(2.5), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(pollInterval ?? TimeSpan.FromHours(6));
        do
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var settings = getSettings();
            if (settings.AutoCheckForUpdates)
            {
                try
                {
                    var result = await CheckForUpdateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (result.UpdateAvailable &&
                        result.Release != null &&
                        ShouldAutoPrompt(settings, result.Release) &&
                        seenTags.Add(result.Release.TagName))
                    {
                        onUpdateAvailable(result);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Offline, transient network error, or GitHub API rate limit — try again next cycle
                }
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
