namespace Maliev.QuoteEngine.Bff.Clients;

public interface IRegistryServiceClient
{
    Task<HttpResponseMessage> SearchThaiLocationsAsync(string query, int limit, CancellationToken cancellationToken);
}

internal sealed class RegistryServiceClient(HttpClient httpClient) : IRegistryServiceClient
{
    public Task<HttpResponseMessage> SearchThaiLocationsAsync(string query, int limit, CancellationToken cancellationToken) =>
        httpClient.GetAsync($"/registry/v1/thai/addresses/autocomplete?query={Uri.EscapeDataString(query)}&limit={limit}", cancellationToken);
}
