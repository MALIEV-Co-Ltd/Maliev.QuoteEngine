using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Maliev.QuoteEngine.Shared.ReferenceData;

namespace Maliev.QuoteEngine.Bff.Clients;

public interface ICurrencyServiceClient
{
    Task<IReadOnlyList<CurrencyOptionDto>> GetCurrenciesAsync(CancellationToken cancellationToken);
}

internal sealed class CurrencyServiceClient(HttpClient httpClient) : ICurrencyServiceClient
{
    public async Task<IReadOnlyList<CurrencyOptionDto>> GetCurrenciesAsync(CancellationToken cancellationToken)
    {
        var response = await httpClient.GetFromJsonAsync<CurrencyPageResponse>(
            "/currency/v1/currencies?pageSize=1000&isActive=true",
            cancellationToken);

        return response?.Items ?? [];
    }

    private sealed class CurrencyPageResponse
    {
        [JsonPropertyName("items")]
        public List<CurrencyOptionDto> Items { get; set; } = [];
    }
}
