using System.Text.Json.Serialization;

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
