// Maliev.QuoteEngine.Bff/Services/IQuoteFileAnalysisStatusService.cs
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Bff.Services;

public interface IQuoteFileAnalysisStatusService
{
    Task<QuoteFileAnalysisStatus?> GetStatusAsync(string storagePath, CancellationToken ct = default);
    Task<bool> CanApplyGeometryEventAsync(string storagePath, string fileId,
        Guid eventId, DateTimeOffset occurredAtUtc, DateTimeOffset processedAtUtc,
        QuoteGeometryEventPhase phase, CancellationToken ct = default);
    Task<bool> CanApplyDfmEventAsync(string storagePath, string fileId,
        Guid eventId, CancellationToken ct = default);
    Task SetProcessingAsync(string storagePath, CancellationToken ct = default);
    Task SetGlbReadyAsync(string storagePath, string glbUrl, string? thumbnailUrl,
        int bodyCount, bool isManifold, CancellationToken ct = default,
        string? viewerStoragePath = null, string? viewerFileExtension = null,
        decimal? volumeCc = null, decimal? surfaceAreaCm2 = null,
        string? fileId = null, decimal? supportVolumeCc = null,
        decimal? boundingBoxXmm = null, decimal? boundingBoxYmm = null,
        decimal? boundingBoxZmm = null, int? triangleCount = null,
        string? nonManifoldReason = null, Guid? eventId = null,
        DateTimeOffset? occurredAtUtc = null, DateTimeOffset? processedAtUtc = null);
    Task SetGeometryMetricsAsync(string storagePath, string fileId,
        decimal? volumeCc, decimal? supportVolumeCc, decimal? surfaceAreaCm2,
        decimal? boundingBoxXmm, decimal? boundingBoxYmm, decimal? boundingBoxZmm,
        int? triangleCount, int bodyCount, bool isManifold, string? nonManifoldReason,
        Guid eventId, DateTimeOffset occurredAtUtc, DateTimeOffset processedAtUtc,
        CancellationToken ct = default);
    Task SetDfmReportsAsync(string storagePath,
        QeFdmDfmReport? fdmReport, QeSlaDfmReport? slaReport, QeCncDfmReport? cncReport,
        IReadOnlyList<string> overlayGlbUrls, string? nonManifoldReason,
        string? analysisErrorCode, CancellationToken ct = default,
        string? fileId = null, Guid? eventId = null,
        DateTimeOffset? occurredAtUtc = null, DateTimeOffset? analyzedAtUtc = null,
        int? bodyCount = null);
    Task SetLocalGeometryMetricsAsync(string storagePath,
        decimal? volumeCc, decimal? surfaceAreaCm2, bool isManifold,
        string? nonManifoldReason, CancellationToken ct = default);
    Task SetLocalDfmReportsAsync(string storagePath,
        QeFdmDfmReport? fdmReport, QeSlaDfmReport? slaReport, QeCncDfmReport? cncReport,
        IReadOnlyList<string> overlayGlbUrls, string? nonManifoldReason,
        CancellationToken ct = default);
    Task SetFailedAsync(string storagePath, string errorCode, CancellationToken ct = default);
    Task SetFailedAsync(string storagePath, string fileId, string errorCode,
        Guid eventId, DateTimeOffset occurredAtUtc, CancellationToken ct = default);
}
