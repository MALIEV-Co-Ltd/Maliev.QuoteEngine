using System.Collections.Concurrent;
using Maliev.QuoteEngine.Bff.Options;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>Durable owner of one public QuoteEngine agent session.</summary>
internal sealed record QuoteAgentSessionOwner(Guid? CustomerId, Guid VisitorId)
{
    public bool IsValid => VisitorId != Guid.Empty;
}

/// <summary>Stores the browser principal that is allowed to access an agent session.</summary>
internal interface IQuoteAgentSessionOwnerStore
{
    Task<QuoteAgentSessionOwner?> GetAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<bool> TryCreateAsync(
        Guid sessionId,
        QuoteAgentSessionOwner owner,
        CancellationToken cancellationToken);

    Task<bool> TryPromoteAsync(
        Guid sessionId,
        Guid expectedVisitorId,
        Guid customerId,
        CancellationToken cancellationToken);
}

internal sealed class InMemoryQuoteAgentSessionOwnerStore : IQuoteAgentSessionOwnerStore
{
    private readonly ConcurrentDictionary<Guid, QuoteAgentSessionOwner> _owners = new();

    public Task<QuoteAgentSessionOwner?> GetAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_owners.TryGetValue(sessionId, out var owner) ? owner : null);
    }

    public Task<bool> TryCreateAsync(
        Guid sessionId,
        QuoteAgentSessionOwner owner,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            sessionId != Guid.Empty && owner.IsValid && _owners.TryAdd(sessionId, owner));
    }

    public Task<bool> TryPromoteAsync(
        Guid sessionId,
        Guid expectedVisitorId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessionId == Guid.Empty || expectedVisitorId == Guid.Empty || customerId == Guid.Empty)
        {
            return Task.FromResult(false);
        }

        while (_owners.TryGetValue(sessionId, out var current))
        {
            if (current.CustomerId.HasValue)
            {
                return Task.FromResult(
                    current.CustomerId == customerId && current.VisitorId == expectedVisitorId);
            }

            if (current.VisitorId != expectedVisitorId)
            {
                return Task.FromResult(false);
            }

            var promoted = current with { CustomerId = customerId };
            if (_owners.TryUpdate(sessionId, promoted, current))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }
}

internal sealed class RedisQuoteAgentSessionOwnerStore(
    IConnectionMultiplexer redis,
    IOptions<QuoteAgentRetentionOptions>? retentionOptions = null)
    : IQuoteAgentSessionOwnerStore
{
    private readonly QuoteAgentRetentionOptions _retention = retentionOptions?.Value ?? new QuoteAgentRetentionOptions();

    private const string GetAndRenewScript = """
        local current = redis.call('GET', KEYS[1])
        if not current then
            return nil
        end

        local desiredTtl = tonumber(ARGV[1])
        if string.sub(current, 1, 9) == 'customer|' then
            desiredTtl = tonumber(ARGV[2])
        end

        local currentTtl = redis.call('PTTL', KEYS[1])
        if currentTtl == -1 or (currentTtl >= 0 and currentTtl < desiredTtl) then
            redis.call('PEXPIRE', KEYS[1], desiredTtl)
        end
        return current
        """;

    private const string PromoteScript = """
        local function renew(ttl)
            local currentTtl = redis.call('PTTL', KEYS[1])
            if currentTtl == -1 or (currentTtl >= 0 and currentTtl < ttl) then
                redis.call('PEXPIRE', KEYS[1], ttl)
            end
        end

        local current = redis.call('GET', KEYS[1])
        if not current then
            return 0
        end
        if current == ARGV[2] then
            renew(tonumber(ARGV[3]))
            return 1
        end
        if current ~= ARGV[1] then
            return 0
        end
        redis.call('SET', KEYS[1], ARGV[2], 'KEEPTTL')
        renew(tonumber(ARGV[3]))
        return 1
        """;

    public async Task<QuoteAgentSessionOwner?> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessionId == Guid.Empty)
        {
            return null;
        }

        var value = await redis.GetDatabase().ScriptEvaluateAsync(
            GetAndRenewScript,
            [BuildKey(sessionId)],
            [ToMilliseconds(_retention.Anonymous), ToMilliseconds(_retention.Customer)]);
        var raw = value.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!TryParse(raw, out var owner))
        {
            throw new InvalidOperationException(
                $"Quote agent session owner '{sessionId:D}' contains an invalid persisted value.");
        }

        return owner;
    }

    public async Task<bool> TryCreateAsync(
        Guid sessionId,
        QuoteAgentSessionOwner owner,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return sessionId != Guid.Empty &&
               owner.IsValid &&
               await redis.GetDatabase().StringSetAsync(
                   BuildKey(sessionId),
                   Encode(owner),
                   owner.CustomerId.HasValue ? _retention.Customer : _retention.Anonymous,
                   when: When.NotExists);
    }

    public async Task<bool> TryPromoteAsync(
        Guid sessionId,
        Guid expectedVisitorId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessionId == Guid.Empty || expectedVisitorId == Guid.Empty || customerId == Guid.Empty)
        {
            return false;
        }

        var anonymousOwner = new QuoteAgentSessionOwner(null, expectedVisitorId);
        var customerOwner = new QuoteAgentSessionOwner(customerId, expectedVisitorId);
        var result = await redis.GetDatabase().ScriptEvaluateAsync(
            PromoteScript,
            [BuildKey(sessionId)],
            [Encode(anonymousOwner), Encode(customerOwner), ToMilliseconds(_retention.Customer)]);
        return (int)result == 1;
    }

    private static RedisKey BuildKey(Guid sessionId) => $"quote-agent:session-owner:{sessionId:D}";

    private static long ToMilliseconds(TimeSpan retention) => checked((long)retention.TotalMilliseconds);

    private static string Encode(QuoteAgentSessionOwner owner) => owner.CustomerId.HasValue
        ? $"customer|{owner.CustomerId.Value:D}|{owner.VisitorId:D}"
        : $"visitor|{owner.VisitorId:D}";

    private static bool TryParse(string? raw, out QuoteAgentSessionOwner owner)
    {
        owner = new QuoteAgentSessionOwner(null, Guid.Empty);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var parts = raw.Split('|');
        if (parts.Length == 2 &&
            parts[0].Equals("visitor", StringComparison.Ordinal) &&
            Guid.TryParse(parts[1], out var visitorId) &&
            visitorId != Guid.Empty)
        {
            owner = new QuoteAgentSessionOwner(null, visitorId);
            return true;
        }

        if (parts.Length == 3 &&
            parts[0].Equals("customer", StringComparison.Ordinal) &&
            Guid.TryParse(parts[1], out var customerId) &&
            customerId != Guid.Empty &&
            Guid.TryParse(parts[2], out visitorId) &&
            visitorId != Guid.Empty)
        {
            owner = new QuoteAgentSessionOwner(customerId, visitorId);
            return true;
        }

        return false;
    }
}
