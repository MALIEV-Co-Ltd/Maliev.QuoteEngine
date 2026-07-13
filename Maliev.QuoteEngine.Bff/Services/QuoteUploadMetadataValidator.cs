using System.Net.Http.Headers;
using Maliev.QuoteEngine.Bff.Clients;

namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>
/// Verifies that UploadService's completed-file record matches the BFF upload locator before the
/// file enters the geometry pipeline. Browser and process-local facts never override this record.
/// </summary>
internal static class QuoteUploadMetadataValidator
{
    private const string ExpectedUploadServiceId = "QuoteEngine";

    internal static bool TryValidateCompletedUpload(
        UploadState upload,
        QuoteUploadMetadata? metadata,
        out Guid canonicalFileId)
    {
        canonicalFileId = Guid.Empty;
        if (metadata is null ||
            string.IsNullOrWhiteSpace(upload.DownstreamUploadId) ||
            !string.Equals(metadata.UploadId, upload.DownstreamUploadId, StringComparison.Ordinal) ||
            !string.Equals(metadata.ServiceId, ExpectedUploadServiceId, StringComparison.Ordinal) ||
            !string.Equals(metadata.StoragePath, upload.StoragePath, StringComparison.Ordinal) ||
            metadata.FileSize != upload.ExpectedSizeBytes ||
            !ContentTypesMatch(metadata.ContentType, upload.ContentType) ||
            !Guid.TryParse(metadata.FileId, out canonicalFileId) ||
            canonicalFileId == Guid.Empty)
        {
            canonicalFileId = Guid.Empty;
            return false;
        }

        return true;
    }

    private static bool ContentTypesMatch(string? authoritative, string? expected)
    {
        return TryNormalizeMediaType(authoritative, out var authoritativeMediaType) &&
               TryNormalizeMediaType(expected, out var expectedMediaType) &&
               string.Equals(authoritativeMediaType, expectedMediaType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeMediaType(string? value, out string mediaType)
    {
        mediaType = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !MediaTypeHeaderValue.TryParse(value, out var parsed) ||
            string.IsNullOrWhiteSpace(parsed.MediaType))
        {
            return false;
        }

        mediaType = parsed.MediaType;
        return true;
    }
}
