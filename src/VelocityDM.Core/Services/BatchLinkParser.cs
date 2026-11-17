using System.Text.RegularExpressions;

namespace VelocityDM.Core.Services;

/// <summary>
/// Parses batch URL lists and expands IDM-style wildcard patterns like [01-10] or [a-f].
/// </summary>
public static partial class BatchLinkParser
{
    private const int MaxExpandedPerLine = 2000;

    public static IReadOnlyList<string> ParseAndExpand(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return Array.Empty<string>();

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var lines = rawInput.Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim().Trim(',', ';', '"', '\'');
            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var expanded in ExpandLine(trimmed))
            {
                if (Uri.TryCreate(expanded, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    var normalized = uri.AbsoluteUri;
                    if (seen.Add(normalized))
                    {
                        results.Add(normalized);
                    }
                }
            }
        }

        return results;
    }

    private static IEnumerable<string> ExpandLine(string pattern)
    {
        var numericMatch = NumericRangeRegex().Match(pattern);
        if (numericMatch.Success)
        {
            var startRaw = numericMatch.Groups[1].Value;
            var endRaw = numericMatch.Groups[2].Value;
            if (int.TryParse(startRaw, out int start) && int.TryParse(endRaw, out int end))
            {
                int width = Math.Max(startRaw.Length, endRaw.Length);
                bool zeroPad = startRaw.StartsWith('0') && startRaw.Length > 1 || endRaw.StartsWith('0') && endRaw.Length > 1;
                int step = start <= end ? 1 : -1;
                int count = Math.Abs(end - start) + 1;
                int capped = Math.Min(count, MaxExpandedPerLine);

                var prefix = pattern[..numericMatch.Index];
                var suffix = pattern[(numericMatch.Index + numericMatch.Length)..];

                var list = new List<string>(capped);
                for (int i = 0; i < capped; i++)
                {
                    int value = start + (i * step);
                    string formatted = zeroPad ? value.ToString($"D{width}") : value.ToString();
                    list.Add(prefix + formatted + suffix);
                }
                return list;
            }
        }

        var alphaMatch = AlphaRangeRegex().Match(pattern);
        if (alphaMatch.Success)
        {
            char startChar = alphaMatch.Groups[1].Value[0];
            char endChar = alphaMatch.Groups[2].Value[0];
            if (char.IsLower(startChar) == char.IsLower(endChar))
            {
                int step = startChar <= endChar ? 1 : -1;
                int count = Math.Abs(endChar - startChar) + 1;
                int capped = Math.Min(count, MaxExpandedPerLine);
                var prefix = pattern[..alphaMatch.Index];
                var suffix = pattern[(alphaMatch.Index + alphaMatch.Length)..];

                var list = new List<string>(capped);
                for (int i = 0; i < capped; i++)
                {
                    char c = (char)(startChar + (i * step));
                    list.Add(prefix + c + suffix);
                }
                return list;
            }
        }

        return new[] { pattern };
    }

    [GeneratedRegex(@"\[(\d+)-(\d+)\]")]
    private static partial Regex NumericRangeRegex();

    [GeneratedRegex(@"\[([a-zA-Z])-([a-zA-Z])\]")]
    private static partial Regex AlphaRangeRegex();
}
