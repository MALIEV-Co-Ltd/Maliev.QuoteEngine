using System.ComponentModel.DataAnnotations;

namespace Maliev.QuoteEngine.Shared.Account;

public sealed record CustomerProfileResponse(
    Guid CustomerId,
    string DisplayName,
    string Email,
    string Phone,
    string CompanyName,
    string PreferredLanguage,
    string? ProfileImageUrl = null,
    string PreferredCurrency = "THB",
    string Timezone = "Asia/Bangkok",
    string Segment = "Self-service manufacturing",
    string Tier = "Customer",
    string NdaStatus = "Active",
    DateTimeOffset? NdaExpiresAt = null,
    string VatNumber = "");

public sealed class CustomerMemoryObserveRequest
{
    [Required]
    [StringLength(80, MinimumLength = 1)]
    public string MemoryType { get; set; } = string.Empty;

    [Required]
    [StringLength(120, MinimumLength = 1)]
    public string Key { get; set; } = string.Empty;

    [Required]
    [StringLength(1200, MinimumLength = 1)]
    public string Value { get; set; } = string.Empty;

    [Range(typeof(decimal), "0", "1")]
    public decimal Confidence { get; set; } = 0.5m;

    [Required]
    [StringLength(80, MinimumLength = 1)]
    public string Source { get; set; } = "unknown";
}

public sealed class CustomerMemoryResponse
{
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }

    public string MemoryType { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public decimal Confidence { get; set; }

    public string Source { get; set; } = string.Empty;

    public int HitCount { get; set; }

    public DateTime LastObservedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public sealed class CustomerMemoryQueryResponse
{
    public Guid CustomerId { get; set; }

    public string Query { get; set; } = string.Empty;

    public int Limit { get; set; }

    public List<CustomerMemoryResponse> Items { get; set; } = [];
}

public sealed record CustomerQuoteVersionSummaryDto(
    Guid VersionId,
    int VersionNumber,
    decimal Total,
    string Currency,
    string? ChangeSummary,
    string? PdfUrl,
    string? PdfStoragePath,
    string? GeneratedByDisplayName,
    DateTimeOffset CreatedAt);

public sealed record CustomerQuoteSummaryDto(
    Guid QuoteId,
    string QuoteNumber,
    string Status,
    decimal Total,
    string Currency,
    DateTimeOffset UpdatedAt,
    string PdfUrl,
    IReadOnlyList<CustomerQuoteVersionSummaryDto>? Versions = null);

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

/// <summary>A customer-visible manufacturing milestone for order progress tracking.</summary>
public sealed record CustomerManufacturingMilestoneDto(
    string Key,
    string Label,
    string Description,
    string State,
    int Percent,
    DateTimeOffset? Timestamp);

/// <summary>Full detail view of a manufacturing order for the customer portal.</summary>
public sealed partial record CustomerOrderDetailDto(
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

public sealed partial record CustomerOrderDetailDto
{
    /// <summary>Gets the formal quotation identifier accepted for this order.</summary>
    public Guid? QuoteId { get; init; }

    /// <summary>Gets the formal quotation number accepted for this order.</summary>
    public string? QuoteNumber { get; init; }

    /// <summary>Gets the immutable quotation version identifier accepted for this order.</summary>
    public Guid? QuoteVersionId { get; init; }

    /// <summary>Gets the immutable quotation version number accepted for this order.</summary>
    public int? QuoteVersionNumber { get; init; }

    public IReadOnlyList<CustomerManufacturingMilestoneDto> ManufacturingMilestones { get; init; } = [];
    public IReadOnlyList<CustomerOrderFileDto> OrderFiles { get; init; } = [];
    public IReadOnlyList<CustomerOrderShippingPartDto> ShippingParts { get; init; } = [];
}

/// <summary>A customer-visible order part used for automatic shipping package planning.</summary>
public sealed class CustomerOrderShippingPartDto
{
    /// <summary>Gets or sets the source part identifier.</summary>
    public Guid PartId { get; set; }

    /// <summary>Gets or sets the customer-visible file or part name.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the manufacturing process code.</summary>
    public string ProcessId { get; set; } = string.Empty;

    /// <summary>Gets or sets the selected material identifier or code.</summary>
    public string MaterialId { get; set; } = string.Empty;

    /// <summary>Gets or sets the ordered quantity for this part.</summary>
    public int Quantity { get; set; } = 1;

    /// <summary>Gets or sets the per-unit part volume in cubic centimeters.</summary>
    public decimal VolumeCc { get; set; }

    /// <summary>Gets or sets the estimated per-unit shipping weight in grams.</summary>
    public decimal WeightGrams { get; set; }

    /// <summary>Gets or sets the bounding-box width in centimeters.</summary>
    public decimal WidthCm { get; set; }

    /// <summary>Gets or sets the bounding-box length in centimeters.</summary>
    public decimal LengthCm { get; set; }

    /// <summary>Gets or sets the bounding-box height in centimeters.</summary>
    public decimal HeightCm { get; set; }
}

/// <summary>A customer-visible file attached to the manufacturing order record.</summary>
public sealed record CustomerOrderFileDto(
    long FileId,
    string FileName,
    string FileRole,
    string FileCategory,
    string ObjectPath,
    string ContentType,
    long FileSizeBytes,
    DateTimeOffset UploadedAt);

public sealed record CustomerOrderFileDownloadResponse(
    long FileId,
    string FileName,
    string DownloadUrl,
    DateTimeOffset ExpiresAt);

public sealed record CustomerNdaDto(
    Guid NdaId,
    string Title,
    string Status,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt = null);

public sealed record CustomerDocumentDto(
    Guid DocumentId,
    string FileName,
    string Kind,
    DateTimeOffset UploadedAt,
    string? StoragePath = null,
    string? ContentType = null,
    long FileSizeBytes = 0,
    string? OrderNumber = null);

public sealed record CustomerDocumentDownloadResponse(
    Guid DocumentId,
    string FileName,
    string DownloadUrl,
    DateTimeOffset ExpiresAt);

public sealed class CustomerDocumentUploadRequest
{
    [Required]
    [MaxLength(260)]
    public string FileName { get; set; } = string.Empty;

    [Required]
    [MaxLength(80)]
    public string Kind { get; set; } = "PurchaseOrder";

    [Required]
    [MaxLength(512)]
    public string StoragePath { get; set; } = string.Empty;

    [Required]
    [MaxLength(120)]
    public string ContentType { get; set; } = "application/octet-stream";

    [Range(1, 50_000_000)]
    public long FileSizeBytes { get; set; }

    [MaxLength(80)]
    public string? OrderNumber { get; set; }
}

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
