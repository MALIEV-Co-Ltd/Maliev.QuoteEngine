using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Maliev.QuoteEngine.Bff.Security;

/// <summary>
/// Reads and writes the signed customer-assistant handoff cookie shared by Web and QuoteEngine.
/// </summary>
public sealed class CustomerAssistantHandoffCookie(IConfiguration configuration, IHostEnvironment hostEnvironment)
{
    /// <summary>Signed handoff cookie name.</summary>
    public const string CookieName = "maliev_customer_assistant_handoff";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads the signed handoff payload from the current request.
    /// </summary>
    public CustomerAssistantHandoffPayload? Read(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var separator = rawValue.LastIndexOf('.');
        if (separator <= 0 || separator == rawValue.Length - 1)
        {
            return null;
        }

        var encodedPayload = rawValue[..separator];
        var encodedSignature = rawValue[(separator + 1)..];
        if (!IsSignatureValid(encodedPayload, encodedSignature))
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlTextEncoder.Decode(encodedPayload));
            var payload = JsonSerializer.Deserialize<CustomerAssistantHandoffPayload>(json, JsonOptions);
            return payload is null || payload.ExpiresAt <= DateTimeOffset.UtcNow ? null : payload;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Appends a signed handoff cookie to the response.
    /// </summary>
    public void Append(
        HttpRequest request,
        HttpResponse response,
        Guid sessionId,
        string? userKey,
        string language,
        bool isAuthenticated)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new CustomerAssistantHandoffPayload(
            sessionId,
            string.IsNullOrWhiteSpace(userKey) ? null : userKey.Trim(),
            string.Equals(language, "th", StringComparison.OrdinalIgnoreCase) ? "th" : "en",
            isAuthenticated,
            now,
            now.AddDays(30));

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var encodedPayload = Base64UrlTextEncoder.Encode(Encoding.UTF8.GetBytes(json));
        var encodedSignature = Sign(encodedPayload);
        var domain = request.Host.Host.EndsWith(".maliev.com", StringComparison.OrdinalIgnoreCase)
            ? ".maliev.com"
            : null;

        response.Cookies.Append(CookieName, $"{encodedPayload}.{encodedSignature}", new CookieOptions
        {
            Domain = domain,
            HttpOnly = true,
            IsEssential = true,
            MaxAge = TimeSpan.FromDays(30),
            Path = "/",
            SameSite = SameSiteMode.Lax,
            Secure = request.IsHttps
        });
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
        var configured = configuration["CustomerAssistant:HandoffSigningKey"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (hostEnvironment.IsProduction())
            {
                throw new InvalidOperationException("CustomerAssistant:HandoffSigningKey must be configured in production.");
            }

            configured = "maliev-local-development-customer-assistant-handoff-key";
        }

        return Encoding.UTF8.GetBytes(configured);
    }
}

/// <summary>
/// Signed handoff cookie payload.
/// </summary>
public sealed record CustomerAssistantHandoffPayload(
    Guid SessionId,
    string? UserKey,
    string Language,
    bool IsAuthenticated,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);
