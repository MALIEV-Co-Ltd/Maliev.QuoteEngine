using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Maliev.QuoteEngine.Bff.Security;

/// <summary>
/// Reads and issues a signed, HttpOnly anonymous-visitor cookie. A verified visitor id is the
/// anonymous browser principal used to own QuoteEngine agent sessions. It also gives one user behind
/// a shared NAT/corporate IP their own rate-limit budget. The cookie is not the abuse floor: a client
/// that drops it still falls back to IP limiting, so rate limiting continues to use valid inbound
/// cookies only while session creation may use the id minted for the current request.
/// </summary>
public sealed class AnonymousVisitorCookie(IConfiguration configuration, IHostEnvironment hostEnvironment)
{
    /// <summary>Signed anonymous-visitor cookie name.</summary>
    public const string CookieName = "maliev_qe_visitor";

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object RequestResolutionKey = new();

    /// <summary>
    /// Returns the visitor id from a valid, unexpired inbound cookie, or null when there is none. Pure
    /// read — never mints. Use this (not <see cref="IssueIfMissing"/>) to derive a rate-limit key.
    /// </summary>
    public Guid? ReadVisitorId(HttpRequest request)
    {
        return ReadVisitorCredential(request)?.VisitorId;
    }

    /// <summary>
    /// Returns the verified, unexpired visitor credential, including its expiry. This is read-only
    /// and is used by long-lived transports to end a connection when the browser capability expires.
    /// </summary>
    public AnonymousVisitorPayload? ReadVisitorCredential(HttpRequest request)
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

            return payload;
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
    /// Resolves the anonymous visitor for the current request. A request with no cookie receives a
    /// fresh signed identity that is immediately usable for creating a new session. A malformed,
    /// tampered, or expired inbound cookie is invalid for anonymous authorization on the current
    /// request and receives a fresh replacement. An authenticated customer may use that replacement
    /// to create a new customer-owned session without an avoidable retry.
    /// </summary>
    public AnonymousVisitorResolution ResolveForRequest(HttpContext context)
    {
        if (context.Items.TryGetValue(RequestResolutionKey, out var cached) &&
            cached is AnonymousVisitorResolution resolution)
        {
            return resolution;
        }

        var hadInboundCookie = context.Request.Cookies.ContainsKey(CookieName);
        var existingVisitorId = ReadVisitorId(context.Request);
        if (existingVisitorId.HasValue)
        {
            resolution = new AnonymousVisitorResolution(existingVisitorId, AnonymousVisitorCredentialStatus.Valid);
        }
        else
        {
            var issuedVisitorId = Issue(context.Request, context.Response);
            resolution = hadInboundCookie
                ? new AnonymousVisitorResolution(issuedVisitorId, AnonymousVisitorCredentialStatus.Invalid)
                : new AnonymousVisitorResolution(issuedVisitorId, AnonymousVisitorCredentialStatus.Issued);
        }

        context.Items[RequestResolutionKey] = resolution;
        return resolution;
    }

    /// <summary>
    /// Issues a fresh signed visitor cookie on the response when the request does not already carry a
    /// valid one. The new id is for <em>future</em> requests' fairness; it deliberately does not affect
    /// how the current request is rate-limited (that request has no valid inbound cookie, so it is
    /// IP-limited). A client that ignores the Set-Cookie simply stays on the IP partition.
    /// </summary>
    public void IssueIfMissing(HttpRequest request, HttpResponse response)
    {
        _ = ResolveForRequest(request.HttpContext);
    }

    private Guid Issue(HttpRequest request, HttpResponse response)
    {
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

        return payload.VisitorId;
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
            if (!hostEnvironment.IsDevelopment() &&
                !hostEnvironment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException(
                    "AnonymousVisitor:SigningKey must be configured outside Development and Testing.");
            }

            configured = "maliev-local-development-anonymous-visitor-key";
        }

        return Encoding.UTF8.GetBytes(configured);
    }
}

/// <summary>Signed anonymous-visitor cookie payload.</summary>
public sealed record AnonymousVisitorPayload(Guid VisitorId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);

/// <summary>Validation state of the anonymous visitor credential on the current request.</summary>
public enum AnonymousVisitorCredentialStatus
{
    /// <summary>A valid signed credential was supplied by the browser.</summary>
    Valid,

    /// <summary>No credential was supplied, so a fresh signed credential was issued.</summary>
    Issued,

    /// <summary>An inbound credential was malformed, tampered, or expired.</summary>
    Invalid
}

/// <summary>Resolved anonymous visitor identity for one HTTP request.</summary>
public sealed record AnonymousVisitorResolution(
    Guid? VisitorId,
    AnonymousVisitorCredentialStatus Status);
