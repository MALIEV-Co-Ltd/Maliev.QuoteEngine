using System.Net.Http.Json;
using System.Text.Json;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>Calls DeliveryService shipping endpoints for customer-facing quote flows.</summary>
public interface IDeliveryServiceClient
{
    /// <summary>Gets available courier options.</summary>
    Task<IReadOnlyList<ShippingCourierDto>> GetShippingCouriersAsync(CancellationToken ct = default);

    /// <summary>Gets live shipping rate options.</summary>
    Task<ShippingRateResponseDto> GetShippingRatesAsync(ShippingRateRequestDto request, CancellationToken ct = default);

    /// <summary>Gets tracking status for a shipment.</summary>
    Task<ShippingTrackingDto?> GetShippingTrackingAsync(string trackingCode, CancellationToken ct = default);
}

internal sealed class DeliveryServiceClient(HttpClient http) : IDeliveryServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ShippingCourierDto>> GetShippingCouriersAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("/delivery/v1/shipping/couriers", ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        return await response.Content.ReadFromJsonAsync<List<ShippingCourierDto>>(JsonOptions, ct) ?? [];
    }

    public async Task<ShippingRateResponseDto> GetShippingRatesAsync(ShippingRateRequestDto request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("/delivery/v1/shipping/rates", request, JsonOptions, ct);
        if (!response.IsSuccessStatusCode)
        {
            return new ShippingRateResponseDto();
        }

        var downstreamRates = await response.Content.ReadFromJsonAsync<List<DownstreamShippingRateOption>>(JsonOptions, ct) ?? [];
        return new ShippingRateResponseDto
        {
            Rates = downstreamRates.Select(rate => new ShippingRateOptionDto
            {
                CourierCode = rate.CourierCode,
                ProductName = FirstNonEmpty(rate.CourierName, rate.CourierCode),
                TotalPrice = rate.Price,
                CurrencyCode = FirstNonEmpty(rate.Currency, "THB"),
                EstimatedDeliveryDate = rate.EstimatedDelivery,
                ServiceLevel = rate.ServiceLevel
            }).ToList()
        };
    }

    public async Task<ShippingTrackingDto?> GetShippingTrackingAsync(string trackingCode, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"/delivery/v1/shipping/tracking/{Uri.EscapeDataString(trackingCode)}", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<ShippingTrackingDto>(JsonOptions, ct);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private sealed class DownstreamShippingRateOption
    {
        public string CourierCode { get; set; } = string.Empty;

        public string CourierName { get; set; } = string.Empty;

        public decimal Price { get; set; }

        public string Currency { get; set; } = "THB";

        public string? ServiceLevel { get; set; }

        public string? EstimatedDelivery { get; set; }
    }
}
