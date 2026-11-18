using System.Diagnostics;
using System.Reflection;
using System.Security;
using System.Text.Json;

namespace VelocityDM.Mac.Helpers;

/// <summary>
/// Extracts the bundled Chrome MV3 extension, registers Chrome/Chromium/Brave/Edge Native Messaging Hosts
/// on macOS, and manages optional LaunchAgent startup.
/// </summary>
public static class MacBrowserRegistrationService
{
    public const string NativeHostName = "com.velocitydm.host";
    public const string ChromeExtensionId = "pcegcihecngonbfglmjgobmlafofmchk";

    private static string AppSupportRoot =>
        Path.Combine(MacPaths.HomeDirectory, "Library", "Application Support");

    private static readonly (string Name, string BrowserDir, string NativeHostsRelPath)[] SupportedBrowsers =
    [
        ("Google Chrome", Path.Combine(AppSupportRoot, "Google", "Chrome"), "NativeMessagingHosts"),
        ("Google Chrome Canary", Path.Combine(AppSupportRoot, "Google", "Chrome Canary"), "NativeMessagingHosts"),
        ("Chromium", Path.Combine(AppSupportRoot, "Chromium"), "NativeMessagingHosts"),
        ("Brave", Path.Combine(AppSupportRoot, "BraveSoftware", "Brave-Browser"), "NativeMessagingHosts"),
        ("Microsoft Edge", Path.Combine(AppSupportRoot, "Microsoft Edge"), "NativeMessagingHosts"),
        ("Arc", Path.Combine(AppSupportRoot, "Arc", "User Data"), "NativeMessagingHosts")
    ];

    public static string ExtensionDirectory => MacPaths.ExtensionDirectory;

    public static string ChromeHostManifestPath =>
        Path.Combine(AppSupportRoot, "Google", "Chrome", "NativeMessagingHosts", $"{NativeHostName}.json");

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

    public static IReadOnlyList<string> RegisterNativeMessagingHosts()
    {
        string? exePath = ResolveHostExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            throw new InvalidOperationException("Cannot determine VelocityDM executable path.");

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
        var registeredBrowsers = new List<string>();

        foreach (var (name, browserDir, relHosts) in SupportedBrowsers)
        {
            // Always write Google Chrome's NativeMessagingHosts manifest; for other browsers write if installed
            if (!string.Equals(name, "Google Chrome", StringComparison.Ordinal) && !Directory.Exists(browserDir))
                continue;

            try
            {
                string hostsDir = Path.Combine(browserDir, relHosts);
                Directory.CreateDirectory(hostsDir);
                string manifestPath = Path.Combine(hostsDir, $"{NativeHostName}.json");
                File.WriteAllText(manifestPath, json);
                registeredBrowsers.Add(name);
            }
            catch
            {
                // Ignore protected or inaccessible browser profile roots
            }
        }

        return registeredBrowsers;
    }

    public static bool IsNativeHostRegistered()
    {
        try
        {
            if (!File.Exists(ChromeHostManifestPath))
                return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(ChromeHostManifestPath));
            if (doc.RootElement.TryGetProperty("path", out var pathProp))
            {
                string? registeredPath = pathProp.GetString();
                string? expected = ResolveHostExecutablePath();
                return !string.IsNullOrWhiteSpace(registeredPath) &&
                       File.Exists(registeredPath) &&
                       (expected == null || string.Equals(registeredPath, expected, StringComparison.Ordinal));
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static string? ResolveHostExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            !Path.GetFileName(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        string appBundleBinary = "/Applications/VelocityDM.app/Contents/MacOS/VelocityDM";
        if (File.Exists(appBundleBinary))
            return appBundleBinary;

        return processPath;
    }

    public static string? TryGetAppBundlePath()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            return null;

        // .../VelocityDM.app/Contents/MacOS/VelocityDM
        var macOsDir = Directory.GetParent(exe);
        var contentsDir = macOsDir?.Parent;
        var bundleDir = contentsDir?.Parent;
        if (macOsDir?.Name == "MacOS" &&
            contentsDir?.Name == "Contents" &&
            bundleDir != null &&
            bundleDir.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return bundleDir.FullName;
        }

        return null;
    }

    public static void SetMacLoginStartup(bool enabled)
    {
        try
        {
            string plistPath = MacPaths.LaunchAgentPlistPath;
            if (!enabled)
            {
                if (File.Exists(plistPath))
                    File.Delete(plistPath);
                return;
            }

            string? exe = ResolveHostExecutablePath();
            if (string.IsNullOrWhiteSpace(exe))
                return;

            string? dir = Path.GetDirectoryName(plistPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string escapedExe = SecurityElement.Escape(exe) ?? exe;
            string plist = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>com.velocitydm.app</string>
                    <key>ProgramArguments</key>
                    <array>
                        <string>{escapedExe}</string>
                        <string>--minimized</string>
                    </array>
                    <key>RunAtLoad</key>
                    <true/>
                </dict>
                </plist>
                """;
            File.WriteAllText(plistPath, plist);
        }
        catch
        {
            // Ignore restricted LaunchAgents directory errors
        }
    }

    public static void OpenChromeExtensionsPage()
    {
        string[] candidates =
        [
            "/Applications/Google Chrome.app",
            Path.Combine(MacPaths.HomeDirectory, "Applications", "Google Chrome.app")
        ];

        var chromeApp = candidates.FirstOrDefault(Directory.Exists);
        if (chromeApp != null)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                ArgumentList = { "-a", chromeApp, "chrome://extensions" },
                UseShellExecute = false
            });
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/open",
            ArgumentList = { "-a", "Google Chrome", "chrome://extensions" },
            UseShellExecute = false
        });
    }

    public static void RevealExtensionInFinder()
    {
        string dir = ExtractExtensionFiles();
        Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/open",
            ArgumentList = { dir },
            UseShellExecute = false
        });
    }

    public static void CopyToMacClipboard(string text)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/pbcopy",
                RedirectStandardInput = true,
                UseShellExecute = false
            });
            if (p != null)
            {
                p.StandardInput.Write(text);
                p.StandardInput.Close();
                p.WaitForExit(1500);
            }
        }
        catch
        {
            // Ignore clipboard errors
        }
    }
}
