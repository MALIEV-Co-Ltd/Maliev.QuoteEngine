using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Account;
using Microsoft.AspNetCore.Mvc;

namespace Maliev.QuoteEngine.Bff.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}/auth")]
public sealed class AuthController(QuoteEnginePrototypeStore store) : ControllerBase
{
    [HttpPost("sign-in")]
    public ActionResult<AuthSessionResponse> SignIn([FromBody] SignInRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        AppendPrototypeCookie();
        return Ok(new AuthSessionResponse(store.PrototypeCustomer.CustomerId, store.PrototypeCustomer.DisplayName, request.Email, true));
    }

    [HttpPost("sign-up")]
    public ActionResult<AuthSessionResponse> SignUp([FromBody] SignUpRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        AppendPrototypeCookie();
        return Ok(new AuthSessionResponse(store.PrototypeCustomer.CustomerId, $"{request.FirstName} {request.LastName}", request.Email, true));
    }

    [HttpPost("google/exchange")]
    public ActionResult<AuthSessionResponse> ExchangeGoogle()
    {
        AppendPrototypeCookie();
        return Ok(new AuthSessionResponse(store.PrototypeCustomer.CustomerId, store.PrototypeCustomer.DisplayName, store.PrototypeCustomer.Email, true));
    }

    [HttpPost("sign-out")]
    public IActionResult SignOutCustomer()
    {
        Response.Cookies.Delete("maliev_quote_customer");
        return NoContent();
    }

    private void AppendPrototypeCookie()
    {
        Response.Cookies.Append("maliev_quote_customer", store.PrototypeCustomer.CustomerId.ToString("D"), new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps
        });
    }
}
