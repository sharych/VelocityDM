using System.Text.Json.Serialization;
using VelocityDM.Core.Models;
using VelocityDM.Core.Services;

namespace VelocityDM.App.Helpers;

public sealed class BridgeCommand
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "download";

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("referrer")]
    public string? Referrer { get; set; }

    [JsonPropertyName("cookies")]
    public string? Cookies { get; set; }

    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; set; }

    [JsonPropertyName("startImmediately")]
    public bool? StartImmediately { get; set; }
}

public static class BridgeAckFactory
{
    public static object Build(BridgeCommand cmd, AppSettings settings, bool ok = true)
    {
        if (!ok)
            return new { ok = false, app = "VelocityDM" };

        string action = (cmd.Action ?? "show").ToLowerInvariant();
        if (action is "show" or "exit" || string.IsNullOrWhiteSpace(cmd.Url))
            return new { ok = true, app = "VelocityDM" };

        var resolved = FileCategoryClassifier.ResolveSuggestedDestination(settings, cmd.Url, cmd.Filename);
        bool startNow = cmd.StartImmediately ?? settings.AutoStartBrowserDownloads;

        return new
        {
            ok = true,
            app = "VelocityDM",
            mode = startNow ? "started" : "popup",
            fileName = resolved.FileName,
            saveDirectory = resolved.SaveDirectory,
            connections = settings.DefaultConnectionsPerDownload
        };
    }
}
