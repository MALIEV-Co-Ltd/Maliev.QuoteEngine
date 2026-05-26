using System.Net;
using System.Text.Json;
using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Account;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace Maliev.QuoteEngine.Bff.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}/account")]
public sealed class AccountController(
    QuoteEnginePrototypeStore store,
    CustomerSessionResolver sessionResolver,
    ICustomerServiceClient customerClient,
    ICountryServiceClient countryClient,
    IQuotationServiceClient quotationClient,
    IOrderServiceClient orderClient,
    IHostEnvironment environment) : ControllerBase
{
    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        // Try real CustomerService first; fall back to the local profile created at sign-in or Web handoff.
        var profile = await customerClient.GetByIdAsync(customerId, cancellationToken);
        if (profile is not null)
        {
            return Ok(profile);
        }

        if (store.TryGetProfile(customerId, out var storedProfile) && storedProfile is not null)
        {
            return Ok(storedProfile);
        }

        Response.Cookies.Delete("maliev_quote_customer");
        return Unauthorized();
    }

    [HttpGet("addresses")]
    [ProducesResponseType(typeof(List<CustomerAddressDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetAddresses(CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        try
        {
            using var response = await customerClient.GetCustomerAddressesAsync(customerId, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (CanUsePrototypeAddressFallback(response))
                {
                    return Ok(store.GetAddresses(customerId));
                }

                return DownstreamProblem(response, "Customer addresses are temporarily unavailable.");
            }

            using var document = await ReadJsonAsync(response, cancellationToken);
            var addresses = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(MapAddress).ToList()
                : [];
            return Ok(addresses);
        }
        catch (Exception exception) when (IsDownstreamAddressFallbackException(exception, cancellationToken))
        {
            return Ok(store.GetAddresses(customerId));
        }
    }

    [HttpPost("addresses")]
    [ProducesResponseType(typeof(CustomerAddressDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateAddress([FromBody] CustomerAddressUpsertRequest request, CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        var countryId = await ResolveCountryIdAsync(request.CountryId, cancellationToken);
        try
        {
            using var response = await customerClient.CreateCustomerAddressAsync(new
            {
                ownerType = "Customer",
                ownerId = customerId,
                type = request.Type,
                isDefault = request.IsDefault,
                placeLabel = request.PlaceLabel,
                placeLabelOther = request.PlaceLabelOther,
                addressLine1 = request.AddressLine1,
                addressLine2 = request.AddressLine2,
                addressLine3 = request.AddressLine3,
                district = request.District,
                city = request.City,
                stateProvince = request.StateProvince,
                postalCode = request.PostalCode,
                countryId,
                recipientName = request.RecipientName,
                recipientPhone = request.RecipientPhone,
                driverNote = request.DriverNote,
                addressSource = string.IsNullOrWhiteSpace(request.AddressSource) ? "Manual" : request.AddressSource,
                googlePlaceId = request.GooglePlaceId,
                formattedAddress = request.FormattedAddress,
                latitude = request.Latitude,
                longitude = request.Longitude
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if (CanUsePrototypeAddressFallback(response))
                {
                    return Ok(store.CreateAddress(customerId, request, countryId));
                }

                return DownstreamProblem(response, "Customer address could not be added.");
            }

            using var document = await ReadJsonAsync(response, cancellationToken);
            return Ok(MapAddress(document.RootElement));
        }
        catch (Exception exception) when (IsDownstreamAddressFallbackException(exception, cancellationToken))
        {
            return Ok(store.CreateAddress(customerId, request, countryId));
        }
    }

    [HttpPatch("addresses/{addressId:guid}")]
    [ProducesResponseType(typeof(CustomerAddressDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> UpdateAddress(Guid addressId, [FromBody] CustomerAddressUpsertRequest request, CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        var ownsAddress = await CustomerOwnsAddressAsync(customerId, addressId, cancellationToken);
        if (!ownsAddress.HasValue)
        {
            if (CanUsePrototypeAddressFallback())
            {
                return UpdatePrototypeAddress(customerId, addressId, request);
            }

            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                AccountProblem("Customer addresses unavailable", "MALIEV could not verify this address belongs to your account.", StatusCodes.Status503ServiceUnavailable));
        }

        if (!ownsAddress.Value)
        {
            return NotFound(AccountProblem("Address not found", "This address is not attached to your customer account.", StatusCodes.Status404NotFound));
        }

        try
        {
            using var response = await customerClient.UpdateCustomerAddressAsync(addressId, new
            {
                type = request.Type,
                isDefault = request.IsDefault,
                placeLabel = request.PlaceLabel,
                placeLabelOther = request.PlaceLabelOther,
                addressLine1 = request.AddressLine1,
                addressLine2 = request.AddressLine2,
                addressLine3 = request.AddressLine3,
                district = request.District,
                city = request.City,
                stateProvince = request.StateProvince,
                postalCode = request.PostalCode,
                countryId = request.CountryId == Guid.Empty ? (Guid?)null : request.CountryId,
                recipientName = request.RecipientName,
                recipientPhone = request.RecipientPhone,
                driverNote = request.DriverNote,
                addressSource = request.AddressSource,
                googlePlaceId = request.GooglePlaceId,
                formattedAddress = request.FormattedAddress,
                latitude = request.Latitude,
                longitude = request.Longitude,
                xmin = request.Version
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if (CanUsePrototypeAddressFallback(response))
                {
                    return UpdatePrototypeAddress(customerId, addressId, request);
                }

                return DownstreamProblem(response, "Customer address could not be updated.");
            }

            using var document = await ReadJsonAsync(response, cancellationToken);
            return Ok(MapAddress(document.RootElement));
        }
        catch (Exception exception) when (IsDownstreamAddressFallbackException(exception, cancellationToken))
        {
            return UpdatePrototypeAddress(customerId, addressId, request);
        }
    }

    [HttpDelete("addresses/{addressId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> DeleteAddress(Guid addressId, [FromQuery] uint version, CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        var ownsAddress = await CustomerOwnsAddressAsync(customerId, addressId, cancellationToken);
        if (!ownsAddress.HasValue)
        {
            if (CanUsePrototypeAddressFallback())
            {
                return DeletePrototypeAddress(customerId, addressId);
            }

            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                AccountProblem("Customer addresses unavailable", "MALIEV could not verify this address belongs to your account.", StatusCodes.Status503ServiceUnavailable));
        }

        if (!ownsAddress.Value)
        {
            return NotFound(AccountProblem("Address not found", "This address is not attached to your customer account.", StatusCodes.Status404NotFound));
        }

        try
        {
            using var response = await customerClient.DeleteCustomerAddressAsync(addressId, new
            {
                xmin = version
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if (CanUsePrototypeAddressFallback(response))
                {
                    return DeletePrototypeAddress(customerId, addressId);
                }

                return DownstreamProblem(response, "Customer address could not be deleted.");
            }

            return NoContent();
        }
        catch (Exception exception) when (IsDownstreamAddressFallbackException(exception, cancellationToken))
        {
            return DeletePrototypeAddress(customerId, addressId);
        }
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

    private async Task<bool?> CustomerOwnsAddressAsync(Guid customerId, Guid addressId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await customerClient.GetCustomerAddressesAsync(customerId, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return CanUsePrototypeAddressFallback(response)
                    ? store.CustomerOwnsAddress(customerId, addressId)
                    : null;
            }

            using var document = await ReadJsonAsync(response, cancellationToken);
            return document.RootElement.ValueKind == JsonValueKind.Array &&
                document.RootElement.EnumerateArray()
                    .Any(item => GetGuid(item, "id", "Id") == addressId);
        }
        catch (Exception exception) when (IsDownstreamAddressFallbackException(exception, cancellationToken))
        {
            return store.CustomerOwnsAddress(customerId, addressId);
        }
    }

    private async Task<Guid> ResolveCountryIdAsync(Guid requestedCountryId, CancellationToken cancellationToken)
    {
        if (requestedCountryId != Guid.Empty)
        {
            return requestedCountryId;
        }

        try
        {
            using var response = await countryClient.GetCountryByIso2Async("TH", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Guid.Empty;
            }

            using var document = await ReadJsonAsync(response, cancellationToken);
            return GetGuid(document.RootElement, "id", "Id") ?? Guid.Empty;
        }
        catch (Exception exception) when (IsDownstreamAddressFallbackException(exception, cancellationToken))
        {
            return Guid.Empty;
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private ObjectResult DownstreamProblem(HttpResponseMessage response, string detail)
    {
        var status = response.StatusCode == HttpStatusCode.NotFound
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status503ServiceUnavailable;
        return StatusCode(status, AccountProblem("Account service unavailable", detail, status));
    }

    private IActionResult UpdatePrototypeAddress(Guid customerId, Guid addressId, CustomerAddressUpsertRequest request)
    {
        var address = store.UpdateAddress(customerId, addressId, request);
        return address is null
            ? NotFound(AccountProblem("Address not found", "This address is not attached to your customer account.", StatusCodes.Status404NotFound))
            : Ok(address);
    }

    private IActionResult DeletePrototypeAddress(Guid customerId, Guid addressId)
    {
        return store.DeleteAddress(customerId, addressId)
            ? NoContent()
            : NotFound(AccountProblem("Address not found", "This address is not attached to your customer account.", StatusCodes.Status404NotFound));
    }

    private bool IsDownstreamAddressFallbackException(Exception exception, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && CanUsePrototypeAddressFallback()
            && exception is HttpRequestException or InvalidOperationException or TaskCanceledException;
    }

    private bool CanUsePrototypeAddressFallback(HttpResponseMessage response)
    {
        if (!CanUsePrototypeAddressFallback())
        {
            return false;
        }

        var status = (int)response.StatusCode;
        return status >= StatusCodes.Status500InternalServerError ||
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
    }

    private bool CanUsePrototypeAddressFallback()
    {
        return environment.IsDevelopment() || environment.IsEnvironment("Testing");
    }

    private static ProblemDetails AccountProblem(string title, string detail, int status)
    {
        return new ProblemDetails
        {
            Title = title,
            Detail = detail,
            Status = status
        };
    }

    private static CustomerAddressDto MapAddress(JsonElement root)
    {
        return new CustomerAddressDto
        {
            Id = GetGuid(root, "id", "Id") ?? Guid.Empty,
            Type = GetString(root, "type", "Type") ?? string.Empty,
            IsDefault = GetBool(root, "isDefault", "IsDefault"),
            PlaceLabel = GetString(root, "placeLabel", "PlaceLabel"),
            PlaceLabelOther = GetString(root, "placeLabelOther", "PlaceLabelOther"),
            AddressLine1 = GetString(root, "addressLine1", "AddressLine1") ?? string.Empty,
            AddressLine2 = GetString(root, "addressLine2", "AddressLine2"),
            AddressLine3 = GetString(root, "addressLine3", "AddressLine3"),
            District = GetString(root, "district", "District"),
            City = GetString(root, "city", "City") ?? string.Empty,
            StateProvince = GetString(root, "stateProvince", "StateProvince") ?? string.Empty,
            PostalCode = GetString(root, "postalCode", "PostalCode") ?? string.Empty,
            CountryId = GetGuid(root, "countryId", "CountryId") ?? Guid.Empty,
            RecipientName = GetString(root, "recipientName", "RecipientName"),
            RecipientPhone = GetString(root, "recipientPhone", "RecipientPhone"),
            DriverNote = GetString(root, "driverNote", "DriverNote"),
            AddressSource = GetString(root, "addressSource", "AddressSource") ?? "Manual",
            GooglePlaceId = GetString(root, "googlePlaceId", "GooglePlaceId"),
            FormattedAddress = GetString(root, "formattedAddress", "FormattedAddress"),
            Latitude = GetDecimal(root, "latitude", "Latitude"),
            Longitude = GetDecimal(root, "longitude", "Longitude"),
            Version = GetUInt(root, "xmin", "Xmin", "version", "Version")
        };
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static Guid? GetGuid(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                Guid.TryParse(value.GetString(), out var id))
            {
                return id;
            }
        }

        return null;
    }

    private static bool GetBool(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }

        return false;
    }

    private static uint GetUInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && uint.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    private static decimal? GetDecimal(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetDecimal(out var number))
            {
                return number;
            }
        }

        return null;
    }
}
