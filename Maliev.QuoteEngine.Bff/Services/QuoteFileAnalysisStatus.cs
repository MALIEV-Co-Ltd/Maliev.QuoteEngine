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
    public string Status { get; init; } = "Processing";      // Processing | GlbReady | DfmAnalysisReady | Failed
    public string? GlbUrl { get; init; }
    public string? ThumbnailUrl { get; init; }
    public int BodyCount { get; init; } = 1;
    public bool IsManifold { get; init; } = true;
    public string? NonManifoldReason { get; init; }
    public string? AnalysisErrorCode { get; init; }
    public QeFdmDfmReport? FdmReport { get; init; }
    public QeSlaDfmReport? SlaReport { get; init; }
    public QeCncDfmReport? CncReport { get; init; }
    public IReadOnlyList<string> OverlayGlbUrls { get; init; } = [];
}
