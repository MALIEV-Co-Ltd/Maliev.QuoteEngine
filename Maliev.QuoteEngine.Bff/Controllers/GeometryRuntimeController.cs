using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Clients;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maliev.QuoteEngine.Bff.Controllers;

/// <summary>
/// Proxies browser geometry runtime assets through the quote BFF origin.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}/geometry/runtime")]
public sealed class GeometryRuntimeController(IQuoteGeometryRuntimeClient runtimeClient) : ControllerBase
{
    /// <summary>
    /// Retrieves the latest browser geometry runtime manifest.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The proxied manifest response.</returns>
    [AllowAnonymous]
    [HttpGet("manifest")]
    public async Task<IActionResult> GetManifest(CancellationToken ct)
    {
        using var response = await runtimeClient.GetRuntimeManifestAsync(ct);
        return await ProxyRuntimeResponseAsync(
            response,
            "application/json; charset=utf-8",
            ct);
    }

    /// <summary>
    /// Retrieves an immutable browser geometry runtime asset by content-hashed name.
    /// </summary>
    /// <param name="assetName">The content-hashed asset name from the manifest.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The proxied asset response.</returns>
    [AllowAnonymous]
    [HttpGet("assets/{assetName}")]
    public async Task<IActionResult> GetAsset(string assetName, CancellationToken ct)
    {
        if (assetName.Contains('/', StringComparison.Ordinal) ||
            assetName.Contains('\\', StringComparison.Ordinal))
        {
            return NotFound();
        }

        using var response = await runtimeClient.GetRuntimeAssetAsync(assetName, ct);
        return await ProxyRuntimeResponseAsync(
            response,
            "text/javascript; charset=utf-8",
            ct);
    }

    private async Task<ContentResult> ProxyRuntimeResponseAsync(
        HttpResponseMessage response,
        string fallbackContentType,
        CancellationToken ct)
    {
        var content = await response.Content.ReadAsStringAsync(ct);
        var cacheControl = response.Headers.CacheControl?.ToString();
        if (!string.IsNullOrWhiteSpace(cacheControl))
        {
            Response.Headers.CacheControl = cacheControl;
        }

        return new ContentResult
        {
            StatusCode = (int)response.StatusCode,
            Content = content,
            ContentType = response.Content.Headers.ContentType?.ToString()
                ?? fallbackContentType
        };
    }
}
