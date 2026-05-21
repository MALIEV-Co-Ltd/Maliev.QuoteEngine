using System.ComponentModel.DataAnnotations;

namespace Maliev.QuoteEngine.Shared.Account;

public sealed record CustomerProfileResponse(
    Guid CustomerId,
    string DisplayName,
    string Email,
    string Phone,
    string CompanyName,
    string PreferredLanguage);

public sealed record CustomerQuoteSummaryDto(
    Guid QuoteId,
    string QuoteNumber,
    string Status,
    decimal Total,
    string Currency,
    DateTimeOffset UpdatedAt,
    string PdfUrl);

public sealed record CustomerOrderSummaryDto(
    Guid OrderId,
    string OrderNumber,
    string Status,
    DateTimeOffset UpdatedAt,
    string TrackingLabel);

/// <summary>A single entry in the customer-visible status timeline of an order.</summary>
public sealed record OrderStatusEntryDto(
    string Status,
    string? CustomerNote,
    DateTimeOffset Timestamp);

/// <summary>Full detail view of a manufacturing order for the customer portal.</summary>
public sealed record CustomerOrderDetailDto(
    Guid OrderId,
    string OrderNumber,
    string CurrentStatus,
    string PaymentStatus,
    decimal? QuotedAmount,
    string? QuoteCurrency,
    DateTimeOffset? PromisedDeliveryDate,
    DateTimeOffset? ActualDeliveryDate,
    string? CustomerPoNumber,
    string? Requirements,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<OrderStatusEntryDto> StatusHistory);

public sealed record CustomerNdaDto(Guid NdaId, string Title, string Status, DateTimeOffset UpdatedAt);

public sealed record CustomerDocumentDto(Guid DocumentId, string FileName, string Kind, DateTimeOffset UploadedAt);

public sealed class CustomerAddressDto
{
    public Guid Id { get; set; }

    public string Type { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    public string? PlaceLabel { get; set; }

    public string? PlaceLabelOther { get; set; }

    public string AddressLine1 { get; set; } = string.Empty;

    public string? AddressLine2 { get; set; }

    public string? AddressLine3 { get; set; }

    public string? District { get; set; }

    public string City { get; set; } = string.Empty;

    public string StateProvince { get; set; } = string.Empty;

    public string PostalCode { get; set; } = string.Empty;

    public Guid CountryId { get; set; }

    public string? RecipientName { get; set; }

    public string? RecipientPhone { get; set; }

    public string? DriverNote { get; set; }

    public string AddressSource { get; set; } = "Manual";

    public string? GooglePlaceId { get; set; }

    public string? FormattedAddress { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public uint Version { get; set; }
}

public sealed class CustomerAddressUpsertRequest
{
    public string Type { get; set; } = "Shipping";

    public bool IsDefault { get; set; }

    public string? PlaceLabel { get; set; }

    public string? PlaceLabelOther { get; set; }

    public string AddressLine1 { get; set; } = string.Empty;

    public string? AddressLine2 { get; set; }

    public string? AddressLine3 { get; set; }

    public string? District { get; set; }

    public string City { get; set; } = string.Empty;

    public string StateProvince { get; set; } = string.Empty;

    public string PostalCode { get; set; } = string.Empty;

    public Guid CountryId { get; set; }

    public string? RecipientName { get; set; }

    public string? RecipientPhone { get; set; }

    public string? DriverNote { get; set; }

    public string AddressSource { get; set; } = "Manual";

    public string? GooglePlaceId { get; set; }

    public string? FormattedAddress { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public uint Version { get; set; }
}

public sealed class SignInRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

public sealed class SignUpRequest
{
    [Required]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    public string LastName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;

    public string CompanyName { get; set; } = string.Empty;
}

public sealed record AuthSessionResponse(Guid CustomerId, string DisplayName, string Email, bool IsAuthenticated);
