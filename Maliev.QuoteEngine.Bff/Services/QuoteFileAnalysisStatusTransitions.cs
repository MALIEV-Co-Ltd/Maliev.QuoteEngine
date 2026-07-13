using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Bff.Services;

internal sealed record GeometryCompletionTransition(
    string StoragePath,
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
    Guid? EventId,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset? ProcessedAtUtc,
    string? ThumbnailStoragePath = null);

internal sealed record GeometryMetricsTransition(
    string StoragePath,
    string FileId,
    decimal? VolumeCc,
    decimal? SupportVolumeCc,
    decimal? SurfaceAreaCm2,
    decimal? BoundingBoxXmm,
    decimal? BoundingBoxYmm,
    decimal? BoundingBoxZmm,
    int? TriangleCount,
    int BodyCount,
    bool IsManifold,
    string? NonManifoldReason,
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset ProcessedAtUtc);

internal sealed record DfmReportsTransition(
    string StoragePath,
    QeFdmDfmReport? FdmReport,
    QeSlaDfmReport? SlaReport,
    QeCncDfmReport? CncReport,
    IReadOnlyList<string> OverlayGlbUrls,
    string? NonManifoldReason,
    string? AnalysisErrorCode,
    string? FileId,
    Guid? EventId,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset? AnalyzedAtUtc,
    int? BodyCount,
    IReadOnlyList<string>? OverlayStoragePaths = null);

internal sealed record LocalGeometryTransition(
    string StoragePath,
    decimal? VolumeCc,
    decimal? SurfaceAreaCm2,
    bool IsManifold,
    string? NonManifoldReason);

internal sealed record LocalDfmReportsTransition(
    string StoragePath,
    QeFdmDfmReport? FdmReport,
    QeSlaDfmReport? SlaReport,
    QeCncDfmReport? CncReport,
    IReadOnlyList<string> OverlayGlbUrls,
    string? NonManifoldReason);

internal sealed record GeometryFailureTransition(
    string StoragePath,
    string FileId,
    string ErrorCode,
    Guid EventId,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Pure state transitions for authoritative and advisory geometry analysis updates.
/// Storage adapters are responsible only for atomically applying the returned snapshots.
/// </summary>
internal static class QuoteFileAnalysisStatusTransitions
{
    private const int MetricsPhase = (int)QuoteGeometryEventPhase.Metrics;
    private const int CompletionPhase = (int)QuoteGeometryEventPhase.Completion;
    private const int FailurePhase = (int)QuoteGeometryEventPhase.Failure;
    private const string PendingAnalysisSource = "pending";
    private const string ServerAnalysisSource = "server_analysis";

    internal static bool CanApplyGeometry(
        QuoteFileAnalysisStatus existing,
        string? fileId,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset processedAtUtc,
        QuoteGeometryEventPhase phase) =>
        ShouldApplyGeometry(existing, fileId, eventId, occurredAtUtc, processedAtUtc, (int)phase);

    internal static bool CanApplyDfm(
        QuoteFileAnalysisStatus existing,
        string? fileId,
        Guid eventId) =>
        CanUseFileId(existing, fileId) &&
        (eventId == Guid.Empty || !existing.ProcessedDfmEventIds.Contains(eventId));

    internal static QuoteFileAnalysisStatus ApplyProcessing(
        QuoteFileAnalysisStatus? existing,
        string storagePath)
    {
        if (existing is null)
        {
            return new QuoteFileAnalysisStatus
            {
                StoragePath = storagePath,
                Status = "Processing",
                AnalysisSource = PendingAnalysisSource
            };
        }

        return existing.IsAuthoritative
            ? existing
            : existing with
            {
                Status = "Processing",
                AnalysisSource = PendingAnalysisSource
            };
    }

    internal static QuoteFileAnalysisStatus ApplyGlbReady(
        QuoteFileAnalysisStatus? existing,
        GeometryCompletionTransition transition)
    {
        QuoteAnalysisArtifactPathValidator.RequireCanonicalGeometryPaths(
            transition.StoragePath,
            transition.ViewerStoragePath,
            transition.ThumbnailStoragePath);
        var id = transition.EventId ?? Guid.Empty;
        var occurred = transition.OccurredAtUtc ?? DateTimeOffset.MinValue;
        var processed = transition.ProcessedAtUtc ?? occurred;
        var hasCompleteMetrics = HasCompleteGeometry(
            transition.VolumeCc,
            transition.BoundingBoxXmm,
            transition.BoundingBoxYmm,
            transition.BoundingBoxZmm,
            transition.TriangleCount);

        if (existing is null)
        {
            return new QuoteFileAnalysisStatus
            {
                StoragePath = transition.StoragePath,
                FileId = NullIfWhiteSpace(transition.FileId),
                Status = "GlbReady",
                IsAuthoritative = true,
                AnalysisSource = ServerAnalysisSource,
                HasAuthoritativeGeometry = hasCompleteMetrics,
                GlbUrl = transition.GlbUrl,
                ViewerStoragePath = transition.ViewerStoragePath,
                ViewerFileExtension = transition.ViewerFileExtension,
                ThumbnailUrl = transition.ThumbnailUrl,
                ThumbnailStoragePath = transition.ThumbnailStoragePath,
                VolumeCc = transition.VolumeCc,
                SupportVolumeCc = transition.SupportVolumeCc,
                SurfaceAreaCm2 = transition.SurfaceAreaCm2,
                BoundingBoxXmm = transition.BoundingBoxXmm,
                BoundingBoxYmm = transition.BoundingBoxYmm,
                BoundingBoxZmm = transition.BoundingBoxZmm,
                TriangleCount = transition.TriangleCount,
                BodyCount = hasCompleteMetrics ? transition.BodyCount : 1,
                IsManifold = hasCompleteMetrics ? transition.IsManifold : true,
                NonManifoldReason = hasCompleteMetrics ? transition.NonManifoldReason : null,
                AnalysisErrorCode = null,
                LastGeometryEventId = transition.EventId,
                LastGeometryEventOccurredAtUtc = transition.OccurredAtUtc,
                LastGeometryProcessedAtUtc = transition.ProcessedAtUtc,
                LastGeometryEventPhase = transition.EventId.HasValue ? CompletionPhase : 0
            };
        }

        if (!ShouldApplyGeometry(existing, transition.FileId, id, occurred, processed, CompletionPhase))
            return existing;

        return existing with
        {
            Status = existing.HasAuthoritativeDfm ? "DfmAnalysisReady" : "GlbReady",
            FileId = NullIfWhiteSpace(transition.FileId) ?? existing.FileId,
            IsAuthoritative = true,
            AnalysisSource = ServerAnalysisSource,
            HasAuthoritativeGeometry = hasCompleteMetrics || existing.HasAuthoritativeGeometry,
            GlbUrl = transition.GlbUrl,
            ViewerStoragePath = transition.ViewerStoragePath ?? existing.ViewerStoragePath,
            ViewerFileExtension = transition.ViewerFileExtension ?? existing.ViewerFileExtension,
            ThumbnailUrl = transition.ThumbnailUrl,
            ThumbnailStoragePath = transition.ThumbnailStoragePath
                ?? existing.ThumbnailStoragePath,
            VolumeCc = transition.VolumeCc ?? existing.VolumeCc,
            SupportVolumeCc = transition.SupportVolumeCc ?? existing.SupportVolumeCc,
            SurfaceAreaCm2 = transition.SurfaceAreaCm2 ?? existing.SurfaceAreaCm2,
            BoundingBoxXmm = transition.BoundingBoxXmm ?? existing.BoundingBoxXmm,
            BoundingBoxYmm = transition.BoundingBoxYmm ?? existing.BoundingBoxYmm,
            BoundingBoxZmm = transition.BoundingBoxZmm ?? existing.BoundingBoxZmm,
            TriangleCount = transition.TriangleCount ?? existing.TriangleCount,
            BodyCount = hasCompleteMetrics ? transition.BodyCount : existing.BodyCount,
            IsManifold = hasCompleteMetrics ? transition.IsManifold : existing.IsManifold,
            NonManifoldReason = hasCompleteMetrics
                ? ResolveNonManifoldReason(
                    transition.IsManifold,
                    transition.NonManifoldReason,
                    existing.NonManifoldReason)
                : existing.NonManifoldReason,
            AnalysisErrorCode = null,
            LastGeometryEventId = transition.EventId ?? existing.LastGeometryEventId,
            LastGeometryEventOccurredAtUtc = transition.OccurredAtUtc ?? existing.LastGeometryEventOccurredAtUtc,
            LastGeometryProcessedAtUtc = transition.ProcessedAtUtc ?? existing.LastGeometryProcessedAtUtc,
            LastGeometryEventPhase = transition.EventId.HasValue
                ? CompletionPhase
                : existing.LastGeometryEventPhase
        };
    }

    internal static QuoteFileAnalysisStatus ApplyGeometryMetrics(
        QuoteFileAnalysisStatus? existing,
        GeometryMetricsTransition transition)
    {
        var hasCompleteMetrics = HasCompleteGeometry(
            transition.VolumeCc,
            transition.BoundingBoxXmm,
            transition.BoundingBoxYmm,
            transition.BoundingBoxZmm,
            transition.TriangleCount);

        if (existing is null)
        {
            return new QuoteFileAnalysisStatus
            {
                StoragePath = transition.StoragePath,
                FileId = transition.FileId,
                Status = "Processing",
                IsAuthoritative = true,
                AnalysisSource = ServerAnalysisSource,
                HasAuthoritativeGeometry = hasCompleteMetrics,
                VolumeCc = transition.VolumeCc,
                SupportVolumeCc = transition.SupportVolumeCc,
                SurfaceAreaCm2 = transition.SurfaceAreaCm2,
                BoundingBoxXmm = transition.BoundingBoxXmm,
                BoundingBoxYmm = transition.BoundingBoxYmm,
                BoundingBoxZmm = transition.BoundingBoxZmm,
                TriangleCount = transition.TriangleCount,
                BodyCount = hasCompleteMetrics ? transition.BodyCount : 1,
                IsManifold = hasCompleteMetrics ? transition.IsManifold : true,
                NonManifoldReason = hasCompleteMetrics ? transition.NonManifoldReason : null,
                LastGeometryEventId = transition.EventId,
                LastGeometryEventOccurredAtUtc = transition.OccurredAtUtc,
                LastGeometryProcessedAtUtc = transition.ProcessedAtUtc,
                LastGeometryEventPhase = MetricsPhase
            };
        }

        if (!hasCompleteMetrics || !CanUseFileId(existing, transition.FileId))
            return existing;

        var backfillsSparseTerminal = !existing.HasAuthoritativeGeometry &&
            existing.LastGeometryEventPhase >= CompletionPhase &&
            IsOlderThanGeometryClock(existing, transition.OccurredAtUtc, transition.ProcessedAtUtc);
        if (!backfillsSparseTerminal && !ShouldApplyGeometry(
                existing,
                transition.FileId,
                transition.EventId,
                transition.OccurredAtUtc,
                transition.ProcessedAtUtc,
                MetricsPhase))
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
            FileId = transition.FileId,
            IsAuthoritative = true,
            AnalysisSource = ServerAnalysisSource,
            HasAuthoritativeGeometry = true,
            VolumeCc = transition.VolumeCc ?? existing.VolumeCc,
            SupportVolumeCc = transition.SupportVolumeCc ?? existing.SupportVolumeCc,
            SurfaceAreaCm2 = transition.SurfaceAreaCm2 ?? existing.SurfaceAreaCm2,
            BoundingBoxXmm = transition.BoundingBoxXmm ?? existing.BoundingBoxXmm,
            BoundingBoxYmm = transition.BoundingBoxYmm ?? existing.BoundingBoxYmm,
            BoundingBoxZmm = transition.BoundingBoxZmm ?? existing.BoundingBoxZmm,
            TriangleCount = transition.TriangleCount ?? existing.TriangleCount,
            BodyCount = transition.BodyCount,
            IsManifold = transition.IsManifold,
            NonManifoldReason = ResolveNonManifoldReason(
                transition.IsManifold,
                transition.NonManifoldReason,
                existing.NonManifoldReason),
            AnalysisErrorCode = backfillsSparseTerminal ? existing.AnalysisErrorCode : null,
            LastGeometryEventId = backfillsSparseTerminal ? existing.LastGeometryEventId : transition.EventId,
            LastGeometryEventOccurredAtUtc = backfillsSparseTerminal
                ? existing.LastGeometryEventOccurredAtUtc
                : transition.OccurredAtUtc,
            LastGeometryProcessedAtUtc = backfillsSparseTerminal
                ? existing.LastGeometryProcessedAtUtc
                : transition.ProcessedAtUtc,
            LastGeometryEventPhase = backfillsSparseTerminal
                ? existing.LastGeometryEventPhase
                : MetricsPhase
        };
    }

    internal static QuoteFileAnalysisStatus ApplyDfmReports(
        QuoteFileAnalysisStatus? existing,
        DfmReportsTransition transition)
    {
        if (existing is null)
        {
            return new QuoteFileAnalysisStatus
            {
                StoragePath = transition.StoragePath,
                FileId = NullIfWhiteSpace(transition.FileId),
                Status = "DfmAnalysisReady",
                IsAuthoritative = true,
                AnalysisSource = ServerAnalysisSource,
                HasAuthoritativeDfm = true,
                FdmReport = transition.FdmReport,
                SlaReport = transition.SlaReport,
                CncReport = transition.CncReport,
                OverlayGlbUrls = [.. transition.OverlayGlbUrls],
                AuthoritativeOverlayStoragePaths =
                    QuoteAnalysisArtifactPathValidator.RequireCanonicalOverlayPaths(
                        transition.StoragePath,
                        transition.OverlayStoragePaths),
                VolumeCc = null,
                SurfaceAreaCm2 = null,
                NonManifoldReason = transition.NonManifoldReason,
                AnalysisErrorCode = transition.AnalysisErrorCode,
                BodyCount = transition.BodyCount > 0 ? transition.BodyCount.Value : 1,
                IsManifold = string.IsNullOrWhiteSpace(transition.NonManifoldReason),
                LastDfmEventId = transition.EventId,
                LastDfmEventOccurredAtUtc = transition.AnalyzedAtUtc ?? transition.OccurredAtUtc,
                ProcessedDfmEventIds = transition.EventId is { } insertedId && insertedId != Guid.Empty
                    ? [insertedId]
                    : []
            };
        }

        if (!CanUseFileId(existing, transition.FileId) ||
            (transition.EventId is { } replayId &&
             replayId != Guid.Empty &&
             existing.ProcessedDfmEventIds.Contains(replayId)))
        {
            return existing;
        }

        return existing with
        {
            Status = "DfmAnalysisReady",
            FileId = NullIfWhiteSpace(transition.FileId) ?? existing.FileId,
            IsAuthoritative = true,
            AnalysisSource = ServerAnalysisSource,
            HasAuthoritativeDfm = true,
            FdmReport = transition.FdmReport ?? existing.FdmReport,
            SlaReport = transition.SlaReport ?? existing.SlaReport,
            CncReport = transition.CncReport ?? existing.CncReport,
            GlbUrl = existing.GlbUrl,
            ViewerStoragePath = existing.ViewerStoragePath,
            ViewerFileExtension = existing.ViewerFileExtension,
            ThumbnailUrl = existing.ThumbnailUrl,
            VolumeCc = existing.VolumeCc,
            SurfaceAreaCm2 = existing.SurfaceAreaCm2,
            BodyCount = transition.BodyCount > 0 ? transition.BodyCount.Value : existing.BodyCount,
            IsManifold = string.IsNullOrWhiteSpace(transition.NonManifoldReason) && existing.IsManifold,
            OverlayGlbUrls = transition.OverlayGlbUrls.Count > 0
                ? existing.OverlayGlbUrls
                    .Concat(transition.OverlayGlbUrls)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : existing.OverlayGlbUrls,
            AuthoritativeOverlayStoragePaths = MergeStoragePaths(
                transition.StoragePath,
                existing.AuthoritativeOverlayStoragePaths,
                transition.OverlayStoragePaths),
            NonManifoldReason = transition.NonManifoldReason ?? existing.NonManifoldReason,
            AnalysisErrorCode = transition.AnalysisErrorCode ?? existing.AnalysisErrorCode,
            LastDfmEventId = transition.EventId ?? existing.LastDfmEventId,
            LastDfmEventOccurredAtUtc = transition.AnalyzedAtUtc ??
                transition.OccurredAtUtc ??
                existing.LastDfmEventOccurredAtUtc,
            ProcessedDfmEventIds = transition.EventId is { } appendedId && appendedId != Guid.Empty
                ? [.. existing.ProcessedDfmEventIds.TakeLast(31), appendedId]
                : existing.ProcessedDfmEventIds
        };
    }

    internal static QuoteFileAnalysisStatus ApplyLocalGeometryMetrics(
        QuoteFileAnalysisStatus? existing,
        LocalGeometryTransition transition)
    {
        if (existing is null)
        {
            return new QuoteFileAnalysisStatus
            {
                StoragePath = transition.StoragePath,
                Status = "Processing",
                AnalysisSource = PendingAnalysisSource,
                AdvisoryAnalysis = new QuoteFileAdvisoryAnalysis
                {
                    VolumeCc = transition.VolumeCc,
                    SurfaceAreaCm2 = transition.SurfaceAreaCm2,
                    IsManifold = transition.IsManifold,
                    NonManifoldReason = ResolveNonManifoldReason(
                        transition.IsManifold,
                        transition.NonManifoldReason,
                        null)
                }
            };
        }

        return existing with
        {
            AdvisoryAnalysis = (existing.AdvisoryAnalysis ?? new QuoteFileAdvisoryAnalysis()) with
            {
                VolumeCc = transition.VolumeCc ?? existing.AdvisoryAnalysis?.VolumeCc,
                SurfaceAreaCm2 = transition.SurfaceAreaCm2 ?? existing.AdvisoryAnalysis?.SurfaceAreaCm2,
                IsManifold = transition.IsManifold,
                NonManifoldReason = ResolveNonManifoldReason(
                    transition.IsManifold,
                    transition.NonManifoldReason,
                    existing.AdvisoryAnalysis?.NonManifoldReason)
            }
        };
    }

    internal static QuoteFileAnalysisStatus ApplyLocalDfmReports(
        QuoteFileAnalysisStatus? existing,
        LocalDfmReportsTransition transition)
    {
        if (existing is null)
        {
            return new QuoteFileAnalysisStatus
            {
                StoragePath = transition.StoragePath,
                Status = "Processing",
                AnalysisSource = PendingAnalysisSource,
                AdvisoryAnalysis = new QuoteFileAdvisoryAnalysis
                {
                    FdmReport = transition.FdmReport,
                    SlaReport = transition.SlaReport,
                    CncReport = transition.CncReport,
                    OverlayGlbUrls = [.. transition.OverlayGlbUrls],
                    NonManifoldReason = transition.NonManifoldReason
                }
            };
        }

        return existing with
        {
            AdvisoryAnalysis = (existing.AdvisoryAnalysis ?? new QuoteFileAdvisoryAnalysis()) with
            {
                FdmReport = transition.FdmReport ?? existing.AdvisoryAnalysis?.FdmReport,
                SlaReport = transition.SlaReport ?? existing.AdvisoryAnalysis?.SlaReport,
                CncReport = transition.CncReport ?? existing.AdvisoryAnalysis?.CncReport,
                OverlayGlbUrls = transition.OverlayGlbUrls.Count > 0
                    ? [.. (existing.AdvisoryAnalysis?.OverlayGlbUrls ?? []), .. transition.OverlayGlbUrls]
                    : existing.AdvisoryAnalysis?.OverlayGlbUrls ?? [],
                NonManifoldReason = transition.NonManifoldReason ?? existing.AdvisoryAnalysis?.NonManifoldReason
            }
        };
    }

    internal static QuoteFileAnalysisStatus ApplyLegacyFailure(
        QuoteFileAnalysisStatus? existing,
        string storagePath,
        string errorCode)
    {
        if (existing is null)
        {
            return new QuoteFileAnalysisStatus
            {
                StoragePath = storagePath,
                Status = "Failed",
                IsAuthoritative = true,
                AnalysisSource = ServerAnalysisSource,
                AnalysisErrorCode = errorCode
            };
        }

        return existing.LastGeometryEventId.HasValue
            ? existing
            : existing with
            {
                Status = "Failed",
                IsAuthoritative = true,
                AnalysisSource = ServerAnalysisSource,
                AnalysisErrorCode = errorCode
            };
    }

    internal static QuoteFileAnalysisStatus ApplyOrderedFailure(
        QuoteFileAnalysisStatus? existing,
        GeometryFailureTransition transition)
    {
        var safeCode = string.IsNullOrWhiteSpace(transition.ErrorCode)
            ? "analysis_failed"
            : transition.ErrorCode.Trim();
        if (existing is null)
        {
            return new QuoteFileAnalysisStatus
            {
                StoragePath = transition.StoragePath,
                FileId = transition.FileId,
                Status = "Failed",
                IsAuthoritative = true,
                AnalysisSource = ServerAnalysisSource,
                AnalysisErrorCode = safeCode,
                LastGeometryEventId = transition.EventId,
                LastGeometryEventOccurredAtUtc = transition.OccurredAtUtc,
                LastGeometryProcessedAtUtc = transition.OccurredAtUtc,
                LastGeometryEventPhase = FailurePhase
            };
        }

        if (!ShouldApplyGeometry(
                existing,
                transition.FileId,
                transition.EventId,
                transition.OccurredAtUtc,
                transition.OccurredAtUtc,
                FailurePhase))
        {
            return existing;
        }

        return existing with
        {
            FileId = transition.FileId,
            Status = "Failed",
            IsAuthoritative = true,
            AnalysisSource = ServerAnalysisSource,
            AnalysisErrorCode = safeCode,
            LastGeometryEventId = transition.EventId,
            LastGeometryEventOccurredAtUtc = transition.OccurredAtUtc,
            LastGeometryProcessedAtUtc = transition.OccurredAtUtc,
            LastGeometryEventPhase = FailurePhase
        };
    }

    private static bool ShouldApplyGeometry(
        QuoteFileAnalysisStatus existing,
        string? fileId,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset processedAtUtc,
        int phase)
    {
        if (!CanUseFileId(existing, fileId) ||
            (eventId != Guid.Empty && existing.LastGeometryEventId == eventId))
        {
            return false;
        }

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

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static IReadOnlyList<string> MergeStoragePaths(
        string sourcePath,
        IReadOnlyList<string> existing,
        IEnumerable<string>? incoming)
    {
        var exact = QuoteAnalysisArtifactPathValidator.RequireCanonicalOverlayPaths(
            sourcePath,
            incoming);
        return exact.Count == 0
            ? existing
            : existing.Concat(exact).Distinct(StringComparer.Ordinal).ToArray();
    }

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
        string? existingNonManifoldReason) =>
        isManifold
            ? null
            : nonManifoldReason ?? existingNonManifoldReason;
}
