using System.Collections.Concurrent;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>
/// In-memory implementation of <see cref="IQuoteFileAnalysisStatusService"/>.
/// Keyed by storage path (case-insensitive). State transitions are storage-independent.
/// </summary>
public sealed class QuoteFileAnalysisStatusService : IQuoteFileAnalysisStatusService
{
    private readonly ConcurrentDictionary<string, QuoteFileAnalysisStatus> _store =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<QuoteFileAnalysisStatus?> GetStatusAsync(
        string storagePath,
        CancellationToken ct = default) =>
        Task.FromResult(_store.TryGetValue(storagePath, out var status) ? status : null);

    public Task<bool> CanApplyGeometryEventAsync(
        string storagePath,
        string fileId,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset processedAtUtc,
        QuoteGeometryEventPhase phase,
        CancellationToken ct = default) =>
        Task.FromResult(
            !_store.TryGetValue(storagePath, out var existing) ||
            QuoteFileAnalysisStatusTransitions.CanApplyGeometry(
                existing,
                fileId,
                eventId,
                occurredAtUtc,
                processedAtUtc,
                phase));

    public Task<bool> CanApplyDfmEventAsync(
        string storagePath,
        string fileId,
        Guid eventId,
        CancellationToken ct = default) =>
        Task.FromResult(
            !_store.TryGetValue(storagePath, out var existing) ||
            QuoteFileAnalysisStatusTransitions.CanApplyDfm(existing, fileId, eventId));

    public Task SetProcessingAsync(string storagePath, CancellationToken ct = default)
    {
        AddOrUpdate(
            storagePath,
            existing => QuoteFileAnalysisStatusTransitions.ApplyProcessing(existing, storagePath));
        return Task.CompletedTask;
    }

    public Task SetGlbReadyAsync(
        string storagePath,
        string glbUrl,
        string? thumbnailUrl,
        int bodyCount,
        bool isManifold,
        CancellationToken ct = default,
        string? viewerStoragePath = null,
        string? viewerFileExtension = null,
        decimal? volumeCc = null,
        decimal? surfaceAreaCm2 = null,
        string? fileId = null,
        decimal? supportVolumeCc = null,
        decimal? boundingBoxXmm = null,
        decimal? boundingBoxYmm = null,
        decimal? boundingBoxZmm = null,
        int? triangleCount = null,
        string? nonManifoldReason = null,
        Guid? eventId = null,
        DateTimeOffset? occurredAtUtc = null,
        DateTimeOffset? processedAtUtc = null)
    {
        var transition = new GeometryCompletionTransition(
            storagePath,
            glbUrl,
            thumbnailUrl,
            bodyCount,
            isManifold,
            viewerStoragePath,
            viewerFileExtension,
            volumeCc,
            surfaceAreaCm2,
            fileId,
            supportVolumeCc,
            boundingBoxXmm,
            boundingBoxYmm,
            boundingBoxZmm,
            triangleCount,
            nonManifoldReason,
            eventId,
            occurredAtUtc,
            processedAtUtc);
        AddOrUpdate(
            storagePath,
            existing => QuoteFileAnalysisStatusTransitions.ApplyGlbReady(existing, transition));
        return Task.CompletedTask;
    }

    public Task SetGeometryMetricsAsync(
        string storagePath,
        string fileId,
        decimal? volumeCc,
        decimal? supportVolumeCc,
        decimal? surfaceAreaCm2,
        decimal? boundingBoxXmm,
        decimal? boundingBoxYmm,
        decimal? boundingBoxZmm,
        int? triangleCount,
        int bodyCount,
        bool isManifold,
        string? nonManifoldReason,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset processedAtUtc,
        CancellationToken ct = default)
    {
        var transition = new GeometryMetricsTransition(
            storagePath,
            fileId,
            volumeCc,
            supportVolumeCc,
            surfaceAreaCm2,
            boundingBoxXmm,
            boundingBoxYmm,
            boundingBoxZmm,
            triangleCount,
            bodyCount,
            isManifold,
            nonManifoldReason,
            eventId,
            occurredAtUtc,
            processedAtUtc);
        AddOrUpdate(
            storagePath,
            existing => QuoteFileAnalysisStatusTransitions.ApplyGeometryMetrics(existing, transition));
        return Task.CompletedTask;
    }

    public Task SetDfmReportsAsync(
        string storagePath,
        QeFdmDfmReport? fdmReport,
        QeSlaDfmReport? slaReport,
        QeCncDfmReport? cncReport,
        IReadOnlyList<string> overlayGlbUrls,
        string? nonManifoldReason,
        string? analysisErrorCode,
        CancellationToken ct = default,
        string? fileId = null,
        Guid? eventId = null,
        DateTimeOffset? occurredAtUtc = null,
        DateTimeOffset? analyzedAtUtc = null,
        int? bodyCount = null)
    {
        var transition = new DfmReportsTransition(
            storagePath,
            fdmReport,
            slaReport,
            cncReport,
            overlayGlbUrls,
            nonManifoldReason,
            analysisErrorCode,
            fileId,
            eventId,
            occurredAtUtc,
            analyzedAtUtc,
            bodyCount);
        AddOrUpdate(
            storagePath,
            existing => QuoteFileAnalysisStatusTransitions.ApplyDfmReports(existing, transition));
        return Task.CompletedTask;
    }

    public Task SetLocalGeometryMetricsAsync(
        string storagePath,
        decimal? volumeCc,
        decimal? surfaceAreaCm2,
        bool isManifold,
        string? nonManifoldReason,
        CancellationToken ct = default)
    {
        var transition = new LocalGeometryTransition(
            storagePath,
            volumeCc,
            surfaceAreaCm2,
            isManifold,
            nonManifoldReason);
        AddOrUpdate(
            storagePath,
            existing => QuoteFileAnalysisStatusTransitions.ApplyLocalGeometryMetrics(existing, transition));
        return Task.CompletedTask;
    }

    public Task SetLocalDfmReportsAsync(
        string storagePath,
        QeFdmDfmReport? fdmReport,
        QeSlaDfmReport? slaReport,
        QeCncDfmReport? cncReport,
        IReadOnlyList<string> overlayGlbUrls,
        string? nonManifoldReason,
        CancellationToken ct = default)
    {
        var transition = new LocalDfmReportsTransition(
            storagePath,
            fdmReport,
            slaReport,
            cncReport,
            overlayGlbUrls,
            nonManifoldReason);
        AddOrUpdate(
            storagePath,
            existing => QuoteFileAnalysisStatusTransitions.ApplyLocalDfmReports(existing, transition));
        return Task.CompletedTask;
    }

    public Task SetFailedAsync(
        string storagePath,
        string errorCode,
        CancellationToken ct = default)
    {
        AddOrUpdate(
            storagePath,
            existing => QuoteFileAnalysisStatusTransitions.ApplyLegacyFailure(
                existing,
                storagePath,
                errorCode));
        return Task.CompletedTask;
    }

    public Task SetFailedAsync(
        string storagePath,
        string fileId,
        string errorCode,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        CancellationToken ct = default)
    {
        var transition = new GeometryFailureTransition(
            storagePath,
            fileId,
            errorCode,
            eventId,
            occurredAtUtc);
        AddOrUpdate(
            storagePath,
            existing => QuoteFileAnalysisStatusTransitions.ApplyOrderedFailure(existing, transition));
        return Task.CompletedTask;
    }

    private void AddOrUpdate(
        string storagePath,
        Func<QuoteFileAnalysisStatus?, QuoteFileAnalysisStatus> transition)
    {
        _store.AddOrUpdate(
            storagePath,
            _ => transition(null),
            (_, existing) => transition(existing));
    }
}
