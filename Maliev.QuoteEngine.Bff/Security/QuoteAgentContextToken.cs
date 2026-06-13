using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Maliev.QuoteEngine.Bff.Security;

/// <summary>
/// Issues and verifies signed QuoteEngine agent context tokens for ChatbotService tool calls.
/// </summary>
public sealed class QuoteAgentContextToken(IConfiguration configuration, IHostEnvironment hostEnvironment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Creates a signed context token for a QuoteEngine agent turn.
    /// </summary>
    public string Create(Guid quoteSessionId, Guid chatbotSessionId, Guid? customerId)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new QuoteAgentContextTokenPayload(
            quoteSessionId,
            chatbotSessionId,
            customerId,
            now,
            now.AddMinutes(15));
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var encodedPayload = Base64UrlTextEncoder.Encode(Encoding.UTF8.GetBytes(json));
        var encodedSignature = Sign(encodedPayload);
        return $"{encodedPayload}.{encodedSignature}";
    }

    /// <summary>
    /// Attempts to verify and decode a signed context token.
    /// </summary>
    public bool TryRead(string? token, out QuoteAgentContext context)
    {
        context = new QuoteAgentContext(Guid.Empty, Guid.Empty, null);
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var separator = token.LastIndexOf('.');
        if (separator <= 0 || separator == token.Length - 1)
        {
            return false;
        }

        var encodedPayload = token[..separator];
        var encodedSignature = token[(separator + 1)..];
        if (!IsSignatureValid(encodedPayload, encodedSignature))
        {
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlTextEncoder.Decode(encodedPayload));
            var payload = JsonSerializer.Deserialize<QuoteAgentContextTokenPayload>(json, JsonOptions);
            if (payload is null ||
                payload.QuoteSessionId == Guid.Empty ||
                payload.ChatbotSessionId == Guid.Empty ||
                payload.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return false;
            }

            context = new QuoteAgentContext(payload.QuoteSessionId, payload.ChatbotSessionId, payload.CustomerId);
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

    private bool IsSignatureValid(string encodedPayload, string encodedSignature)
    {
        var expected = Sign(encodedPayload);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(encodedSignature);
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private string Sign(string encodedPayload)
    {
        using var hmac = new HMACSHA256(GetSigningKey());
        return Base64UrlTextEncoder.Encode(hmac.ComputeHash(Encoding.UTF8.GetBytes(encodedPayload)));
    }

    private byte[] GetSigningKey()
    {
        var configured = configuration["QuoteAgent:ContextSigningKey"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (hostEnvironment.IsProduction())
            {
                throw new InvalidOperationException("QuoteAgent:ContextSigningKey must be configured in production.");
            }

            configured = "maliev-local-development-quote-agent-context-key";
        }

        return Encoding.UTF8.GetBytes(configured);
    }
}

/// <summary>
/// Verified QuoteEngine agent context.
/// </summary>
public sealed record QuoteAgentContext(Guid QuoteSessionId, Guid ChatbotSessionId, Guid? CustomerId);

internal sealed record QuoteAgentContextTokenPayload(
    Guid QuoteSessionId,
    Guid ChatbotSessionId,
    Guid? CustomerId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);
