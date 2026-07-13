namespace Maliev.QuoteEngine.Bff.Services;

public sealed record QuoteAnalysisPreviewRefresh(
    long ExpectedRevision,
    string? ViewerStoragePath,
    string? ViewerUrl,
    string? ThumbnailStoragePath,
    string? ThumbnailUrl,
    IReadOnlyList<string> OverlayStoragePaths,
    IReadOnlyList<string> OverlayUrls);

public sealed record QuoteAnalysisPreviewResolution(
    QuoteFileAnalysisStatus Status,
    string Availability,
    int? RetryAfterSeconds = null);
