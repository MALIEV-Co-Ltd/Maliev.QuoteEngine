using System.ComponentModel.DataAnnotations;

namespace Maliev.QuoteEngine.Shared.Quotes;

/// <summary>Address payload used for customer shipping rate requests.</summary>
public sealed class ShippingAddressDto
{
    /// <summary>Contact name.</summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>Street address.</summary>
    [Required]
    public string Address { get; set; } = string.Empty;

    /// <summary>District or subdistrict.</summary>
    [Required]
    public string District { get; set; } = string.Empty;

    /// <summary>State or amphoe.</summary>
    [Required]
    public string State { get; set; } = string.Empty;

    /// <summary>Province.</summary>
    [Required]
    public string Province { get; set; } = string.Empty;

    /// <summary>Postal code.</summary>
    [Required]
    public string Postcode { get; set; } = string.Empty;

    /// <summary>ISO 3166-1 alpha-2 country code.</summary>
    public string CountryCode { get; set; } = "TH";

    /// <summary>Contact phone number.</summary>
    [Required]
    public string Tel { get; set; } = string.Empty;

    /// <summary>Contact email address.</summary>
    public string? Email { get; set; }
}

/// <summary>Parcel dimensions and weight used for live shipping rate requests.</summary>
public sealed class ShippingParcelDto
{
    /// <summary>Parcel display name.</summary>
    [Required]
    public string Name { get; set; } = "MALIEV shipment";

    /// <summary>Parcel weight in grams.</summary>
    [Range(1, double.MaxValue)]
    public decimal Weight { get; set; }

    /// <summary>Parcel width in centimeters.</summary>
    [Range(0.1, double.MaxValue)]
    public decimal Width { get; set; }

    /// <summary>Parcel length in centimeters.</summary>
    [Range(0.1, double.MaxValue)]
    public decimal Length { get; set; }

    /// <summary>Parcel height in centimeters.</summary>
    [Range(0.1, double.MaxValue)]
    public decimal Height { get; set; }
}

/// <summary>A quoted project part used for automatic package planning.</summary>
public sealed class ShippingPackagePartDto
{
    /// <summary>Part display name.</summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>Ordered quantity for this part.</summary>
    [Range(1, 100_000)]
    public int Quantity { get; set; } = 1;

    /// <summary>Single-part weight in grams.</summary>
    [Range(1, double.MaxValue)]
    public decimal Weight { get; set; }

    /// <summary>Part bounding-box width in centimeters.</summary>
    [Range(0.1, double.MaxValue)]
    public decimal Width { get; set; }

    /// <summary>Part bounding-box length in centimeters.</summary>
    [Range(0.1, double.MaxValue)]
    public decimal Length { get; set; }

    /// <summary>Part bounding-box height in centimeters.</summary>
    [Range(0.1, double.MaxValue)]
    public decimal Height { get; set; }

    /// <summary>Optional per-side wrapping margin in centimeters.</summary>
    public decimal? PackagingMargin { get; set; }
}

/// <summary>Package planning constraints and packaging material allowances.</summary>
public sealed class ShippingPackagingOptionsDto
{
    /// <summary>Default per-side wrapping margin around each part in centimeters.</summary>
    public decimal PartMargin { get; set; } = 1.5m;

    /// <summary>Per-side box filler margin in centimeters.</summary>
    public decimal BoxMargin { get; set; } = 3m;

    /// <summary>Default wrapping material weight per part in grams.</summary>
    public decimal PartPackagingWeight { get; set; } = 10m;

    /// <summary>Default carton/filler weight per box in grams.</summary>
    public decimal BoxPackagingWeight { get; set; } = 250m;

    /// <summary>Maximum preferred package weight in grams before splitting.</summary>
    public decimal MaxPackageWeight { get; set; } = 20_000m;

    /// <summary>Maximum preferred package length in centimeters before splitting.</summary>
    public decimal MaxPackageLength { get; set; } = 60m;

    /// <summary>Maximum preferred package width in centimeters before splitting.</summary>
    public decimal MaxPackageWidth { get; set; } = 45m;

    /// <summary>Maximum preferred package height in centimeters before splitting.</summary>
    public decimal MaxPackageHeight { get; set; } = 45m;
}

/// <summary>Request to fetch shipping rates from DeliveryService.</summary>
public sealed class ShippingRateRequestDto
{
    /// <summary>Origin address.</summary>
    [Required]
    public ShippingAddressDto From { get; set; } = new();

    /// <summary>Destination address.</summary>
    [Required]
    public ShippingAddressDto To { get; set; } = new();

    /// <summary>Parcel dimensions and weight.</summary>
    [Required]
    public ShippingParcelDto Parcel { get; set; } = new();

    /// <summary>Optional courier codes to restrict the rate request.</summary>
    public List<string> CourierCodes { get; set; } = [];

    /// <summary>Optional project parts. When present, DeliveryService calculates packages from bounding boxes.</summary>
    public List<ShippingPackagePartDto> Parts { get; set; } = [];

    /// <summary>Package planning constraints and material margins.</summary>
    public ShippingPackagingOptionsDto Packaging { get; set; } = new();

    /// <summary>Whether to use public SHIPPOP rate data.</summary>
    public bool UsePublicRates { get; set; }
}

/// <summary>Response containing available shipping rate options.</summary>
public sealed class ShippingRateResponseDto
{
    /// <summary>Available shipping rate options.</summary>
    public List<ShippingRateOptionDto> Rates { get; set; } = [];
}

/// <summary>A single customer-selectable courier rate option.</summary>
public sealed class ShippingRateOptionDto
{
    /// <summary>Courier code from the shipping gateway.</summary>
    public string CourierCode { get; set; } = string.Empty;

    /// <summary>Carrier product name.</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Total price.</summary>
    public decimal TotalPrice { get; set; }

    /// <summary>Currency code for the price.</summary>
    public string CurrencyCode { get; set; } = "THB";

    /// <summary>Estimated delivery date in ISO format.</summary>
    public string? EstimatedDeliveryDate { get; set; }

    /// <summary>Raw service level returned by the gateway.</summary>
    public string? ServiceLevel { get; set; }

    /// <summary>Courier logo URL.</summary>
    public string? CourierLogoUrl { get; set; }

    /// <summary>Number of packages included in this rate.</summary>
    public int PackageCount { get; set; } = 1;

    /// <summary>Total package weight in grams.</summary>
    public decimal TotalWeight { get; set; }

    /// <summary>Package breakdown used for this rate.</summary>
    public List<ShippingPackageQuoteDto> Packages { get; set; } = [];

    /// <summary>Shipping gateway that served this rate option.</summary>
    public string Provider { get; set; } = string.Empty;
}

/// <summary>Package breakdown used by a courier rate quote.</summary>
public sealed class ShippingPackageQuoteDto
{
    /// <summary>One-based package number.</summary>
    public int PackageNumber { get; set; }

    /// <summary>Package display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Package weight in grams.</summary>
    public decimal Weight { get; set; }

    /// <summary>Package width in centimeters.</summary>
    public decimal Width { get; set; }

    /// <summary>Package length in centimeters.</summary>
    public decimal Length { get; set; }

    /// <summary>Package height in centimeters.</summary>
    public decimal Height { get; set; }

    /// <summary>Whether this package exceeds preferred box constraints.</summary>
    public bool IsOversized { get; set; }

    /// <summary>Courier price for this package.</summary>
    public decimal Price { get; set; }

    /// <summary>Package quote currency.</summary>
    public string Currency { get; set; } = "THB";

    /// <summary>Courier lead-time text for this package.</summary>
    public string? EstimatedDelivery { get; set; }

    /// <summary>Items allocated into this package.</summary>
    public List<ShippingPackageItemDto> Items { get; set; } = [];
}

/// <summary>Part quantity allocated into a planned package.</summary>
public sealed class ShippingPackageItemDto
{
    /// <summary>Part name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Quantity of this part in the package.</summary>
    public int Quantity { get; set; }

    /// <summary>Wrapped unit width in centimeters.</summary>
    public decimal UnitWidth { get; set; }

    /// <summary>Wrapped unit length in centimeters.</summary>
    public decimal UnitLength { get; set; }

    /// <summary>Wrapped unit height in centimeters.</summary>
    public decimal UnitHeight { get; set; }

    /// <summary>Wrapped unit weight in grams.</summary>
    public decimal UnitWeight { get; set; }
}

/// <summary>Courier option exposed by DeliveryService.</summary>
public sealed class ShippingCourierDto
{
    /// <summary>Courier code from the shipping gateway.</summary>
    public string CourierCode { get; set; } = string.Empty;

    /// <summary>Courier display name.</summary>
    public string CourierName { get; set; } = string.Empty;

    /// <summary>Optional courier note.</summary>
    public string? Note { get; set; }

    /// <summary>Courier logo URL.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Shipping scope.</summary>
    public string Scope { get; set; } = "domestic";

    /// <summary>Shipping gateway that served this courier option.</summary>
    public string Provider { get; set; } = string.Empty;
}

/// <summary>Current shipment tracking status.</summary>
public sealed class ShippingTrackingDto
{
    /// <summary>Tracking code.</summary>
    public string TrackingCode { get; set; } = string.Empty;

    /// <summary>Courier code.</summary>
    public string? CourierCode { get; set; }

    /// <summary>Courier name.</summary>
    public string? CourierName { get; set; }

    /// <summary>Latest tracking status.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Latest status description.</summary>
    public string? Description { get; set; }

    /// <summary>Shipping gateway that served this tracking status.</summary>
    public string Provider { get; set; } = string.Empty;
}
