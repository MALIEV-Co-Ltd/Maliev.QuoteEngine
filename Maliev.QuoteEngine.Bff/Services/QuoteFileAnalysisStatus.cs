// Maliev.QuoteEngine.Bff/Services/QuoteFileAnalysisStatus.cs
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>
/// In-memory snapshot of analysis progress for a single uploaded file.
/// All writes go through IQuoteFileAnalysisStatusService; never mutate directly.
/// </summary>
public sealed record QuoteFileAnalysisStatus
{
    public required string StoragePath { get; init; }
    public string? FileId { get; init; }
    public string Status { get; init; } = "Processing";      // Processing | GlbReady | DfmAnalysisReady | Failed
    public bool IsAuthoritative { get; init; }
    public string AnalysisSource { get; init; } = "pending";
    public bool HasAuthoritativeGeometry { get; init; }
    public bool HasAuthoritativeDfm { get; init; }
    public string? GlbUrl { get; init; }
    public string? ViewerStoragePath { get; init; }
    public string? ViewerFileExtension { get; init; }
    public string? ThumbnailUrl { get; init; }
    public decimal? VolumeCc { get; init; }
    public decimal? SupportVolumeCc { get; init; }
    public decimal? SurfaceAreaCm2 { get; init; }
    public decimal? BoundingBoxXmm { get; init; }
    public decimal? BoundingBoxYmm { get; init; }
    public decimal? BoundingBoxZmm { get; init; }
    public int? TriangleCount { get; init; }
    public int BodyCount { get; init; } = 1;
    public bool IsManifold { get; init; } = true;
    public string? NonManifoldReason { get; init; }
    public string? AnalysisErrorCode { get; init; }
    public QeFdmDfmReport? FdmReport { get; init; }
    public QeSlaDfmReport? SlaReport { get; init; }
    public QeCncDfmReport? CncReport { get; init; }
    public IReadOnlyList<string> OverlayGlbUrls { get; init; } = [];
    public QuoteFileAdvisoryAnalysis? AdvisoryAnalysis { get; init; }
    public Guid? LastGeometryEventId { get; init; }
    public DateTimeOffset? LastGeometryEventOccurredAtUtc { get; init; }
    public DateTimeOffset? LastGeometryProcessedAtUtc { get; init; }
    public int LastGeometryEventPhase { get; init; }
    public Guid? LastDfmEventId { get; init; }
    public DateTimeOffset? LastDfmEventOccurredAtUtc { get; init; }
    public IReadOnlyList<Guid> ProcessedDfmEventIds { get; init; } = [];
}

public enum QuoteGeometryEventPhase
{
    Metrics = 1,
    Completion = 2,
    Failure = 3
}

/// <summary>
/// Browser-computed geometry and DFM observations that may improve the preview experience but
/// can never satisfy authoritative manufacturing or commercial gates.
/// </summary>
public sealed record QuoteFileAdvisoryAnalysis
{
    public string Source { get; init; } = "browser_local_advisory";
    public decimal? VolumeCc { get; init; }
    public decimal? SurfaceAreaCm2 { get; init; }
    public bool IsManifold { get; init; } = true;
    public string? NonManifoldReason { get; init; }
    public QeFdmDfmReport? FdmReport { get; init; }
    public QeSlaDfmReport? SlaReport { get; init; }
    public QeCncDfmReport? CncReport { get; init; }
    public IReadOnlyList<string> OverlayGlbUrls { get; init; } = [];
}
