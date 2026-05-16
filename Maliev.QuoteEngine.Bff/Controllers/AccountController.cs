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
        var customerId = sessionResolver.ResolveCustomerId();
        return Ok(store.GetProfile(customerId));
    }

    [HttpGet("quotes")]
    public IActionResult GetQuotes()
    {
        var customerId = sessionResolver.ResolveCustomerId();
        return Ok(store.GetQuotes(customerId));
    }

    [HttpGet("orders")]
    public IActionResult GetOrders()
    {
        var customerId = sessionResolver.ResolveCustomerId();
        return Ok(store.GetOrders(customerId));
    }

    [HttpGet("ndas")]
    public IActionResult GetNdas()
    {
        _ = sessionResolver.ResolveCustomerId();
        return Ok(store.Ndas);
    }

    [HttpGet("documents")]
    public IActionResult GetDocuments()
    {
        _ = sessionResolver.ResolveCustomerId();
        return Ok(store.Documents);
    }
}
