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
    private const int MetricsPhase = (int)QuoteGeometryEventPhase.Metrics;
    private const int CompletionPhase = (int)QuoteGeometryEventPhase.Completion;
    private const int FailurePhase = (int)QuoteGeometryEventPhase.Failure;
    private const string PendingAnalysisSource = "pending";
    private const string ServerAnalysisSource = "server_analysis";

    private readonly ConcurrentDictionary<string, QuoteFileAnalysisStatus> _store
        = new(StringComparer.OrdinalIgnoreCase);

    public Task<QuoteFileAnalysisStatus?> GetStatusAsync(string storagePath, CancellationToken ct = default)
        => Task.FromResult(_store.TryGetValue(storagePath, out var s) ? s : null);

    public Task<bool> CanApplyGeometryEventAsync(string storagePath, string fileId,
        Guid eventId, DateTimeOffset occurredAtUtc, DateTimeOffset processedAtUtc,
        QuoteGeometryEventPhase phase, CancellationToken ct = default) =>
        Task.FromResult(!_store.TryGetValue(storagePath, out var existing) ||
            ShouldApplyGeometry(existing, fileId, eventId, occurredAtUtc, processedAtUtc, (int)phase));

    public Task<bool> CanApplyDfmEventAsync(string storagePath, string fileId,
        Guid eventId, CancellationToken ct = default) =>
        Task.FromResult(!_store.TryGetValue(storagePath, out var existing) ||
            (CanUseFileId(existing, fileId) &&
             (eventId == Guid.Empty || !existing.ProcessedDfmEventIds.Contains(eventId))));

    public Task SetProcessingAsync(string storagePath, CancellationToken ct = default)
    {
        _store.AddOrUpdate(
            storagePath,
            _ => new QuoteFileAnalysisStatus
            {
                StoragePath = storagePath,
                Status = "Processing",
                AnalysisSource = PendingAnalysisSource
            },
            (_, existing) => existing.IsAuthoritative
                ? existing
                : existing with
                {
                    Status = "Processing",
                    AnalysisSource = PendingAnalysisSource
                });
        return Task.CompletedTask;
    }

    public Task SetGlbReadyAsync(string storagePath, string glbUrl, string? thumbnailUrl,
        int bodyCount, bool isManifold, CancellationToken ct = default,
        string? viewerStoragePath = null, string? viewerFileExtension = null,
        decimal? volumeCc = null, decimal? surfaceAreaCm2 = null,
        string? fileId = null, decimal? supportVolumeCc = null,
        decimal? boundingBoxXmm = null, decimal? boundingBoxYmm = null,
        decimal? boundingBoxZmm = null, int? triangleCount = null,
        string? nonManifoldReason = null, Guid? eventId = null,
        DateTimeOffset? occurredAtUtc = null, DateTimeOffset? processedAtUtc = null)
    {
        var id = eventId ?? Guid.Empty;
        var occurred = occurredAtUtc ?? DateTimeOffset.MinValue;
        var processed = processedAtUtc ?? occurred;
        var hasCompleteMetrics = HasCompleteGeometry(
            volumeCc, boundingBoxXmm, boundingBoxYmm, boundingBoxZmm, triangleCount);
        _store.AddOrUpdate(
            storagePath,
            _ => new QuoteFileAnalysisStatus
            {
                StoragePath = storagePath,
                FileId = NullIfWhiteSpace(fileId),
                Status = "GlbReady",
                IsAuthoritative = true,
                AnalysisSource = ServerAnalysisSource,
                HasAuthoritativeGeometry = hasCompleteMetrics,
                GlbUrl = glbUrl,
                ViewerStoragePath = viewerStoragePath,
                ViewerFileExtension = viewerFileExtension,
                ThumbnailUrl = thumbnailUrl,
                VolumeCc = volumeCc,
                SupportVolumeCc = supportVolumeCc,
                SurfaceAreaCm2 = surfaceAreaCm2,
                BoundingBoxXmm = boundingBoxXmm,
                BoundingBoxYmm = boundingBoxYmm,
                BoundingBoxZmm = boundingBoxZmm,
                TriangleCount = triangleCount,
                BodyCount = hasCompleteMetrics ? bodyCount : 1,
                IsManifold = hasCompleteMetrics ? isManifold : true,
                NonManifoldReason = hasCompleteMetrics ? nonManifoldReason : null,
                AnalysisErrorCode = null,
                LastGeometryEventId = eventId,
                LastGeometryEventOccurredAtUtc = occurredAtUtc,
                LastGeometryProcessedAtUtc = processedAtUtc,
                LastGeometryEventPhase = eventId.HasValue ? CompletionPhase : 0
            },
            (_, existing) => !ShouldApplyGeometry(existing, fileId, id, occurred, processed, CompletionPhase)
                ? existing
                : existing with
                {
                    Status = existing.HasAuthoritativeDfm ? "DfmAnalysisReady" : "GlbReady",
                    FileId = NullIfWhiteSpace(fileId) ?? existing.FileId,
                    IsAuthoritative = true,
                    AnalysisSource = ServerAnalysisSource,
                    HasAuthoritativeGeometry = hasCompleteMetrics || existing.HasAuthoritativeGeometry,
                    GlbUrl = glbUrl,
                    ViewerStoragePath = viewerStoragePath ?? existing.ViewerStoragePath,
                    ViewerFileExtension = viewerFileExtension ?? existing.ViewerFileExtension,
                    ThumbnailUrl = thumbnailUrl,
                    VolumeCc = volumeCc ?? existing.VolumeCc,
                    SupportVolumeCc = supportVolumeCc ?? existing.SupportVolumeCc,
                    SurfaceAreaCm2 = surfaceAreaCm2 ?? existing.SurfaceAreaCm2,
                    BoundingBoxXmm = boundingBoxXmm ?? existing.BoundingBoxXmm,
                    BoundingBoxYmm = boundingBoxYmm ?? existing.BoundingBoxYmm,
                    BoundingBoxZmm = boundingBoxZmm ?? existing.BoundingBoxZmm,
                    TriangleCount = triangleCount ?? existing.TriangleCount,
                    BodyCount = hasCompleteMetrics ? bodyCount : existing.BodyCount,
                    IsManifold = hasCompleteMetrics ? isManifold : existing.IsManifold,
                    NonManifoldReason = hasCompleteMetrics
                    ? ResolveNonManifoldReason(isManifold, nonManifoldReason, existing.NonManifoldReason)
                    : existing.NonManifoldReason,
                    AnalysisErrorCode = null,
                    LastGeometryEventId = eventId ?? existing.LastGeometryEventId,
                    LastGeometryEventOccurredAtUtc = occurredAtUtc ?? existing.LastGeometryEventOccurredAtUtc,
                    LastGeometryProcessedAtUtc = processedAtUtc ?? existing.LastGeometryProcessedAtUtc,
                    LastGeometryEventPhase = eventId.HasValue ? CompletionPhase : existing.LastGeometryEventPhase
                });
        return Task.CompletedTask;
    }

    public Task SetGeometryMetricsAsync(string storagePath, string fileId,
        decimal? volumeCc, decimal? supportVolumeCc, decimal? surfaceAreaCm2,
        decimal? boundingBoxXmm, decimal? boundingBoxYmm, decimal? boundingBoxZmm,
        int? triangleCount, int bodyCount, bool isManifold, string? nonManifoldReason,
        Guid eventId, DateTimeOffset occurredAtUtc, DateTimeOffset processedAtUtc,
        CancellationToken ct = default)
    {
        var hasCompleteMetrics = HasCompleteGeometry(
            volumeCc, boundingBoxXmm, boundingBoxYmm, boundingBoxZmm, triangleCount);
        _store.AddOrUpdate(
            storagePath,
            _ => new QuoteFileAnalysisStatus
            {
                StoragePath = storagePath,
                FileId = fileId,
                Status = "Processing",
                IsAuthoritative = true,
                AnalysisSource = ServerAnalysisSource,
                HasAuthoritativeGeometry = hasCompleteMetrics,
                VolumeCc = volumeCc,
                SupportVolumeCc = supportVolumeCc,
                SurfaceAreaCm2 = surfaceAreaCm2,
                BoundingBoxXmm = boundingBoxXmm,
                BoundingBoxYmm = boundingBoxYmm,
                BoundingBoxZmm = boundingBoxZmm,
                TriangleCount = triangleCount,
                BodyCount = hasCompleteMetrics ? bodyCount : 1,
                IsManifold = hasCompleteMetrics ? isManifold : true,
                NonManifoldReason = hasCompleteMetrics ? nonManifoldReason : null,
                LastGeometryEventId = eventId,
                LastGeometryEventOccurredAtUtc = occurredAtUtc,
                LastGeometryProcessedAtUtc = processedAtUtc,
                LastGeometryEventPhase = MetricsPhase
            },
            (_, existing) =>
            {
                if (!hasCompleteMetrics || !CanUseFileId(existing, fileId))
                    return existing;

                var backfillsSparseTerminal = !existing.HasAuthoritativeGeometry &&
                    existing.LastGeometryEventPhase >= CompletionPhase &&
                    IsOlderThanGeometryClock(existing, occurredAtUtc, processedAtUtc);
                if (!backfillsSparseTerminal && !ShouldApplyGeometry(
                        existing, fileId, eventId, occurredAtUtc, processedAtUtc, MetricsPhase))
                {
                    return existing;
                }

                return existing with
                {
                    Status = backfillsSparseTerminal
                        ? existing.Status
                        : existing.HasAuthoritativeDfm
                            ? "DfmAnalysisReady"
                            : existing.GlbUrl is not null
                                ? "GlbReady"
                            : "Processing",
                    FileId = fileId,
                    IsAuthoritative = true,
                    AnalysisSource = ServerAnalysisSource,
                    HasAuthoritativeGeometry = true,
                    VolumeCc = volumeCc ?? existing.VolumeCc,
                    SupportVolumeCc = supportVolumeCc ?? existing.SupportVolumeCc,
                    SurfaceAreaCm2 = surfaceAreaCm2 ?? existing.SurfaceAreaCm2,
                    BoundingBoxXmm = boundingBoxXmm ?? existing.BoundingBoxXmm,
                    BoundingBoxYmm = boundingBoxYmm ?? existing.BoundingBoxYmm,
                    BoundingBoxZmm = boundingBoxZmm ?? existing.BoundingBoxZmm,
                    TriangleCount = triangleCount ?? existing.TriangleCount,
                    BodyCount = bodyCount,
                    IsManifold = isManifold,
                    NonManifoldReason = ResolveNonManifoldReason(isManifold, nonManifoldReason, existing.NonManifoldReason),
                    AnalysisErrorCode = backfillsSparseTerminal ? existing.AnalysisErrorCode : null,
                    LastGeometryEventId = backfillsSparseTerminal ? existing.LastGeometryEventId : eventId,
                    LastGeometryEventOccurredAtUtc = backfillsSparseTerminal
                        ? existing.LastGeometryEventOccurredAtUtc
                        : occurredAtUtc,
                    LastGeometryProcessedAtUtc = backfillsSparseTerminal
                        ? existing.LastGeometryProcessedAtUtc
                        : processedAtUtc,
                    LastGeometryEventPhase = backfillsSparseTerminal
                        ? existing.LastGeometryEventPhase
                        : MetricsPhase
                };
            });
        return Task.CompletedTask;
    }

    public Task SetDfmReportsAsync(string storagePath,
        QeFdmDfmReport? fdmReport, QeSlaDfmReport? slaReport, QeCncDfmReport? cncReport,
        IReadOnlyList<string> overlayGlbUrls, string? nonManifoldReason,
        string? analysisErrorCode, CancellationToken ct = default,
        string? fileId = null, Guid? eventId = null,
        DateTimeOffset? occurredAtUtc = null, DateTimeOffset? analyzedAtUtc = null,
        int? bodyCount = null)
    {
        _store.AddOrUpdate(
            storagePath,
            _ => new QuoteFileAnalysisStatus
            {
                StoragePath = storagePath,
                FileId = NullIfWhiteSpace(fileId),
                Status = "DfmAnalysisReady",
                IsAuthoritative = true,
                AnalysisSource = ServerAnalysisSource,
                HasAuthoritativeDfm = true,
                FdmReport = fdmReport,
                SlaReport = slaReport,
                CncReport = cncReport,
                ViewerStoragePath = storagePath,
                OverlayGlbUrls = overlayGlbUrls,
                VolumeCc = null,
                SurfaceAreaCm2 = null,
                NonManifoldReason = nonManifoldReason,
                AnalysisErrorCode = analysisErrorCode,
                BodyCount = bodyCount > 0 ? bodyCount.Value : 1,
                IsManifold = string.IsNullOrWhiteSpace(nonManifoldReason),
                LastDfmEventId = eventId,
                LastDfmEventOccurredAtUtc = analyzedAtUtc ?? occurredAtUtc,
                ProcessedDfmEventIds = eventId is { } insertedId && insertedId != Guid.Empty ? [insertedId] : []
            },
            (_, existing) => !CanUseFileId(existing, fileId) ||
                (eventId is { } replayId && replayId != Guid.Empty && existing.ProcessedDfmEventIds.Contains(replayId))
                ? existing
                : existing with
                {
                    Status = "DfmAnalysisReady",
                    FileId = NullIfWhiteSpace(fileId) ?? existing.FileId,
                    IsAuthoritative = true,
                    AnalysisSource = ServerAnalysisSource,
                    HasAuthoritativeDfm = true,
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
                    BodyCount = bodyCount > 0 ? bodyCount.Value : existing.BodyCount,
                    IsManifold = string.IsNullOrWhiteSpace(nonManifoldReason) && existing.IsManifold,
                    // Accumulate overlay URLs across multiple DfmAnalysisReady events
                    OverlayGlbUrls = overlayGlbUrls.Count > 0
                    ? existing.OverlayGlbUrls.Concat(overlayGlbUrls).Distinct(StringComparer.Ordinal).ToArray()
                    : existing.OverlayGlbUrls,
                    NonManifoldReason = nonManifoldReason ?? existing.NonManifoldReason,
                    AnalysisErrorCode = analysisErrorCode ?? existing.AnalysisErrorCode,
                    LastDfmEventId = eventId ?? existing.LastDfmEventId,
                    LastDfmEventOccurredAtUtc = analyzedAtUtc ?? occurredAtUtc ?? existing.LastDfmEventOccurredAtUtc,
                    ProcessedDfmEventIds = eventId is { } appendedId && appendedId != Guid.Empty
                    ? [.. existing.ProcessedDfmEventIds.TakeLast(31), appendedId]
                    : existing.ProcessedDfmEventIds
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
                AnalysisSource = PendingAnalysisSource,
                AdvisoryAnalysis = new QuoteFileAdvisoryAnalysis
                {
                    VolumeCc = volumeCc,
                    SurfaceAreaCm2 = surfaceAreaCm2,
                    IsManifold = isManifold,
                    NonManifoldReason = ResolveNonManifoldReason(isManifold, nonManifoldReason, null)
                }
            },
            (_, existing) => existing with
            {
                AdvisoryAnalysis = (existing.AdvisoryAnalysis ?? new QuoteFileAdvisoryAnalysis()) with
                {
                    VolumeCc = volumeCc ?? existing.AdvisoryAnalysis?.VolumeCc,
                    SurfaceAreaCm2 = surfaceAreaCm2 ?? existing.AdvisoryAnalysis?.SurfaceAreaCm2,
                    IsManifold = isManifold,
                    NonManifoldReason = ResolveNonManifoldReason(
                        isManifold,
                        nonManifoldReason,
                        existing.AdvisoryAnalysis?.NonManifoldReason)
                }
            });
        return Task.CompletedTask;
    }

    public Task SetLocalDfmReportsAsync(string storagePath,
        QeFdmDfmReport? fdmReport, QeSlaDfmReport? slaReport, QeCncDfmReport? cncReport,
        IReadOnlyList<string> overlayGlbUrls, string? nonManifoldReason,
        CancellationToken ct = default)
    {
        _store.AddOrUpdate(
            storagePath,
            _ => new QuoteFileAnalysisStatus
            {
                StoragePath = storagePath,
                Status = "Processing",
                AnalysisSource = PendingAnalysisSource,
                AdvisoryAnalysis = new QuoteFileAdvisoryAnalysis
                {
                    FdmReport = fdmReport,
                    SlaReport = slaReport,
                    CncReport = cncReport,
                    OverlayGlbUrls = overlayGlbUrls,
                    NonManifoldReason = nonManifoldReason
                }
            },
            (_, existing) => existing with
            {
                AdvisoryAnalysis = (existing.AdvisoryAnalysis ?? new QuoteFileAdvisoryAnalysis()) with
                {
                    FdmReport = fdmReport ?? existing.AdvisoryAnalysis?.FdmReport,
                    SlaReport = slaReport ?? existing.AdvisoryAnalysis?.SlaReport,
                    CncReport = cncReport ?? existing.AdvisoryAnalysis?.CncReport,
                    OverlayGlbUrls = overlayGlbUrls.Count > 0
                        ? [.. (existing.AdvisoryAnalysis?.OverlayGlbUrls ?? []), .. overlayGlbUrls]
                        : existing.AdvisoryAnalysis?.OverlayGlbUrls ?? [],
                    NonManifoldReason = nonManifoldReason ?? existing.AdvisoryAnalysis?.NonManifoldReason
                }
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
                IsAuthoritative = true,
                AnalysisSource = ServerAnalysisSource,
                AnalysisErrorCode = errorCode
            },
            (_, existing) => existing.LastGeometryEventId.HasValue
                ? existing
                : existing with
                {
                    Status = "Failed",
                    IsAuthoritative = true,
                    AnalysisSource = ServerAnalysisSource,
                    AnalysisErrorCode = errorCode
                });
        return Task.CompletedTask;
    }

    public Task SetFailedAsync(string storagePath, string fileId, string errorCode,
        Guid eventId, DateTimeOffset occurredAtUtc, CancellationToken ct = default)
    {
        var safeCode = string.IsNullOrWhiteSpace(errorCode) ? "analysis_failed" : errorCode.Trim();
        _store.AddOrUpdate(
            storagePath,
            _ => new QuoteFileAnalysisStatus
            {
                StoragePath = storagePath,
                FileId = fileId,
                Status = "Failed",
                IsAuthoritative = true,
                AnalysisSource = ServerAnalysisSource,
                AnalysisErrorCode = safeCode,
                LastGeometryEventId = eventId,
                LastGeometryEventOccurredAtUtc = occurredAtUtc,
                LastGeometryProcessedAtUtc = occurredAtUtc,
                LastGeometryEventPhase = FailurePhase
            },
            (_, existing) => !ShouldApplyGeometry(
                    existing, fileId, eventId, occurredAtUtc, occurredAtUtc, FailurePhase)
                ? existing
                : existing with
                {
                    FileId = fileId,
                    Status = "Failed",
                    IsAuthoritative = true,
                    AnalysisSource = ServerAnalysisSource,
                    AnalysisErrorCode = safeCode,
                    LastGeometryEventId = eventId,
                    LastGeometryEventOccurredAtUtc = occurredAtUtc,
                    LastGeometryProcessedAtUtc = occurredAtUtc,
                    LastGeometryEventPhase = FailurePhase
                });
        return Task.CompletedTask;
    }

    private static bool ShouldApplyGeometry(
        QuoteFileAnalysisStatus existing,
        string? fileId,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset processedAtUtc,
        int phase)
    {
        if (!CanUseFileId(existing, fileId) || (eventId != Guid.Empty && existing.LastGeometryEventId == eventId))
            return false;

        if (eventId == Guid.Empty)
            return existing.LastGeometryEventId is null;

        var candidateTime = processedAtUtc > occurredAtUtc ? processedAtUtc : occurredAtUtc;
        var currentTime = existing.LastGeometryProcessedAtUtc ?? existing.LastGeometryEventOccurredAtUtc;
        if (currentTime is null || candidateTime > currentTime.Value)
            return true;
        if (candidateTime < currentTime.Value)
            return false;
        if (phase != existing.LastGeometryEventPhase)
            return phase > existing.LastGeometryEventPhase;

        return existing.LastGeometryEventId is not { } currentId || eventId.CompareTo(currentId) > 0;
    }

    private static bool CanUseFileId(QuoteFileAnalysisStatus existing, string? fileId) =>
        string.IsNullOrWhiteSpace(fileId) ||
        string.IsNullOrWhiteSpace(existing.FileId) ||
        string.Equals(existing.FileId, fileId, StringComparison.Ordinal);

    private static bool IsOlderThanGeometryClock(
        QuoteFileAnalysisStatus existing,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset processedAtUtc)
    {
        var current = existing.LastGeometryProcessedAtUtc ?? existing.LastGeometryEventOccurredAtUtc;
        var candidate = processedAtUtc > occurredAtUtc ? processedAtUtc : occurredAtUtc;
        return current.HasValue && candidate < current.Value;
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool HasCompleteGeometry(
        decimal? volumeCc,
        decimal? boundingBoxXmm,
        decimal? boundingBoxYmm,
        decimal? boundingBoxZmm,
        int? triangleCount) =>
        volumeCc > 0 &&
        boundingBoxXmm > 0 &&
        boundingBoxYmm > 0 &&
        boundingBoxZmm > 0 &&
        triangleCount > 0;

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
