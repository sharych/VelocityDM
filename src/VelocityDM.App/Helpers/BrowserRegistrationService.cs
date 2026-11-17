using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace VelocityDM.App.Helpers;

/// <summary>
/// Extracts the bundled Chrome MV3 extension, registers Chrome/Edge Native Messaging Host in HKCU,
/// and manages optional Windows startup registration.
/// </summary>
public static class BrowserRegistrationService
{
    public const string NativeHostName = "com.velocitydm.host";
    public const string ChromeExtensionId = "pcegcihecngonbfglmjgobmlafofmchk";

    private const string ChromeNativeRegPath = @"Software\Google\Chrome\NativeMessagingHosts\" + NativeHostName;
    private const string EdgeNativeRegPath = @"Software\Microsoft\Edge\NativeMessagingHosts\" + NativeHostName;
    private const string RunRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "VelocityDM";

    public static string LocalDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VelocityDM");

    public static string ExtensionDirectory =>
        Path.Combine(LocalDataDir, "ChromeExtension");

    public static string HostManifestPath =>
        Path.Combine(LocalDataDir, $"{NativeHostName}.json");

    public static bool IsNativeHostRegistered()
    {
        try
        {
            if (!File.Exists(HostManifestPath))
                return false;

            using var key = Registry.CurrentUser.OpenSubKey(ChromeNativeRegPath);
            var val = key?.GetValue(null) as string;
            return string.Equals(val, HostManifestPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string ExtractExtensionFiles()
    {
        Directory.CreateDirectory(ExtensionDirectory);
        var assembly = Assembly.GetExecutingAssembly();

        var resources = new (string ResourceName, string FileName)[]
        {
            ("VelocityDM.Extension.manifest.json", "manifest.json"),
            ("VelocityDM.Extension.background.js", "background.js"),
            ("VelocityDM.Extension.popup.html", "popup.html"),
            ("VelocityDM.Extension.popup.js", "popup.js"),
            ("VelocityDM.Extension.icon128.png", "icon128.png")
        };

        foreach (var (resName, fileName) in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resName);
            if (stream == null)
                continue;

            string destPath = Path.Combine(ExtensionDirectory, fileName);
            using var fs = File.Create(destPath);
            stream.CopyTo(fs);
        }

        return ExtensionDirectory;
    }

    public static void RegisterNativeMessagingHost()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            throw new InvalidOperationException("Cannot determine VelocityDM executable path.");

        Directory.CreateDirectory(LocalDataDir);

        var manifest = new
        {
            name = NativeHostName,
            description = "VelocityDM Parallel Download Manager Native Messaging Host",
            path = exePath,
            type = "stdio",
            allowed_origins = new[]
            {
                $"chrome-extension://{ChromeExtensionId}/"
            }
        };

        string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(HostManifestPath, json);

        using (var chromeKey = Registry.CurrentUser.CreateSubKey(ChromeNativeRegPath))
        {
            chromeKey?.SetValue(null, HostManifestPath, RegistryValueKind.String);
        }

        using (var edgeKey = Registry.CurrentUser.CreateSubKey(EdgeNativeRegPath))
        {
            edgeKey?.SetValue(null, HostManifestPath, RegistryValueKind.String);
        }
    }

    public static void SetWindowsStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunRegPath);
            if (key == null) return;

            if (enabled)
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue(RunValueName, $"\"{exePath}\" --minimized", RegistryValueKind.String);
                }
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Ignore registry write failures in restricted environments
        }
    }

    public static void OpenChromeExtensionsPage()
    {
        string[] chromeCandidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
        };

        var chromeExe = chromeCandidates.FirstOrDefault(File.Exists);
        if (chromeExe != null)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = chromeExe,
                Arguments = "chrome://extensions",
                UseShellExecute = true
            });
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "chrome.exe",
            Arguments = "chrome://extensions",
            UseShellExecute = true
        });
    }
}
