using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Mvc;

namespace Maliev.QuoteEngine.Bff.Controllers;

/// <summary>Customer shipping endpoints backed by DeliveryService.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}/shipping")]
public sealed class ShippingController(
    IDeliveryServiceClient deliveryServiceClient,
    CustomerSessionResolver sessionResolver) : ControllerBase
{
    /// <summary>Gets available courier options.</summary>
    [HttpGet("couriers")]
    public async Task<ActionResult<IReadOnlyList<ShippingCourierDto>>> GetCouriers(CancellationToken ct)
    {
        if (!sessionResolver.TryResolveCustomerId(out _))
        {
            return Unauthorized();
        }

        var couriers = await deliveryServiceClient.GetShippingCouriersAsync(ct);
        return Ok(couriers);
    }

    /// <summary>Gets live shipping rate options.</summary>
    [HttpPost("rates")]
    public async Task<ActionResult<ShippingRateResponseDto>> GetRates(
        [FromBody] ShippingRateRequestDto request,
        CancellationToken ct)
    {
        if (!sessionResolver.TryResolveCustomerId(out _))
        {
            return Unauthorized();
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var rates = await deliveryServiceClient.GetShippingRatesAsync(request, ct);
        return Ok(rates);
    }

    /// <summary>Gets tracking status for a shipment.</summary>
    [HttpGet("tracking/{trackingCode}")]
    public async Task<ActionResult<ShippingTrackingDto>> GetTracking(
        [FromRoute] string trackingCode,
        CancellationToken ct)
    {
        if (!sessionResolver.TryResolveCustomerId(out _))
        {
            return Unauthorized();
        }

        var tracking = await deliveryServiceClient.GetShippingTrackingAsync(trackingCode, ct);
        return tracking is null ? NotFound() : Ok(tracking);
    }
}
