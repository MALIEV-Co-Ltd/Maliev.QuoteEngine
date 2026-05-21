namespace Maliev.QuoteEngine.Bff.Clients;

public interface ICountryServiceClient
{
    Task<HttpResponseMessage> GetCountryByIso2Async(string iso2, CancellationToken cancellationToken);
}

internal sealed class CountryServiceClient(HttpClient httpClient) : ICountryServiceClient
{
    public Task<HttpResponseMessage> GetCountryByIso2Async(string iso2, CancellationToken cancellationToken) =>
        httpClient.GetAsync($"/country/v1/countries/iso2/{Uri.EscapeDataString(iso2)}", cancellationToken);
}
