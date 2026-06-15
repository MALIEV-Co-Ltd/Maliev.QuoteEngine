using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using StackExchange.Redis;

namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>
/// Stores customer-scoped Google Drive connector tokens for Make Studio.
/// </summary>
public interface IGoogleDriveConnectorStore
{
    /// <summary>Returns whether the customer has an active Google Drive connection.</summary>
    bool IsConnected(Guid customerId);

    /// <summary>Gets the current Google Drive connection for the customer, when available.</summary>
    GoogleDriveConnection? Get(Guid customerId);

    /// <summary>Saves or replaces the Google Drive connection for the customer.</summary>
    void Save(GoogleDriveConnection connection);

    /// <summary>Removes the Google Drive connection for the customer.</summary>
    void Remove(Guid customerId);
}

/// <summary>
/// Customer-scoped Google Drive OAuth token snapshot.
/// </summary>
public sealed record GoogleDriveConnection(
    Guid CustomerId,
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt,
    string? Email,
    DateTimeOffset ConnectedAt);

/// <summary>
/// Process-local connector token store used until the durable connector service is introduced.
/// </summary>
public sealed class InMemoryGoogleDriveConnectorStore : IGoogleDriveConnectorStore
{
    private readonly ConcurrentDictionary<Guid, GoogleDriveConnection> _connections = new();

    public bool IsConnected(Guid customerId)
    {
        return _connections.TryGetValue(customerId, out var connection) &&
               connection.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1);
    }

    public GoogleDriveConnection? Get(Guid customerId)
    {
        return _connections.TryGetValue(customerId, out var connection) ? connection : null;
    }

    public void Save(GoogleDriveConnection connection)
    {
        _connections[connection.CustomerId] = connection;
    }

    public void Remove(Guid customerId)
    {
        _connections.TryRemove(customerId, out _);
    }
}

/// <summary>
/// Redis-backed customer connector token store encrypted with ASP.NET Data Protection.
/// </summary>
public sealed class RedisGoogleDriveConnectorStore(
    IConnectionMultiplexer redis,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<RedisGoogleDriveConnectorStore> logger) : IGoogleDriveConnectorStore
{
    private static readonly TimeSpan DefaultConnectionTtl = TimeSpan.FromDays(90);
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("quote-engine.google-drive.connection.v1");

    public bool IsConnected(Guid customerId)
    {
        var connection = Get(customerId);
        return connection is not null &&
               (connection.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1) ||
                !string.IsNullOrWhiteSpace(connection.RefreshToken));
    }

    public GoogleDriveConnection? Get(Guid customerId)
    {
        try
        {
            var value = redis.GetDatabase().StringGet(Key(customerId));
            if (value.IsNullOrEmpty)
            {
                return null;
            }

            var json = _protector.Unprotect(value!);
            return JsonSerializer.Deserialize<GoogleDriveConnection>(json);
        }
        catch (Exception ex) when (ex is JsonException or System.Security.Cryptography.CryptographicException or RedisException)
        {
            logger.LogWarning(ex, "Unable to read Google Drive connector state for customer {CustomerId}.", customerId);
            return null;
        }
    }

    public void Save(GoogleDriveConnection connection)
    {
        var json = JsonSerializer.Serialize(connection);
        var protectedJson = _protector.Protect(json);
        redis.GetDatabase().StringSet(
            Key(connection.CustomerId),
            protectedJson,
            string.IsNullOrWhiteSpace(connection.RefreshToken)
                ? TimeSpan.FromHours(12)
                : DefaultConnectionTtl);
    }

    public void Remove(Guid customerId)
    {
        redis.GetDatabase().KeyDelete(Key(customerId));
    }

    private static string Key(Guid customerId)
    {
        return $"quote:connectors:google-drive:{customerId:D}";
    }
}
