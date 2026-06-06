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
    IQuotationServiceClient quotationClient,
    IOrderServiceClient orderClient,
    IHostEnvironment environment) : ControllerBase
{
    private static readonly HashSet<string> AllowedDocumentKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "PurchaseOrder",
        "Invoice",
        "Receipt",
        "Requirement"
    };

    private static readonly HashSet<string> AllowedDocumentContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/png",
        "image/jpeg",
        "image/webp",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/octet-stream"
    };

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
        if (!sessionResolver.TryResolveCustomerId(out var customerId)) return Unauthorized();

        var customerOrders = await orderClient.GetByCustomerAsync(customerId.ToString("D"), cancellationToken);
        if (!customerOrders.Any(order => string.Equals(order.OrderNumber, orderNumber, StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound();
        }

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
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        return Ok(store.GetDocuments(customerId));
    }

    [HttpPost("documents")]
    public IActionResult UploadDocument([FromBody] CustomerDocumentUploadRequest request)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var validationProblem = ValidateDocumentUpload(request);
        if (validationProblem is not null)
        {
            return validationProblem;
        }

        return Ok(store.UploadDocument(customerId, request));
    }

    private BadRequestObjectResult? ValidateDocumentUpload(CustomerDocumentUploadRequest request)
    {
        if (!AllowedDocumentKinds.Contains(request.Kind.Trim()))
        {
            return BadRequest(AccountProblem(
                "Unsupported document kind",
                "Customer documents must be purchase orders, invoices, receipts, or requirements.",
                StatusCodes.Status400BadRequest));
        }

        if (!IsSafeCustomerDocumentStoragePath(request.StoragePath))
        {
            return BadRequest(AccountProblem(
                "Unsafe document storage path",
                "Customer document storage paths must be relative paths under customer document storage.",
                StatusCodes.Status400BadRequest));
        }

        if (!AllowedDocumentContentTypes.Contains(request.ContentType.Trim()))
        {
            return BadRequest(AccountProblem(
                "Unsupported document content type",
                "Customer documents must be PDF, image, Word, or Excel files.",
                StatusCodes.Status400BadRequest));
        }

        return null;
    }

    private static bool IsSafeCustomerDocumentStoragePath(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            return false;
        }

        var normalized = storagePath.Trim().Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Contains("://", StringComparison.Ordinal)
            || normalized.Split(['/'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
        {
            return false;
        }

        return normalized.StartsWith("customer-documents/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("customers/", StringComparison.OrdinalIgnoreCase);
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

    private bool IsDownstreamAddressFallbackException(Exception exception, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && CanUsePrototypeAddressFallback()
            && exception is HttpRequestException or InvalidOperationException or TaskCanceledException;
    }

    private ObjectResult DownstreamProblem(HttpResponseMessage response, string detail)
    {
        var status = response.StatusCode == HttpStatusCode.NotFound
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status503ServiceUnavailable;
        return StatusCode(status, AccountProblem("Account service unavailable", detail, status));
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

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
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
