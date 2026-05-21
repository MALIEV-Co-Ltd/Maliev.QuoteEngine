using System.Security.Claims;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Account;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maliev.QuoteEngine.Bff.Controllers;

/// <summary>
/// Handles browser-based customer Google sign-in for the Quote Engine.
/// </summary>
[Route("auth")]
public sealed class GoogleAuthController(
    QuoteEnginePrototypeStore store,
    IConfiguration configuration,
    ICustomerServiceClient customerClient) : Controller
{
    private const string ExternalScheme = "MalievQuoteExternal";

    /// <summary>
    /// Starts customer Google sign-in.
    /// </summary>
    [HttpGet("google")]
    [AllowAnonymous]
    public IActionResult Google([FromQuery] string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientId"]) ||
            string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientSecret"]))
        {
            return RedirectWithError("/auth/sign-in", "Google sign-in is not configured yet.");
        }

        var redirect = Url.Action(nameof(GoogleCallback), new { returnUrl = NormalizeReturnUrl(returnUrl) })!;
        return Challenge(new AuthenticationProperties { RedirectUri = redirect }, GoogleDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Completes customer Google sign-in after Google validates the browser identity.
    /// </summary>
    [HttpGet("google/callback")]
    [AllowAnonymous]
    public async Task<IActionResult> GoogleCallback([FromQuery] string? returnUrl = null)
    {
        var external = await HttpContext.AuthenticateAsync(ExternalScheme);
        if (!external.Succeeded || external.Principal is null)
        {
            return RedirectWithError("/auth/sign-in", "Google sign-in could not be completed.");
        }

        var email = external.Principal.FindFirstValue(ClaimTypes.Email);
        var name = external.Principal.FindFirstValue(ClaimTypes.Name) ?? email;
        var googleUserId = external.Principal.FindFirstValue(ClaimTypes.NameIdentifier);

        await HttpContext.SignOutAsync(ExternalScheme);

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(googleUserId))
        {
            return RedirectWithError("/auth/sign-in", "Google did not return a verified customer identity.");
        }

        var profile = await EnsureCustomerProfileAsync(email, name ?? DisplayNameFromEmail(email), HttpContext.RequestAborted);
        if (profile is null)
        {
            return RedirectWithError("/auth/sign-in", "Quote Engine could not create or load your customer account.");
        }

        AppendCustomerCookie(profile.CustomerId);
        return LocalRedirect(NormalizeReturnUrl(returnUrl));
    }

    private void AppendCustomerCookie(Guid customerId)
    {
        Response.Cookies.Append("maliev_quote_customer", customerId.ToString("D"), new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps
        });
    }

    private IActionResult RedirectWithError(string path, string error)
    {
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return Redirect($"{path}{separator}error={Uri.EscapeDataString(error)}");
    }

    private string NormalizeReturnUrl(string? returnUrl)
    {
        return !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? returnUrl
            : "/projects/new";
    }

    private async Task<CustomerProfileResponse?> EnsureCustomerProfileAsync(
        string email,
        string displayName,
        CancellationToken cancellationToken)
    {
        var customer = await customerClient.EnsureCustomerAsync(email, displayName, ct: cancellationToken);
        return customer is null
            ? null
            : store.UpsertCustomer(
                customer.CustomerId,
                customer.Email,
                customer.DisplayName,
                customer.Phone,
                customer.CompanyName,
                customer.PreferredLanguage);
    }

    private static string DisplayNameFromEmail(string email)
    {
        var name = email.Split('@', 2)[0].Replace(".", " ", StringComparison.Ordinal).Replace("_", " ", StringComparison.Ordinal).Trim();
        return string.IsNullOrWhiteSpace(name) ? "Quote customer" : name;
    }
}
