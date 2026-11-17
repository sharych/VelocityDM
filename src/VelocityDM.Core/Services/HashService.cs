using System.Security.Cryptography;

namespace VelocityDM.Core.Services;

/// <summary>
/// Computes and verifies SHA-256 checksums for downloaded files.
/// </summary>
public static class HashService
{
    public static string? NormalizeHex(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return null;

        var cleaned = hash.Trim().Replace(" ", string.Empty).Replace("-", string.Empty);
        return cleaned.ToLowerInvariant();
    }

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 131072,
            useAsync: true);

        byte[] hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public static bool AreEqual(string? actualHex, string? expectedHex)
    {
        var a = NormalizeHex(actualHex);
        var e = NormalizeHex(expectedHex);
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(e))
            return false;

        return string.Equals(a, e, StringComparison.OrdinalIgnoreCase);
    }
}
