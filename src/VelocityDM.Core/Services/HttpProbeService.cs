using System.Net;
using System.Net.Http.Headers;
using VelocityDM.Core.Models;

namespace VelocityDM.Core.Services;

public sealed class ProbeResult
{
    public required string OriginalUrl { get; init; }
    public required string FinalUrl { get; init; }
    public required string FileName { get; init; }
    public long? TotalBytes { get; init; }
    public bool SupportsRanges { get; init; }
    public string? ContentType { get; init; }
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
    public FileCategory SuggestedCategory { get; init; }
}

/// <summary>
/// Probes target URLs via HEAD and GET Range: bytes=0-0 fallback to determine filename, size, and range support.
/// </summary>
public sealed class HttpProbeService
{
    private readonly HttpClient _httpClient;

    public HttpProbeService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ProbeResult> ProbeAsync(
        string url,
        CancellationToken cancellationToken = default,
        string? referrer = null,
        string? cookieHeader = null,
        string? userAgent = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only valid HTTP and HTTPS URLs are supported.", nameof(url));
        }

        string finalUrl = uri.AbsoluteUri;
        string? fileName = null;
        long? totalBytes = null;
        bool supportsRanges = false;
        bool explicitNoRanges = false;
        string? contentType = null;
        string? eTag = null;
        string? lastModified = null;
        bool needRangeProbe = false;

        try
        {
            using var headRequest = BuildRequest(HttpMethod.Head, uri, referrer, cookieHeader, userAgent);
            using var headResponse = await _httpClient.SendAsync(
                headRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (headResponse.IsSuccessStatusCode)
            {
                finalUrl = headResponse.RequestMessage?.RequestUri?.AbsoluteUri ?? finalUrl;
                contentType = headResponse.Content.Headers.ContentType?.MediaType;
                totalBytes = headResponse.Content.Headers.ContentLength;
                fileName = ExtractFileName(headResponse, finalUrl, contentType);
                eTag = headResponse.Headers.ETag?.ToString();
                lastModified = headResponse.Content.Headers.LastModified?.ToString("R");

                if (headResponse.Headers.AcceptRanges.Any(v => v.Equals("bytes", StringComparison.OrdinalIgnoreCase)))
                {
                    supportsRanges = true;
                }
                else if (headResponse.Headers.AcceptRanges.Any(v => v.Equals("none", StringComparison.OrdinalIgnoreCase)))
                {
                    explicitNoRanges = true;
                    supportsRanges = false;
                }
                else
                {
                    needRangeProbe = true;
                }

                if (totalBytes is null)
                {
                    needRangeProbe = !explicitNoRanges;
                }
            }
            else if (headResponse.StatusCode is HttpStatusCode.MethodNotAllowed
                     or HttpStatusCode.NotImplemented
                     or HttpStatusCode.Forbidden
                     or HttpStatusCode.BadRequest)
            {
                needRangeProbe = true;
            }
            else
            {
                headResponse.EnsureSuccessStatusCode();
            }
        }
        catch (HttpRequestException) when (!needRangeProbe)
        {
            needRangeProbe = true;
        }

        if (needRangeProbe)
        {
            using var rangeRequest = BuildRequest(HttpMethod.Get, new Uri(finalUrl), referrer, cookieHeader, userAgent);
            rangeRequest.Headers.Range = new RangeHeaderValue(0, 0);

            using var rangeResponse = await _httpClient.SendAsync(
                rangeRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (rangeResponse.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Possible zero-byte file: retry normal GET header check
                using var fallbackGet = BuildRequest(HttpMethod.Get, new Uri(finalUrl), referrer, cookieHeader, userAgent);
                using var getResponse = await _httpClient.SendAsync(
                    fallbackGet,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                getResponse.EnsureSuccessStatusCode();

                finalUrl = getResponse.RequestMessage?.RequestUri?.AbsoluteUri ?? finalUrl;
                contentType ??= getResponse.Content.Headers.ContentType?.MediaType;
                totalBytes ??= getResponse.Content.Headers.ContentLength ?? 0;
                fileName ??= ExtractFileName(getResponse, finalUrl, contentType);
                eTag ??= getResponse.Headers.ETag?.ToString();
                lastModified ??= getResponse.Content.Headers.LastModified?.ToString("R");
                supportsRanges = false;
            }
            else
            {
                rangeResponse.EnsureSuccessStatusCode();

                finalUrl = rangeResponse.RequestMessage?.RequestUri?.AbsoluteUri ?? finalUrl;
                contentType ??= rangeResponse.Content.Headers.ContentType?.MediaType;
                eTag ??= rangeResponse.Headers.ETag?.ToString();
                lastModified ??= rangeResponse.Content.Headers.LastModified?.ToString("R");

                if (rangeResponse.StatusCode == HttpStatusCode.PartialContent)
                {
                    supportsRanges = true;
                    totalBytes = rangeResponse.Content.Headers.ContentRange?.Length ?? totalBytes;
                }
                else
                {
                    supportsRanges = false;
                    totalBytes ??= rangeResponse.Content.Headers.ContentLength;
                }

                fileName = ExtractFileName(rangeResponse, finalUrl, contentType) ?? fileName;
            }
        }

        fileName = FileCategoryClassifier.SanitizeFileName(fileName, BuildFallbackName(finalUrl, contentType));
        var category = FileCategoryClassifier.Classify(fileName, contentType);

        return new ProbeResult
        {
            OriginalUrl = url,
            FinalUrl = finalUrl,
            FileName = fileName,
            TotalBytes = totalBytes,
            SupportsRanges = supportsRanges && totalBytes is > 0,
            ContentType = contentType,
            ETag = eTag,
            LastModified = lastModified,
            SuggestedCategory = category
        };
    }

    internal static string? ExtractFileName(HttpResponseMessage response, string finalUrl, string? contentType)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var dispName = disposition?.FileNameStar ?? disposition?.FileName;
        if (!string.IsNullOrWhiteSpace(dispName))
        {
            dispName = dispName.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(dispName))
                return Uri.UnescapeDataString(dispName);
        }

        if (Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri))
        {
            var segment = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(segment) && segment != "/")
            {
                var unescaped = Uri.UnescapeDataString(segment);
                if (string.IsNullOrWhiteSpace(Path.GetExtension(unescaped)))
                {
                    var guessedExt = FileCategoryClassifier.GuessExtensionFromMime(contentType);
                    if (!string.IsNullOrWhiteSpace(guessedExt))
                        return unescaped + guessedExt;
                }
                return unescaped;
            }
        }

        return null;
    }

    private static string BuildFallbackName(string url, string? contentType)
    {
        var ext = FileCategoryClassifier.GuessExtensionFromMime(contentType);
        if (string.IsNullOrEmpty(ext)) ext = ".bin";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return FileCategoryClassifier.SanitizeFileName($"{uri.Host}_download{ext}", $"download{ext}");
        }
        return $"download{ext}";
    }

    internal static HttpRequestMessage BuildRequest(
        HttpMethod method,
        Uri uri,
        string? referrer,
        string? cookieHeader,
        string? userAgent)
    {
        var req = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrWhiteSpace(referrer) && Uri.TryCreate(referrer, UriKind.Absolute, out var refUri))
        {
            req.Headers.Referrer = refUri;
        }
        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }
        return req;
    }
}
