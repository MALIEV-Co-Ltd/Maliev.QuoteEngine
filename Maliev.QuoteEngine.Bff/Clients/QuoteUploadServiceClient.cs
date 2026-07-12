// Maliev.QuoteEngine.Bff/Clients/QuoteUploadServiceClient.cs
using System.Net;
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
    /// Gets authoritative metadata for a completed UploadService file.
    /// UploadService creates this resource only after the upload is completed.
    /// </summary>
    public virtual async Task<QuoteUploadMetadata?> GetCompletedFileMetadataAsync(
        string uploadId,
        CancellationToken ct)
    {
        using var response = await http.GetAsync(
            $"/upload/v1/files/{Uri.EscapeDataString(uploadId)}",
            ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError(
                "UploadService metadata lookup failed {Status} for {UploadId}: {Body}",
                response.StatusCode,
                uploadId,
                responseBody);
            response.EnsureSuccessStatusCode();
        }

        return await response.Content.ReadFromJsonAsync<QuoteUploadMetadata>(cancellationToken: ct)
            ?? throw new InvalidOperationException("UploadService returned empty file metadata.");
    }

    /// <summary>
    /// Streams a chunk of file bytes to UploadService, forwarding the Content-Range header.
    /// </summary>
    public virtual async Task StreamUploadAsync(Stream body, string contentType, long contentLength,
        string contentRange, string downstreamUploadId, string storagePath, CancellationToken ct)
    {
        var progress = await StreamUploadWithProgressAsync(
            body,
            contentType,
            contentLength,
            contentRange,
            downstreamUploadId,
            storagePath,
            ct);
        if (!progress.IsComplete)
        {
            throw new HttpRequestException(
                "UploadService accepted only part of a one-shot upload.",
                inner: null,
                statusCode: (System.Net.HttpStatusCode)308);
        }
    }

    /// <summary>
    /// Streams one resumable chunk and returns the downstream acknowledged progress.
    /// </summary>
    public virtual async Task<QuoteUploadStreamProgress> StreamUploadWithProgressAsync(
        Stream body,
        string contentType,
        long contentLength,
        string contentRange,
        string downstreamUploadId,
        string storagePath,
        CancellationToken ct)
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

        if ((int)response.StatusCode == 308)
        {
            var progress = await response.Content.ReadFromJsonAsync<ResumeUploadProgressResponse>(cancellationToken: ct);
            return new QuoteUploadStreamProgress(
                IsComplete: false,
                BytesReceived: progress?.BytesReceived);
        }

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

        return new QuoteUploadStreamProgress(IsComplete: true, BytesReceived: null);
    }

    /// <summary>
    /// Returns a short-lived signed download URL for the given GCS storage path.
    /// </summary>
    public virtual async Task<string> GetDownloadUrlByPathAsync(string storagePath,
        int expirationMinutes = 60, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("/upload/v1/files/by-path/signed-url", new
        {
            storagePath,
            expirationMinutes
        }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError(
                "UploadService signed URL failed {Status} for {StoragePath}: {Body}",
                response.StatusCode,
                storagePath,
                responseBody);
            response.EnsureSuccessStatusCode();
        }

        var result = await response.Content.ReadFromJsonAsync<SignedUrlResponse>(ct);
        return result?.SignedUrl ?? throw new InvalidOperationException("UploadService returned null URL");
    }

    /// <summary>
    /// Downloads file bytes for a storage path through UploadService's signed URL flow.
    /// </summary>
    public virtual async Task<byte[]> GetFileBytesByPathAsync(
        string storagePath,
        long maxBytes,
        CancellationToken ct = default)
    {
        var signedUrl = await GetDownloadUrlByPathAsync(storagePath, expirationMinutes: 10, ct);
        using var response = await http.GetAsync(signedUrl, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError(
                "UploadService signed download failed {Status} for {StoragePath}: {Body}",
                response.StatusCode,
                storagePath,
                responseBody);
            response.EnsureSuccessStatusCode();
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > 0 && contentLength > maxBytes)
        {
            throw new InvalidOperationException(
                $"UploadService file {storagePath} exceeds the {maxBytes} byte inline attachment limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            if (memory.Length + read > maxBytes)
            {
                throw new InvalidOperationException(
                    $"UploadService file {storagePath} exceeds the {maxBytes} byte inline attachment limit.");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
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

    private sealed record SignedUrlResponse(string SignedUrl);

    private sealed record InitiateResumableUploadResponse(string UploadId, string SessionUri, DateTime ExpiresAt, long TotalSize);
}

/// <summary>Represents the progress acknowledged by UploadService for one resumable chunk.</summary>
public sealed record QuoteUploadStreamProgress(bool IsComplete, long? BytesReceived);

/// <summary>Authoritative completed-file metadata returned by UploadService.</summary>
public sealed record QuoteUploadMetadata(
    string FileId,
    string UploadId,
    string ServiceId,
    string StoragePath,
    long FileSize,
    string ContentType);

file sealed record ResumeUploadProgressResponse(long? BytesReceived);
