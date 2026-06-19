using System.Collections.Concurrent;
using StackExchange.Redis;

namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>
/// Stores the mapping between a public QuoteEngine session and its downstream ChatbotService session.
/// </summary>
public interface IQuoteAgentConversationMap
{
    /// <summary>Gets the mapped ChatbotService session ID for a QuoteEngine session.</summary>
    Task<Guid?> GetChatbotSessionIdAsync(Guid quoteSessionId, CancellationToken cancellationToken);

    /// <summary>Stores the mapped ChatbotService session ID for a QuoteEngine session.</summary>
    Task StoreChatbotSessionIdAsync(Guid quoteSessionId, Guid chatbotSessionId, CancellationToken cancellationToken);
}

internal sealed class InMemoryQuoteAgentConversationMap : IQuoteAgentConversationMap
{
    private readonly ConcurrentDictionary<Guid, Guid> _mappings = new();

    public Task<Guid?> GetChatbotSessionIdAsync(Guid quoteSessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_mappings.TryGetValue(quoteSessionId, out var chatbotSessionId)
            ? chatbotSessionId
            : (Guid?)null);
    }

    public Task StoreChatbotSessionIdAsync(Guid quoteSessionId, Guid chatbotSessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (quoteSessionId != Guid.Empty && chatbotSessionId != Guid.Empty)
        {
            _mappings[quoteSessionId] = chatbotSessionId;
        }

        return Task.CompletedTask;
    }
}

internal sealed class RedisQuoteAgentConversationMap(
    IConnectionMultiplexer redis,
    ILogger<RedisQuoteAgentConversationMap> logger) : IQuoteAgentConversationMap
{
    private static readonly TimeSpan MappingTtl = TimeSpan.FromDays(2);

    public async Task<Guid?> GetChatbotSessionIdAsync(Guid quoteSessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (quoteSessionId == Guid.Empty)
        {
            return null;
        }

        var value = await redis.GetDatabase().StringGetAsync(BuildKey(quoteSessionId));
        return Guid.TryParse(value.ToString(), out var chatbotSessionId) && chatbotSessionId != Guid.Empty
            ? chatbotSessionId
            : null;
    }

    public async Task StoreChatbotSessionIdAsync(Guid quoteSessionId, Guid chatbotSessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (quoteSessionId == Guid.Empty || chatbotSessionId == Guid.Empty)
        {
            return;
        }

        try
        {
            await redis.GetDatabase().StringSetAsync(
                BuildKey(quoteSessionId),
                chatbotSessionId.ToString("D"),
                MappingTtl);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(
                ex,
                "Failed to persist QuoteEngine to ChatbotService session mapping for {QuoteSessionId}.",
                quoteSessionId);
        }
    }

    private static RedisKey BuildKey(Guid quoteSessionId)
    {
        return $"quote-agent:conversation-map:{quoteSessionId:D}";
    }
}
