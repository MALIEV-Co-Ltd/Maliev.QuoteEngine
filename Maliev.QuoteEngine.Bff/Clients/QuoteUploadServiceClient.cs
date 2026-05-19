// Maliev.QuoteEngine.Bff/Clients/QuoteUploadServiceClient.cs
using System.Net.Http.Json;

namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>
/// HTTP client for the UploadService. Handles file byte streaming and signed URL generation.
/// Base address configured as "https+http://UploadService" via Aspire service discovery.
/// </summary>
public class QuoteUploadServiceClient(HttpClient http, ILogger<QuoteUploadServiceClient> logger)
{
    /// <summary>
    /// Streams a chunk of file bytes to UploadService, forwarding the Content-Range header.
    /// </summary>
    public virtual async Task StreamUploadAsync(Stream body, string contentType, long contentLength,
        string contentRange, string storagePath, CancellationToken ct)
    {
        using var content = new StreamContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        content.Headers.TryAddWithoutValidation("Content-Range", contentRange);

        var response = await http.PutAsync(
            $"upload/v1/resumable?path={Uri.EscapeDataString(storagePath)}",
            content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("UploadService stream failed {Status}: {Body}", response.StatusCode, responseBody);
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
}
