using DotNet.Testcontainers.Configurations;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using System.Text.Json.Nodes;
using Testcontainers.Redis;

namespace Maliev.QuoteEngine.Tests;

public sealed class RedisQuoteFileAnalysisStatusServiceTests : IAsyncLifetime
{
    private const string StoragePath = "quotes/temp/session/part.step";
    private const string FileId = "file-123";
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-07-14T02:03:04Z");

    static RedisQuoteFileAnalysisStatusServiceTests()
    {
        TestcontainersSettings.ResourceReaperEnabled = false;
    }

    private readonly RedisContainer _redis = new RedisBuilder(
        "redis:7.0-alpine@sha256:c9d92d840fd011c908f040592857c724ae6d877f2aba5c40ad963276507386b2")
        .Build();

    public Task InitializeAsync() => _redis.StartAsync();

    public Task DisposeAsync() => _redis.DisposeAsync().AsTask();

    [Fact]
    public async Task TwoInstances_WriteOnOneInstance_IsVisibleToTheOther()
    {
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var first = new RedisQuoteFileAnalysisStatusService(firstConnection);
        var second = new RedisQuoteFileAnalysisStatusService(secondConnection);

        await first.SetGeometryMetricsAsync(
            StoragePath, FileId, 12.5m, 1.25m, 42.5m, 10m, 20m, 30m, 456, 1, true, null,
            Guid.NewGuid(), BaseTime, BaseTime);

        var result = await second.GetStatusAsync(StoragePath);
        Assert.NotNull(result);
        Assert.Equal(FileId, result.FileId);
        Assert.Equal(12.5m, result.VolumeCc);
        Assert.True(result.HasAuthoritativeGeometry);
    }

    [Fact]
    public async Task TwoInstances_ConcurrentGlbAndDfmUpdates_PreserveBothWithoutLostUpdate()
    {
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var first = new RedisQuoteFileAnalysisStatusService(firstConnection);
        var second = new RedisQuoteFileAnalysisStatusService(secondConnection);
        await first.SetProcessingAsync(StoragePath);
        using var gate = new ManualResetEventSlim(false);
        var completionId = Guid.NewGuid();
        var dfmId = Guid.NewGuid();
        var glbTask = Task.Run(async () =>
        {
            gate.Wait();
            await first.SetGlbReadyAsync(
                StoragePath, "https://signed/part.glb", "https://signed/part.png", 1, true,
                viewerStoragePath: StoragePath + "_viewer.glb", viewerFileExtension: ".glb",
                volumeCc: 12.5m, surfaceAreaCm2: 42.5m, fileId: FileId,
                boundingBoxXmm: 10m, boundingBoxYmm: 20m, boundingBoxZmm: 30m,
                triangleCount: 456, eventId: completionId,
                occurredAtUtc: BaseTime, processedAtUtc: BaseTime.AddSeconds(1));
        });
        var dfmTask = Task.Run(async () =>
        {
            gate.Wait();
            await second.SetDfmReportsAsync(
                StoragePath, Fdm(), null, null, ["https://signed/overlay.glb"], null, null,
                fileId: FileId, eventId: dfmId, occurredAtUtc: BaseTime,
                analyzedAtUtc: BaseTime.AddSeconds(1));
        });

        gate.Set();
        await Task.WhenAll(glbTask, dfmTask);

        var result = await first.GetStatusAsync(StoragePath);
        Assert.NotNull(result);
        Assert.Equal("DfmAnalysisReady", result.Status);
        Assert.Equal("https://signed/part.glb", result.GlbUrl);
        Assert.Equal("https://signed/part.png", result.ThumbnailUrl);
        Assert.NotNull(result.FdmReport);
        Assert.Equal(["https://signed/overlay.glb"], result.OverlayGlbUrls);
        Assert.Equal(completionId, result.LastGeometryEventId);
        Assert.Contains(dfmId, result.ProcessedDfmEventIds);
    }

    [Fact]
    public async Task RedisState_CaseInsensitiveKeyAndOrderingReplayGatesMatchInMemoryBehavior()
    {
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var first = new RedisQuoteFileAnalysisStatusService(firstConnection);
        var second = new RedisQuoteFileAnalysisStatusService(secondConnection);
        var metricsId = Guid.NewGuid();
        await first.SetGeometryMetricsAsync(
            "QUOTES/Temp/Session/Part.step", FileId,
            12.5m, 1.25m, 42.5m, 10m, 20m, 30m, 456, 1, true, null,
            metricsId, BaseTime, BaseTime);

        await second.SetFailedAsync(
            "quotes/temp/session/part.STEP", FileId, "older_failure", Guid.NewGuid(),
            BaseTime.AddSeconds(-1));
        await second.SetGeometryMetricsAsync(
            StoragePath, FileId, 99m, 9m, 99m, 99m, 99m, 99m, 999, 9, false, "duplicate",
            metricsId, BaseTime.AddMinutes(1), BaseTime.AddMinutes(1));

        var result = await first.GetStatusAsync(StoragePath);
        Assert.NotNull(result);
        Assert.Equal("Processing", result.Status);
        Assert.Equal(12.5m, result.VolumeCc);
        Assert.Equal(metricsId, result.LastGeometryEventId);
        Assert.True(result.IsManifold);
        Assert.Equal(
            RedisQuoteFileAnalysisStatusService.BuildDurableKey("QUOTES/Temp/Session/Part.step"),
            RedisQuoteFileAnalysisStatusService.BuildDurableKey(StoragePath));
    }

    [Fact]
    public async Task DurableAndPreviewState_UseSeparateTtlsAndDfmDoesNotExtendPreview()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var service = new RedisQuoteFileAnalysisStatusService(connection);
        await service.SetGlbReadyAsync(
            StoragePath, "https://signed/part.glb", "https://signed/part.png", 1, true,
            fileId: FileId, eventId: Guid.NewGuid(), occurredAtUtc: BaseTime,
            processedAtUtc: BaseTime);
        var database = connection.GetDatabase();
        var durableKey = RedisQuoteFileAnalysisStatusService.BuildDurableKey(StoragePath);
        var previewKey = RedisQuoteFileAnalysisStatusService.BuildPreviewKey(StoragePath);
        var durableJson = await database.StringGetAsync(durableKey);
        var previewJson = await database.StringGetAsync(previewKey);
        Assert.DoesNotContain("https://signed/part.glb", durableJson.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("https://signed/part.png", durableJson.ToString(), StringComparison.Ordinal);
        Assert.Contains("https://signed/part.glb", previewJson.ToString(), StringComparison.Ordinal);
        Assert.True(await database.KeyExpireAsync(previewKey, TimeSpan.FromMinutes(10)));
        Assert.True(await database.KeyExpireAsync(durableKey, TimeSpan.FromMinutes(1)));

        await service.SetDfmReportsAsync(
            StoragePath, Fdm(), null, null, ["https://signed/overlay.glb"], null, null,
            fileId: FileId, eventId: Guid.NewGuid());

        var durableTtl = await database.KeyTimeToLiveAsync(durableKey);
        var previewTtl = await database.KeyTimeToLiveAsync(previewKey);
        var overlayKey = RedisQuoteFileAnalysisStatusService.BuildOverlayKey(StoragePath);
        var overlayTtl = await database.KeyTimeToLiveAsync(overlayKey);
        durableJson = await database.StringGetAsync(durableKey);
        var overlayJson = await database.StringGetAsync(overlayKey);
        Assert.DoesNotContain("https://signed/overlay.glb", durableJson.ToString(), StringComparison.Ordinal);
        Assert.Contains("https://signed/overlay.glb", overlayJson.ToString(), StringComparison.Ordinal);
        Assert.NotNull(durableTtl);
        Assert.InRange(durableTtl.Value, TimeSpan.FromHours(47), TimeSpan.FromHours(48));
        Assert.NotNull(previewTtl);
        Assert.InRange(previewTtl.Value, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
        Assert.NotNull(overlayTtl);
        Assert.InRange(overlayTtl.Value, TimeSpan.FromMinutes(54), TimeSpan.FromMinutes(55));
        var merged = await service.GetStatusAsync(StoragePath);
        Assert.Equal("https://signed/part.glb", merged?.GlbUrl);
        Assert.NotNull(merged?.FdmReport);
        Assert.Equal(["https://signed/overlay.glb"], merged?.OverlayGlbUrls);
    }

    [Fact]
    public async Task RefreshPreviewUrls_WritesOnlyEphemeralKeysAndIsVisibleAcrossInstances()
    {
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var first = new RedisQuoteFileAnalysisStatusService(firstConnection);
        var second = new RedisQuoteFileAnalysisStatusService(secondConnection);
        var viewerPath = StoragePath + "_viewer.glb";
        var thumbnailPath = StoragePath + "_thumbnail_small.webp";
        var overlayPath = StoragePath + "_thin_wall_overlay.glb";
        await first.SetGlbReadyAsync(
            StoragePath, "https://expired/part.glb", "https://expired/part.webp", 1, true,
            viewerStoragePath: viewerPath, viewerFileExtension: ".glb", fileId: FileId,
            thumbnailStoragePath: thumbnailPath);
        await first.SetDfmReportsAsync(
            StoragePath, Fdm(), null, null, ["https://expired/overlay.glb"], null, null,
            fileId: FileId, eventId: Guid.NewGuid(), overlayStoragePaths: [overlayPath]);
        var database = firstConnection.GetDatabase();
        var durableKey = RedisQuoteFileAnalysisStatusService.BuildDurableKey(StoragePath);
        var durableBefore = await database.StringGetAsync(durableKey);
        var durableTtlBefore = await database.KeyTimeToLiveAsync(durableKey);
        await database.KeyDeleteAsync([
            RedisQuoteFileAnalysisStatusService.BuildPreviewKey(StoragePath),
            RedisQuoteFileAnalysisStatusService.BuildOverlayKey(StoragePath)]);
        var expired = Assert.IsType<QuoteFileAnalysisStatus>(await second.GetStatusAsync(StoragePath));
        Assert.Null(expired.GlbUrl);
        Assert.Empty(expired.OverlayGlbUrls);

        var refreshed = await first.RefreshPreviewUrlsAsync(
            StoragePath,
            new QuoteAnalysisPreviewRefresh(
                expired.Revision,
                viewerPath,
                "https://signed/part.glb",
                thumbnailPath,
                "https://signed/part.webp",
                [overlayPath],
                ["https://signed/overlay.glb"]));

        Assert.NotNull(refreshed);
        Assert.Equal(expired.Revision, refreshed.Revision);
        Assert.Equal(durableBefore, await database.StringGetAsync(durableKey));
        var durableTtlAfter = await database.KeyTimeToLiveAsync(durableKey);
        Assert.NotNull(durableTtlBefore);
        Assert.NotNull(durableTtlAfter);
        Assert.True(durableTtlAfter <= durableTtlBefore);
        var crossReplica = Assert.IsType<QuoteFileAnalysisStatus>(await second.GetStatusAsync(StoragePath));
        Assert.Equal("https://signed/part.glb", crossReplica.GlbUrl);
        Assert.Equal("https://signed/part.webp", crossReplica.ThumbnailUrl);
        Assert.Equal(["https://signed/overlay.glb"], crossReplica.OverlayGlbUrls);
    }

    [Fact]
    public async Task RefreshPreviewUrls_StaleRevisionDoesNotCreateEphemeralKeys()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var service = new RedisQuoteFileAnalysisStatusService(connection);
        var viewerPath = StoragePath + "_viewer.glb";
        await service.SetGlbReadyAsync(
            StoragePath, "https://expired/part.glb", null, 1, true,
            viewerStoragePath: viewerPath, viewerFileExtension: ".glb", fileId: FileId);
        var database = connection.GetDatabase();
        await database.KeyDeleteAsync(RedisQuoteFileAnalysisStatusService.BuildPreviewKey(StoragePath));
        var current = Assert.IsType<QuoteFileAnalysisStatus>(await service.GetStatusAsync(StoragePath));

        var result = await service.RefreshPreviewUrlsAsync(
            StoragePath,
            new QuoteAnalysisPreviewRefresh(
                current.Revision - 1,
                viewerPath,
                "https://signed/part.glb",
                null,
                null,
                [],
                []));

        Assert.NotNull(result);
        Assert.Equal(current.Revision, result.Revision);
        Assert.Equal(current.StoragePath, result.StoragePath);
        Assert.Null(result.GlbUrl);
        Assert.False(await database.KeyExistsAsync(
            RedisQuoteFileAnalysisStatusService.BuildPreviewKey(StoragePath)));
    }

    [Fact]
    public async Task PreviewResolver_TwoReplicas_CoalesceSigningThroughRedisLease()
    {
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var firstStore = new RedisQuoteFileAnalysisStatusService(firstConnection);
        var secondStore = new RedisQuoteFileAnalysisStatusService(secondConnection);
        var viewerPath = StoragePath + "_viewer.glb";
        await firstStore.SetGlbReadyAsync(
            StoragePath, "https://expired/part.glb", null, 1, true,
            viewerStoragePath: viewerPath, viewerFileExtension: ".glb", fileId: FileId);
        await firstConnection.GetDatabase().KeyDeleteAsync(
            RedisQuoteFileAnalysisStatusService.BuildPreviewKey(StoragePath));
        var expired = Assert.IsType<QuoteFileAnalysisStatus>(await firstStore.GetStatusAsync(StoragePath));
        var signer = new GatedPreviewSigner();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        var firstResolver = new QuoteAnalysisPreviewUrlResolver(
            firstStore, signer, firstConnection, TimeProvider.System, lifetime,
            NullLogger<QuoteAnalysisPreviewUrlResolver>.Instance);
        var secondResolver = new QuoteAnalysisPreviewUrlResolver(
            secondStore, signer, secondConnection, TimeProvider.System, lifetime,
            NullLogger<QuoteAnalysisPreviewUrlResolver>.Instance);

        var firstRequest = firstResolver.ResolveAsync(StoragePath, expired);
        await signer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRequest = secondResolver.ResolveAsync(StoragePath, expired);
        await Task.Delay(100);
        signer.Release("https://signed/part.glb");
        var results = await Task.WhenAll(firstRequest, secondRequest).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, result => Assert.Equal("ready", result.Availability));
        Assert.Equal(1, signer.Attempts);
        Assert.All(results, result => Assert.Equal("https://signed/part.glb", result.Status.GlbUrl));
    }

    [Fact]
    public async Task PreviewResolver_SigningOutage_BackoffIsSharedAcrossReplicas()
    {
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var firstStore = new RedisQuoteFileAnalysisStatusService(firstConnection);
        var secondStore = new RedisQuoteFileAnalysisStatusService(secondConnection);
        var viewerPath = StoragePath + "_viewer.glb";
        await firstStore.SetGlbReadyAsync(
            StoragePath, "https://expired/part.glb", null, 1, true,
            viewerStoragePath: viewerPath, viewerFileExtension: ".glb", fileId: FileId);
        await firstConnection.GetDatabase().KeyDeleteAsync(
            RedisQuoteFileAnalysisStatusService.BuildPreviewKey(StoragePath));
        var expired = Assert.IsType<QuoteFileAnalysisStatus>(await firstStore.GetStatusAsync(StoragePath));
        var signer = new FailingPreviewSigner();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        var firstResolver = new QuoteAnalysisPreviewUrlResolver(
            firstStore, signer, firstConnection, TimeProvider.System, lifetime,
            NullLogger<QuoteAnalysisPreviewUrlResolver>.Instance);
        var secondResolver = new QuoteAnalysisPreviewUrlResolver(
            secondStore, signer, secondConnection, TimeProvider.System, lifetime,
            NullLogger<QuoteAnalysisPreviewUrlResolver>.Instance);

        var first = await firstResolver.ResolveAsync(StoragePath, expired);
        var second = await secondResolver.ResolveAsync(StoragePath, expired);

        Assert.Equal("temporarily_unavailable", first.Availability);
        Assert.Equal("temporarily_unavailable", second.Availability);
        Assert.Equal(1, signer.Attempts);
        Assert.True(await firstConnection.GetDatabase().KeyExistsAsync(
            RedisQuoteFileAnalysisStatusService.BuildPreviewRefreshFailureKey(StoragePath)));
    }

    [Fact]
    public async Task PreviewResolver_RedisDfmBeforeGeometry_DoesNotInventOrResignViewerSource()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var store = new RedisQuoteFileAnalysisStatusService(connection);
        await store.SetDfmReportsAsync(
            StoragePath, Fdm(), null, null, [], null, null,
            fileId: FileId, eventId: Guid.NewGuid());
        var status = Assert.IsType<QuoteFileAnalysisStatus>(await store.GetStatusAsync(StoragePath));
        var signer = new FailingPreviewSigner();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        var resolver = new QuoteAnalysisPreviewUrlResolver(
            store, signer, connection, TimeProvider.System, lifetime,
            NullLogger<QuoteAnalysisPreviewUrlResolver>.Instance);

        var first = await resolver.ResolveAsync(StoragePath, status);
        var second = await resolver.ResolveAsync(StoragePath, status);

        Assert.Equal("unavailable", first.Availability);
        Assert.Equal("unavailable", second.Availability);
        Assert.Null(status.ViewerStoragePath);
        Assert.Equal(0, signer.Attempts);
    }

    [Fact]
    public async Task PreviewResolver_LeaseWaitRejectsChangedInternalStorageIdentityBeforeSigning()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var store = new RedisQuoteFileAnalysisStatusService(connection);
        var viewerPath = StoragePath + "_viewer.glb";
        await store.SetGlbReadyAsync(
            StoragePath, "https://expired/part.glb", null, 1, true,
            viewerStoragePath: viewerPath, viewerFileExtension: ".glb", fileId: FileId);
        var database = connection.GetDatabase();
        await database.KeyDeleteAsync(RedisQuoteFileAnalysisStatusService.BuildPreviewKey(StoragePath));
        var expired = Assert.IsType<QuoteFileAnalysisStatus>(await store.GetStatusAsync(StoragePath));
        const string leaseToken = "test-held-lease";
        Assert.True(await database.LockTakeAsync(
            RedisQuoteFileAnalysisStatusService.BuildPreviewRefreshLockKey(StoragePath),
            leaseToken,
            TimeSpan.FromMinutes(1)));
        var signer = new FailingPreviewSigner();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        var resolver = new QuoteAnalysisPreviewUrlResolver(
            store, signer, connection, TimeProvider.System, lifetime,
            NullLogger<QuoteAnalysisPreviewUrlResolver>.Instance);

        var pending = resolver.ResolveAsync(StoragePath, expired);
        await Task.Delay(100);
        var durableKey = RedisQuoteFileAnalysisStatusService.BuildDurableKey(StoragePath);
        var durable = JsonNode.Parse((await database.StringGetAsync(durableKey)).ToString());
        Assert.NotNull(durable);
        durable["status"]!["storagePath"] = "quotes/other/customer/private.step";
        await database.StringSetAsync(durableKey, durable.ToJsonString());
        Assert.True(await database.LockReleaseAsync(
            RedisQuoteFileAnalysisStatusService.BuildPreviewRefreshLockKey(StoragePath),
            leaseToken));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("temporarily_unavailable", result.Availability);
        Assert.Null(result.Status.ViewerStoragePath);
        Assert.Equal(0, signer.Attempts);
    }

    [Fact]
    public async Task DfmWithoutNewOverlays_DoesNotExtendExistingOverlayExpiry()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var service = new RedisQuoteFileAnalysisStatusService(connection);
        await service.SetDfmReportsAsync(
            StoragePath, Fdm(), null, null, ["https://signed/overlay.glb"], null, null,
            fileId: FileId, eventId: Guid.NewGuid());
        var database = connection.GetDatabase();
        var overlayKey = RedisQuoteFileAnalysisStatusService.BuildOverlayKey(StoragePath);
        Assert.True(await database.KeyExpireAsync(overlayKey, TimeSpan.FromMinutes(5)));

        await service.SetDfmReportsAsync(
            StoragePath, null, null, Cnc(), [], null, null,
            fileId: FileId, eventId: Guid.NewGuid());

        var ttl = await database.KeyTimeToLiveAsync(overlayKey);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
        var result = await service.GetStatusAsync(StoragePath);
        Assert.Equal(["https://signed/overlay.glb"], result?.OverlayGlbUrls);
    }

    [Fact]
    public async Task GetStatus_WhenPreviewExpires_ReturnsDurableStateWithoutSignedUrls()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var service = new RedisQuoteFileAnalysisStatusService(connection);
        await service.SetGlbReadyAsync(
            StoragePath, "https://signed/part.glb", "https://signed/part.png", 1, true,
            volumeCc: 12.5m, fileId: FileId, eventId: Guid.NewGuid(),
            occurredAtUtc: BaseTime, processedAtUtc: BaseTime,
            viewerStoragePath: StoragePath + "_viewer.glb",
            thumbnailStoragePath: StoragePath + "_thumbnail_small.webp");
        Assert.True(await connection.GetDatabase().KeyDeleteAsync(
            RedisQuoteFileAnalysisStatusService.BuildPreviewKey(StoragePath)));

        var result = await service.GetStatusAsync(StoragePath);

        Assert.NotNull(result);
        Assert.Equal("GlbReady", result.Status);
        Assert.Equal(12.5m, result.VolumeCc);
        Assert.Null(result.GlbUrl);
        Assert.Null(result.ThumbnailUrl);
        Assert.Equal(StoragePath + "_viewer.glb", result.ViewerStoragePath);
        Assert.Equal(StoragePath + "_thumbnail_small.webp", result.ThumbnailStoragePath);
    }

    [Fact]
    public async Task GetStatus_WhenOverlayPreviewExpires_PreservesAuthoritativeSourcePathsOnly()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var service = new RedisQuoteFileAnalysisStatusService(connection);
        await service.SetDfmReportsAsync(
            StoragePath, Fdm(), null, null, ["https://signed/overlay.glb"], null, null,
            fileId: FileId, eventId: Guid.NewGuid(),
            overlayStoragePaths: [StoragePath + "_thin_wall_overlay.glb"]);
        Assert.True(await connection.GetDatabase().KeyDeleteAsync(
            RedisQuoteFileAnalysisStatusService.BuildOverlayKey(StoragePath)));

        var result = await service.GetStatusAsync(StoragePath);

        Assert.NotNull(result);
        Assert.Empty(result.OverlayGlbUrls);
        Assert.Equal(
            [StoragePath + "_thin_wall_overlay.glb"],
            result.AuthoritativeOverlayStoragePaths);
        Assert.Empty(result.AdvisoryAnalysis?.OverlayGlbUrls ?? []);
    }

    [Fact]
    public async Task ThumbnailOnlyPreview_RoundTripsWhenViewerUrlIsEmpty()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var service = new RedisQuoteFileAnalysisStatusService(connection);
        await service.SetGlbReadyAsync(
            StoragePath, "", "https://signed/part.png", 1, true,
            fileId: FileId, eventId: Guid.NewGuid(), occurredAtUtc: BaseTime,
            processedAtUtc: BaseTime);

        var result = await service.GetStatusAsync(StoragePath);

        Assert.NotNull(result);
        Assert.Equal("", result.GlbUrl);
        Assert.Equal("https://signed/part.png", result.ThumbnailUrl);
    }

    [Fact]
    public async Task GetStatus_WhenDurableStateIsMissing_IgnoresOrphanPreview()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var service = new RedisQuoteFileAnalysisStatusService(connection);
        await service.SetGlbReadyAsync(
            StoragePath, "https://signed/part.glb", null, 1, true,
            fileId: FileId, eventId: Guid.NewGuid(), occurredAtUtc: BaseTime,
            processedAtUtc: BaseTime);
        Assert.True(await connection.GetDatabase().KeyDeleteAsync(
            RedisQuoteFileAnalysisStatusService.BuildDurableKey(StoragePath)));

        Assert.Null(await service.GetStatusAsync(StoragePath));
    }

    [Fact]
    public async Task GetStatus_WhenPreviewRevisionIsStale_DoesNotMergeSignedUrls()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var service = new RedisQuoteFileAnalysisStatusService(connection);
        await service.SetGlbReadyAsync(
            StoragePath, "https://signed/part.glb", null, 1, true,
            fileId: FileId, eventId: Guid.NewGuid(), occurredAtUtc: BaseTime,
            processedAtUtc: BaseTime);
        var database = connection.GetDatabase();
        var previewKey = RedisQuoteFileAnalysisStatusService.BuildPreviewKey(StoragePath);
        var preview = JsonNode.Parse((await database.StringGetAsync(previewKey)).ToString())!.AsObject();
        preview["generation"] = Guid.Empty;
        Assert.True(await database.StringSetAsync(previewKey, preview.ToJsonString(), TimeSpan.FromMinutes(55)));

        var result = await service.GetStatusAsync(StoragePath);

        Assert.NotNull(result);
        Assert.Null(result.GlbUrl);
    }

    [Fact]
    public async Task DuplicateNoOp_DoesNotRefreshDurableTtl()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var service = new RedisQuoteFileAnalysisStatusService(connection);
        var eventId = Guid.NewGuid();
        await service.SetGeometryMetricsAsync(
            StoragePath, FileId, 12.5m, 1.25m, 42.5m, 10m, 20m, 30m, 456, 1, true, null,
            eventId, BaseTime, BaseTime);
        var database = connection.GetDatabase();
        var durableKey = RedisQuoteFileAnalysisStatusService.BuildDurableKey(StoragePath);
        Assert.True(await database.KeyExpireAsync(durableKey, TimeSpan.FromMinutes(10)));

        await service.SetGeometryMetricsAsync(
            StoragePath, FileId, 99m, 9m, 99m, 99m, 99m, 99m, 999, 9, false, "duplicate",
            eventId, BaseTime.AddMinutes(1), BaseTime.AddMinutes(1));

        var ttl = await database.KeyTimeToLiveAsync(durableKey);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task GetStatusAndCanApply_DoNotRefreshDurableTtl()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var service = new RedisQuoteFileAnalysisStatusService(connection);
        await service.SetProcessingAsync(StoragePath);
        var database = connection.GetDatabase();
        var durableKey = RedisQuoteFileAnalysisStatusService.BuildDurableKey(StoragePath);
        Assert.True(await database.KeyExpireAsync(durableKey, TimeSpan.FromMinutes(10)));

        Assert.NotNull(await service.GetStatusAsync(StoragePath));
        Assert.True(await service.CanApplyGeometryEventAsync(
            StoragePath,
            FileId,
            Guid.NewGuid(),
            BaseTime,
            BaseTime,
            QuoteGeometryEventPhase.Metrics));

        var ttl = await database.KeyTimeToLiveAsync(durableKey);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task CorruptEphemeralJson_IsIgnoredWhileDurableStateRemainsAvailable()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var service = new RedisQuoteFileAnalysisStatusService(connection);
        await service.SetGlbReadyAsync(
            StoragePath, "https://signed/part.glb", null, 1, true,
            fileId: FileId, eventId: Guid.NewGuid(), occurredAtUtc: BaseTime,
            processedAtUtc: BaseTime);
        await service.SetDfmReportsAsync(
            StoragePath, Fdm(), null, null, ["https://signed/overlay.glb"], null, null,
            fileId: FileId, eventId: Guid.NewGuid());
        var database = connection.GetDatabase();
        Assert.True(await database.StringSetAsync(
            RedisQuoteFileAnalysisStatusService.BuildPreviewKey(StoragePath), "{}", TimeSpan.FromMinutes(10)));
        Assert.True(await database.StringSetAsync(
            RedisQuoteFileAnalysisStatusService.BuildOverlayKey(StoragePath), "{}", TimeSpan.FromMinutes(10)));

        var result = await service.GetStatusAsync(StoragePath);

        Assert.NotNull(result);
        Assert.Equal("DfmAnalysisReady", result.Status);
        Assert.NotNull(result.FdmReport);
        Assert.Null(result.GlbUrl);
        Assert.Empty(result.OverlayGlbUrls);
    }

    [Fact]
    public async Task StructurallyInvalidDurableJson_FailsClosedWithStorageError()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var service = new RedisQuoteFileAnalysisStatusService(connection);
        await service.SetProcessingAsync(StoragePath);
        Assert.True(await connection.GetDatabase().StringSetAsync(
            RedisQuoteFileAnalysisStatusService.BuildDurableKey(StoragePath),
            "{}",
            TimeSpan.FromMinutes(10)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetStatusAsync(StoragePath));

        Assert.Contains("durable analysis status value is invalid", exception.Message, StringComparison.Ordinal);
        Assert.IsType<System.Text.Json.JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task InjectedClock_StopsServingAbsolutelyExpiredPreviewWithoutWaitingForRedisExpiry()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var clock = new MutableTimeProvider(BaseTime);
        var service = new RedisQuoteFileAnalysisStatusService(
            connection,
            clock,
            new RedisQuoteFileAnalysisStatusOptions
            {
                DurableLifetime = TimeSpan.FromHours(2),
                EphemeralLifetime = TimeSpan.FromMinutes(5)
            });
        await service.SetGlbReadyAsync(
            StoragePath, "https://signed/part.glb", null, 1, true,
            fileId: FileId, eventId: Guid.NewGuid(), occurredAtUtc: BaseTime,
            processedAtUtc: BaseTime);
        clock.Advance(TimeSpan.FromMinutes(6));

        var result = await service.GetStatusAsync(StoragePath);

        Assert.NotNull(result);
        Assert.Null(result.GlbUrl);
        Assert.True(await connection.GetDatabase().KeyExistsAsync(
            RedisQuoteFileAnalysisStatusService.BuildPreviewKey(StoragePath)));
    }

    [Fact]
    public async Task Constructor_RejectsEphemeralLifetimeAboveSignedUrlCeiling()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());

        Assert.Throws<ArgumentOutOfRangeException>(() => new RedisQuoteFileAnalysisStatusService(
            connection,
            TimeProvider.System,
            new RedisQuoteFileAnalysisStatusOptions
            {
                DurableLifetime = TimeSpan.FromHours(2),
                EphemeralLifetime = TimeSpan.FromMinutes(56)
            }));
    }

    [Fact]
    public void StorageKey_PreservesIdentitySupportsUnicodeAndSharesClusterSlot()
    {
        var durableKey = RedisQuoteFileAnalysisStatusService.BuildDurableKey(StoragePath).ToString();
        var previewKey = RedisQuoteFileAnalysisStatusService.BuildPreviewKey(StoragePath).ToString();
        var overlayKey = RedisQuoteFileAnalysisStatusService.BuildOverlayKey(StoragePath).ToString();
        var eventId = Guid.NewGuid();
        var claimKey = RedisQuoteFileAnalysisStatusService.BuildClaimKey(
            StoragePath, QuoteAnalysisEventLane.Geometry).ToString();
        var receiptKey = RedisQuoteFileAnalysisStatusService.BuildReceiptKey(
            StoragePath, QuoteAnalysisEventLane.Geometry, eventId).ToString();
        var processedKey = RedisQuoteFileAnalysisStatusService.BuildProcessedEventSetKey(
            StoragePath, QuoteAnalysisEventLane.Geometry).ToString();
        Assert.NotEqual(
            durableKey,
            RedisQuoteFileAnalysisStatusService.BuildDurableKey($" {StoragePath}").ToString());
        Assert.NotEqual(
            durableKey,
            RedisQuoteFileAnalysisStatusService.BuildDurableKey(StoragePath.Replace('/', '\\')).ToString());
        Assert.Equal(
            RedisQuoteFileAnalysisStatusService.BuildDurableKey("quotes/Σ.step"),
            RedisQuoteFileAnalysisStatusService.BuildDurableKey("QUOTES/σ.STEP"));
        Assert.NotEqual(
            RedisQuoteFileAnalysisStatusService.BuildDurableKey("quotes/café.step"),
            RedisQuoteFileAnalysisStatusService.BuildDurableKey("quotes/cafe\u0301.step"));
        Assert.Contains(ExtractHashTag(durableKey), previewKey, StringComparison.Ordinal);
        Assert.Contains(ExtractHashTag(durableKey), overlayKey, StringComparison.Ordinal);
        Assert.Contains(ExtractHashTag(durableKey), claimKey, StringComparison.Ordinal);
        Assert.Contains(ExtractHashTag(durableKey), receiptKey, StringComparison.Ordinal);
        Assert.Contains(ExtractHashTag(durableKey), processedKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoInstances_ThaiStoragePath_RoundTripsAcrossCaseInsensitivePrefix()
    {
        const string thaiPath = "quotes/temp/ชิ้นงาน.step";
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var first = new RedisQuoteFileAnalysisStatusService(firstConnection);
        var second = new RedisQuoteFileAnalysisStatusService(secondConnection);
        await first.SetProcessingAsync(thaiPath);

        var result = await second.GetStatusAsync("QUOTES/TEMP/ชิ้นงาน.STEP");

        Assert.NotNull(result);
        Assert.Equal(thaiPath, result.StoragePath);
    }

    [Fact]
    public async Task AdvisoryOverlay_IsEphemeralAndMergedWithoutEnteringDurableJson()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var service = new RedisQuoteFileAnalysisStatusService(connection);
        await service.SetLocalDfmReportsAsync(
            StoragePath, Fdm(), null, null,
            ["blob:browser-overlay", "blob:browser-overlay"], "browser issue");
        var database = connection.GetDatabase();
        var durableJson = await database.StringGetAsync(
            RedisQuoteFileAnalysisStatusService.BuildDurableKey(StoragePath));

        Assert.DoesNotContain("blob:browser-overlay", durableJson.ToString(), StringComparison.Ordinal);
        var result = await service.GetStatusAsync(StoragePath);
        Assert.Equal(
            ["blob:browser-overlay", "blob:browser-overlay"],
            result?.AdvisoryAnalysis?.OverlayGlbUrls);
    }

    [Fact]
    public async Task GetStatus_PreCancelled_ThrowsWithoutReturningState()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var service = new RedisQuoteFileAnalysisStatusService(connection);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetStatusAsync(StoragePath, cancellation.Token));
    }

    private static QeFdmDfmReport Fdm() => new(1, 2, 3.5m, true, 4, []);

    private static QeCncDfmReport Cnc() => new(2, false, true, 3, false, false, false, []);

    private static string ExtractHashTag(string key)
    {
        var start = key.IndexOf('{', StringComparison.Ordinal);
        var end = key.IndexOf('}', start + 1);
        return key[start..(end + 1)];
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class GatedPreviewSigner()
        : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
    {
        private readonly TaskCompletionSource<string> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts { get; private set; }

        public void Release(string url) => _release.TrySetResult(url);

        public override async Task<string> GetDownloadUrlByPathAsync(
            string storagePath,
            int expirationMinutes = 60,
            CancellationToken ct = default)
        {
            Attempts++;
            Started.TrySetResult();
            return await _release.Task.WaitAsync(ct);
        }
    }

    private sealed class FailingPreviewSigner()
        : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
    {
        public int Attempts { get; private set; }

        public override Task<string> GetDownloadUrlByPathAsync(
            string storagePath,
            int expirationMinutes = 60,
            CancellationToken ct = default)
        {
            Attempts++;
            return Task.FromException<string>(new HttpRequestException("simulated signing outage"));
        }
    }
}

public sealed class QuoteFileAnalysisStatusRegistrationTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void AddQuoteFileAnalysisStatus_WithoutRedis_AllowsInMemoryOnlyInLocalEnvironments(
        string environmentName)
    {
        var services = CreateServices(environmentName);
        services.AddQuoteFileAnalysisStatus();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<QuoteFileAnalysisStatusService>(
            provider.GetRequiredService<IQuoteFileAnalysisStatusService>());
    }

    [Fact]
    public void AddQuoteFileAnalysisStatus_WithoutRedis_ProductionFailsClosed()
    {
        var services = CreateServices(Environments.Production);
        services.AddQuoteFileAnalysisStatus();

        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(
            provider.GetRequiredService<IQuoteFileAnalysisStatusService>);
        Assert.Contains("requires Redis", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddQuoteFileAnalysisStatus_WithRedis_SelectsRedisAdapter()
    {
        var services = CreateServices(Environments.Production);
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());
        services.AddQuoteFileAnalysisStatus();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<RedisQuoteFileAnalysisStatusService>(
            provider.GetRequiredService<IQuoteFileAnalysisStatusService>());
    }

    [Fact]
    public void Program_UsesEnvironmentSafeAnalysisStatusRegistration()
    {
        var program = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Maliev.QuoteEngine.Bff", "Program.cs"));

        Assert.Contains("builder.Services.AddQuoteFileAnalysisStatus();", program, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AddSingleton<IQuoteFileAnalysisStatusService, QuoteFileAnalysisStatusService>",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddScoped<IQuoteAnalysisPreviewUrlResolver, QuoteAnalysisPreviewUrlResolver>",
            program,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AddSingleton<IQuoteAnalysisPreviewUrlResolver, QuoteAnalysisPreviewUrlResolver>",
            program,
            StringComparison.Ordinal);
    }

    private static ServiceCollection CreateServices(string environmentName)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        return services;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Maliev.QuoteEngine.slnx")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the QuoteEngine repository root.");
    }
}
