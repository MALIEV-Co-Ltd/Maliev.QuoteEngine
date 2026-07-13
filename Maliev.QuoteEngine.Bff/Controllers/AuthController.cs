using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace Maliev.QuoteEngine.Bff.Controllers;

/// <summary>
/// Customer authentication for Make Studio. Sign-in completes against
/// AuthService from this BFF — Google OAuth runs here as a browser flow and
/// email credentials are exchanged server-to-server. The customer never leaves
/// the studio for the Maliev.Web frontend.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}/auth")]
public sealed class AuthController(
    CustomerSessionResolver sessionResolver,
    IAuthServiceClient authClient,
    ICustomerRegistrationClient registrationClient,
    IConfiguration configuration,
    IDataProtectionProvider dataProtectionProvider,
    IHostEnvironment environment,
    QuoteEnginePrototypeStore store,
    ILogger<AuthController> logger) : ControllerBase
{
    private const string GoogleFlowCookieName = "maliev_qe_google_flow";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _googleFlowProtector = dataProtectionProvider.CreateProtector(
        "Maliev.QuoteEngine.GoogleIdentityFlow.v1");
    private static readonly string[] CustomerAccountPermissions =
    [
        "customer.profile.read",
        "customer.profile.write",
        "customer.addresses.manage",
        "order.orders.read"
    ];

    [HttpGet("session")]
    public ActionResult<QuoteAuthStatusResponse> Session()
    {
        if (sessionResolver.TryResolveCustomerId(out var customerId))
        {
            var displayName = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue(ClaimTypes.Email);
            var profileImageUrl = User.FindFirstValue("profile_image_url") ?? User.FindFirstValue("picture");
            return Ok(new QuoteAuthStatusResponse(true, customerId, displayName, profileImageUrl));
        }

        return Ok(new QuoteAuthStatusResponse(false, null, null));
    }

    [HttpPost("sign-out")]
    public new async Task<IActionResult> SignOut()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        DeleteGoogleDriveFlowCookie();
        return NoContent();
    }

    /// <summary>
    /// Development/testing convenience: issues a real customer session cookie for the prototype
    /// "Demo Customer" so local testers and automated agents can reach sign-in-gated surfaces
    /// without real credentials. Hard-gated to Development/Testing — returns 404 in every other
    /// environment so it can never be reached in production.
    /// </summary>
    [HttpGet("dev-sign-in")]
    [AllowAnonymous]
    public async Task<IActionResult> DevSignIn([FromQuery] string? returnUrl = null)
    {
        if (!(environment.IsDevelopment() || environment.IsEnvironment("Testing")))
        {
            return NotFound();
        }

        var prototype = store.PrototypeCustomer;
        var principalId = prototype.CustomerId.ToString();
        await SignInCustomerAsync(new AuthUser
        {
            UserId = principalId,
            PrincipalId = principalId,
            CustomerId = principalId,
            Email = prototype.Email,
            Name = prototype.DisplayName,
            ProfileImageUrl = prototype.ProfileImageUrl,
            EmailVerified = true
        });

        logger.LogInformation(
            "Issued development prototype customer session for {CustomerId}", prototype.CustomerId);
        return Redirect(NormalizeReturnUrl(returnUrl));
    }

    /// <summary>Issues the browser configuration for Google's official Identity Services button.</summary>
    [HttpPost("google/config")]
    [AllowAnonymous]
    public async Task<ActionResult<QuoteGoogleIdentityConfigResponse>> GetGoogleConfig(
        CancellationToken cancellationToken)
    {
        var clientId = configuration["Authentication:Google:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        using var nonceResponse = await authClient.IssueCustomerGoogleNonceAsync(
            new { application = "quote-engine" },
            cancellationToken);
        if (!nonceResponse.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "AuthService Google nonce issue failed with status {StatusCode}",
                nonceResponse.StatusCode);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var nonce = await nonceResponse.Content.ReadFromJsonAsync<AuthGoogleNonceResponse>(
            JsonOptions,
            cancellationToken);
        if (nonce is null ||
            nonce.Nonce.Length is < 32 or > 256 ||
            nonce.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        Response.Cookies.Append(
            GoogleFlowCookieName,
            _googleFlowProtector.Protect(nonce.Nonce),
            GoogleFlowCookieOptions(nonce.ExpiresAtUtc));
        return Ok(new QuoteGoogleIdentityConfigResponse(
            clientId.Trim(),
            nonce.Nonce,
            nonce.ExpiresAtUtc));
    }

    /// <summary>Exchanges a raw GIS credential with AuthService and issues the MALIEV customer cookie.</summary>
    [HttpPost("google")]
    [AllowAnonymous]
    public async Task<ActionResult<QuoteAuthStatusResponse>> ExchangeGoogleCredential(
        [FromBody] QuoteGoogleIdentityExchangeRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Credential) ||
            request.Credential.Length > 8192 ||
            request.Nonce.Length is < 32 or > 256 ||
            !TryReadBoundGoogleNonce(out var boundNonce) ||
            !FixedTimeEquals(request.Nonce, boundNonce))
        {
            return Unauthorized();
        }

        using var response = await authClient.ExchangeCustomerGoogleAsync(new
        {
            credential = request.Credential,
            application = "quote-engine",
            nonce = request.Nonce,
            preferred_language = string.Equals(
                request.PreferredLanguage,
                "th",
                StringComparison.OrdinalIgnoreCase) ? "th" : "en",
            timezone = "Asia/Bangkok"
        }, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Customer Google exchange failed with status {StatusCode}", response.StatusCode);
            return response.StatusCode switch
            {
                HttpStatusCode.Conflict => Conflict(new ProblemDetails
                {
                    Title = "Existing MALIEV account verification required",
                    Detail = "Sign in with your existing MALIEV email first, then link Google from your account."
                }),
                HttpStatusCode.ServiceUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable),
                _ => Unauthorized()
            };
        }

        var session = await response.Content.ReadFromJsonAsync<AuthLoginResponse>(JsonOptions, cancellationToken);
        if (session?.User is null)
        {
            return Unauthorized();
        }

        session.User.EmailVerified = true;
        await SignInCustomerAsync(session.User);
        Response.Cookies.Delete(GoogleFlowCookieName, GoogleFlowCookieOptions(DateTime.UtcNow));
        return SignedInStatus(session.User);
    }

    /// <summary>Signs a customer in with email and password against AuthService.</summary>
    [HttpPost("sign-in")]
    [AllowAnonymous]
    public async Task<ActionResult<QuoteAuthStatusResponse>> SignInWithEmail(
        [FromBody] QuoteEmailAuthRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Unauthorized();
        }

        using var response = await authClient.LoginAsync(new
        {
            username = request.Email,
            password = request.Password,
            user_type = "customer"
        }, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return Unauthorized();
        }

        var session = await response.Content.ReadFromJsonAsync<AuthLoginResponse>(JsonOptions, cancellationToken);
        if (session?.User is null)
        {
            return Unauthorized();
        }

        await SignInCustomerAsync(session.User);
        return SignedInStatus(session.User);
    }

    /// <summary>Registers a customer account with their chosen password, then signs them in.</summary>
    [HttpPost("sign-up")]
    [AllowAnonymous]
    public async Task<ActionResult<QuoteAuthStatusResponse>> SignUpWithEmail(
        [FromBody] QuoteEmailAuthRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Unauthorized();
        }

        var (firstName, lastName) = SplitFullName(request.FullName, request.Email);
        using var register = await registrationClient.RegisterCustomerAsync(new
        {
            email = request.Email,
            firstName,
            lastName,
            password = request.Password,
            registrationMethod = "Email",
            preferredLanguage = string.Equals(request.Language, "th", StringComparison.OrdinalIgnoreCase) ? "th" : "en",
            timezone = "Asia/Bangkok"
        }, cancellationToken);

        if (register.StatusCode == HttpStatusCode.Conflict || !register.IsSuccessStatusCode)
        {
            logger.LogInformation("Customer sign-up rejected with status {StatusCode}", register.StatusCode);
            return Conflict();
        }

        return await SignInWithEmail(request, cancellationToken);
    }

    private ActionResult<QuoteAuthStatusResponse> SignedInStatus(AuthUser user)
    {
        Guid? customerId = Guid.TryParse(user.CustomerId, out var parsed) ? parsed : null;
        return Ok(new QuoteAuthStatusResponse(true, customerId, user.Name ?? user.Email, user.ProfileImageUrl));
    }

    private async Task SignInCustomerAsync(AuthUser user)
    {
        DeleteGoogleDriveFlowCookie();
        var principalId = user.PrincipalId ?? user.UserId;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, principalId),
            new("principal_id", principalId),
            new("user_type", "customer")
        };

        if (!string.IsNullOrWhiteSpace(user.CustomerId))
        {
            claims.Add(new Claim("customer_id", user.CustomerId));
        }

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        if (!string.IsNullOrWhiteSpace(user.Name))
        {
            claims.Add(new Claim(ClaimTypes.Name, user.Name));
        }

        if (!string.IsNullOrWhiteSpace(user.ProfileImageUrl))
        {
            claims.Add(new Claim("profile_image_url", user.ProfileImageUrl));
        }

        foreach (var permission in CustomerAccountPermissions)
        {
            claims.Add(new Claim("permission", permission));
        }

        claims.Add(new Claim("email_verified", user.EmailVerified.ToString().ToLowerInvariant()));

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(14)
            });
    }

    private void DeleteGoogleDriveFlowCookie()
    {
        Response.Cookies.Delete(GoogleDriveOAuthFlow.CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = GoogleDriveOAuthFlow.CookiePath
        });
    }

    private string NormalizeReturnUrl(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) &&
            returnUrl.StartsWith("/", StringComparison.Ordinal) &&
            !returnUrl.StartsWith("//", StringComparison.Ordinal) &&
            !returnUrl.StartsWith("/\\", StringComparison.Ordinal))
        {
            return returnUrl;
        }

        return "/quotes";
    }

    private bool TryReadBoundGoogleNonce(out string nonce)
    {
        nonce = string.Empty;
        if (!Request.Cookies.TryGetValue(GoogleFlowCookieName, out var protectedNonce) ||
            string.IsNullOrWhiteSpace(protectedNonce))
        {
            return false;
        }

        try
        {
            nonce = _googleFlowProtector.Unprotect(protectedNonce);
            return nonce.Length is >= 32 and <= 256;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(left)),
            SHA256.HashData(Encoding.UTF8.GetBytes(right)));

    private static CookieOptions GoogleFlowCookieOptions(DateTime expiresAtUtc) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        Path = "/",
        Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc))
    };

    private static (string FirstName, string LastName) SplitFullName(string? fullName, string email)
    {
        var source = string.IsNullOrWhiteSpace(fullName) ? email.Split('@')[0] : fullName.Trim();
        var parts = source.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => ("Customer", "Account"),
            1 => (parts[0], "Customer"),
            _ => (parts[0], string.Join(' ', parts[1..]))
        };
    }

    private sealed class AuthLoginResponse
    {
        [JsonPropertyName("user")]
        public AuthUser? User { get; set; }
    }

    private sealed class AuthGoogleNonceResponse
    {
        [JsonPropertyName("nonce")]
        public string Nonce { get; set; } = string.Empty;

        [JsonPropertyName("expires_at_utc")]
        public DateTime ExpiresAtUtc { get; set; }
    }

    private sealed class AuthUser
    {
        [JsonPropertyName("user_id")]
        public string UserId { get; set; } = string.Empty;

        [JsonPropertyName("principal_id")]
        public string? PrincipalId { get; set; }

        [JsonPropertyName("customer_id")]
        public string? CustomerId { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("profile_image_url")]
        public string? ProfileImageUrl { get; set; }

        [JsonPropertyName("email_verified")]
        public bool EmailVerified { get; set; }
    }
}
