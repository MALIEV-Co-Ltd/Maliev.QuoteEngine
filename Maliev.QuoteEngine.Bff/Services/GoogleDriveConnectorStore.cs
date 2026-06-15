using System.Collections.Concurrent;

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
