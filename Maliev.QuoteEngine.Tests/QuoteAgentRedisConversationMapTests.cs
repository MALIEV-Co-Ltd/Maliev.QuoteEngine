using DotNet.Testcontainers.Configurations;
using Maliev.QuoteEngine.Bff.Services;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteAgentRedisConversationMapTests : IAsyncLifetime
{
    static QuoteAgentRedisConversationMapTests()
    {
        // This fixture always disposes its single container. Avoid a second Docker Hub dependency
        // for Ryuk so the contract test remains reliable on ephemeral CI runners.
        TestcontainersSettings.ResourceReaperEnabled = false;
    }

    private readonly RedisContainer _redis = new RedisBuilder(
        "redis:7.0-alpine@sha256:c9d92d840fd011c908f040592857c724ae6d877f2aba5c40ad963276507386b2")
        .Build();

    public Task InitializeAsync() => _redis.StartAsync();

    public Task DisposeAsync() => _redis.DisposeAsync().AsTask();

    [Fact]
    public async Task Anonymous_owner_creation_sets_thirty_day_retention()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var store = new RedisQuoteAgentSessionOwnerStore(connection);
        var quoteSessionId = Guid.NewGuid();
        var owner = new QuoteAgentSessionOwner(null, Guid.NewGuid());

        Assert.True(await store.TryCreateAsync(quoteSessionId, owner, CancellationToken.None));

        var timeToLive = await connection.GetDatabase().KeyTimeToLiveAsync(
            $"quote-agent:session-owner:{quoteSessionId:D}");
        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.FromDays(29), TimeSpan.FromDays(30));
    }

    [Fact]
    public async Task Anonymous_owner_read_renews_short_retention()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var store = new RedisQuoteAgentSessionOwnerStore(connection);
        var quoteSessionId = Guid.NewGuid();
        var owner = new QuoteAgentSessionOwner(null, Guid.NewGuid());
        var key = $"quote-agent:session-owner:{quoteSessionId:D}";
        var database = connection.GetDatabase();
        Assert.True(await store.TryCreateAsync(quoteSessionId, owner, CancellationToken.None));
        Assert.True(await database.KeyExpireAsync(key, TimeSpan.FromMinutes(1)));

        var resolved = await store.GetAsync(quoteSessionId, CancellationToken.None);

        Assert.Equal(owner, resolved);
        var timeToLive = await database.KeyTimeToLiveAsync(key);
        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.FromDays(29), TimeSpan.FromDays(30));
    }

    [Fact]
    public async Task Customer_owner_read_does_not_shorten_longer_retention()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var store = new RedisQuoteAgentSessionOwnerStore(connection);
        var quoteSessionId = Guid.NewGuid();
        var owner = new QuoteAgentSessionOwner(Guid.NewGuid(), Guid.NewGuid());
        var key = $"quote-agent:session-owner:{quoteSessionId:D}";
        var database = connection.GetDatabase();
        Assert.True(await store.TryCreateAsync(quoteSessionId, owner, CancellationToken.None));
        Assert.True(await database.KeyExpireAsync(key, TimeSpan.FromDays(400)));

        var resolved = await store.GetAsync(quoteSessionId, CancellationToken.None);

        Assert.Equal(owner, resolved);
        var timeToLive = await database.KeyTimeToLiveAsync(key);
        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.FromDays(399), TimeSpan.FromDays(400));
    }

    [Fact]
    public async Task Anonymous_owner_promotion_sets_customer_retention()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var store = new RedisQuoteAgentSessionOwnerStore(connection);
        var quoteSessionId = Guid.NewGuid();
        var visitorId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        Assert.True(await store.TryCreateAsync(
            quoteSessionId,
            new QuoteAgentSessionOwner(null, visitorId),
            CancellationToken.None));

        Assert.True(await store.TryPromoteAsync(
            quoteSessionId,
            visitorId,
            customerId,
            CancellationToken.None));

        var timeToLive = await connection.GetDatabase().KeyTimeToLiveAsync(
            $"quote-agent:session-owner:{quoteSessionId:D}");
        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.FromDays(364), TimeSpan.FromDays(365));
    }

    [Fact]
    public async Task Anonymous_mapping_creation_sets_thirty_day_retention()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var map = new RedisQuoteAgentConversationMap(
            connection,
            NullLogger<RedisQuoteAgentConversationMap>.Instance);
        var quoteSessionId = Guid.NewGuid();

        await map.StoreMappingAsync(
            quoteSessionId,
            Guid.NewGuid(),
            null,
            CancellationToken.None);

        var timeToLive = await connection.GetDatabase().KeyTimeToLiveAsync(
            $"quote-agent:conversation-map:{quoteSessionId:D}");
        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.FromDays(29), TimeSpan.FromDays(30));
    }

    [Fact]
    public async Task Legacy_mapping_read_preserves_parsing_and_sets_anonymous_retention()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var map = new RedisQuoteAgentConversationMap(
            connection,
            NullLogger<RedisQuoteAgentConversationMap>.Instance);
        var quoteSessionId = Guid.NewGuid();
        var chatbotSessionId = Guid.NewGuid();
        var key = $"quote-agent:conversation-map:{quoteSessionId:D}";
        var database = connection.GetDatabase();
        Assert.True(await database.StringSetAsync(key, chatbotSessionId.ToString("D")));

        var mapping = await map.GetMappingAsync(quoteSessionId, CancellationToken.None);

        Assert.NotNull(mapping);
        Assert.Equal(chatbotSessionId, mapping.ChatbotSessionId);
        Assert.Null(mapping.CustomerId);
        var timeToLive = await database.KeyTimeToLiveAsync(key);
        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.FromDays(29), TimeSpan.FromDays(30));
    }

    [Fact]
    public async Task Customer_mapping_read_does_not_shorten_longer_retention()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var map = new RedisQuoteAgentConversationMap(
            connection,
            NullLogger<RedisQuoteAgentConversationMap>.Instance);
        var quoteSessionId = Guid.NewGuid();
        var key = $"quote-agent:conversation-map:{quoteSessionId:D}";
        var database = connection.GetDatabase();
        await map.StoreMappingAsync(
            quoteSessionId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            CancellationToken.None);
        Assert.True(await database.KeyExpireAsync(key, TimeSpan.FromDays(400)));

        Assert.NotNull(await map.GetMappingAsync(quoteSessionId, CancellationToken.None));

        var timeToLive = await database.KeyTimeToLiveAsync(key);
        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.FromDays(399), TimeSpan.FromDays(400));
    }

    [Fact]
    public async Task Anonymous_mapping_promotes_to_customer_without_changing_chatbot_conversation()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var map = new RedisQuoteAgentConversationMap(
            connection,
            NullLogger<RedisQuoteAgentConversationMap>.Instance);
        var quoteSessionId = Guid.NewGuid();
        var chatbotSessionId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        await map.StoreMappingAsync(quoteSessionId, chatbotSessionId, null, CancellationToken.None);
        await map.StoreMappingAsync(quoteSessionId, chatbotSessionId, customerId, CancellationToken.None);
        var promoted = await map.GetMappingAsync(quoteSessionId, CancellationToken.None);

        Assert.NotNull(promoted);
        Assert.Equal(chatbotSessionId, promoted.ChatbotSessionId);
        Assert.Equal(customerId, promoted.CustomerId);
        var timeToLive = await connection.GetDatabase().KeyTimeToLiveAsync(
            $"quote-agent:conversation-map:{quoteSessionId:D}");
        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.FromDays(364), TimeSpan.FromDays(365));
    }
}
