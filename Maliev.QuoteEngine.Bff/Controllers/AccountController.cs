using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Services;
using Microsoft.AspNetCore.Mvc;

namespace Maliev.QuoteEngine.Bff.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}/account")]
public sealed class AccountController(QuoteEnginePrototypeStore store, CustomerSessionResolver sessionResolver) : ControllerBase
{
    [HttpGet("profile")]
    public IActionResult GetProfile()
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        return Ok(store.GetProfile(customerId));
    }

    [HttpGet("quotes")]
    public IActionResult GetQuotes()
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        return Ok(store.GetQuotes(customerId));
    }

    [HttpGet("orders")]
    public IActionResult GetOrders()
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        return Ok(store.GetOrders(customerId));
    }

    [HttpGet("ndas")]
    public IActionResult GetNdas()
    {
        if (!sessionResolver.TryResolveCustomerId(out _))
        {
            return Unauthorized();
        }

        return Ok(store.Ndas);
    }

    [HttpGet("documents")]
    public IActionResult GetDocuments()
    {
        if (!sessionResolver.TryResolveCustomerId(out _))
        {
            return Unauthorized();
        }

        return Ok(store.Documents);
    }
}
