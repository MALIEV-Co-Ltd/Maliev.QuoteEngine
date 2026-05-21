using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Account;
using Microsoft.AspNetCore.Mvc;

namespace Maliev.QuoteEngine.Bff.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}/address")]
public sealed class AddressController(IConfiguration configuration, CustomerSessionResolver sessionResolver) : ControllerBase
{
    [HttpGet("google-config")]
    [ProducesResponseType(typeof(GoogleAddressConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<GoogleAddressConfigResponse> GetGoogleConfig()
    {
        if (!sessionResolver.TryResolveCustomerId(out _))
        {
            return Unauthorized();
        }

        var section = configuration.GetSection("GoogleMaps");
        return Ok(new GoogleAddressConfigResponse
        {
            ApiKey = section["BrowserApiKey"] ?? string.Empty,
            MapId = section["MapId"],
            DefaultLatitude = section.GetValue<double?>("DefaultLatitude") ?? 13.7563,
            DefaultLongitude = section.GetValue<double?>("DefaultLongitude") ?? 100.5018,
            DefaultZoom = section.GetValue<int?>("DefaultZoom") ?? 12,
            IncludedRegionCodes = section.GetSection("IncludedRegionCodes").Get<string[]>() ?? ["th"]
        });
    }
}
