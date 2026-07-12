// Maliev.QuoteEngine.Bff/Services/IQuoteFileAnalysisStatusService.cs
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Bff.Services;

public interface IQuoteFileAnalysisStatusService
{
    Task<QuoteFileAnalysisStatus?> GetStatusAsync(string storagePath, CancellationToken ct = default);
    Task SetProcessingAsync(string storagePath, CancellationToken ct = default);
    Task SetGlbReadyAsync(string storagePath, string glbUrl, string? thumbnailUrl,
        int bodyCount, bool isManifold, CancellationToken ct = default,
        string? viewerStoragePath = null, string? viewerFileExtension = null,
        decimal? volumeCc = null, decimal? surfaceAreaCm2 = null);
    Task SetDfmReportsAsync(string storagePath,
        QeFdmDfmReport? fdmReport, QeSlaDfmReport? slaReport, QeCncDfmReport? cncReport,
        IReadOnlyList<string> overlayGlbUrls, string? nonManifoldReason,
        string? analysisErrorCode, CancellationToken ct = default);
    Task SetLocalGeometryMetricsAsync(string storagePath,
        decimal? volumeCc, decimal? surfaceAreaCm2, bool isManifold,
        string? nonManifoldReason, CancellationToken ct = default);
    Task SetLocalDfmReportsAsync(string storagePath,
        QeFdmDfmReport? fdmReport, QeSlaDfmReport? slaReport, QeCncDfmReport? cncReport,
        IReadOnlyList<string> overlayGlbUrls, string? nonManifoldReason,
        CancellationToken ct = default);
    Task SetFailedAsync(string storagePath, string errorCode, CancellationToken ct = default);
}
