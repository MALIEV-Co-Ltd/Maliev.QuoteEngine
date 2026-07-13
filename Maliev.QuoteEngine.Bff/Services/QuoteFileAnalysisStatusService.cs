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
    private readonly Dictionary<string, InMemoryClaim> _claims = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InMemoryReceipt> _receipts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _claimGate = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _claimLifetime;

    public QuoteFileAnalysisStatusService()
        : this(TimeProvider.System, TimeSpan.FromMinutes(2))
    {
    }

    internal QuoteFileAnalysisStatusService(TimeProvider timeProvider, TimeSpan claimLifetime)
    {
        _timeProvider = timeProvider;
        _claimLifetime = claimLifetime > TimeSpan.Zero
            ? claimLifetime
            : throw new ArgumentOutOfRangeException(nameof(claimLifetime));
    }

    public Task<QuoteFileAnalysisStatus?> GetStatusAsync(
        string storagePath,
        CancellationToken ct = default) =>
        Task.FromResult(_store.TryGetValue(storagePath, out var status) ? status : null);

    public Task<QuoteFileAnalysisStatus?> RefreshPreviewUrlsAsync(
        string storagePath,
        QuoteAnalysisPreviewRefresh refresh,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        while (_store.TryGetValue(storagePath, out var existing))
        {
            if (!CanRefreshPreview(existing, refresh))
                return Task.FromResult<QuoteFileAnalysisStatus?>(existing);

            var next = existing with
            {
                GlbUrl = refresh.ViewerUrl,
                ThumbnailUrl = refresh.ThumbnailUrl,
                OverlayGlbUrls = [.. refresh.OverlayUrls]
            };
            if (_store.TryUpdate(storagePath, next, existing))
                return Task.FromResult<QuoteFileAnalysisStatus?>(next);
        }

        return Task.FromResult<QuoteFileAnalysisStatus?>(null);
    }

    public Task<QuoteAnalysisEventClaim> ClaimGeometryCompletionAsync(
        string storagePath,
        string fileId,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset processedAtUtc,
        CancellationToken ct = default) =>
        ClaimAsync(
            storagePath,
            fileId,
            eventId,
            QuoteAnalysisEventLane.Geometry,
            existing => existing is null || QuoteFileAnalysisStatusTransitions.CanApplyGeometry(
                existing,
                fileId,
                eventId,
                occurredAtUtc,
                processedAtUtc,
                QuoteGeometryEventPhase.Completion),
            ct);

    public Task<QuoteAnalysisEventClaim> ClaimDfmEventAsync(
        string storagePath,
        string fileId,
        Guid eventId,
        CancellationToken ct = default) =>
        ClaimAsync(
            storagePath,
            fileId,
            eventId,
            QuoteAnalysisEventLane.Dfm,
            existing => existing is null ||
                QuoteFileAnalysisStatusTransitions.CanApplyDfm(existing, fileId, eventId),
            ct);

    public Task<bool> RenewAnalysisClaimAsync(
        QuoteAnalysisEventClaim claim,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_claimGate)
        {
            var key = ClaimKey(claim.StoragePath, claim.Lane);
            if (claim.Token is null || !_claims.TryGetValue(key, out var held) || held.Token != claim.Token)
                return Task.FromResult(false);

            if (held.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                _claims.Remove(key);
                return Task.FromResult(false);
            }

            _claims[key] = held with { ExpiresAtUtc = _timeProvider.GetUtcNow() + _claimLifetime };
            return Task.FromResult(true);
        }
    }

    public Task<QuoteAnalysisFinalizeResult> FinalizeGeometryCompletionAsync(
        QuoteAnalysisEventClaim claim,
        QuoteGeometryCompletionUpdate update,
        CancellationToken ct = default)
    {
        QuoteAnalysisClaimValidator.ValidateGeometry(claim, update);
        var transition = new GeometryCompletionTransition(
            claim.StoragePath,
            update.GlbUrl,
            update.ThumbnailUrl,
            update.BodyCount,
            update.IsManifold,
            update.ViewerStoragePath,
            update.ViewerFileExtension,
            update.VolumeCc,
            update.SurfaceAreaCm2,
            update.FileId,
            update.SupportVolumeCc,
            update.BoundingBoxXmm,
            update.BoundingBoxYmm,
            update.BoundingBoxZmm,
            update.TriangleCount,
            update.NonManifoldReason,
            update.EventId,
            update.OccurredAtUtc,
            update.ProcessedAtUtc,
            update.ThumbnailStoragePath);
        return FinalizeAsync(
            claim,
            existing => QuoteFileAnalysisStatusTransitions.ApplyGlbReady(existing, transition),
            ct);
    }

    public Task<QuoteAnalysisFinalizeResult> FinalizeDfmAnalysisAsync(
        QuoteAnalysisEventClaim claim,
        QuoteDfmAnalysisUpdate update,
        CancellationToken ct = default)
    {
        QuoteAnalysisClaimValidator.ValidateDfm(claim, update);
        var transition = new DfmReportsTransition(
            claim.StoragePath,
            update.FdmReport,
            update.SlaReport,
            update.CncReport,
            update.OverlayGlbUrls,
            update.NonManifoldReason,
            update.AnalysisErrorCode,
            update.FileId,
            update.EventId,
            update.OccurredAtUtc,
            update.AnalyzedAtUtc,
            update.BodyCount,
            update.OverlayStoragePaths);
        return FinalizeAsync(
            claim,
            existing => QuoteFileAnalysisStatusTransitions.ApplyDfmReports(existing, transition),
            ct);
    }

    public Task MarkAnalysisNotificationDispatchedAsync(
        QuoteAnalysisEventClaim claim,
        long revision,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_claimGate)
        {
            if (!OwnsClaim(claim))
                throw new InvalidOperationException("The analysis event claim is no longer held.");

            var receiptKey = ReceiptKey(claim);
            if (!_receipts.TryGetValue(receiptKey, out var receipt) || receipt.Revision != revision)
                throw new InvalidOperationException("The pending analysis notification receipt was not found.");

            _receipts[receiptKey] = receipt with { Dispatched = true };
            _claims.Remove(ClaimKey(claim.StoragePath, claim.Lane));
        }

        return Task.CompletedTask;
    }

    public Task ReleaseAnalysisClaimAsync(
        QuoteAnalysisEventClaim claim,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_claimGate)
        {
            if (OwnsClaim(claim))
                _claims.Remove(ClaimKey(claim.StoragePath, claim.Lane));
        }

        return Task.CompletedTask;
    }

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
        DateTimeOffset? processedAtUtc = null,
        string? thumbnailStoragePath = null)
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
            processedAtUtc,
            thumbnailStoragePath);
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
        int? bodyCount = null,
        IReadOnlyList<string>? overlayStoragePaths = null)
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
            bodyCount,
            overlayStoragePaths);
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
            _ => transition(null) with { Revision = 1 },
            (_, existing) =>
            {
                var next = transition(existing);
                return ReferenceEquals(next, existing)
                    ? existing
                    : next with { Revision = existing.Revision + 1 };
            });
    }

    internal static bool CanRefreshPreview(
        QuoteFileAnalysisStatus status,
        QuoteAnalysisPreviewRefresh refresh) =>
        status.Revision == refresh.ExpectedRevision &&
        string.Equals(status.ViewerStoragePath, refresh.ViewerStoragePath, StringComparison.Ordinal) &&
        string.Equals(status.ThumbnailStoragePath, refresh.ThumbnailStoragePath, StringComparison.Ordinal) &&
        status.AuthoritativeOverlayStoragePaths.SequenceEqual(
            refresh.OverlayStoragePaths, StringComparer.Ordinal) &&
        refresh.OverlayStoragePaths.Count == refresh.OverlayUrls.Count &&
        QuoteAnalysisArtifactPathValidator.AreCanonicalGeometryPaths(
            status.StoragePath,
            refresh.ViewerStoragePath,
            refresh.ThumbnailStoragePath) &&
        QuoteAnalysisArtifactPathValidator.AreCanonicalOverlayPaths(
            status.StoragePath,
            refresh.OverlayStoragePaths) &&
        HasBoundHttpsCapability(refresh.ViewerStoragePath, refresh.ViewerUrl) &&
        HasBoundHttpsCapability(refresh.ThumbnailStoragePath, refresh.ThumbnailUrl) &&
        refresh.OverlayUrls.All(IsHttpsCapability);

    private static bool HasBoundHttpsCapability(string? storagePath, string? url) =>
        storagePath is null ? string.IsNullOrEmpty(url) : IsHttpsCapability(url);

    private static bool IsHttpsCapability(string? url) =>
        QuoteAnalysisArtifactPathValidator.IsHttpsCapability(url);

    private async Task<QuoteAnalysisEventClaim> ClaimAsync(
        string storagePath,
        string fileId,
        Guid eventId,
        QuoteAnalysisEventLane lane,
        Func<QuoteFileAnalysisStatus?, bool> canApply,
        CancellationToken ct)
    {
        var key = ClaimKey(storagePath, lane);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var token = Guid.NewGuid().ToString("N");
            var expiresAt = _timeProvider.GetUtcNow() + _claimLifetime;
            lock (_claimGate)
            {
                if (_claims.TryGetValue(key, out var held) && held.ExpiresAtUtc <= _timeProvider.GetUtcNow())
                    _claims.Remove(key);

                if (!_claims.ContainsKey(key))
                {
                    _claims[key] = new InMemoryClaim(token, expiresAt);
                    var receiptKey = ReceiptKey(storagePath, lane, eventId);
                    if (_receipts.TryGetValue(receiptKey, out var receipt))
                    {
                        if (!receipt.Dispatched)
                        {
                            _store.TryGetValue(storagePath, out var pendingSnapshot);
                            return new QuoteAnalysisEventClaim(
                                storagePath,
                                fileId,
                                eventId,
                                lane,
                                QuoteAnalysisClaimDisposition.PendingNotification,
                                token,
                                expiresAt,
                                receipt.Revision,
                                pendingSnapshot);
                        }

                        _claims.Remove(key);
                        return DuplicateClaim(storagePath, fileId, eventId, lane);
                    }

                    _store.TryGetValue(storagePath, out var existing);
                    if (!canApply(existing))
                    {
                        _claims.Remove(key);
                        return DuplicateClaim(storagePath, fileId, eventId, lane);
                    }

                    return new QuoteAnalysisEventClaim(
                        storagePath,
                        fileId,
                        eventId,
                        lane,
                        QuoteAnalysisClaimDisposition.Acquired,
                        token,
                        expiresAt);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
        }
    }

    private Task<QuoteAnalysisFinalizeResult> FinalizeAsync(
        QuoteAnalysisEventClaim claim,
        Func<QuoteFileAnalysisStatus?, QuoteFileAnalysisStatus> transition,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_claimGate)
        {
            if (!OwnsClaim(claim))
                return Task.FromResult(new QuoteAnalysisFinalizeResult(false, 0, null));

            _store.TryGetValue(claim.StoragePath, out var existing);
            var next = transition(existing);
            if (existing is not null && ReferenceEquals(next, existing))
            {
                _claims.Remove(ClaimKey(claim.StoragePath, claim.Lane));
                return Task.FromResult(new QuoteAnalysisFinalizeResult(false, existing.Revision, existing));
            }

            var revision = (existing?.Revision ?? 0) + 1;
            next = next with { Revision = revision };
            _store[claim.StoragePath] = next;
            _receipts[ReceiptKey(claim)] = new InMemoryReceipt(revision, false);
            return Task.FromResult(new QuoteAnalysisFinalizeResult(true, revision, next));
        }
    }

    private bool OwnsClaim(QuoteAnalysisEventClaim claim) =>
        claim.Token is not null &&
        _claims.TryGetValue(ClaimKey(claim.StoragePath, claim.Lane), out var held) &&
        held.Token == claim.Token &&
        held.ExpiresAtUtc > _timeProvider.GetUtcNow();

    private static QuoteAnalysisEventClaim DuplicateClaim(
        string storagePath,
        string fileId,
        Guid eventId,
        QuoteAnalysisEventLane lane) =>
        new(
            storagePath,
            fileId,
            eventId,
            lane,
            QuoteAnalysisClaimDisposition.DuplicateOrStale,
            null,
            null);

    private static string ClaimKey(string storagePath, QuoteAnalysisEventLane lane) =>
        $"{lane}:{storagePath}";

    private static string ReceiptKey(QuoteAnalysisEventClaim claim) =>
        ReceiptKey(claim.StoragePath, claim.Lane, claim.EventId);

    private static string ReceiptKey(string storagePath, QuoteAnalysisEventLane lane, Guid eventId) =>
        $"{lane}:{storagePath}:{eventId:N}";

    private sealed record InMemoryClaim(string Token, DateTimeOffset ExpiresAtUtc);

    private sealed record InMemoryReceipt(long Revision, bool Dispatched);
}
