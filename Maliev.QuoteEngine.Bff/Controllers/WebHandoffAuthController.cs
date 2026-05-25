using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Security;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Account;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maliev.QuoteEngine.Bff.Controllers;

/// <summary>
/// Accepts signed customer session handoffs from Maliev.Web.
/// </summary>
[Route("auth")]
public sealed class WebHandoffAuthController(
    QuoteEnginePrototypeStore store,
    ICustomerServiceClient customerClient,
    CustomerSessionHandoffToken handoffToken,
    ILogger<WebHandoffAuthController> logger) : Controller
{
    /// <summary>
    /// Converts a verified Web customer session into the local QuoteEngine customer session.
    /// </summary>
    [HttpGet("web-handoff")]
    [AllowAnonymous]
    public async Task<IActionResult> WebHandoff([FromQuery] string? token, [FromQuery] string? returnUrl = null)
    {
        if (!handoffToken.TryRead(token, out var payload))
        {
            return RedirectWithError("/auth/sign-in", "Your MALIEV session could not be continued. Please sign in again.");
        }

        var profile = await LoadProfileAsync(payload, HttpContext.RequestAborted);
        AppendCustomerCookie(profile.CustomerId);
        return LocalRedirect(NormalizeReturnUrl(returnUrl));
    }

    private async Task<CustomerProfileResponse> LoadProfileAsync(CustomerSessionHandoffPayload payload, CancellationToken cancellationToken)
    {
        var profile = await customerClient.GetByIdAsync(payload.CustomerId, cancellationToken);
        if (profile is not null)
        {
            return store.UpsertCustomer(
                profile.CustomerId,
                profile.Email,
                profile.DisplayName,
                profile.Phone,
                profile.CompanyName,
                profile.PreferredLanguage);
        }

        logger.LogWarning("CustomerService did not return profile {CustomerId} during Web session handoff.", payload.CustomerId);
        return store.UpsertCustomer(
            payload.CustomerId,
            payload.Email ?? "customer@example.com",
            string.IsNullOrWhiteSpace(payload.DisplayName) ? "MALIEV customer" : payload.DisplayName,
            string.Empty,
            string.Empty,
            "en");
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
}
