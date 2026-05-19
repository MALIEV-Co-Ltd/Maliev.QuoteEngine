// Maliev.QuoteEngine.Shared/Quotes/QeSignalRPayloads.cs
namespace Maliev.QuoteEngine.Shared.Quotes;

/// <summary>
/// Pushed to the client via SignalR "GlbReady" when geometry analysis completes.
/// When <see cref="Failed"/> is true, <see cref="GlbUrl"/> is empty and
/// <see cref="ErrorCode"/> contains the failure reason.
/// </summary>
public sealed record QeGlbReadyPayload(
    string StoragePath,
    string GlbUrl,
    string? ThumbnailUrl,
    int BodyCount,
    bool IsManifold,
    bool Failed,
    string? ErrorCode);

/// <summary>
/// Pushed to the client via SignalR "DfmAnalysisReady" when DFM analysis is ready.
/// Always reflects the merged state of all process reports received so far.
/// </summary>
public sealed record QeDfmAnalysisReadyPayload(
    string StoragePath,
    bool IsManifold,
    string? NonManifoldReason,
    QeFdmDfmReport? FdmReport,
    QeSlaDfmReport? SlaReport,
    QeCncDfmReport? CncReport,
    IReadOnlyList<string> OverlayGlbUrls,
    string? AnalysisErrorCode);
