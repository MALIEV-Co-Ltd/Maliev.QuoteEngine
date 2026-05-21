using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Services;
using Microsoft.AspNetCore.Mvc;

namespace Maliev.QuoteEngine.Bff.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}/account")]
public sealed class AccountController(
    QuoteEnginePrototypeStore store,
    CustomerSessionResolver sessionResolver,
    ICustomerServiceClient customerClient,
    IQuotationServiceClient quotationClient,
    IOrderServiceClient orderClient) : ControllerBase
{
    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        // Try real CustomerService first; fall back to prototype store (prototype sign-in path)
        var profile = await customerClient.GetByIdAsync(customerId, cancellationToken);
        return Ok(profile ?? store.GetProfile(customerId));
    }

    [HttpGet("quotes")]
    public async Task<IActionResult> GetQuotes(CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId)) return Unauthorized();
        var quotes = await quotationClient.GetByCustomerAsync(customerId, cancellationToken);
        return Ok(quotes);
    }

    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders(CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId)) return Unauthorized();
        var orders = await orderClient.GetByCustomerAsync(customerId.ToString("D"), cancellationToken);
        return Ok(orders);
    }

    [HttpGet("orders/{orderNumber}")]
    public async Task<IActionResult> GetOrderDetail(string orderNumber, CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out _)) return Unauthorized();
        var detail = await orderClient.GetDetailAsync(orderNumber, cancellationToken);
        return detail is null ? NotFound() : Ok(detail);
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
