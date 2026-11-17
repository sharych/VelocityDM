namespace VelocityDM.App.Helpers;

public static class ByteFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string FormatBytes(long? bytes)
    {
        if (bytes is null or < 0)
            return "Unknown";

        if (bytes.Value == 0)
            return "0 B";

        double value = bytes.Value;
        int unit = 0;
        while (value >= 1024.0 && unit < Units.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }

        return unit == 0
            ? $"{value:0} {Units[unit]}"
            : $"{value:0.##} {Units[unit]}";
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0.5)
            return "—";

        double value = bytesPerSecond;
        int unit = 0;
        while (value >= 1024.0 && unit < Units.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }

        return unit == 0
            ? $"{value:0} {Units[unit]}/s"
            : $"{value:0.00} {Units[unit]}/s";
    }

    public static string FormatEta(double? seconds)
    {
        if (seconds is null or < 0 || double.IsNaN(seconds.Value) || double.IsInfinity(seconds.Value))
            return "—";

        var ts = TimeSpan.FromSeconds(Math.Min(seconds.Value, 99 * 3600));
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes:00}m";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes}m {ts.Seconds:00}s";
        return $"{Math.Max(1, ts.Seconds)}s";
    }
}
