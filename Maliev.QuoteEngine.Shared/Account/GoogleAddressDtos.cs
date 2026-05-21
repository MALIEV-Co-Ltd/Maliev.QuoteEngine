namespace Maliev.QuoteEngine.Shared.Account;

public sealed class GoogleAddressConfigResponse
{
    public string ApiKey { get; set; } = string.Empty;

    public string? MapId { get; set; }

    public double DefaultLatitude { get; set; } = 13.7563;

    public double DefaultLongitude { get; set; } = 100.5018;

    public int DefaultZoom { get; set; } = 12;

    public string[] IncludedRegionCodes { get; set; } = ["th"];
}

public sealed class GoogleAddressSelection
{
    public string Source { get; set; } = "GooglePlace";

    public string? PlaceId { get; set; }

    public string? FormattedAddress { get; set; }

    public string? AddressLine1 { get; set; }

    public string? District { get; set; }

    public string? City { get; set; }

    public string? StateProvince { get; set; }

    public string? PostalCode { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }
}
