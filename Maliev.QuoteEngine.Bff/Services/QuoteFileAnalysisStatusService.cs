// Maliev.QuoteEngine.Bff/Services/QuoteFileAnalysisStatusService.cs
using System.Collections.Concurrent;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>
/// In-memory implementation of IQuoteFileAnalysisStatusService.
/// Keyed by storagePath (case-insensitive). Redis-upgradable.
/// </summary>
public sealed class QuoteFileAnalysisStatusService : IQuoteFileAnalysisStatusService
{
    private readonly ConcurrentDictionary<string, QuoteFileAnalysisStatus> _store
        = new(StringComparer.OrdinalIgnoreCase);

    public Task<QuoteFileAnalysisStatus?> GetStatusAsync(string storagePath, CancellationToken ct = default)
        => Task.FromResult(_store.TryGetValue(storagePath, out var s) ? s : null);

    public Task SetProcessingAsync(string storagePath, CancellationToken ct = default)
    {
        _store.AddOrUpdate(
            storagePath,
            _ => new QuoteFileAnalysisStatus { StoragePath = storagePath, Status = "Processing" },
            (_, existing) => existing with { Status = "Processing" });
        return Task.CompletedTask;
    }

    public Task SetGlbReadyAsync(string storagePath, string glbUrl, string? thumbnailUrl,
        int bodyCount, bool isManifold, CancellationToken ct = default,
        string? viewerStoragePath = null, string? viewerFileExtension = null)
    {
        _store.AddOrUpdate(
            storagePath,
            _ => new QuoteFileAnalysisStatus
            {
                StoragePath = storagePath,
                Status = "GlbReady",
                GlbUrl = glbUrl,
                ViewerStoragePath = viewerStoragePath,
                ViewerFileExtension = viewerFileExtension,
                ThumbnailUrl = thumbnailUrl,
                VolumeCc = null,
                SurfaceAreaCm2 = null,
                BodyCount = bodyCount,
                IsManifold = isManifold
            },
            (_, existing) => existing with
            {
                Status = "GlbReady",
                GlbUrl = glbUrl,
                ViewerStoragePath = viewerStoragePath ?? existing.ViewerStoragePath,
                ViewerFileExtension = viewerFileExtension ?? existing.ViewerFileExtension,
                ThumbnailUrl = thumbnailUrl,
                VolumeCc = existing.VolumeCc,
                SurfaceAreaCm2 = existing.SurfaceAreaCm2,
                BodyCount = bodyCount,
                IsManifold = isManifold
            });
        return Task.CompletedTask;
    }

    public Task SetDfmReportsAsync(string storagePath,
        QeFdmDfmReport? fdmReport, QeSlaDfmReport? slaReport, QeCncDfmReport? cncReport,
        IReadOnlyList<string> overlayGlbUrls, string? nonManifoldReason,
        string? analysisErrorCode, CancellationToken ct = default)
    {
        _store.AddOrUpdate(
            storagePath,
            _ => new QuoteFileAnalysisStatus
            {
                StoragePath = storagePath,
                Status = "DfmAnalysisReady",
                FdmReport = fdmReport,
                SlaReport = slaReport,
                CncReport = cncReport,
                ViewerStoragePath = storagePath,
                OverlayGlbUrls = overlayGlbUrls,
                VolumeCc = null,
                SurfaceAreaCm2 = null,
                NonManifoldReason = nonManifoldReason,
                AnalysisErrorCode = analysisErrorCode
            },
            (_, existing) => existing with
            {
                Status = "DfmAnalysisReady",
                // Merge-not-overwrite: keep existing non-null reports
                FdmReport = fdmReport ?? existing.FdmReport,
                SlaReport = slaReport ?? existing.SlaReport,
                CncReport = cncReport ?? existing.CncReport,
                GlbUrl = existing.GlbUrl,
                ViewerStoragePath = existing.ViewerStoragePath,
                ViewerFileExtension = existing.ViewerFileExtension,
                ThumbnailUrl = existing.ThumbnailUrl,
                VolumeCc = existing.VolumeCc,
                SurfaceAreaCm2 = existing.SurfaceAreaCm2,
                BodyCount = existing.BodyCount,
                IsManifold = existing.IsManifold,
                // Accumulate overlay URLs across multiple DfmAnalysisReady events
                OverlayGlbUrls = overlayGlbUrls.Count > 0
                    ? [.. existing.OverlayGlbUrls, .. overlayGlbUrls]
                    : existing.OverlayGlbUrls,
                NonManifoldReason = nonManifoldReason ?? existing.NonManifoldReason,
                AnalysisErrorCode = analysisErrorCode ?? existing.AnalysisErrorCode
            });
        return Task.CompletedTask;
    }

    public Task SetLocalGeometryMetricsAsync(string storagePath,
        decimal? volumeCc, decimal? surfaceAreaCm2, bool isManifold,
        string? nonManifoldReason, CancellationToken ct = default)
    {
        _store.AddOrUpdate(
            storagePath,
            _ => new QuoteFileAnalysisStatus
            {
                StoragePath = storagePath,
                Status = "Processing",
                VolumeCc = volumeCc,
                SurfaceAreaCm2 = surfaceAreaCm2,
                IsManifold = isManifold,
                NonManifoldReason = ResolveNonManifoldReason(isManifold, nonManifoldReason, null)
            },
            (_, existing) => existing with
            {
                VolumeCc = volumeCc ?? existing.VolumeCc,
                SurfaceAreaCm2 = surfaceAreaCm2 ?? existing.SurfaceAreaCm2,
                IsManifold = isManifold,
                NonManifoldReason = ResolveNonManifoldReason(
                    isManifold,
                    nonManifoldReason,
                    existing.NonManifoldReason)
            });
        return Task.CompletedTask;
    }

    public Task SetFailedAsync(string storagePath, string errorCode, CancellationToken ct = default)
    {
        _store.AddOrUpdate(
            storagePath,
            _ => new QuoteFileAnalysisStatus
            {
                StoragePath = storagePath,
                Status = "Failed",
                AnalysisErrorCode = errorCode
            },
            (_, existing) => existing with { Status = "Failed", AnalysisErrorCode = errorCode });
        return Task.CompletedTask;
    }

    private static string? ResolveNonManifoldReason(
        bool isManifold,
        string? nonManifoldReason,
        string? existingNonManifoldReason)
    {
        return isManifold
            ? null
            : nonManifoldReason ?? existingNonManifoldReason;
    }
}
