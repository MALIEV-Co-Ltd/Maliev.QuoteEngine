using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Account;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Mvc;

namespace Maliev.QuoteEngine.Bff.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}/auth")]
public sealed class AuthController(QuoteEnginePrototypeStore store) : ControllerBase
{
    [HttpGet("session")]
    public ActionResult<QuoteAuthStatusResponse> Session()
    {
        Guid? customerId = null;
        var signedIn = false;
        if (Request.Cookies.TryGetValue("maliev_quote_customer", out var rawCustomerId)
            && Guid.TryParse(rawCustomerId, out var parsedCustomerId))
        {
            signedIn = true;
            customerId = parsedCustomerId;
        }

        var profile = customerId.HasValue ? store.GetProfile(customerId.Value) : null;
        return Ok(signedIn
            ? new QuoteAuthStatusResponse(true, customerId, profile?.DisplayName ?? store.PrototypeCustomer.DisplayName)
            : new QuoteAuthStatusResponse(false, null, null));
    }

    [HttpPost("sign-in")]
    public ActionResult<AuthSessionResponse> SignIn([FromBody] SignInRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var profile = store.GetOrCreateCustomer(request.Email, DisplayNameFromEmail(request.Email));
        AppendCustomerCookie(profile.CustomerId);
        return Ok(new AuthSessionResponse(profile.CustomerId, profile.DisplayName, profile.Email, true));
    }

    [HttpPost("sign-up")]
    public ActionResult<AuthSessionResponse> SignUp([FromBody] SignUpRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var displayName = $"{request.FirstName} {request.LastName}".Trim();
        var profile = store.GetOrCreateCustomer(request.Email, displayName, request.Phone, request.CompanyName);
        AppendCustomerCookie(profile.CustomerId);
        return Ok(new AuthSessionResponse(profile.CustomerId, profile.DisplayName, profile.Email, true));
    }

    [HttpPost("google/exchange")]
    public ActionResult<AuthSessionResponse> ExchangeGoogle()
    {
        var profile = store.GetOrCreateCustomer(store.PrototypeCustomer.Email, store.PrototypeCustomer.DisplayName, store.PrototypeCustomer.Phone, store.PrototypeCustomer.CompanyName);
        AppendCustomerCookie(profile.CustomerId);
        return Ok(new AuthSessionResponse(profile.CustomerId, profile.DisplayName, profile.Email, true));
    }

    [HttpPost("sign-out")]
    public IActionResult SignOutCustomer()
    {
        Response.Cookies.Delete("maliev_quote_customer");
        return NoContent();
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

    private static string DisplayNameFromEmail(string email)
    {
        var name = email.Split('@', 2)[0].Replace(".", " ", StringComparison.Ordinal).Replace("_", " ", StringComparison.Ordinal).Trim();
        return string.IsNullOrWhiteSpace(name) ? "Quote customer" : name;
    }
}
