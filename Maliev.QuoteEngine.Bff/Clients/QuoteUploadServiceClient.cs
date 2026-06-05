// Maliev.QuoteEngine.Bff/Clients/QuoteUploadServiceClient.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>
/// HTTP client for the UploadService. Handles file byte streaming and signed URL generation.
/// Base address configured as "https+http://UploadService" via Aspire service discovery.
/// </summary>
public class QuoteUploadServiceClient(HttpClient http, ILogger<QuoteUploadServiceClient> logger)
{
    /// <summary>
    /// Initiates a downstream UploadService resumable upload session.
    /// </summary>
    public virtual async Task<string> InitiateResumableUploadAsync(
        string fileName,
        string contentType,
        long totalSize,
        string storagePath,
        IReadOnlyDictionary<string, string>? metadataTags,
        CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/upload/v1/uploads/resumable", new
        {
            path = storagePath,
            fileName,
            serviceName = "QuoteEngine",
            contentType,
            totalSize,
            overwrite = true,
            metadataTags
        }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("UploadService initiate resumable failed {Status}: {Body}", response.StatusCode, responseBody);
            response.EnsureSuccessStatusCode();
        }

        var result = await response.Content.ReadFromJsonAsync<InitiateResumableUploadResponse>(cancellationToken: ct);
        return result?.UploadId ?? throw new InvalidOperationException("UploadService returned null upload ID.");
    }

    /// <summary>
    /// Streams a chunk of file bytes to UploadService, forwarding the Content-Range header.
    /// </summary>
    public virtual async Task StreamUploadAsync(Stream body, string contentType, long contentLength,
        string contentRange, string downstreamUploadId, string storagePath, CancellationToken ct)
    {
        using var content = new StreamContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        content.Headers.ContentRange = ContentRangeHeaderValue.Parse(contentRange);
        if (contentLength > 0)
        {
            content.Headers.ContentLength = contentLength;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/upload/v1/uploads/resumable/{Uri.EscapeDataString(downstreamUploadId)}")
        {
            Content = content
        };

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError(
                "UploadService stream failed {Status} for {StoragePath}: {Body}",
                response.StatusCode,
                storagePath,
                responseBody);
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// Returns a short-lived signed download URL for the given GCS storage path.
    /// </summary>
    public virtual async Task<string> GetDownloadUrlByPathAsync(string storagePath,
        int expirationMinutes = 60, CancellationToken ct = default)
    {
        var response = await http.GetAsync(
            $"upload/v1/signed-url?path={Uri.EscapeDataString(storagePath)}&expirationMinutes={expirationMinutes}",
            ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SignedUrlResponse>(ct);
        return result?.Url ?? throw new InvalidOperationException("UploadService returned null URL");
    }

    /// <summary>
    /// Copies a file within the storage bucket (used for demo file duplication if needed).
    /// </summary>
    public virtual async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("upload/v1/copy",
            new { SourcePath = sourcePath, DestinationPath = destinationPath }, ct);
        response.EnsureSuccessStatusCode();
    }

    private sealed record SignedUrlResponse(string Url);

    private sealed record InitiateResumableUploadResponse(string UploadId, string SessionUri, DateTime ExpiresAt, long TotalSize);
}
