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
        _ = sessionResolver.ResolveCustomerId();
        return Ok(store.PrototypeCustomer);
    }

    [HttpGet("quotes")]
    public IActionResult GetQuotes()
    {
        _ = sessionResolver.ResolveCustomerId();
        return Ok(store.Quotes);
    }

    [HttpGet("orders")]
    public IActionResult GetOrders()
    {
        _ = sessionResolver.ResolveCustomerId();
        return Ok(store.Orders);
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
