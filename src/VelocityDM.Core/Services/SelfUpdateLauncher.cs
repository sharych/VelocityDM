using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace VelocityDM.Core.Services;

public sealed record SelfUpdateLaunchResult(
    bool RestartInitiated,
    string Message);

/// <summary>
/// Launches platform-specific detached self-update scripts for Windows (Inno Setup EXE or Portable ZIP)
/// and macOS (.dmg app bundle replacement) after SHA-256 verification.
/// </summary>
public static class SelfUpdateLauncher
{
    private const string WindowsUninstallSubkey =
        @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8D4F61C2-9B3E-4E12-A7C4-65F2D9B31A08}_is1";

    public static async Task<SelfUpdateLaunchResult> ApplyUpdateAndScheduleRestartAsync(
        PreparedUpdate prepared,
        string? macAppBundlePath = null,
        bool registerBrowserAfterPortableUpdate = true,
        CancellationToken cancellationToken = default)
    {
        int currentPid = Environment.ProcessId;
        string? currentExe = Environment.ProcessPath;

        if (OperatingSystem.IsWindows())
        {
            if (prepared.Release.SelectedAsset.PackageKind == UpdatePackageKind.WindowsPortableZip)
            {
                string targetDir = Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory;
                string exeToLaunch = !string.IsNullOrWhiteSpace(currentExe)
                    ? currentExe
                    : Path.Combine(targetDir, "VelocityDM.exe");

                string extractedDir = Path.Combine(prepared.StagingDirectory, "extracted");
                await Task.Run(() =>
                {
                    if (Directory.Exists(extractedDir))
                        Directory.Delete(extractedDir, recursive: true);
                    ZipFile.ExtractToDirectory(prepared.PackageFilePath, extractedDir, overwriteFiles: true);
                }, cancellationToken).ConfigureAwait(false);

                string payloadSource = ResolveExtractedRoot(extractedDir, "VelocityDM.exe");
                string scriptPath = Path.Combine(prepared.StagingDirectory, "apply-portable-update.ps1");
                string script = BuildWindowsPortableScript(
                    currentPid,
                    payloadSource,
                    targetDir,
                    exeToLaunch,
                    prepared.StagingDirectory,
                    registerBrowserAfterPortableUpdate);

                await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken)
                    .ConfigureAwait(false);

                StartHiddenPowerShell(scriptPath);
                return new SelfUpdateLaunchResult(true, "Portable update staged. Restarting VelocityDM...");
            }
            else
            {
                string fallbackExe = !string.IsNullOrWhiteSpace(currentExe) &&
                    currentExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? currentExe
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Programs",
                        "VelocityDM",
                        "VelocityDM.exe");

                string scriptPath = Path.Combine(prepared.StagingDirectory, "apply-installer-update.ps1");
                string script = BuildWindowsInstallerScript(
                    currentPid,
                    prepared.PackageFilePath,
                    fallbackExe,
                    prepared.StagingDirectory);

                await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken)
                    .ConfigureAwait(false);

                StartHiddenPowerShell(scriptPath);
                return new SelfUpdateLaunchResult(true, "Installer launched. VelocityDM will restart once installation completes.");
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            string targetAppBundle = !string.IsNullOrWhiteSpace(macAppBundlePath)
                ? macAppBundlePath
                : "/Applications/VelocityDM.app";

            string scriptPath = Path.Combine(
                Path.GetTempPath(),
                $"velocitydm-update-{Guid.NewGuid():N}.sh");

            string script = BuildMacDmgScript(
                currentPid,
                prepared.PackageFilePath,
                targetAppBundle,
                prepared.StagingDirectory,
                scriptPath);

            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
                .ConfigureAwait(false);

            try
            {
                File.SetUnixFileMode(scriptPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
                // Fallback via bash invocation still works
            }

            StartDetachedBashScript(scriptPath);
            return new SelfUpdateLaunchResult(true, "Update verified. Replacing VelocityDM.app and restarting...");
        }

        throw new PlatformNotSupportedException("Self-update is supported on Windows and macOS.");
    }

    public static string BuildWindowsInstallerScript(
        int oldPid,
        string installerExePath,
        string fallbackAppExePath,
        string stagingDir)
    {
        string pidLiteral = oldPid.ToString();
        string setupLiteral = EscapePowerShellSingleQuoted(installerExePath);
        string fallbackLiteral = EscapePowerShellSingleQuoted(fallbackAppExePath);
        string stagingLiteral = EscapePowerShellSingleQuoted(stagingDir);
        string regLiteral = EscapePowerShellSingleQuoted(WindowsUninstallSubkey);

        return $$"""
$ErrorActionPreference = 'SilentlyContinue'
$oldPid = {{pidLiteral}}
$setupPath = '{{setupLiteral}}'
$fallbackExe = '{{fallbackLiteral}}'
$stagingDir = '{{stagingLiteral}}'
$uninstallKey = '{{regLiteral}}'

try {
    Wait-Process -Id $oldPid -Timeout 20 -ErrorAction SilentlyContinue
} catch {}
Start-Sleep -Milliseconds 400

$proc = Start-Process -FilePath $setupPath -ArgumentList @('/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CLOSEAPPLICATIONS') -Wait -PassThru

$launchExe = $fallbackExe
try {
    if (Test-Path $uninstallKey) {
        $loc = (Get-ItemProperty -Path $uninstallKey -ErrorAction SilentlyContinue).InstallLocation
        if ($loc) {
            $candidate = Join-Path $loc 'VelocityDM.exe'
            if (Test-Path $candidate) { $launchExe = $candidate }
        }
    }
} catch {}

if (Test-Path $launchExe) {
    Start-Process -FilePath $launchExe
}

Start-Sleep -Seconds 2
Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
""";
    }

    public static string BuildWindowsPortableScript(
        int oldPid,
        string extractedSourceDir,
        string targetAppDir,
        string exeToLaunch,
        string stagingDir,
        bool registerBrowser)
    {
        string pidLiteral = oldPid.ToString();
        string sourceLiteral = EscapePowerShellSingleQuoted(extractedSourceDir);
        string targetLiteral = EscapePowerShellSingleQuoted(targetAppDir);
        string exeLiteral = EscapePowerShellSingleQuoted(exeToLaunch);
        string stagingLiteral = EscapePowerShellSingleQuoted(stagingDir);
        string regBool = registerBrowser ? "$true" : "$false";

        return $$"""
$ErrorActionPreference = 'Stop'
$oldPid = {{pidLiteral}}
$sourceDir = '{{sourceLiteral}}'
$targetDir = '{{targetLiteral}}'
$launchExe = '{{exeLiteral}}'
$stagingDir = '{{stagingLiteral}}'
$registerBrowser = {{regBool}}

try {
    Wait-Process -Id $oldPid -Timeout 20 -ErrorAction SilentlyContinue
} catch {}
Start-Sleep -Milliseconds 500

for ($attempt = 1; $attempt -le 6; $attempt++) {
    try {
        Copy-Item -Path (Join-Path $sourceDir '*') -Destination $targetDir -Recurse -Force
        break
    } catch {
        if ($attempt -eq 6) { break }
        Start-Sleep -Milliseconds 500
    }
}

if ($registerBrowser -and (Test-Path $launchExe)) {
    try {
        Start-Process -FilePath $launchExe -ArgumentList @('--register-browser') -Wait -WindowStyle Hidden
    } catch {}
}

if (Test-Path $launchExe) {
    Start-Process -FilePath $launchExe
}

Start-Sleep -Seconds 2
Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
""";
    }

    public static string BuildMacDmgScript(
        int oldPid,
        string dmgPath,
        string targetAppBundle,
        string stagingDir,
        string scriptSelfPath)
    {
        string logPath = Path.Combine(Path.GetTempPath(), "velocitydm-selfupdate.log");
        string mountDir = Path.Combine(stagingDir, "mnt");
        string backupApp = Path.Combine(stagingDir, "VelocityDM.backup.app");

        return $$"""
#!/usr/bin/env bash
set -uo pipefail
exec >'{{EscapeBashSingleQuoted(logPath)}}' 2>&1

OLD_PID={{oldPid}}
DMG_PATH='{{EscapeBashSingleQuoted(dmgPath)}}'
TARGET_APP='{{EscapeBashSingleQuoted(targetAppBundle)}}'
STAGING_DIR='{{EscapeBashSingleQuoted(stagingDir)}}'
MOUNT_DIR='{{EscapeBashSingleQuoted(mountDir)}}'
BACKUP_APP='{{EscapeBashSingleQuoted(backupApp)}}'
SCRIPT_PATH='{{EscapeBashSingleQuoted(scriptSelfPath)}}'

cleanup() {
  hdiutil detach "$MOUNT_DIR" -force >/dev/null 2>&1 || true
  rm -rf "$STAGING_DIR" >/dev/null 2>&1 || true
  rm -f "$SCRIPT_PATH" >/dev/null 2>&1 || true
}
trap cleanup EXIT

for _ in $(seq 1 40); do
  if ! kill -0 "$OLD_PID" 2>/dev/null; then
    break
  fi
  sleep 0.5
done
sleep 0.3

mkdir -p "$MOUNT_DIR"
if ! hdiutil attach -nobrowse -readonly -mountpoint "$MOUNT_DIR" "$DMG_PATH"; then
  exit 1
fi

SOURCE_APP="$MOUNT_DIR/VelocityDM.app"
if [ ! -d "$SOURCE_APP" ]; then
  SOURCE_APP="$(find "$MOUNT_DIR" -maxdepth 1 -name '*.app' -print -quit || true)"
fi

if [ -z "$SOURCE_APP" ] || [ ! -d "$SOURCE_APP" ]; then
  exit 1
fi

  TARGET_PARENT="$(dirname "$TARGET_APP")"
  mkdir -p "$TARGET_PARENT" 2>/dev/null || true
  if [ -w "$TARGET_PARENT" ]; then
    if [ -d "$TARGET_APP" ]; then
      mv "$TARGET_APP" "$BACKUP_APP" || true
    fi
    if ! ditto "$SOURCE_APP" "$TARGET_APP"; then
      if [ -d "$BACKUP_APP" ] && [ ! -d "$TARGET_APP" ]; then
        mv "$BACKUP_APP" "$TARGET_APP" || true
      fi
      exit 1
    fi
  else
    /usr/bin/osascript - "$SOURCE_APP" "$TARGET_APP" <<'OSA' || exit 1
on run argv
  set srcApp to quoted form of item 1 of argv
  set dstApp to quoted form of item 2 of argv
  do shell script "rm -rf " & dstApp & " && ditto " & srcApp & " " & dstApp with administrator privileges
end run
OSA
  fi

  xattr -dr com.apple.quarantine "$TARGET_APP" >/dev/null 2>&1 || true
  "$TARGET_APP/Contents/MacOS/VelocityDM" --register-browser >/dev/null 2>&1 || true

  hdiutil detach "$MOUNT_DIR" -force >/dev/null 2>&1 || true
  open "$TARGET_APP"
""";
    }

    private static string ResolveExtractedRoot(string extractedDir, string expectedExeName)
    {
        if (File.Exists(Path.Combine(extractedDir, expectedExeName)))
            return extractedDir;

        foreach (string subDir in Directory.EnumerateDirectories(extractedDir))
        {
            if (File.Exists(Path.Combine(subDir, expectedExeName)))
                return subDir;
        }

        return extractedDir;
    }

    private static void StartHiddenPowerShell(string scriptPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Path.GetTempPath()
        });
    }

    private static void StartDetachedBashScript(string scriptPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"\"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        });
    }

    private static string EscapePowerShellSingleQuoted(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string EscapeBashSingleQuoted(string value) =>
        value.Replace("'", "'\\''", StringComparison.Ordinal);
}
