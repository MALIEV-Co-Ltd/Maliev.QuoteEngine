using System.Text.Json;
using DotNet.Testcontainers.Configurations;
using Maliev.QuoteEngine.Bff.Options;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Maliev.QuoteEngine.Tests;

public sealed class InMemoryQuoteUploadStateStoreTests
{
    [Fact]
    public async Task InitiateAsync_AuthenticatedCustomerDoesNotRequireVisitorCookie()
    {
        var store = new InMemoryQuoteUploadStateStore();
        var customerId = Guid.NewGuid();

        var created = await store.InitiateAsync(
            new InitiateQuoteUploadRequest
            {
                FileName = "customer.step",
                ContentType = "model/step",
                FileSizeBytes = 100,
                QuoteSessionId = Guid.NewGuid().ToString("D")
            },
            customerId,
            visitorId: null,
            CancellationToken.None);

        Assert.Equal(customerId, created.CustomerId);
        Assert.Null(created.VisitorId);
        Assert.False(created.IsTemporary);
    }

    [Fact]
    public async Task InitiateAsync_SanitizesFileNameAndIndexesCanonicalPath()
    {
        var store = new InMemoryQuoteUploadStateStore();
        var sessionId = Guid.NewGuid();
        var visitorId = Guid.NewGuid();

        var created = await store.InitiateAsync(
            new InitiateQuoteUploadRequest
            {
                FileName = "../bracket.step",
                ContentType = "model/step",
                FileSizeBytes = 1234,
                QuoteSessionId = sessionId.ToString("D")
            },
            customerId: null,
            visitorId,
            CancellationToken.None);

        Assert.Equal("bracket.step", created.FileName);
        Assert.Equal($"quotes/temp/{sessionId:N}/{created.UploadId}/bracket.step", created.StoragePath);
        Assert.Equal(created, await store.GetAsync(created.UploadId, CancellationToken.None));
        Assert.Equal(created, await store.FindByStoragePathAsync(created.StoragePath, CancellationToken.None));
    }

    [Fact]
    public async Task PromoteSessionAsync_RequiresExpectedVisitorAndPromotesEverySessionUpload()
    {
        var store = new InMemoryQuoteUploadStateStore();
        var sessionId = Guid.NewGuid();
        var visitorId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var first = await InitiateAsync(store, sessionId, visitorId, "first.step");
        var second = await InitiateAsync(store, sessionId, visitorId, "second.step");

        Assert.False(await store.PromoteSessionAsync(
            sessionId,
            Guid.NewGuid(),
            customerId,
            CancellationToken.None));
        Assert.True(await store.PromoteSessionAsync(
            sessionId,
            visitorId,
            customerId,
            CancellationToken.None));

        Assert.Equal(customerId, (await store.GetAsync(first.UploadId, CancellationToken.None))?.CustomerId);
        Assert.Equal(customerId, (await store.GetAsync(second.UploadId, CancellationToken.None))?.CustomerId);
    }

    [Fact]
    public async Task TryAdvanceAsync_AllowsOnlyContiguousBoundedProgress()
    {
        var store = new InMemoryQuoteUploadStateStore();
        var created = await InitiateAsync(store, Guid.NewGuid(), Guid.NewGuid(), "part.step", 10);

        Assert.Null(await store.TryAdvanceAsync(created.UploadId, 1, 5, CancellationToken.None));
        Assert.Equal(5, (await store.TryAdvanceAsync(created.UploadId, 0, 5, CancellationToken.None))?.ReceivedBytes);
        Assert.Null(await store.TryAdvanceAsync(created.UploadId, 0, 6, CancellationToken.None));
        Assert.Equal("Uploaded", (await store.TryAdvanceAsync(created.UploadId, 5, 10, CancellationToken.None))?.Status);
        Assert.Null(await store.TryAdvanceAsync(created.UploadId, 10, 11, CancellationToken.None));
    }

    [Fact]
    public async Task ProcessingTransition_ReplacesProvisionalFileIdWithCanonicalFileId()
    {
        var store = new InMemoryQuoteUploadStateStore();
        var created = await InitiateAsync(store, Guid.NewGuid(), Guid.NewGuid(), "part.step");
        var canonicalFileId = Guid.NewGuid();

        var processing = await store.MarkProcessingAsync(
            created.UploadId,
            canonicalFileId,
            CancellationToken.None);

        Assert.Equal(canonicalFileId, processing.FileId);
        Assert.Equal("Processing", processing.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => store.MarkProcessingAsync(
            created.UploadId,
            Guid.Empty,
            CancellationToken.None));
    }

    [Fact]
    public async Task TrackAgentUploadAsync_PersistsCompletedUploadAndDownstreamBinding()
    {
        var store = new InMemoryQuoteUploadStateStore();
        var uploadId = Guid.NewGuid().ToString("N");
        var tracked = await store.TrackAgentUploadAsync(
            uploadId,
            Guid.NewGuid(),
            "agent.step",
            "model/step",
            120,
            $"quotes/temp/{Guid.NewGuid():N}/{uploadId}/agent.step",
            Guid.NewGuid(),
            customerId: null,
            visitorId: Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal("Uploaded", tracked.Status);
        Assert.Equal(120, tracked.ReceivedBytes);
        Assert.Equal(uploadId, tracked.DownstreamUploadId);

        var rebound = await store.AttachDownstreamAsync(
            uploadId,
            $"downstream-{Guid.NewGuid():N}",
            CancellationToken.None);
        Assert.StartsWith("downstream-", rebound.DownstreamUploadId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportHandoffAsync_RejectsNonCanonicalStoragePath()
    {
        var store = new InMemoryQuoteUploadStateStore();
        var request = new QuoteUploadHandoffRequest
        {
            QuoteSessionId = Guid.NewGuid().ToString("D"),
            Files =
            [
                new QuoteUploadHandoffFileDto
                {
                    UploadId = Guid.NewGuid().ToString("N"),
                    FileName = "part.step",
                    StoragePath = "../foreign/part.step",
                    ContentType = "model/step",
                    FileSizeBytes = 100
                }
            ]
        };

        await Assert.ThrowsAsync<ArgumentException>(() => store.ImportHandoffAsync(
            request,
            customerId: null,
            visitorId: Guid.NewGuid(),
            CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_CanceledToken_ThrowsWithoutReading()
    {
        var store = new InMemoryQuoteUploadStateStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.GetAsync(Guid.NewGuid().ToString("N"), cancellation.Token));
    }

    private static Task<UploadState> InitiateAsync(
        IQuoteUploadStateStore store,
        Guid sessionId,
        Guid visitorId,
        string fileName,
        long size = 100) =>
        store.InitiateAsync(
            new InitiateQuoteUploadRequest
            {
                FileName = fileName,
                ContentType = "model/step",
                FileSizeBytes = size,
                QuoteSessionId = sessionId.ToString("D")
            },
            customerId: null,
            visitorId,
            CancellationToken.None);
}

public sealed class RedisQuoteUploadStateStoreTests : IAsyncLifetime
{
    static RedisQuoteUploadStateStoreTests()
    {
        TestcontainersSettings.ResourceReaperEnabled = false;
    }

    private readonly RedisContainer _redis = new RedisBuilder(
        "redis:7.0-alpine@sha256:c9d92d840fd011c908f040592857c724ae6d877f2aba5c40ad963276507386b2")
        .Build();

    public Task InitializeAsync() => _redis.StartAsync();

    public Task DisposeAsync() => _redis.DisposeAsync().AsTask();

    [Fact]
    public async Task StateCreatedThroughOneConnection_IsReadableThroughAnotherConnectionAndPathIndex()
    {
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var first = CreateStore(firstConnection);
        var second = CreateStore(secondConnection);
        var sessionId = Guid.NewGuid();
        var visitorId = Guid.NewGuid();

        var created = await InitiateAsync(first, sessionId, visitorId, "cross-pod.step");

        var byId = await second.GetAsync(created.UploadId, CancellationToken.None);
        var byPath = await second.FindByStoragePathAsync(created.StoragePath, CancellationToken.None);
        Assert.NotNull(byId);
        Assert.NotNull(byPath);
        Assert.Equal(created.UploadId, byId.UploadId);
        Assert.Equal(created.StoragePath, byId.StoragePath);
        Assert.Equal(created.VisitorId, byId.VisitorId);
        Assert.Empty(byId.Findings);
        Assert.Equal(byId.UploadId, byPath.UploadId);
    }

    [Fact]
    public async Task CustomerOnlyState_IsReadableAcrossConnections()
    {
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var first = CreateStore(firstConnection);
        var second = CreateStore(secondConnection);
        var customerId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var created = await first.InitiateAsync(
            Request(sessionId, "customer-only.step"),
            customerId,
            visitorId: null,
            CancellationToken.None);

        var recovered = await second.GetAsync(created.UploadId, CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(customerId, recovered.CustomerId);
        Assert.Null(recovered.VisitorId);
        Assert.True(await second.PromoteSessionAsync(
            sessionId,
            Guid.NewGuid(),
            customerId,
            CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentTryAdvanceAsync_AcceptsOnlyOneWriterForExpectedOffset()
    {
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var first = CreateStore(firstConnection);
        var second = CreateStore(secondConnection);
        var created = await InitiateAsync(first, Guid.NewGuid(), Guid.NewGuid(), "part.step", 10);

        var attempts = await Task.WhenAll(
            first.TryAdvanceAsync(created.UploadId, 0, 4, CancellationToken.None),
            second.TryAdvanceAsync(created.UploadId, 0, 6, CancellationToken.None));

        var raw = await firstConnection.GetDatabase().StringGetAsync($"quote:upload-state:{created.UploadId}");
        Assert.True(
            attempts.Count(state => state is not null) == 1,
            $"Expected one successful CAS. Raw state: {raw}");
        var persisted = await first.GetAsync(created.UploadId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.True(persisted.ReceivedBytes is 4 or 6);
    }

    [Fact]
    public async Task AuthorizedMutation_ReconstructsMissingPathAndSessionIndexes()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var store = CreateStore(connection);
        var sessionId = Guid.NewGuid();
        var visitorId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var created = await InitiateAsync(store, sessionId, visitorId, "recover.step", 10);
        var database = connection.GetDatabase();
        Assert.True(await database.KeyDeleteAsync(BuildPathKey(created.StoragePath)));
        Assert.True(await database.KeyDeleteAsync($"quote:upload-session:{sessionId:D}"));

        var advanced = await store.TryAdvanceAsync(
            created.UploadId,
            expectedReceivedBytes: 0,
            receivedBytes: 5,
            CancellationToken.None);

        Assert.NotNull(advanced);
        Assert.Equal(created.UploadId, (await store.FindByStoragePathAsync(
            created.StoragePath,
            CancellationToken.None))?.UploadId);
        Assert.True(await store.PromoteSessionAsync(
            sessionId,
            visitorId,
            customerId,
            CancellationToken.None));
        Assert.Equal(customerId, (await store.GetAsync(created.UploadId, CancellationToken.None))?.CustomerId);
    }

    [Fact]
    public async Task PromoteSessionAsync_IsAtomicForExpectedVisitorAndRenewsCustomerRetention()
    {
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var first = CreateStore(firstConnection);
        var second = CreateStore(secondConnection);
        var sessionId = Guid.NewGuid();
        var visitorId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var competingCustomerId = Guid.NewGuid();
        var created = await InitiateAsync(first, sessionId, visitorId, "part.step");

        var attempts = await Task.WhenAll(
            first.PromoteSessionAsync(sessionId, visitorId, customerId, CancellationToken.None),
            second.PromoteSessionAsync(sessionId, visitorId, competingCustomerId, CancellationToken.None));

        Assert.Single(attempts, promoted => promoted);
        var expectedCustomerId = attempts[0] ? customerId : competingCustomerId;
        var promotedState = await first.GetAsync(created.UploadId, CancellationToken.None);
        var raw = await firstConnection.GetDatabase().StringGetAsync($"quote:upload-state:{created.UploadId}");
        Assert.True(
            promotedState?.CustomerId == expectedCustomerId,
            $"Expected customer {expectedCustomerId:D}. Raw state: {raw}");
        var ttl = await firstConnection.GetDatabase().KeyTimeToLiveAsync(
            $"quote:upload-state:{created.UploadId}");
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));
    }

    [Fact]
    public async Task PromoteSessionAsync_PrunesExpiredMemberAndPromotesSurvivingUpload()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var store = CreateStore(connection);
        var sessionId = Guid.NewGuid();
        var visitorId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var expired = await InitiateAsync(store, sessionId, visitorId, "expired.step");
        var surviving = await InitiateAsync(store, sessionId, visitorId, "surviving.step");
        var database = connection.GetDatabase();
        Assert.True(await database.KeyDeleteAsync($"quote:upload-state:{expired.UploadId}"));

        Assert.True(await store.PromoteSessionAsync(
            sessionId,
            visitorId,
            customerId,
            CancellationToken.None));

        Assert.Equal(customerId, (await store.GetAsync(surviving.UploadId, CancellationToken.None))?.CustomerId);
        Assert.False(await database.SetContainsAsync($"quote:upload-session:{sessionId:D}", expired.UploadId));
    }

    [Fact]
    public async Task GetAsync_DoesNotRenewUploadOrIndexRetention()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var store = CreateStore(connection);
        var created = await InitiateAsync(store, Guid.NewGuid(), Guid.NewGuid(), "bounded.step");
        var database = connection.GetDatabase();
        var stateKey = $"quote:upload-state:{created.UploadId}";
        Assert.True(await database.KeyExpireAsync(stateKey, TimeSpan.FromMinutes(1)));

        Assert.NotNull(await store.GetAsync(created.UploadId, CancellationToken.None));
        var ttl = await database.KeyTimeToLiveAsync(stateKey);

        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromSeconds(50), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task MalformedStateAndPoisonedPathIndex_FailClosed()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var store = CreateStore(connection);
        var database = connection.GetDatabase();
        var uploadId = Guid.NewGuid().ToString("N");
        var path = $"quotes/temp/{Guid.NewGuid():N}/{uploadId}/part.step";
        Assert.True(await database.StringSetAsync($"quote:upload-state:{uploadId}", "{not-json"));
        Assert.True(await database.StringSetAsync(BuildPathKey(path), uploadId));

        Assert.Null(await store.GetAsync(uploadId, CancellationToken.None));
        Assert.Null(await store.FindByStoragePathAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task PreviewCapabilities_AreNotPersisted()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var store = CreateStore(connection);
        var created = await store.TrackAgentUploadAsync(
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid(),
            "sample.step",
            "model/step",
            100,
            $"quotes/temp/{Guid.NewGuid():N}/{Guid.NewGuid():N}/sample.step",
            Guid.NewGuid(),
            customerId: null,
            visitorId: Guid.NewGuid(),
            CancellationToken.None);
        var analyzed = await store.MarkDemoAnalyzedAsync(
            created.UploadId,
            new DemoModeOptions
            {
                GlbUrl = "https://storage.example/signed-viewer?secret=viewer-token",
                ThumbnailUrl = "https://storage.example/signed-thumb?secret=thumb-token"
            },
            CancellationToken.None);

        Assert.Null(analyzed.ViewerGlbUrl);
        Assert.Null(analyzed.ThumbnailUrl);
        var raw = await connection.GetDatabase().StringGetAsync($"quote:upload-state:{created.UploadId}");
        Assert.DoesNotContain("https://", raw.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", raw.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExistingUploadIdAndPath_CannotBeReboundToAnotherSessionOrVisitor()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var store = CreateStore(connection);
        var uploadId = Guid.NewGuid().ToString("N");
        var originalSessionId = Guid.NewGuid();
        var originalVisitorId = Guid.NewGuid();
        var path = $"quotes/temp/{originalSessionId:N}/{uploadId}/part.step";
        var originalFileId = Guid.NewGuid();
        await store.TrackAgentUploadAsync(
            uploadId,
            originalFileId,
            "part.step",
            "model/step",
            100,
            path,
            originalSessionId,
            customerId: null,
            originalVisitorId,
            CancellationToken.None);

        var replay = await store.TrackAgentUploadAsync(
            uploadId,
            originalFileId,
            "part.step",
            "model/step",
            100,
            path,
            originalSessionId,
            customerId: null,
            originalVisitorId,
            CancellationToken.None);
        Assert.Equal(originalSessionId.ToString("D"), replay.QuoteSessionId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TrackAgentUploadAsync(
            uploadId,
            originalFileId,
            "part.step",
            "model/step",
            100,
            path,
            Guid.NewGuid(),
            customerId: null,
            visitorId: Guid.NewGuid(),
            CancellationToken.None));

        var persisted = await store.GetAsync(uploadId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(originalSessionId.ToString("D"), persisted.QuoteSessionId);
        Assert.Equal(originalVisitorId, persisted.VisitorId);
    }

    [Fact]
    public async Task AnonymousAndCustomerStateUseConfiguredRetention()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var store = CreateStore(connection);
        var anonymous = await InitiateAsync(store, Guid.NewGuid(), Guid.NewGuid(), "anonymous.step");
        var customer = await store.InitiateAsync(
            Request(Guid.NewGuid(), "customer.step"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            CancellationToken.None);

        var database = connection.GetDatabase();
        var anonymousTtl = await database.KeyTimeToLiveAsync($"quote:upload-state:{anonymous.UploadId}");
        var customerTtl = await database.KeyTimeToLiveAsync($"quote:upload-state:{customer.UploadId}");
        Assert.NotNull(anonymousTtl);
        Assert.NotNull(customerTtl);
        Assert.InRange(anonymousTtl.Value, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
        Assert.InRange(customerTtl.Value, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));
    }

    private static RedisQuoteUploadStateStore CreateStore(IConnectionMultiplexer connection) =>
        new(
            connection,
            Options.Create(new QuoteAgentRetentionOptions
            {
                Anonymous = TimeSpan.FromMinutes(10),
                Customer = TimeSpan.FromMinutes(20)
            }));

    private static Task<UploadState> InitiateAsync(
        IQuoteUploadStateStore store,
        Guid sessionId,
        Guid visitorId,
        string fileName,
        long size = 100) =>
        store.InitiateAsync(
            Request(sessionId, fileName, size),
            customerId: null,
            visitorId,
            CancellationToken.None);

    private static InitiateQuoteUploadRequest Request(Guid sessionId, string fileName, long size = 100) => new()
    {
        FileName = fileName,
        ContentType = "model/step",
        FileSizeBytes = size,
        QuoteSessionId = sessionId.ToString("D")
    };

    private static string BuildPathKey(string storagePath)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(storagePath));
        return $"quote:upload-path:{Convert.ToHexStringLower(hash)}";
    }
}
