using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.WebUtilities;

namespace Maliev.QuoteEngine.Bff.Security;

/// <summary>
/// Verifies signed upload handoff tokens issued by Maliev.Web.
/// </summary>
public sealed class QuoteUploadHandoffToken(IConfiguration configuration, IHostEnvironment hostEnvironment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Attempts to verify and decode a signed Web upload handoff token.
    /// </summary>
    public bool TryRead(string? token, out QuoteUploadHandoffRequest request)
    {
        request = new QuoteUploadHandoffRequest();
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
            var payload = JsonSerializer.Deserialize<QuoteUploadHandoffTokenPayload>(json, JsonOptions);
            if (payload is null || payload.ExpiresAt <= DateTimeOffset.UtcNow || payload.Files.Count == 0)
            {
                return false;
            }

            request = new QuoteUploadHandoffRequest
            {
                QuoteSessionId = payload.QuoteSessionId,
                Files = payload.Files
            };
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
        var configured = configuration["QuoteUploadHandoff:SigningKey"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (hostEnvironment.IsProduction())
            {
                throw new InvalidOperationException("QuoteUploadHandoff:SigningKey must be configured in production.");
            }

            configured = "maliev-local-development-quote-upload-handoff-key";
        }

        return Encoding.UTF8.GetBytes(configured);
    }
}

internal sealed record QuoteUploadHandoffTokenPayload(
    string QuoteSessionId,
    List<QuoteUploadHandoffFileDto> Files,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);
