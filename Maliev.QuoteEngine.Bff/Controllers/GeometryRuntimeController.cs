using Asp.Versioning;
using Maliev.QuoteEngine.Bff;
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
public sealed class GeometryRuntimeController(
    IQuoteGeometryRuntimeClient runtimeClient,
    BffMetrics bffMetrics,
    ILogger<GeometryRuntimeController> logger) : ControllerBase
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
        return await ProxyRuntimeAssetResponseAsync(
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

    private async Task<IActionResult> ProxyRuntimeAssetResponseAsync(
        HttpResponseMessage response,
        string fallbackContentType,
        CancellationToken ct)
    {
        var cacheControl = response.Headers.CacheControl?.ToString();
        if (!string.IsNullOrWhiteSpace(cacheControl))
        {
            Response.Headers.CacheControl = cacheControl;
        }

        var contentType = response.Content.Headers.ContentType?.ToString()
            ?? fallbackContentType;
        if (!response.IsSuccessStatusCode)
        {
            return new ContentResult
            {
                StatusCode = (int)response.StatusCode,
                Content = await response.Content.ReadAsStringAsync(ct),
                ContentType = contentType
            };
        }

        return new FileContentResult(
            await response.Content.ReadAsByteArrayAsync(ct),
            contentType);
    }

    /// <summary>
    /// Records that the browser-first local DFM runtime completed on the client.
    /// </summary>
    /// <param name="request">The browser runtime telemetry payload.</param>
    /// <returns>A no-content acknowledgement.</returns>
    [AllowAnonymous]
    [HttpPost("telemetry")]
    public IActionResult RecordTelemetry([FromBody] BrowserGeometryRuntimeTelemetryRequest request)
    {
        if (request.IsStarted)
        {
            bffMetrics.RecordBrowserDfmRuntimeStart(
                request.ProcessCode,
                request.Authority,
                request.ExecutionMode);

            logger.LogInformation(
                "Browser-first quote DFM local runtime started for process {ProcessCode}",
                request.ProcessCode);

            return NoContent();
        }

        if (request.IsTerminalUnavailable)
        {
            bffMetrics.RecordBrowserDfmRuntimeTerminalAttempt(
                request.ProcessCode,
                request.Reason,
                request.Authority,
                request.ExecutionMode);

            logger.LogInformation(
                "Browser-first quote DFM local runtime ended before report for process {ProcessCode}; reason={Reason}",
                request.ProcessCode,
                request.Reason);

            return NoContent();
        }

        bffMetrics.RecordBrowserDfmRuntimeCompletion(
            request.ProcessCode,
            request.Accepted,
            request.Authority,
            request.ExecutionMode);

        logger.LogInformation(
            "Browser-first quote DFM completed locally for process {ProcessCode}; accepted={Accepted}; issues={IssueCount}; warnings={WarningCount}; faces={FaceCount}",
            request.ProcessCode,
            request.Accepted,
            request.IssueCount,
            request.WarningCount,
            request.FaceCount);

        return NoContent();
    }
}

/// <summary>
/// Browser-first local DFM runtime telemetry emitted by the QuoteEngine viewer.
/// </summary>
public sealed class BrowserGeometryRuntimeTelemetryRequest
{
    /// <summary>The manufacturing process code analyzed by the browser runtime.</summary>
    public string? ProcessCode { get; set; }

    /// <summary>The browser runtime package version.</summary>
    public string? RuntimeVersion { get; set; }

    /// <summary>The browser DFM algorithm version.</summary>
    public string? AlgorithmVersion { get; set; }

    /// <summary>The runtime authority marker.</summary>
    public string? Authority { get; set; }

    /// <summary>The runtime execution mode marker.</summary>
    public string? ExecutionMode { get; set; }

    /// <summary>Telemetry event status, for example <c>started</c>, <c>complete</c>, or <c>unavailable</c>.</summary>
    public string? Status { get; set; }

    /// <summary>Low-cardinality reason why a local attempt ended before producing a report.</summary>
    public string? Reason { get; set; }

    /// <summary>Whether Blazor accepted and applied the local DFM result.</summary>
    public bool Accepted { get; set; }

    /// <summary>The number of local DFM issues produced by the browser runtime.</summary>
    public int? IssueCount { get; set; }

    /// <summary>The number of warning-or-higher local DFM issues.</summary>
    public int? WarningCount { get; set; }

    /// <summary>The number of triangle faces analyzed locally.</summary>
    public double? FaceCount { get; set; }

    /// <summary>The browser runtime input hash, logged only for correlation and never used as a metric tag.</summary>
    public string? InputHash { get; set; }

    /// <summary>Whether this payload represents a terminal local runtime attempt.</summary>
    public bool IsTerminalUnavailable =>
        string.Equals(Status, "unavailable", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(Reason);

    /// <summary>Whether this payload represents the start of a local runtime attempt.</summary>
    public bool IsStarted => string.Equals(Status, "started", StringComparison.OrdinalIgnoreCase);
}
