namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>
/// HTTP implementation for the GeometryService browser runtime contract.
/// </summary>
public sealed class QuoteGeometryRuntimeClient(HttpClient httpClient) : IQuoteGeometryRuntimeClient
{
    /// <inheritdoc />
    public Task<HttpResponseMessage> GetRuntimeManifestAsync(CancellationToken ct = default)
    {
        return httpClient.GetAsync(
            "/geometry/client-runtime/manifest.json",
            HttpCompletionOption.ResponseHeadersRead,
            ct);
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetRuntimeAssetAsync(
        string assetName,
        CancellationToken ct = default)
    {
        return httpClient.GetAsync(
            $"/geometry/client-runtime/assets/{Uri.EscapeDataString(assetName)}",
            HttpCompletionOption.ResponseHeadersRead,
            ct);
    }
}
