using System.Collections.Concurrent;
using Maliev.QuoteEngine.Bff.Options;
using Microsoft.Extensions.Options;
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
            var incoming = new QuoteAgentConversationMapping(chatbotSessionId, customerId);
            _mappings.AddOrUpdate(
                quoteSessionId,
                incoming,
                (_, current) => MergeMapping(current, incoming));
        }

        return Task.CompletedTask;
    }

    private static QuoteAgentConversationMapping MergeMapping(
        QuoteAgentConversationMapping current,
        QuoteAgentConversationMapping incoming)
    {
        if (current.ChatbotSessionId != incoming.ChatbotSessionId ||
            (current.CustomerId.HasValue &&
             incoming.CustomerId.HasValue &&
             current.CustomerId != incoming.CustomerId))
        {
            throw new InvalidOperationException(
                "The quote session is already bound to a different ChatbotService conversation or customer.");
        }

        return current.CustomerId.HasValue ? current : incoming;
    }
}

internal sealed class RedisQuoteAgentConversationMap(
    IConnectionMultiplexer redis,
    ILogger<RedisQuoteAgentConversationMap> logger,
    IOptions<QuoteAgentRetentionOptions>? retentionOptions = null) : IQuoteAgentConversationMap
{
    private readonly QuoteAgentRetentionOptions _retention = retentionOptions?.Value ?? new QuoteAgentRetentionOptions();

    private const string GetAndRenewScript = """
        local current = redis.call('GET', KEYS[1])
        if not current then
            return nil
        end

        local desiredTtl = tonumber(ARGV[1])
        local decoded, mapping = pcall(cjson.decode, current)
        if decoded and type(mapping) == 'table' and
           mapping.CustomerId ~= nil and mapping.CustomerId ~= cjson.null and mapping.CustomerId ~= '' then
            desiredTtl = tonumber(ARGV[2])
        end

        local currentTtl = redis.call('PTTL', KEYS[1])
        if currentTtl == -1 or (currentTtl >= 0 and currentTtl < desiredTtl) then
            redis.call('PEXPIRE', KEYS[1], desiredTtl)
        end
        return current
        """;

    private const string StoreMappingScript = """
        local function renew(ttl)
            local currentTtl = redis.call('PTTL', KEYS[1])
            if currentTtl == -1 or (currentTtl >= 0 and currentTtl < ttl) then
                redis.call('PEXPIRE', KEYS[1], ttl)
            end
        end

        local current = redis.call('GET', KEYS[1])
        if not current then
            local desiredTtl = tonumber(ARGV[4])
            if ARGV[2] ~= '' then
                desiredTtl = tonumber(ARGV[5])
            end
            redis.call('SET', KEYS[1], ARGV[3], 'PX', desiredTtl)
            return 1
        end

        local currentChatbot = nil
        local currentCustomer = ''
        if current == ARGV[1] then
            currentChatbot = current
        else
            local decoded, mapping = pcall(cjson.decode, current)
            if not decoded then
                return -2
            end
            currentChatbot = mapping.ChatbotSessionId
            currentCustomer = mapping.CustomerId
            if currentCustomer == nil or currentCustomer == cjson.null then
                currentCustomer = ''
            end
        end

        if currentChatbot ~= ARGV[1] then
            return 0
        end
        if currentCustomer ~= '' and ARGV[2] ~= '' and currentCustomer ~= ARGV[2] then
            return 0
        end
        if currentCustomer ~= '' and ARGV[2] == '' then
            renew(tonumber(ARGV[5]))
            return 1
        end

        local desiredTtl = tonumber(ARGV[4])
        if currentCustomer ~= '' or ARGV[2] ~= '' then
            desiredTtl = tonumber(ARGV[5])
        end
        redis.call('SET', KEYS[1], ARGV[3], 'KEEPTTL')
        renew(desiredTtl)
        return 1
        """;

    public async Task<QuoteAgentConversationMapping?> GetMappingAsync(Guid quoteSessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (quoteSessionId == Guid.Empty)
        {
            return null;
        }

        var value = await redis.GetDatabase().ScriptEvaluateAsync(
            GetAndRenewScript,
            [BuildKey(quoteSessionId)],
            [ToMilliseconds(_retention.Anonymous), ToMilliseconds(_retention.Customer)]);
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

        var chatbotId = chatbotSessionId.ToString("D");
        var customer = customerId?.ToString("D") ?? string.Empty;
        var serialized = System.Text.Json.JsonSerializer.Serialize(new RedisConversationMapping(
            chatbotId,
            customerId?.ToString("D")));
        var result = (long)await redis.GetDatabase().ScriptEvaluateAsync(
            StoreMappingScript,
            [BuildKey(quoteSessionId)],
            [
                chatbotId,
                customer,
                serialized,
                ToMilliseconds(_retention.Anonymous),
                ToMilliseconds(_retention.Customer)
            ]);
        if (result == 0)
        {
            throw new InvalidOperationException(
                "The quote session is already bound to a different ChatbotService conversation or customer.");
        }

        if (result < 0)
        {
            throw new InvalidOperationException(
                "The existing QuoteEngine conversation mapping is malformed and cannot be replaced safely.");
        }
    }

    private static RedisKey BuildKey(Guid quoteSessionId)
    {
        return $"quote-agent:conversation-map:{quoteSessionId:D}";
    }

    private static long ToMilliseconds(TimeSpan retention) => checked((long)retention.TotalMilliseconds);

    private sealed record RedisConversationMapping(string ChatbotSessionId, string? CustomerId);
}
