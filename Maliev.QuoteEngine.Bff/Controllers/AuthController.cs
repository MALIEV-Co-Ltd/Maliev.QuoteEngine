using System.Security.Claims;
using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Maliev.QuoteEngine.Bff.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}/auth")]
public sealed class AuthController(CustomerSessionResolver sessionResolver) : ControllerBase
{
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
}
