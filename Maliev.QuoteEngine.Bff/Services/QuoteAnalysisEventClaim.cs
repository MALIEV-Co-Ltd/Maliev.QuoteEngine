using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Bff.Services;

public enum QuoteAnalysisEventLane
{
    Geometry,
    Dfm
}

public enum QuoteAnalysisClaimDisposition
{
    Acquired,
    PendingNotification,
    DuplicateOrStale
}

public sealed record QuoteAnalysisEventClaim(
    string StoragePath,
    string FileId,
    Guid EventId,
    QuoteAnalysisEventLane Lane,
    QuoteAnalysisClaimDisposition Disposition,
    string? Token,
    DateTimeOffset? ExpiresAtUtc,
    long? Revision = null,
    QuoteFileAnalysisStatus? Snapshot = null);

public sealed record QuoteAnalysisFinalizeResult(
    bool Applied,
    long Revision,
    QuoteFileAnalysisStatus? Snapshot);

public sealed record QuoteGeometryCompletionUpdate(
    string GlbUrl,
    string? ThumbnailUrl,
    int BodyCount,
    bool IsManifold,
    string? ViewerStoragePath,
    string? ViewerFileExtension,
    decimal? VolumeCc,
    decimal? SurfaceAreaCm2,
    string? FileId,
    decimal? SupportVolumeCc,
    decimal? BoundingBoxXmm,
    decimal? BoundingBoxYmm,
    decimal? BoundingBoxZmm,
    int? TriangleCount,
    string? NonManifoldReason,
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset ProcessedAtUtc);

public sealed record QuoteDfmAnalysisUpdate(
    QeFdmDfmReport? FdmReport,
    QeSlaDfmReport? SlaReport,
    QeCncDfmReport? CncReport,
    IReadOnlyList<string> OverlayGlbUrls,
    string? NonManifoldReason,
    string? AnalysisErrorCode,
    string? FileId,
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset AnalyzedAtUtc,
    int? BodyCount);

public abstract class QuoteAnalysisTransientException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed class QuoteAnalysisClaimLostException(string storagePath, Guid eventId)
    : QuoteAnalysisTransientException(
        $"The analysis event claim for '{storagePath}' and event '{eventId}' was lost.");

public sealed class QuoteAnalysisPreviewSigningException(string storagePath, Exception innerException)
    : QuoteAnalysisTransientException(
        $"The analysis preview for '{storagePath}' could not be signed.",
        innerException);

public sealed class QuoteAnalysisNotificationDeliveryException(
    string storagePath,
    Guid eventId,
    Exception innerException)
    : QuoteAnalysisTransientException(
        $"The analysis notification for '{storagePath}' and event '{eventId}' could not be delivered.",
        innerException);

public sealed class QuoteAnalysisConcurrencyException(string storagePath)
    : QuoteAnalysisTransientException(
        $"The analysis state for '{storagePath}' could not be finalized because of concurrent updates.");

internal static class QuoteAnalysisClaimValidator
{
    internal static void ValidateGeometry(
        QuoteAnalysisEventClaim claim,
        QuoteGeometryCompletionUpdate update) =>
        Validate(claim, QuoteAnalysisEventLane.Geometry, update.FileId, update.EventId);

    internal static void ValidateDfm(
        QuoteAnalysisEventClaim claim,
        QuoteDfmAnalysisUpdate update) =>
        Validate(claim, QuoteAnalysisEventLane.Dfm, update.FileId, update.EventId);

    private static void Validate(
        QuoteAnalysisEventClaim claim,
        QuoteAnalysisEventLane expectedLane,
        string? updateFileId,
        Guid updateEventId)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.Disposition != QuoteAnalysisClaimDisposition.Acquired ||
            string.IsNullOrWhiteSpace(claim.Token))
        {
            throw new ArgumentException("Only an acquired analysis claim can be finalized.", nameof(claim));
        }

        if (claim.Lane != expectedLane)
            throw new ArgumentException("The analysis update does not match the claim lane.", nameof(claim));
        if (claim.EventId != updateEventId)
            throw new ArgumentException("The analysis update does not match the claim event.", nameof(claim));
        if (!string.Equals(claim.FileId, updateFileId, StringComparison.Ordinal))
            throw new ArgumentException("The analysis update does not match the claim file.", nameof(claim));
    }
}
