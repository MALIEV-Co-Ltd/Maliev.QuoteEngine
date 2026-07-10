using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Account;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Maliev.QuoteEngine.Bff.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}/address")]
public sealed class AddressController(
    IConfiguration configuration,
    CustomerSessionResolver sessionResolver,
    IRegistryServiceClient registryClient) : ControllerBase
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

    [HttpGet("thai-locations")]
    [ProducesResponseType(typeof(List<ThaiAddressRegistryLocationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<ThaiAddressRegistryLocationDto>>> SearchThaiLocationsAsync(
        [FromQuery] string? query,
        [FromQuery] int limit = 8,
        CancellationToken cancellationToken = default)
    {
        if (!sessionResolver.TryResolveCustomerId(out _))
        {
            return Unauthorized();
        }

        var normalizedQuery = query?.Trim() ?? string.Empty;
        if (normalizedQuery.Length < 2)
        {
            return Ok(new List<ThaiAddressRegistryLocationDto>());
        }

        var normalizedLimit = Math.Clamp(limit, 1, 20);
        try
        {
            using var response = await registryClient.SearchThaiLocationsAsync(normalizedQuery, normalizedLimit, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Ok(new List<ThaiAddressRegistryLocationDto>());
            }

            var lookup = await ThaiAddressRegistryResponseMapper.ParseAsync(response.Content, cancellationToken);
            return Ok(lookup.IsAvailable
                ? lookup.Locations.ToList()
                : []);
        }
        catch (HttpRequestException)
        {
            return Ok(new List<ThaiAddressRegistryLocationDto>());
        }
        catch (JsonException)
        {
            return Ok(new List<ThaiAddressRegistryLocationDto>());
        }
    }
}
