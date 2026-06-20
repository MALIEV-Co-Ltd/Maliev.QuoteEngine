using System.Collections.Concurrent;
using StackExchange.Redis;

namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>
/// Stores the mapping between a public QuoteEngine session and its downstream ChatbotService session.
/// </summary>
public interface IQuoteAgentConversationMap
{
    /// <summary>Gets the mapped ChatbotService session for a QuoteEngine session.</summary>
    Task<QuoteAgentConversationMapping?> GetMappingAsync(Guid quoteSessionId, CancellationToken cancellationToken);

    /// <summary>Stores the mapped ChatbotService session for a QuoteEngine session.</summary>
    Task StoreMappingAsync(
        Guid quoteSessionId,
        Guid chatbotSessionId,
        Guid? customerId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Durable link between a public QuoteEngine session and its downstream ChatbotService conversation.
/// </summary>
public sealed record QuoteAgentConversationMapping(Guid ChatbotSessionId, Guid? CustomerId);

internal sealed class InMemoryQuoteAgentConversationMap : IQuoteAgentConversationMap
{
    private readonly ConcurrentDictionary<Guid, QuoteAgentConversationMapping> _mappings = new();

    public Task<QuoteAgentConversationMapping?> GetMappingAsync(Guid quoteSessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_mappings.TryGetValue(quoteSessionId, out var mapping) ? mapping : null);
    }

    public Task StoreMappingAsync(
        Guid quoteSessionId,
        Guid chatbotSessionId,
        Guid? customerId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (quoteSessionId != Guid.Empty && chatbotSessionId != Guid.Empty)
        {
            _mappings[quoteSessionId] = new QuoteAgentConversationMapping(chatbotSessionId, customerId);
        }

        return Task.CompletedTask;
    }
}

internal sealed class RedisQuoteAgentConversationMap(
    IConnectionMultiplexer redis,
    ILogger<RedisQuoteAgentConversationMap> logger) : IQuoteAgentConversationMap
{
    private static readonly TimeSpan MappingTtl = TimeSpan.FromDays(2);

    public async Task<QuoteAgentConversationMapping?> GetMappingAsync(Guid quoteSessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (quoteSessionId == Guid.Empty)
        {
            return null;
        }

        var value = await redis.GetDatabase().StringGetAsync(BuildKey(quoteSessionId));
        var raw = value.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (Guid.TryParse(raw, out var legacyChatbotSessionId) && legacyChatbotSessionId != Guid.Empty)
        {
            return new QuoteAgentConversationMapping(legacyChatbotSessionId, null);
        }

        try
        {
            var mapping = System.Text.Json.JsonSerializer.Deserialize<RedisConversationMapping>(raw);
            return mapping is not null &&
                   Guid.TryParse(mapping.ChatbotSessionId, out var chatbotSessionId) &&
                   chatbotSessionId != Guid.Empty
                ? new QuoteAgentConversationMapping(
                    chatbotSessionId,
                    Guid.TryParse(mapping.CustomerId, out var customerId) && customerId != Guid.Empty
                        ? customerId
                        : null)
                : null;
        }
        catch (System.Text.Json.JsonException ex)
        {
            logger.LogWarning(
                ex,
                "Failed to parse QuoteEngine to ChatbotService session mapping for {QuoteSessionId}.",
                quoteSessionId);
            return null;
        }
    }

    public async Task StoreMappingAsync(
        Guid quoteSessionId,
        Guid chatbotSessionId,
        Guid? customerId,
        CancellationToken cancellationToken)
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
                System.Text.Json.JsonSerializer.Serialize(new RedisConversationMapping(
                    chatbotSessionId.ToString("D"),
                    customerId?.ToString("D"))),
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

    private sealed record RedisConversationMapping(string ChatbotSessionId, string? CustomerId);
}
