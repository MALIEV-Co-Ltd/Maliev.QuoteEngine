using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Shared.ReferenceData;
using Microsoft.AspNetCore.Mvc;

namespace Maliev.QuoteEngine.Bff.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}/reference-data")]
public sealed class ReferenceDataController(
    ICurrencyServiceClient currencyClient,
    IHostEnvironment environment,
    ILogger<ReferenceDataController> logger) : ControllerBase
{
    [HttpGet("currencies")]
    [ProducesResponseType(typeof(List<CurrencyOptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<List<CurrencyOptionDto>>> GetCurrencies(CancellationToken cancellationToken)
    {
        try
        {
            var currencies = await currencyClient.GetCurrenciesAsync(cancellationToken);
            return Ok(currencies
                .Where(currency => currency.IsActive && !string.IsNullOrWhiteSpace(currency.Code))
                .OrderByDescending(currency => currency.IsPrimary)
                .ThenBy(currency => currency.Code, StringComparer.OrdinalIgnoreCase)
                .ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CurrencyService did not return customer currency options.");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails
                {
                    Title = "Currency reference data unavailable",
                    Detail = environment.IsDevelopment() ? ex.Message : null,
                    Status = StatusCodes.Status503ServiceUnavailable
                });
        }
    }
}
