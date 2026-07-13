using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Maliev.QuoteEngine.Shared.Quotes;
using StackExchange.Redis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Maliev.QuoteEngine.Bff.Services;

internal sealed record RedisQuoteFileAnalysisStatusOptions
{
    internal TimeSpan DurableLifetime { get; init; } = TimeSpan.FromHours(48);
    internal TimeSpan EphemeralLifetime { get; init; } = TimeSpan.FromMinutes(55);
    internal TimeSpan ProcessedEventLifetime { get; init; } = TimeSpan.FromDays(30);
    internal TimeSpan ClaimLifetime { get; init; } = TimeSpan.FromMinutes(2);
    internal TimeSpan ClaimRetryDelay { get; init; } = TimeSpan.FromMilliseconds(25);
    internal int MaxUpdateAttempts { get; init; } = 8;
}

/// <summary>
/// Distributed analysis-status adapter. Durable manufacturing facts and short-lived signed preview
/// material are stored separately and joined only when their file identity and category generation match.
/// </summary>
internal sealed class RedisQuoteFileAnalysisStatusService : IQuoteFileAnalysisStatusService
{
    private const int SchemaVersion = 1;
    private const string KeyPrefix = "quote-engine:analysis-status:v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly RedisQuoteFileAnalysisStatusOptions _options;

    internal RedisQuoteFileAnalysisStatusService(
        IConnectionMultiplexer redis,
        TimeProvider? timeProvider = null,
        RedisQuoteFileAnalysisStatusOptions? options = null)
    {
        _database = redis.GetDatabase();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new RedisQuoteFileAnalysisStatusOptions();
        if (_options.DurableLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Durable lifetime must be positive.");
        if (_options.EphemeralLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Ephemeral lifetime must be positive.");
        if (_options.EphemeralLifetime > TimeSpan.FromMinutes(55))
            throw new ArgumentOutOfRangeException(nameof(options), "Ephemeral lifetime cannot exceed 55 minutes.");
        if (_options.DurableLifetime <= _options.EphemeralLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Durable lifetime must be longer than ephemeral preview lifetime.");
        }
        if (_options.ProcessedEventLifetime < _options.DurableLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Processed-event lifetime must cover the durable analysis lifetime.");
        }
        if (_options.MaxUpdateAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Max update attempts must be positive.");
        if (_options.ClaimLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Claim lifetime must be positive.");
        if (_options.ClaimRetryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Claim retry delay must be positive.");
    }

    public async Task<QuoteFileAnalysisStatus?> GetStatusAsync(
        string storagePath,
        CancellationToken ct = default)
    {
        var snapshot = await ReadAsync(storagePath, ct);
        return snapshot.State;
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

    public async Task<bool> RenewAnalysisClaimAsync(
        QuoteAnalysisEventClaim claim,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return claim.Token is not null && await _database.LockExtendAsync(
            BuildClaimKey(claim.StoragePath, claim.Lane),
            claim.Token,
            _options.ClaimLifetime).WaitAsync(ct);
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
        return FinalizeUnderClaimAsync(
            claim,
            existing => QuoteFileAnalysisStatusTransitions.ApplyGlbReady(existing, transition),
            EphemeralUpdate.Geometry,
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
        return FinalizeUnderClaimAsync(
            claim,
            existing => QuoteFileAnalysisStatusTransitions.ApplyDfmReports(existing, transition),
            new EphemeralUpdate(update.OverlayGlbUrls, null, false),
            ct);
    }

    public async Task MarkAnalysisNotificationDispatchedAsync(
        QuoteAnalysisEventClaim claim,
        long revision,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (claim.Token is null)
            throw new InvalidOperationException("The analysis event claim has no lease token.");

        var receiptKey = BuildReceiptKey(claim.StoragePath, claim.Lane, claim.EventId);
        var raw = await _database.StringGetAsync(receiptKey).WaitAsync(ct);
        var receipt = DeserializeReceipt(raw);
        if (receipt is null || receipt.Revision != revision)
            throw new InvalidOperationException("The pending analysis notification receipt was not found.");

        var transaction = _database.CreateTransaction();
        transaction.AddCondition(Condition.StringEqual(
            BuildClaimKey(claim.StoragePath, claim.Lane), claim.Token));
        transaction.AddCondition(Condition.StringEqual(receiptKey, raw));
        _ = transaction.StringSetAsync(
            receiptKey,
            JsonSerializer.Serialize(receipt with { Dispatched = true }, JsonOptions),
            _options.EphemeralLifetime);
        _ = transaction.KeyDeleteAsync(BuildClaimKey(claim.StoragePath, claim.Lane));
        if (!await transaction.ExecuteAsync().WaitAsync(ct))
            throw new InvalidOperationException("The analysis event claim was lost before notification dispatch completed.");
    }

    public async Task ReleaseAnalysisClaimAsync(
        QuoteAnalysisEventClaim claim,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (claim.Token is not null)
        {
            await _database.LockReleaseAsync(
                BuildClaimKey(claim.StoragePath, claim.Lane), claim.Token).WaitAsync(ct);
        }
    }

    public async Task<bool> CanApplyGeometryEventAsync(
        string storagePath,
        string fileId,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset processedAtUtc,
        QuoteGeometryEventPhase phase,
        CancellationToken ct = default)
    {
        var existing = await GetStatusAsync(storagePath, ct);
        return existing is null || QuoteFileAnalysisStatusTransitions.CanApplyGeometry(
            existing,
            fileId,
            eventId,
            occurredAtUtc,
            processedAtUtc,
            phase);
    }

    public async Task<bool> CanApplyDfmEventAsync(
        string storagePath,
        string fileId,
        Guid eventId,
        CancellationToken ct = default)
    {
        var existing = await GetStatusAsync(storagePath, ct);
        return existing is null || QuoteFileAnalysisStatusTransitions.CanApplyDfm(existing, fileId, eventId);
    }

    public Task SetProcessingAsync(string storagePath, CancellationToken ct = default) =>
        UpdateAsync(
            storagePath,
            existing => QuoteFileAnalysisStatusTransitions.ApplyProcessing(existing, storagePath),
            EphemeralUpdate.None,
            ct);

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
        return UpdateAsync(
            storagePath,
            existing => QuoteFileAnalysisStatusTransitions.ApplyGlbReady(existing, transition),
            EphemeralUpdate.Geometry,
            ct);
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
        return UpdateAsync(
            storagePath,
            existing => QuoteFileAnalysisStatusTransitions.ApplyGeometryMetrics(existing, transition),
            EphemeralUpdate.None,
            ct);
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
        return UpdateAsync(
            storagePath,
            existing => QuoteFileAnalysisStatusTransitions.ApplyDfmReports(existing, transition),
            new EphemeralUpdate(overlayGlbUrls, null, false),
            ct);
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
        return UpdateAsync(
            storagePath,
            existing => QuoteFileAnalysisStatusTransitions.ApplyLocalGeometryMetrics(existing, transition),
            EphemeralUpdate.None,
            ct);
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
        return UpdateAsync(
            storagePath,
            existing => QuoteFileAnalysisStatusTransitions.ApplyLocalDfmReports(existing, transition),
            new EphemeralUpdate(null, overlayGlbUrls, false),
            ct);
    }

    public Task SetFailedAsync(
        string storagePath,
        string errorCode,
        CancellationToken ct = default) =>
        UpdateAsync(
            storagePath,
            existing => QuoteFileAnalysisStatusTransitions.ApplyLegacyFailure(
                existing,
                storagePath,
                errorCode),
            EphemeralUpdate.None,
            ct);

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
        return UpdateAsync(
            storagePath,
            existing => QuoteFileAnalysisStatusTransitions.ApplyOrderedFailure(existing, transition),
            EphemeralUpdate.None,
            ct);
    }

    internal static RedisKey BuildDurableKey(string storagePath) => BuildKey("state", storagePath);

    internal static RedisKey BuildPreviewKey(string storagePath) => BuildKey("preview", storagePath);

    internal static RedisKey BuildOverlayKey(string storagePath) => BuildKey("overlays", storagePath);

    internal static RedisKey BuildClaimKey(
        string storagePath,
        QuoteAnalysisEventLane lane) =>
        BuildKey($"claim:{lane.ToString().ToLowerInvariant()}", storagePath);

    internal static RedisKey BuildReceiptKey(
        string storagePath,
        QuoteAnalysisEventLane lane,
        Guid eventId) =>
        BuildKey($"receipt:{lane.ToString().ToLowerInvariant()}:{eventId:N}", storagePath);

    internal static RedisKey BuildProcessedEventSetKey(
        string storagePath,
        QuoteAnalysisEventLane lane) =>
        BuildKey($"processed:{lane.ToString().ToLowerInvariant()}", storagePath);

    private async Task<QuoteAnalysisEventClaim> ClaimAsync(
        string storagePath,
        string fileId,
        Guid eventId,
        QuoteAnalysisEventLane lane,
        Func<QuoteFileAnalysisStatus?, bool> canApply,
        CancellationToken ct)
    {
        var claimKey = BuildClaimKey(storagePath, lane);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var token = Guid.NewGuid().ToString("N");
            if (!await _database.LockTakeAsync(claimKey, token, _options.ClaimLifetime).WaitAsync(ct))
            {
                await Task.Delay(_options.ClaimRetryDelay, ct);
                continue;
            }

            try
            {
                var receiptTask = _database.StringGetAsync(BuildReceiptKey(storagePath, lane, eventId));
                var processedTask = _database.SetContainsAsync(
                    BuildProcessedEventSetKey(storagePath, lane), eventId.ToString("N"));
                var receipt = DeserializeReceipt(await receiptTask.WaitAsync(ct));
                var alreadyProcessed = await processedTask.WaitAsync(ct);
                if (receipt is not null)
                {
                    if (!receipt.Dispatched)
                    {
                        var pending = await GetStatusAsync(storagePath, ct)
                            ?? throw new InvalidOperationException(
                                "A pending analysis notification receipt has no durable status snapshot.");
                        return new QuoteAnalysisEventClaim(
                            storagePath,
                            fileId,
                            eventId,
                            lane,
                            QuoteAnalysisClaimDisposition.PendingNotification,
                            token,
                            _timeProvider.GetUtcNow() + _options.ClaimLifetime,
                            receipt.Revision,
                            pending);
                    }

                    await _database.LockReleaseAsync(claimKey, token).WaitAsync(ct);
                    return DuplicateClaim(storagePath, fileId, eventId, lane);
                }

                if (alreadyProcessed)
                {
                    await _database.LockReleaseAsync(claimKey, token).WaitAsync(ct);
                    return DuplicateClaim(storagePath, fileId, eventId, lane);
                }

                var existing = await GetStatusAsync(storagePath, ct);
                if (!canApply(existing))
                {
                    await _database.LockReleaseAsync(claimKey, token).WaitAsync(ct);
                    return DuplicateClaim(storagePath, fileId, eventId, lane);
                }

                return new QuoteAnalysisEventClaim(
                    storagePath,
                    fileId,
                    eventId,
                    lane,
                    QuoteAnalysisClaimDisposition.Acquired,
                    token,
                    _timeProvider.GetUtcNow() + _options.ClaimLifetime);
            }
            catch
            {
                await _database.LockReleaseAsync(claimKey, token);
                throw;
            }
        }
    }

    private async Task<QuoteAnalysisFinalizeResult> FinalizeUnderClaimAsync(
        QuoteAnalysisEventClaim claim,
        Func<QuoteFileAnalysisStatus?, QuoteFileAnalysisStatus> transition,
        EphemeralUpdate ephemeralUpdate,
        CancellationToken ct)
    {
        if (claim.Disposition != QuoteAnalysisClaimDisposition.Acquired || claim.Token is null)
            return new QuoteAnalysisFinalizeResult(false, 0, claim.Snapshot);

        for (var attempt = 0; attempt < _options.MaxUpdateAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await ReadAsync(claim.StoragePath, ct);
            var next = transition(snapshot.State);
            if (snapshot.State is not null && ReferenceEquals(next, snapshot.State))
            {
                await ReleaseAnalysisClaimAsync(claim, ct);
                return new QuoteAnalysisFinalizeResult(false, snapshot.Revision, snapshot.State);
            }

            var revision = snapshot.Revision + 1;
            next = next with { Revision = revision };
            var now = _timeProvider.GetUtcNow();
            var geometryGeneration = ephemeralUpdate.RefreshGeometry
                ? Guid.NewGuid()
                : snapshot.GeometryGeneration;
            var authoritativeOverlayGeneration = ephemeralUpdate.Authoritative is { Count: > 0 }
                ? Guid.NewGuid()
                : snapshot.AuthoritativeOverlayGeneration;
            var advisoryOverlayGeneration = ephemeralUpdate.Advisory is { Count: > 0 }
                ? Guid.NewGuid()
                : snapshot.AdvisoryOverlayGeneration;
            var durable = new DurableEnvelope(
                SchemaVersion,
                revision,
                geometryGeneration,
                authoritativeOverlayGeneration,
                advisoryOverlayGeneration,
                StripEphemeral(next));
            var geometry = BuildGeometryEnvelope(
                snapshot,
                next,
                ephemeralUpdate.RefreshGeometry,
                geometryGeneration,
                now);
            var overlays = BuildOverlayEnvelope(
                snapshot,
                next,
                ephemeralUpdate,
                authoritativeOverlayGeneration,
                advisoryOverlayGeneration,
                now);
            var fileIdentityChanged = !SameFileIdentity(snapshot.State?.FileId, next.FileId);
            var writeGeometry = ephemeralUpdate.RefreshGeometry ||
                (fileIdentityChanged && snapshot.Geometry is not null);
            var writeOverlays = ephemeralUpdate.Authoritative is { Count: > 0 } ||
                ephemeralUpdate.Advisory is { Count: > 0 } ||
                (fileIdentityChanged && snapshot.Overlays is not null);
            var transaction = _database.CreateTransaction();
            transaction.AddCondition(snapshot.RawDurable.IsNull
                ? Condition.KeyNotExists(BuildDurableKey(claim.StoragePath))
                : Condition.StringEqual(BuildDurableKey(claim.StoragePath), snapshot.RawDurable));
            transaction.AddCondition(Condition.StringEqual(
                BuildClaimKey(claim.StoragePath, claim.Lane), claim.Token));
            _ = transaction.StringSetAsync(
                BuildDurableKey(claim.StoragePath),
                JsonSerializer.Serialize(durable, JsonOptions),
                _options.DurableLifetime);
            if (writeGeometry)
            {
                QueueEphemeralWrite(
                    transaction,
                    BuildPreviewKey(claim.StoragePath),
                    geometry,
                    geometry?.ExpiresAtUtc,
                    now);
            }

            if (writeOverlays)
                QueueOverlayWrite(transaction, BuildOverlayKey(claim.StoragePath), overlays, now);

            _ = transaction.StringSetAsync(
                BuildReceiptKey(claim.StoragePath, claim.Lane, claim.EventId),
                JsonSerializer.Serialize(new NotificationReceipt(SchemaVersion, revision, false), JsonOptions),
                _options.EphemeralLifetime);
            var processedSetKey = BuildProcessedEventSetKey(claim.StoragePath, claim.Lane);
            _ = transaction.SetAddAsync(processedSetKey, claim.EventId.ToString("N"));
            _ = transaction.KeyExpireAsync(processedSetKey, _options.ProcessedEventLifetime);
            if (await transaction.ExecuteAsync().WaitAsync(ct))
                return new QuoteAnalysisFinalizeResult(true, revision, next);

            var heldToken = await _database.LockQueryAsync(
                BuildClaimKey(claim.StoragePath, claim.Lane)).WaitAsync(ct);
            if (!heldToken.Equals((RedisValue)claim.Token))
                return new QuoteAnalysisFinalizeResult(false, 0, null);

            if (attempt + 1 < _options.MaxUpdateAttempts)
                await Task.Delay(TimeSpan.FromMilliseconds(attempt + 1), ct);
        }

        throw new QuoteAnalysisConcurrencyException(claim.StoragePath);
    }

    private static NotificationReceipt? DeserializeReceipt(RedisValue raw)
    {
        if (raw.IsNull)
            return null;

        try
        {
            var receipt = JsonSerializer.Deserialize<NotificationReceipt>(raw.ToString(), JsonOptions)
                ?? throw new JsonException("The analysis notification receipt was empty.");
            if (receipt.SchemaVersion != SchemaVersion || receipt.Revision <= 0)
                throw new JsonException("The analysis notification receipt is invalid.");
            return receipt;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The analysis notification receipt is invalid.", ex);
        }
    }

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

    private async Task UpdateAsync(
        string storagePath,
        Func<QuoteFileAnalysisStatus?, QuoteFileAnalysisStatus> transition,
        EphemeralUpdate ephemeralUpdate,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < _options.MaxUpdateAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await ReadAsync(storagePath, ct);
            var next = transition(snapshot.State);
            if (snapshot.State is not null && ReferenceEquals(next, snapshot.State))
                return;

            var revision = snapshot.Revision + 1;
            next = next with { Revision = revision };
            var now = _timeProvider.GetUtcNow();
            var geometryGeneration = ephemeralUpdate.RefreshGeometry
                ? Guid.NewGuid()
                : snapshot.GeometryGeneration;
            var authoritativeOverlayGeneration = ephemeralUpdate.Authoritative is { Count: > 0 }
                ? Guid.NewGuid()
                : snapshot.AuthoritativeOverlayGeneration;
            var advisoryOverlayGeneration = ephemeralUpdate.Advisory is { Count: > 0 }
                ? Guid.NewGuid()
                : snapshot.AdvisoryOverlayGeneration;
            var durable = new DurableEnvelope(
                SchemaVersion,
                revision,
                geometryGeneration,
                authoritativeOverlayGeneration,
                advisoryOverlayGeneration,
                StripEphemeral(next));
            var geometry = BuildGeometryEnvelope(
                snapshot,
                next,
                ephemeralUpdate.RefreshGeometry,
                geometryGeneration,
                now);
            var overlays = BuildOverlayEnvelope(
                snapshot,
                next,
                ephemeralUpdate,
                authoritativeOverlayGeneration,
                advisoryOverlayGeneration,
                now);
            var fileIdentityChanged = !SameFileIdentity(snapshot.State?.FileId, next.FileId);
            var writeGeometry = ephemeralUpdate.RefreshGeometry ||
                (fileIdentityChanged && snapshot.Geometry is not null);
            var writeOverlays = ephemeralUpdate.Authoritative is { Count: > 0 } ||
                ephemeralUpdate.Advisory is { Count: > 0 } ||
                (fileIdentityChanged && snapshot.Overlays is not null);
            var transaction = _database.CreateTransaction();
            transaction.AddCondition(snapshot.RawDurable.IsNull
                ? Condition.KeyNotExists(BuildDurableKey(storagePath))
                : Condition.StringEqual(BuildDurableKey(storagePath), snapshot.RawDurable));
            _ = transaction.StringSetAsync(
                BuildDurableKey(storagePath),
                JsonSerializer.Serialize(durable, JsonOptions),
                _options.DurableLifetime);
            if (writeGeometry)
            {
                QueueEphemeralWrite(
                    transaction,
                    BuildPreviewKey(storagePath),
                    geometry,
                    geometry?.ExpiresAtUtc,
                    now);
            }

            if (writeOverlays)
                QueueOverlayWrite(transaction, BuildOverlayKey(storagePath), overlays, now);

            if (await transaction.ExecuteAsync().WaitAsync(ct))
                return;

            if (attempt + 1 < _options.MaxUpdateAttempts)
                await Task.Delay(TimeSpan.FromMilliseconds(attempt + 1), ct);
        }

        throw new InvalidOperationException(
            $"Analysis status update for '{storagePath}' exceeded the Redis concurrency retry limit.");
    }

    private async Task<RedisSnapshot> ReadAsync(
        string storagePath,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var durableKey = BuildDurableKey(storagePath);
        var values = await _database.StringGetAsync(
            [durableKey, BuildPreviewKey(storagePath), BuildOverlayKey(storagePath)]).WaitAsync(ct);
        if (values[0].IsNull)
            return new RedisSnapshot(values[0], 0, null, null, null, null, null, null);

        DurableEnvelope durable;
        try
        {
            durable = JsonSerializer.Deserialize<DurableEnvelope>(values[0].ToString(), JsonOptions)
                ?? throw new JsonException("The durable analysis status value was empty.");
            if (durable.Status is null)
                throw new JsonException("The durable analysis status snapshot was missing.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The durable analysis status value is invalid.", ex);
        }

        if (durable.SchemaVersion != SchemaVersion)
            throw new InvalidOperationException($"Unsupported analysis status schema version {durable.SchemaVersion}.");

        var now = _timeProvider.GetUtcNow();
        var geometry = DeserializeGeometry(values[1], durable, now);
        var overlays = DeserializeOverlays(values[2], durable, now);
        var state = MergeEphemeral(durable.Status, geometry, overlays) with
        {
            Revision = durable.Revision
        };
        return new RedisSnapshot(
            values[0],
            durable.Revision,
            durable.GeometryGeneration,
            durable.AuthoritativeOverlayGeneration,
            durable.AdvisoryOverlayGeneration,
            state,
            geometry,
            overlays);
    }

    private GeometryPreviewEnvelope? DeserializeGeometry(
        RedisValue value,
        DurableEnvelope durable,
        DateTimeOffset now)
    {
        if (value.IsNull)
            return null;

        GeometryPreviewEnvelope? preview;
        try
        {
            preview = JsonSerializer.Deserialize<GeometryPreviewEnvelope>(value.ToString(), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        return preview is not null &&
               preview.SchemaVersion == SchemaVersion &&
               preview.Generation == durable.GeometryGeneration &&
               preview.ExpiresAtUtc > now &&
               IsMissingOrNonWhitespace(preview.GlbUrl) &&
               IsMissingOrNonWhitespace(preview.ThumbnailUrl) &&
               (!string.IsNullOrWhiteSpace(preview.GlbUrl) ||
                !string.IsNullOrWhiteSpace(preview.ThumbnailUrl)) &&
               SameFileIdentity(preview.FileId, durable.Status.FileId)
            ? preview
            : null;
    }

    private OverlayPreviewEnvelope? DeserializeOverlays(
        RedisValue value,
        DurableEnvelope durable,
        DateTimeOffset now)
    {
        if (value.IsNull)
            return null;

        OverlayPreviewEnvelope? preview;
        try
        {
            preview = JsonSerializer.Deserialize<OverlayPreviewEnvelope>(value.ToString(), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (preview is null ||
            preview.SchemaVersion != SchemaVersion ||
            preview.AuthoritativeGeneration != durable.AuthoritativeOverlayGeneration ||
            preview.AdvisoryGeneration != durable.AdvisoryOverlayGeneration ||
            preview.Authoritative is null ||
            preview.Advisory is null ||
            preview.Authoritative.Any(item => item is null || string.IsNullOrWhiteSpace(item.Url)) ||
            preview.Advisory.Any(item => item is null || string.IsNullOrWhiteSpace(item.Url)) ||
            !SameFileIdentity(preview.FileId, durable.Status.FileId))
        {
            return null;
        }

        return preview with
        {
            Authoritative = [.. preview.Authoritative.Where(item => item.ExpiresAtUtc > now)],
            Advisory = [.. preview.Advisory.Where(item => item.ExpiresAtUtc > now)]
        };
    }

    private static QuoteFileAnalysisStatus MergeEphemeral(
        QuoteFileAnalysisStatus durable,
        GeometryPreviewEnvelope? geometry,
        OverlayPreviewEnvelope? overlays)
    {
        var advisory = durable.AdvisoryAnalysis;
        if (advisory is not null && overlays is not null)
        {
            advisory = advisory with
            {
                OverlayGlbUrls = [.. overlays.Advisory.Select(item => item.Url)]
            };
        }

        return durable with
        {
            GlbUrl = geometry?.GlbUrl,
            ThumbnailUrl = geometry?.ThumbnailUrl,
            OverlayGlbUrls = overlays is null
                ? []
                : [.. overlays.Authoritative.Select(item => item.Url)],
            AdvisoryAnalysis = advisory
        };
    }

    private static QuoteFileAnalysisStatus StripEphemeral(QuoteFileAnalysisStatus state) =>
        state with
        {
            GlbUrl = null,
            ThumbnailUrl = null,
            OverlayGlbUrls = [],
            AdvisoryAnalysis = state.AdvisoryAnalysis is null
                ? null
                : state.AdvisoryAnalysis with { OverlayGlbUrls = [] }
        };

    private GeometryPreviewEnvelope? BuildGeometryEnvelope(
        RedisSnapshot snapshot,
        QuoteFileAnalysisStatus next,
        bool refresh,
        Guid? generation,
        DateTimeOffset now)
    {
        if (refresh)
        {
            return new GeometryPreviewEnvelope(
                SchemaVersion,
                generation ?? throw new InvalidOperationException("Geometry preview generation is required."),
                next.FileId,
                next.GlbUrl,
                next.ThumbnailUrl,
                now + _options.EphemeralLifetime);
        }

        return snapshot.Geometry is null
            ? null
            : snapshot.Geometry with
            {
                FileId = next.FileId
            };
    }

    private OverlayPreviewEnvelope? BuildOverlayEnvelope(
        RedisSnapshot snapshot,
        QuoteFileAnalysisStatus next,
        EphemeralUpdate update,
        Guid? authoritativeGeneration,
        Guid? advisoryGeneration,
        DateTimeOffset now)
    {
        var authoritative = snapshot.Overlays?.Authoritative ?? [];
        if (update.Authoritative is { Count: > 0 })
        {
            var existingByUrl = authoritative
                .GroupBy(item => item.Url, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            authoritative =
            [
                .. next.OverlayGlbUrls.Select(url => existingByUrl.TryGetValue(url, out var existing)
                    ? existing
                    : new EphemeralUrl(url, now + _options.EphemeralLifetime))
            ];
        }

        var advisory = snapshot.Overlays?.Advisory ?? [];
        if (update.Advisory is { Count: > 0 })
        {
            advisory =
            [
                .. advisory,
                .. update.Advisory.Select(url => new EphemeralUrl(url, now + _options.EphemeralLifetime))
            ];
        }

        if (authoritative.Count == 0 && advisory.Count == 0)
            return null;

        return new OverlayPreviewEnvelope(
            SchemaVersion,
            authoritativeGeneration,
            advisoryGeneration,
            next.FileId,
            authoritative,
            advisory);
    }

    private static void QueueEphemeralWrite(
        ITransaction transaction,
        RedisKey key,
        GeometryPreviewEnvelope? envelope,
        DateTimeOffset? expiresAtUtc,
        DateTimeOffset now)
    {
        if (envelope is null || expiresAtUtc is null || expiresAtUtc <= now)
        {
            _ = transaction.KeyDeleteAsync(key);
            return;
        }

        _ = transaction.StringSetAsync(
            key,
            JsonSerializer.Serialize(envelope, JsonOptions),
            expiresAtUtc.Value - now);
    }

    private static void QueueOverlayWrite(
        ITransaction transaction,
        RedisKey key,
        OverlayPreviewEnvelope? envelope,
        DateTimeOffset now)
    {
        var expiry = envelope?.Authoritative
            .Concat(envelope.Advisory)
            .Select(item => (DateTimeOffset?)item.ExpiresAtUtc)
            .Max();
        if (envelope is null || expiry is null || expiry <= now)
        {
            _ = transaction.KeyDeleteAsync(key);
            return;
        }

        _ = transaction.StringSetAsync(
            key,
            JsonSerializer.Serialize(envelope, JsonOptions),
            expiry.Value - now);
    }

    private static RedisKey BuildKey(string kind, string storagePath)
    {
        ArgumentNullException.ThrowIfNull(storagePath);
        var canonical = storagePath.ToUpperInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return $"{KeyPrefix}:{{{hash}}}:{kind}";
    }

    private static bool SameFileIdentity(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static bool IsMissingOrNonWhitespace(string? value) =>
        string.IsNullOrEmpty(value) || !string.IsNullOrWhiteSpace(value);

    private sealed record DurableEnvelope(
        int SchemaVersion,
        long Revision,
        Guid? GeometryGeneration,
        Guid? AuthoritativeOverlayGeneration,
        Guid? AdvisoryOverlayGeneration,
        QuoteFileAnalysisStatus Status);

    private sealed record GeometryPreviewEnvelope(
        int SchemaVersion,
        Guid Generation,
        string? FileId,
        string? GlbUrl,
        string? ThumbnailUrl,
        DateTimeOffset ExpiresAtUtc);

    private sealed record OverlayPreviewEnvelope(
        int SchemaVersion,
        Guid? AuthoritativeGeneration,
        Guid? AdvisoryGeneration,
        string? FileId,
        IReadOnlyList<EphemeralUrl> Authoritative,
        IReadOnlyList<EphemeralUrl> Advisory);

    private sealed record EphemeralUrl(string Url, DateTimeOffset ExpiresAtUtc);

    private sealed record NotificationReceipt(
        int SchemaVersion,
        long Revision,
        bool Dispatched);

    private sealed record RedisSnapshot(
        RedisValue RawDurable,
        long Revision,
        Guid? GeometryGeneration,
        Guid? AuthoritativeOverlayGeneration,
        Guid? AdvisoryOverlayGeneration,
        QuoteFileAnalysisStatus? State,
        GeometryPreviewEnvelope? Geometry,
        OverlayPreviewEnvelope? Overlays);

    private sealed record EphemeralUpdate(
        IReadOnlyList<string>? Authoritative,
        IReadOnlyList<string>? Advisory,
        bool RefreshGeometry)
    {
        internal static EphemeralUpdate None { get; } = new(null, null, false);
        internal static EphemeralUpdate Geometry { get; } = new(null, null, true);
    }
}

internal static class QuoteFileAnalysisStatusServiceCollectionExtensions
{
    internal static IServiceCollection AddQuoteFileAnalysisStatus(this IServiceCollection services)
    {
        services.AddSingleton<IQuoteFileAnalysisStatusService>(provider =>
        {
            var redis = provider.GetService<IConnectionMultiplexer>();
            if (redis is not null)
            {
                return new RedisQuoteFileAnalysisStatusService(
                    redis,
                    provider.GetService<TimeProvider>() ?? TimeProvider.System);
            }

            var environment = provider.GetRequiredService<IHostEnvironment>();
            if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
                return new QuoteFileAnalysisStatusService();

            throw new InvalidOperationException("Durable QuoteEngine analysis status requires Redis.");
        });
        return services;
    }
}
