using DotNet.Testcontainers.Configurations;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteAnalysisEventClaimTests : IAsyncLifetime
{
    private const string StoragePath = "quotes/temp/session/claim.step";
    private const string FileId = "file-claim";
    private static readonly DateTimeOffset OccurredAt = DateTimeOffset.Parse("2026-07-14T03:04:05Z");

    static QuoteAnalysisEventClaimTests()
    {
        TestcontainersSettings.ResourceReaperEnabled = false;
    }

    private readonly RedisContainer _redis = new RedisBuilder(
        "redis:7.0-alpine@sha256:c9d92d840fd011c908f040592857c724ae6d877f2aba5c40ad963276507386b2")
        .Build();

    public Task InitializeAsync() => _redis.StartAsync();

    public Task DisposeAsync() => _redis.DisposeAsync().AsTask();

    [Fact]
    public async Task TwoInstances_SameGeometryEvent_OnlyOneClaimRunsAtATime()
    {
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var first = new RedisQuoteFileAnalysisStatusService(firstConnection);
        var second = new RedisQuoteFileAnalysisStatusService(secondConnection);
        var eventId = Guid.NewGuid();

        var winner = await first.ClaimGeometryCompletionAsync(
            StoragePath, FileId, eventId, OccurredAt, OccurredAt.AddSeconds(1));
        Assert.Equal(QuoteAnalysisClaimDisposition.Acquired, winner.Disposition);

        using var waiting = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var contenderTask = second.ClaimGeometryCompletionAsync(
            StoragePath, FileId, eventId, OccurredAt, OccurredAt.AddSeconds(1), waiting.Token);
        await Task.Delay(100, waiting.Token);
        Assert.False(contenderTask.IsCompleted);

        await first.ReleaseAnalysisClaimAsync(winner);
        var contender = await contenderTask;
        Assert.Equal(QuoteAnalysisClaimDisposition.Acquired, contender.Disposition);
        await second.ReleaseAnalysisClaimAsync(contender);
    }

    [Fact]
    public async Task Finalize_WithLostToken_IsRejectedAndCannotOverwriteTakeover()
    {
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var first = new RedisQuoteFileAnalysisStatusService(firstConnection);
        var second = new RedisQuoteFileAnalysisStatusService(secondConnection);
        var eventId = Guid.NewGuid();
        var stale = await first.ClaimGeometryCompletionAsync(
            StoragePath, FileId, eventId, OccurredAt, OccurredAt.AddSeconds(1));

        var database = firstConnection.GetDatabase();
        await database.KeyDeleteAsync(RedisQuoteFileAnalysisStatusService.BuildClaimKey(
            StoragePath, QuoteAnalysisEventLane.Geometry));
        var replacement = await second.ClaimGeometryCompletionAsync(
            StoragePath, FileId, eventId, OccurredAt, OccurredAt.AddSeconds(1));

        var staleFinalize = await first.FinalizeGeometryCompletionAsync(
            stale, GeometryUpdate(eventId));

        Assert.False(staleFinalize.Applied);
        Assert.True(await second.RenewAnalysisClaimAsync(replacement));
        Assert.Null(await first.GetStatusAsync(StoragePath));
        await second.ReleaseAnalysisClaimAsync(replacement);
    }

    [Fact]
    public async Task FinalizedPendingNotification_RetryDoesNotRequireAnotherFinalize()
    {
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var first = new RedisQuoteFileAnalysisStatusService(firstConnection);
        var second = new RedisQuoteFileAnalysisStatusService(secondConnection);
        var eventId = Guid.NewGuid();
        var claim = await first.ClaimGeometryCompletionAsync(
            StoragePath, FileId, eventId, OccurredAt, OccurredAt.AddSeconds(1));

        var finalized = await first.FinalizeGeometryCompletionAsync(claim, GeometryUpdate(eventId));
        Assert.True(finalized.Applied);
        Assert.True(finalized.Revision > 0);
        await first.ReleaseAnalysisClaimAsync(claim);

        var retry = await second.ClaimGeometryCompletionAsync(
            StoragePath, FileId, eventId, OccurredAt, OccurredAt.AddSeconds(1));

        Assert.Equal(QuoteAnalysisClaimDisposition.PendingNotification, retry.Disposition);
        Assert.Equal(finalized.Revision, retry.Revision);
        Assert.NotNull(retry.Snapshot);
        Assert.Equal("https://signed/part.glb", retry.Snapshot.GlbUrl);

        await second.MarkAnalysisNotificationDispatchedAsync(retry, finalized.Revision);
        var duplicate = await first.ClaimGeometryCompletionAsync(
            StoragePath, FileId, eventId, OccurredAt, OccurredAt.AddSeconds(1));
        Assert.Equal(QuoteAnalysisClaimDisposition.DuplicateOrStale, duplicate.Disposition);
    }

    [Fact]
    public async Task GeometryAndDfmClaims_UseIndependentLanes()
    {
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var first = new RedisQuoteFileAnalysisStatusService(firstConnection);
        var second = new RedisQuoteFileAnalysisStatusService(secondConnection);
        var geometry = await first.ClaimGeometryCompletionAsync(
            StoragePath, FileId, Guid.NewGuid(), OccurredAt, OccurredAt.AddSeconds(1));

        var dfm = await second.ClaimDfmEventAsync(StoragePath, FileId, Guid.NewGuid());

        Assert.Equal(QuoteAnalysisClaimDisposition.Acquired, geometry.Disposition);
        Assert.Equal(QuoteAnalysisClaimDisposition.Acquired, dfm.Disposition);
        await first.ReleaseAnalysisClaimAsync(geometry);
        await second.ReleaseAnalysisClaimAsync(dfm);
    }

    [Fact]
    public async Task WaitingClaim_CancellationDoesNotReleaseCurrentOwner()
    {
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var first = new RedisQuoteFileAnalysisStatusService(firstConnection);
        var second = new RedisQuoteFileAnalysisStatusService(secondConnection);
        var owner = await first.ClaimGeometryCompletionAsync(
            StoragePath, FileId, Guid.NewGuid(), OccurredAt, OccurredAt.AddSeconds(1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            second.ClaimGeometryCompletionAsync(
                StoragePath,
                FileId,
                Guid.NewGuid(),
                OccurredAt,
                OccurredAt.AddSeconds(2),
                cancellation.Token));

        Assert.True(await first.RenewAnalysisClaimAsync(owner));
        await first.ReleaseAnalysisClaimAsync(owner);
    }

    [Fact]
    public async Task DfmClaimFinalize_PreservesGeometryAndReturnsMergedRevision()
    {
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var first = new RedisQuoteFileAnalysisStatusService(firstConnection);
        var second = new RedisQuoteFileAnalysisStatusService(secondConnection);
        await first.SetGeometryMetricsAsync(
            StoragePath, FileId, 12.5m, 1.25m, 42.5m, 10m, 20m, 30m, 456, 1, true, null,
            Guid.NewGuid(), OccurredAt, OccurredAt.AddSeconds(1));
        var eventId = Guid.NewGuid();
        var claim = await second.ClaimDfmEventAsync(StoragePath, FileId, eventId);

        var finalized = await second.FinalizeDfmAnalysisAsync(
            claim,
            new QuoteDfmAnalysisUpdate(
                new QeFdmDfmReport(0, 0, 0, false, 0, []),
                null,
                null,
                ["https://signed/overlay.glb"],
                null,
                null,
                FileId,
                eventId,
                OccurredAt,
                OccurredAt.AddSeconds(2),
                1));

        Assert.True(finalized.Applied);
        Assert.Equal(2, finalized.Revision);
        Assert.NotNull(finalized.Snapshot);
        Assert.Equal(12.5m, finalized.Snapshot.VolumeCc);
        Assert.NotNull(finalized.Snapshot.FdmReport);
        Assert.Equal(["https://signed/overlay.glb"], finalized.Snapshot.OverlayGlbUrls);
        await second.MarkAnalysisNotificationDispatchedAsync(claim, finalized.Revision);
    }

    [Fact]
    public async Task DurableProcessedMarker_RejectsDfmReplayAfterBoundedHistoryAndReceiptAreGone()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var service = new RedisQuoteFileAnalysisStatusService(connection);
        var originalEventId = Guid.NewGuid();
        var original = await service.ClaimDfmEventAsync(StoragePath, FileId, originalEventId);
        var finalized = await service.FinalizeDfmAnalysisAsync(original, DfmUpdate(originalEventId));
        await service.MarkAnalysisNotificationDispatchedAsync(original, finalized.Revision);

        for (var index = 0; index < 33; index++)
        {
            var eventId = Guid.NewGuid();
            await service.SetDfmReportsAsync(
                StoragePath,
                new QeFdmDfmReport(index, 0, 0, false, 0, []),
                null,
                null,
                [],
                null,
                null,
                fileId: FileId,
                eventId: eventId,
                occurredAtUtc: OccurredAt.AddSeconds(index + 1),
                analyzedAtUtc: OccurredAt.AddSeconds(index + 1));
        }

        var state = await service.GetStatusAsync(StoragePath);
        Assert.NotNull(state);
        Assert.DoesNotContain(originalEventId, state.ProcessedDfmEventIds);
        await connection.GetDatabase().KeyDeleteAsync(
            RedisQuoteFileAnalysisStatusService.BuildReceiptKey(
                StoragePath, QuoteAnalysisEventLane.Dfm, originalEventId));

        var replay = await service.ClaimDfmEventAsync(StoragePath, FileId, originalEventId);

        Assert.Equal(QuoteAnalysisClaimDisposition.DuplicateOrStale, replay.Disposition);
    }

    [Fact]
    public async Task Finalize_RejectsUpdateThatDoesNotMatchClaimIdentity()
    {
        var service = new QuoteFileAnalysisStatusService();
        var eventId = Guid.NewGuid();
        var claim = await service.ClaimGeometryCompletionAsync(
            StoragePath, FileId, eventId, OccurredAt, OccurredAt.AddSeconds(1));

        var mismatched = GeometryUpdate(Guid.NewGuid());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.FinalizeGeometryCompletionAsync(claim, mismatched));

        Assert.True(await service.RenewAnalysisClaimAsync(claim));
        await service.ReleaseAnalysisClaimAsync(claim);
    }

    [Fact]
    public async Task InMemoryRenew_RejectsExpiredClaimAndAllowsTakeover()
    {
        var time = new ManualTimeProvider(OccurredAt);
        var service = new QuoteFileAnalysisStatusService(time, TimeSpan.FromSeconds(1));
        var first = await service.ClaimGeometryCompletionAsync(
            StoragePath, FileId, Guid.NewGuid(), OccurredAt, OccurredAt.AddSeconds(1));
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.False(await service.RenewAnalysisClaimAsync(first));

        var replacement = await service.ClaimGeometryCompletionAsync(
            StoragePath, FileId, Guid.NewGuid(), OccurredAt, OccurredAt.AddSeconds(2));
        Assert.Equal(QuoteAnalysisClaimDisposition.Acquired, replacement.Disposition);
        await service.ReleaseAnalysisClaimAsync(replacement);
    }

    private static QuoteGeometryCompletionUpdate GeometryUpdate(Guid eventId) => new(
        GlbUrl: "https://signed/part.glb",
        ThumbnailUrl: "https://signed/part.png",
        BodyCount: 1,
        IsManifold: true,
        ViewerStoragePath: StoragePath + "_viewer.glb",
        ViewerFileExtension: ".glb",
        VolumeCc: 12.5m,
        SurfaceAreaCm2: 42.5m,
        FileId: FileId,
        SupportVolumeCc: 1.25m,
        BoundingBoxXmm: 10m,
        BoundingBoxYmm: 20m,
        BoundingBoxZmm: 30m,
        TriangleCount: 456,
        NonManifoldReason: null,
        EventId: eventId,
        OccurredAtUtc: OccurredAt,
        ProcessedAtUtc: OccurredAt.AddSeconds(1));

    private static QuoteDfmAnalysisUpdate DfmUpdate(Guid eventId) => new(
        new QeFdmDfmReport(0, 0, 0, false, 0, []),
        null,
        null,
        [],
        null,
        null,
        FileId,
        eventId,
        OccurredAt,
        OccurredAt.AddSeconds(1),
        1);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
