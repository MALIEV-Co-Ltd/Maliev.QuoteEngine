using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Maliev.QuoteEngine.Bff.Security;

/// <summary>
/// Validates short-lived signed customer session handoff tokens from trusted MALIEV apps.
/// </summary>
public sealed class CustomerSessionHandoffToken(IConfiguration configuration, IHostEnvironment hostEnvironment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Attempts to read and verify a signed customer session token.</summary>
    public bool TryRead(string? token, out CustomerSessionHandoffPayload payload)
    {
        payload = default!;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.', 2);
        if (parts.Length != 2 || !SignatureMatches(parts[0], parts[1]))
        {
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlTextEncoder.Decode(parts[0]));
            var parsed = JsonSerializer.Deserialize<CustomerSessionHandoffPayload>(json, JsonOptions);
            if (parsed is null || parsed.CustomerId == Guid.Empty || parsed.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return false;
            }

            payload = parsed;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool SignatureMatches(string encodedPayload, string encodedSignature)
    {
        using var hmac = new HMACSHA256(GetSigningKey());
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(encodedPayload));
        byte[] actual;
        try
        {
            actual = Base64UrlTextEncoder.Decode(encodedSignature);
        }
        catch (FormatException)
        {
            return false;
        }

        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private byte[] GetSigningKey()
    {
        var configured = configuration["CustomerSession:HandoffSigningKey"]
            ?? configuration["CustomerAssistant:HandoffSigningKey"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (hostEnvironment.IsProduction())
            {
                throw new InvalidOperationException("CustomerSession:HandoffSigningKey must be configured in production.");
            }

            configured = "maliev-local-development-customer-session-handoff-key";
        }

        return Encoding.UTF8.GetBytes(configured);
    }
}

/// <summary>
/// Signed customer session payload passed from Web to QuoteEngine.
/// </summary>
public sealed record CustomerSessionHandoffPayload(
    Guid CustomerId,
    string? PrincipalId,
    string? Email,
    string? DisplayName,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);
