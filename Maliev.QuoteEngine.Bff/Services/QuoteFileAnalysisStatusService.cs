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
        _store[storagePath] = new QuoteFileAnalysisStatus { StoragePath = storagePath, Status = "Processing" };
        return Task.CompletedTask;
    }

    public Task SetGlbReadyAsync(string storagePath, string glbUrl, string? thumbnailUrl,
        int bodyCount, bool isManifold, CancellationToken ct = default)
    {
        _store.AddOrUpdate(
            storagePath,
            _ => new QuoteFileAnalysisStatus
            {
                StoragePath = storagePath,
                Status = "GlbReady",
                GlbUrl = glbUrl,
                ThumbnailUrl = thumbnailUrl,
                BodyCount = bodyCount,
                IsManifold = isManifold
            },
            (_, existing) => existing with
            {
                Status = "GlbReady",
                GlbUrl = glbUrl,
                ThumbnailUrl = thumbnailUrl,
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
                OverlayGlbUrls = overlayGlbUrls,
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
                // Accumulate overlay URLs across multiple DfmAnalysisReady events
                OverlayGlbUrls = overlayGlbUrls.Count > 0
                    ? [.. existing.OverlayGlbUrls, .. overlayGlbUrls]
                    : existing.OverlayGlbUrls,
                NonManifoldReason = nonManifoldReason ?? existing.NonManifoldReason,
                AnalysisErrorCode = analysisErrorCode ?? existing.AnalysisErrorCode
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
}
