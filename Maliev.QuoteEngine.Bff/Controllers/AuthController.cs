using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Account;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Mvc;

namespace Maliev.QuoteEngine.Bff.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}/auth")]
public sealed class AuthController(QuoteEnginePrototypeStore store, ICustomerServiceClient customerClient) : ControllerBase
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
    public async Task<ActionResult<AuthSessionResponse>> SignIn(
        [FromBody] SignInRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var profile = await EnsureCustomerProfileAsync(
            request.Email,
            DisplayNameFromEmail(request.Email),
            cancellationToken: cancellationToken);
        if (profile is null)
        {
            return CustomerServiceUnavailable();
        }

        AppendCustomerCookie(profile.CustomerId);
        return Ok(new AuthSessionResponse(profile.CustomerId, profile.DisplayName, profile.Email, true));
    }

    [HttpPost("sign-up")]
    public async Task<ActionResult<AuthSessionResponse>> SignUp(
        [FromBody] SignUpRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var displayName = $"{request.FirstName} {request.LastName}".Trim();
        var profile = await EnsureCustomerProfileAsync(
            request.Email,
            displayName,
            request.Phone,
            request.CompanyName,
            cancellationToken);
        if (profile is null)
        {
            return CustomerServiceUnavailable();
        }

        AppendCustomerCookie(profile.CustomerId);
        return Ok(new AuthSessionResponse(profile.CustomerId, profile.DisplayName, profile.Email, true));
    }

    [HttpPost("google/exchange")]
    public async Task<ActionResult<AuthSessionResponse>> ExchangeGoogle(CancellationToken cancellationToken)
    {
        var profile = await EnsureCustomerProfileAsync(
            store.PrototypeCustomer.Email,
            store.PrototypeCustomer.DisplayName,
            store.PrototypeCustomer.Phone,
            store.PrototypeCustomer.CompanyName,
            cancellationToken);
        if (profile is null)
        {
            return CustomerServiceUnavailable();
        }

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

    private async Task<CustomerProfileResponse?> EnsureCustomerProfileAsync(
        string email,
        string displayName,
        string phone = "",
        string companyName = "",
        CancellationToken cancellationToken = default)
    {
        var customer = await customerClient.EnsureCustomerAsync(email, displayName, phone, cancellationToken);
        return customer is null
            ? null
            : store.UpsertCustomer(
                customer.CustomerId,
                customer.Email,
                customer.DisplayName,
                customer.Phone,
                string.IsNullOrWhiteSpace(customer.CompanyName) ? companyName : customer.CompanyName,
                customer.PreferredLanguage,
                customer.ProfileImageUrl,
                customer.PreferredCurrency,
                customer.Timezone,
                customer.Segment,
                customer.Tier,
                customer.NdaStatus,
                customer.NdaExpiresAt);
    }

    private ObjectResult CustomerServiceUnavailable() =>
        StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
        {
            Title = "Customer service unavailable.",
            Detail = "Quote Engine could not create or load the customer account needed for formal quotes."
        });

    private static string DisplayNameFromEmail(string email)
    {
        var name = email.Split('@', 2)[0].Replace(".", " ", StringComparison.Ordinal).Replace("_", " ", StringComparison.Ordinal).Trim();
        return string.IsNullOrWhiteSpace(name) ? "Quote customer" : name;
    }
}
