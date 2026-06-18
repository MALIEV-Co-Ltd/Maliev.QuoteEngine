using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Maliev.QuoteEngine.Bff.Security;

/// <summary>
/// Reads and issues a signed, HttpOnly anonymous-visitor cookie used to rate-limit unauthenticated
/// QuoteEngine traffic fairly (S1). The visitor id gives one user behind a shared NAT/corporate IP
/// their own budget instead of sharing the IP's; it is NOT an abuse barrier on its own (a client that
/// drops the cookie simply falls back to IP-based limiting), so reading and issuing are deliberately
/// separate: rate limiting keys on the id from a <em>valid inbound cookie only</em>, never on one minted
/// for the current request.
/// </summary>
public sealed class AnonymousVisitorCookie(IConfiguration configuration, IHostEnvironment hostEnvironment)
{
    /// <summary>Signed anonymous-visitor cookie name.</summary>
    public const string CookieName = "maliev_qe_visitor";

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Returns the visitor id from a valid, unexpired inbound cookie, or null when there is none. Pure
    /// read — never mints. Use this (not <see cref="IssueIfMissing"/>) to derive a rate-limit key.
    /// </summary>
    public Guid? ReadVisitorId(HttpRequest request)
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
            var payload = JsonSerializer.Deserialize<AnonymousVisitorPayload>(json, JsonOptions);
            if (payload is null || payload.VisitorId == Guid.Empty || payload.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return null;
            }

            return payload.VisitorId;
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
    /// Issues a fresh signed visitor cookie on the response when the request does not already carry a
    /// valid one. The new id is for <em>future</em> requests' fairness; it deliberately does not affect
    /// how the current request is rate-limited (that request has no valid inbound cookie, so it is
    /// IP-limited). A client that ignores the Set-Cookie simply stays on the IP partition.
    /// </summary>
    public void IssueIfMissing(HttpRequest request, HttpResponse response)
    {
        if (ReadVisitorId(request) is not null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var payload = new AnonymousVisitorPayload(Guid.NewGuid(), now, now.Add(Lifetime));
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var encodedPayload = Base64UrlTextEncoder.Encode(Encoding.UTF8.GetBytes(json));
        var encodedSignature = Sign(encodedPayload);

        response.Cookies.Append(CookieName, $"{encodedPayload}.{encodedSignature}", new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            MaxAge = Lifetime,
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
        var configured = configuration["AnonymousVisitor:SigningKey"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (hostEnvironment.IsProduction())
            {
                throw new InvalidOperationException("AnonymousVisitor:SigningKey must be configured in production.");
            }

            configured = "maliev-local-development-anonymous-visitor-key";
        }

        return Encoding.UTF8.GetBytes(configured);
    }
}

/// <summary>Signed anonymous-visitor cookie payload.</summary>
public sealed record AnonymousVisitorPayload(Guid VisitorId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);
