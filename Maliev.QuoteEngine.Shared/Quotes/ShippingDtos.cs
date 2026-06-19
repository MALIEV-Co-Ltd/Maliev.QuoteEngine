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

    /// <summary>Shipping scope.</summary>
    public string Scope { get; set; } = "domestic";
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
}
