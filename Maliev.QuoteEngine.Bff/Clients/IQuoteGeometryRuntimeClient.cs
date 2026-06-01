namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>
/// Client for retrieving browser geometry runtime metadata and immutable assets from GeometryService.
/// </summary>
public interface IQuoteGeometryRuntimeClient
{
    /// <summary>
    /// Retrieves the latest compatible browser geometry runtime manifest.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The downstream GeometryService response.</returns>
    Task<HttpResponseMessage> GetRuntimeManifestAsync(CancellationToken ct = default);

    /// <summary>
    /// Retrieves an immutable browser geometry runtime asset by content-hashed name.
    /// </summary>
    /// <param name="assetName">The content-hashed asset name from the manifest.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The downstream GeometryService response.</returns>
    Task<HttpResponseMessage> GetRuntimeAssetAsync(
        string assetName,
        CancellationToken ct = default);
}
