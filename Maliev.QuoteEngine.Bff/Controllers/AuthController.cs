using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
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
    ILogger<AuthController> logger) : ControllerBase
{
    private const string ExternalScheme = "MalievExternal";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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
            return Ok(new QuoteAuthStatusResponse(true, customerId, displayName));
        }

        return Ok(new QuoteAuthStatusResponse(false, null, null));
    }

    [HttpPost("sign-out")]
    public new async Task<IActionResult> SignOut()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    /// <summary>Starts customer Google sign-in as a same-tab browser flow.</summary>
    [HttpGet("/auth/google")]
    [AllowAnonymous]
    public IActionResult Google([FromQuery] string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientId"]) ||
            string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientSecret"]))
        {
            return Redirect(AppendError(NormalizeReturnUrl(returnUrl), "Google sign-in is not configured yet."));
        }

        var redirect = Url.Action(nameof(GoogleCallback), new { returnUrl = NormalizeReturnUrl(returnUrl) })!;
        return Challenge(new AuthenticationProperties { RedirectUri = redirect }, GoogleDefaults.AuthenticationScheme);
    }

    /// <summary>Completes Google sign-in: exchanges the verified identity with AuthService and issues the shared cookie.</summary>
    [HttpGet("/auth/google/callback")]
    [AllowAnonymous]
    public async Task<IActionResult> GoogleCallback([FromQuery] string? returnUrl = null, CancellationToken cancellationToken = default)
    {
        var external = await HttpContext.AuthenticateAsync(ExternalScheme);
        if (!external.Succeeded || external.Principal is null)
        {
            return Redirect(AppendError(NormalizeReturnUrl(returnUrl), "Google sign-in could not be completed."));
        }

        var email = external.Principal.FindFirstValue(ClaimTypes.Email);
        var name = external.Principal.FindFirstValue(ClaimTypes.Name) ?? email;
        var googleUserId = external.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var profileImageUrl = external.Principal.FindFirstValue("picture");

        await HttpContext.SignOutAsync(ExternalScheme);

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(googleUserId))
        {
            return Redirect(AppendError(NormalizeReturnUrl(returnUrl), "Google did not return a verified customer identity."));
        }

        using var response = await authClient.ExchangeCustomerGoogleAsync(new
        {
            email,
            full_name = name,
            google_user_id = googleUserId,
            email_verified = true,
            profile_image_url = profileImageUrl,
            preferred_language = "en",
            timezone = "Asia/Bangkok"
        }, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Customer Google exchange failed with status {StatusCode}", response.StatusCode);
            return Redirect(AppendError(NormalizeReturnUrl(returnUrl), "Google sign-in could not create a MALIEV customer session."));
        }

        var session = await response.Content.ReadFromJsonAsync<AuthLoginResponse>(JsonOptions, cancellationToken);
        if (session?.User is null)
        {
            return Redirect(AppendError(NormalizeReturnUrl(returnUrl), "Google sign-in returned an incomplete customer session."));
        }

        session.User.EmailVerified = true;
        await SignInCustomerAsync(session.User);
        return Redirect(NormalizeReturnUrl(returnUrl));
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
        return Ok(new QuoteAuthStatusResponse(true, customerId, user.Name ?? user.Email));
    }

    private async Task SignInCustomerAsync(AuthUser user)
    {
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

    private static string AppendError(string path, string error)
    {
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{path}{separator}authError={Uri.EscapeDataString(error)}";
    }

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
