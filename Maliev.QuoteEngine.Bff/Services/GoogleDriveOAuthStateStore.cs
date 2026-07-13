using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using StackExchange.Redis;

namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>Shared browser-cookie metadata for the Google Drive OAuth transaction.</summary>
public static class GoogleDriveOAuthFlow
{
    /// <summary>HttpOnly browser-binding cookie deleted whenever the customer session changes.</summary>
    public const string CookieName = "__Host-maliev_qe_google_drive_flow";

    /// <summary>Uses the host-wide path required by the <c>__Host-</c> cookie prefix.</summary>
    public const string CookiePath = "/";
}

/// <summary>
/// Stores one-time Google Drive OAuth authorization state until the provider callback completes.
/// </summary>
public interface IGoogleDriveOAuthStateStore
{
    /// <summary>Stores a pending authorization flow.</summary>
    Task SaveAsync(GoogleDriveOAuthState state, CancellationToken cancellationToken = default);

    /// <summary>Atomically removes and returns a pending authorization flow.</summary>
    Task<GoogleDriveOAuthState?> ConsumeAsync(
        string nonce,
        Guid customerId,
        string principalId,
        string browserSecretHash,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Server-only Google Drive OAuth flow state. The PKCE verifier and browser binding never leave the BFF.
/// </summary>
public sealed record GoogleDriveOAuthState(
    string Nonce,
    Guid CustomerId,
    string PrincipalId,
    string BrowserSecretHash,
    string CodeVerifier,
    string RedirectUri,
    string ReturnUrl,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Process-local one-time state store for Development and Testing only.
/// </summary>
public sealed class InMemoryGoogleDriveOAuthStateStore : IGoogleDriveOAuthStateStore
{
    private readonly ConcurrentDictionary<string, GoogleDriveOAuthState> _states = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveAsync(GoogleDriveOAuthState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_states.TryAdd(
                Key(state.Nonce, state.CustomerId, state.PrincipalId, state.BrowserSecretHash),
                state))
        {
            throw new InvalidOperationException("Google Drive OAuth state nonce already exists.");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<GoogleDriveOAuthState?> ConsumeAsync(
        string nonce,
        Guid customerId,
        string principalId,
        string browserSecretHash,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _states.TryRemove(Key(nonce, customerId, principalId, browserSecretHash), out var state)
                ? state
                : null);
    }

    private static string Key(string nonce, Guid customerId, string principalId, string browserSecretHash) =>
        GoogleDriveOAuthStateKey.Create(nonce, customerId, principalId, browserSecretHash);
}

/// <summary>
/// Redis-backed one-time OAuth state store. Values are encrypted because they contain PKCE verifiers.
/// </summary>
public sealed class RedisGoogleDriveOAuthStateStore(
    IConnectionMultiplexer redis,
    IDataProtectionProvider dataProtectionProvider,
    TimeProvider timeProvider,
    ILogger<RedisGoogleDriveOAuthStateStore> logger) : IGoogleDriveOAuthStateStore
{
    private const string ConsumeScript = "local value = redis.call('GET', KEYS[1]); " +
        "if value then redis.call('DEL', KEYS[1]); end; return value";
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(
        "quote-engine.google-drive.oauth-state.v1");

    /// <inheritdoc />
    public async Task SaveAsync(GoogleDriveOAuthState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ttl = state.ExpiresAt - timeProvider.GetUtcNow();
        if (ttl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Google Drive OAuth state must expire in the future.");
        }

        var protectedState = _protector.Protect(JsonSerializer.Serialize(state));
        var stored = await redis.GetDatabase().StringSetAsync(
            Key(state.Nonce, state.CustomerId, state.PrincipalId, state.BrowserSecretHash),
            protectedState,
            ttl,
            When.NotExists);
        if (!stored)
        {
            throw new InvalidOperationException("Google Drive OAuth state nonce already exists.");
        }
    }

    /// <inheritdoc />
    public async Task<GoogleDriveOAuthState?> ConsumeAsync(
        string nonce,
        Guid customerId,
        string principalId,
        string browserSecretHash,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await redis.GetDatabase().ScriptEvaluateAsync(
                ConsumeScript,
                [Key(nonce, customerId, principalId, browserSecretHash)]);
            if (value.IsNull)
            {
                return null;
            }

            return JsonSerializer.Deserialize<GoogleDriveOAuthState>(_protector.Unprotect((string)value!));
        }
        catch (Exception ex) when (ex is JsonException or System.Security.Cryptography.CryptographicException or RedisException)
        {
            logger.LogWarning(ex, "Unable to consume Google Drive OAuth state.");
            return null;
        }
    }

    private static RedisKey Key(string nonce, Guid customerId, string principalId, string browserSecretHash) =>
        GoogleDriveOAuthStateKey.Create(nonce, customerId, principalId, browserSecretHash);
}

internal static class GoogleDriveOAuthStateKey
{
    public static string Create(string nonce, Guid customerId, string principalId, string browserSecretHash)
    {
        var binding = $"{customerId:D}\n{principalId}\n{browserSecretHash}";
        var bindingHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding)));
        return $"quote:oauth:google-drive:{nonce}:{bindingHash}";
    }
}
