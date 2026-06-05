using System.Globalization;
using System.Text;
using Asp.Versioning;
using Maliev.QuoteEngine.Bff;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Polly.Timeout;

namespace Maliev.QuoteEngine.Bff.Controllers;

/// <summary>
/// Proxies browser geometry runtime assets through the quote BFF origin.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}/geometry/runtime")]
public sealed class GeometryRuntimeController(
    IQuoteGeometryRuntimeClient runtimeClient,
    IQuoteFileAnalysisStatusService statusService,
    GeometryRuntimeFallbackProvider runtimeFallbackProvider,
    BffMetrics bffMetrics,
    ILogger<GeometryRuntimeController> logger) : ControllerBase
{
    private static readonly string[] RuntimeExecutionHeaders =
    [
        "X-Maliev-Geometry-Execution-Mode",
        "X-Maliev-Geometry-Authority",
        "X-Maliev-Geometry-Server-Role"
    ];

    /// <summary>
    /// Retrieves the latest browser geometry runtime manifest.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The proxied manifest response.</returns>
    [AllowAnonymous]
    [HttpGet("manifest")]
    public async Task<IActionResult> GetManifest(CancellationToken ct)
    {
        try
        {
            using var response = await runtimeClient.GetRuntimeManifestAsync(ct);
            if (response.IsSuccessStatusCode)
            {
                return await ProxyRuntimeResponseAsync(
                    response,
                    "application/json; charset=utf-8",
                    ct);
            }

            logger.LogWarning(
                "GeometryService runtime manifest returned {StatusCode}; serving packaged QuoteEngine runtime fallback",
                (int)response.StatusCode);
            return RuntimeManifestFallbackResult(runtimeFallbackProvider.GetManifest());
        }
        catch (Exception ex) when (IsRuntimeDeliveryUnavailable(ex, ct))
        {
            logger.LogWarning(
                ex,
                "GeometryService runtime manifest was unavailable; serving packaged QuoteEngine runtime fallback");
            return RuntimeManifestFallbackResult(runtimeFallbackProvider.GetManifest());
        }
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

        if (runtimeFallbackProvider.TryGetAsset(assetName, out var packagedAsset))
        {
            return RuntimeAssetFallbackResult(packagedAsset!);
        }

        try
        {
            using var response = await runtimeClient.GetRuntimeAssetAsync(assetName, ct);
            if (response.IsSuccessStatusCode || !runtimeFallbackProvider.TryGetAsset(assetName, out var fallbackAsset))
            {
                return await ProxyRuntimeAssetResponseAsync(
                    response,
                    "text/javascript; charset=utf-8",
                    ct);
            }

            logger.LogWarning(
                "GeometryService runtime asset {AssetName} returned {StatusCode}; serving packaged QuoteEngine runtime fallback",
                assetName,
                (int)response.StatusCode);
            return RuntimeAssetFallbackResult(fallbackAsset!);
        }
        catch (Exception ex) when (IsRuntimeDeliveryUnavailable(ex, ct))
        {
            if (!runtimeFallbackProvider.TryGetAsset(assetName, out var fallbackAsset))
            {
                logger.LogWarning(
                    ex,
                    "GeometryService runtime asset {AssetName} was unavailable and no packaged fallback matched",
                    assetName);
                return NotFound();
            }

            logger.LogWarning(
                ex,
                "GeometryService runtime asset {AssetName} was unavailable; serving packaged QuoteEngine runtime fallback",
                assetName);
            return RuntimeAssetFallbackResult(fallbackAsset!);
        }
    }

    private ContentResult RuntimeManifestFallbackResult(GeometryRuntimeFallbackAsset asset)
    {
        ApplyRuntimeFallbackHeaders(asset);
        return new ContentResult
        {
            StatusCode = StatusCodes.Status200OK,
            Content = Encoding.UTF8.GetString(asset.Content),
            ContentType = asset.ContentType
        };
    }

    private FileContentResult RuntimeAssetFallbackResult(GeometryRuntimeFallbackAsset asset)
    {
        ApplyRuntimeFallbackHeaders(asset);
        return new FileContentResult(asset.Content, asset.ContentType);
    }

    private void ApplyRuntimeFallbackHeaders(GeometryRuntimeFallbackAsset asset)
    {
        Response.Headers.CacheControl = asset.CacheControl;
        Response.Headers["X-Maliev-Geometry-Execution-Mode"] = "primary_interactive";
        Response.Headers["X-Maliev-Geometry-Authority"] = "local_primary";
        Response.Headers["X-Maliev-Geometry-Server-Role"] = "fallback_and_final_validation";
    }

    private static bool IsRuntimeDeliveryUnavailable(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested &&
        ex is HttpRequestException
            or TaskCanceledException
            or OperationCanceledException
            or TimeoutRejectedException;

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
        ForwardRuntimeExecutionHeaders(response);

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
        ForwardRuntimeExecutionHeaders(response);

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
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A no-content acknowledgement.</returns>
    [AllowAnonymous]
    [HttpPost("telemetry")]
    public async Task<IActionResult> RecordTelemetry(
        [FromBody] BrowserGeometryRuntimeTelemetryRequest request,
        CancellationToken ct = default)
    {
        if (request.IsStarted)
        {
            bffMetrics.RecordBrowserDfmRuntimeStart(
                request.ProcessCode,
                request.Authority,
                request.ExecutionMode,
                request.InputByteCount,
                request.InputTriangleCount);

            logger.LogInformation(
                "Browser-first quote DFM local runtime started for process {ProcessCode}; inputBytes={InputByteCount}; inputTriangles={InputTriangleCount}",
                request.ProcessCode,
                request.InputByteCount,
                request.InputTriangleCount);

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
            request.ExecutionMode,
            request.InputByteCount,
            request.InputTriangleCount);

        if (request.Accepted
            && TryBuildLocalGeometryMetrics(
                request,
                out var volumeCc,
                out var surfaceAreaCm2,
                out var isManifold,
                out var nonManifoldReason))
        {
            await statusService.SetLocalGeometryMetricsAsync(
                request.StoragePath!,
                volumeCc,
                surfaceAreaCm2,
                isManifold,
                nonManifoldReason,
                ct);
        }

        logger.LogInformation(
            "Browser-first quote DFM completed locally for process {ProcessCode}; accepted={Accepted}; issues={IssueCount}; warnings={WarningCount}; faces={FaceCount}",
            request.ProcessCode,
            request.Accepted,
            request.IssueCount,
            request.WarningCount,
            request.FaceCount);

        return NoContent();
    }

    private static bool TryBuildLocalGeometryMetrics(
        BrowserGeometryRuntimeTelemetryRequest request,
        out decimal? volumeCc,
        out decimal? surfaceAreaCm2,
        out bool isManifold,
        out string? nonManifoldReason)
    {
        volumeCc = null;
        surfaceAreaCm2 = null;
        isManifold = request.Metrics?.IsManifold ?? true;
        nonManifoldReason = null;

        if (string.IsNullOrWhiteSpace(request.StoragePath) || request.Metrics is null)
        {
            return false;
        }

        if (TryGetFiniteNonNegative(request.Metrics.VolumeMm3, out var volumeMm3)
            && volumeMm3 <= (double)decimal.MaxValue)
        {
            volumeCc = (decimal)volumeMm3 / 1_000m;
        }

        if (TryGetFiniteNonNegative(request.Metrics.SurfaceAreaMm2, out var surfaceAreaMm2)
            && surfaceAreaMm2 <= (double)decimal.MaxValue)
        {
            surfaceAreaCm2 = (decimal)surfaceAreaMm2 / 100m;
        }

        if (TryGetFiniteNonNegative(request.Metrics.NonManifoldEdgeCount, out var edgeCountValue)
            && edgeCountValue > 0
            && edgeCountValue <= int.MaxValue)
        {
            var edgeCount = Math.Max(1, (int)Math.Round(edgeCountValue, MidpointRounding.AwayFromZero));
            isManifold = false;
            nonManifoldReason = string.Create(
                CultureInfo.InvariantCulture,
                $"Browser local DFM found {edgeCount:N0} non-manifold edge(s).");
        }

        return volumeCc.HasValue ||
            surfaceAreaCm2.HasValue ||
            request.Metrics.IsManifold.HasValue ||
            nonManifoldReason is not null;
    }

    private void ForwardRuntimeExecutionHeaders(HttpResponseMessage response)
    {
        foreach (var headerName in RuntimeExecutionHeaders)
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                Response.Headers[headerName] = string.Join(",", values);
            }
        }
    }

    private static bool TryGetFiniteNonNegative(double? value, out double number)
    {
        number = value.GetValueOrDefault();
        return value.HasValue && double.IsFinite(number) && number >= 0;
    }
}

/// <summary>
/// Browser-first local DFM runtime telemetry emitted by the QuoteEngine viewer.
/// </summary>
public sealed class BrowserGeometryRuntimeTelemetryRequest
{
    /// <summary>The storage path of the active quote part whose browser runtime produced the result.</summary>
    public string? StoragePath { get; set; }

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

    /// <summary>Mesh metrics computed locally by the browser runtime.</summary>
    public BrowserGeometryRuntimeMetrics? Metrics { get; set; }

    /// <summary>The browser runtime input hash, logged only for correlation and never used as a metric tag.</summary>
    public string? InputHash { get; set; }

    /// <summary>The approximate browser-local runtime input size in bytes.</summary>
    public long? InputByteCount { get; set; }

    /// <summary>The approximate browser-local runtime triangle workload.</summary>
    public long? InputTriangleCount { get; set; }

    /// <summary>Whether this payload represents a terminal local runtime attempt.</summary>
    public bool IsTerminalUnavailable =>
        string.Equals(Status, "unavailable", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(Reason);

    /// <summary>Whether this payload represents the start of a local runtime attempt.</summary>
    public bool IsStarted => string.Equals(Status, "started", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Local browser mesh metrics accepted from the GeometryService-owned browser runtime.
/// </summary>
public sealed class BrowserGeometryRuntimeMetrics
{
    /// <summary>Number of mesh vertices analyzed locally.</summary>
    public double? VertexCount { get; set; }

    /// <summary>Number of mesh triangle faces analyzed locally.</summary>
    public double? FaceCount { get; set; }

    /// <summary>Computed local volume in cubic millimeters.</summary>
    public double? VolumeMm3 { get; set; }

    /// <summary>Computed local surface area in square millimeters.</summary>
    public double? SurfaceAreaMm2 { get; set; }

    /// <summary>Computed local bounding box dimensions in millimeters.</summary>
    public BrowserGeometryRuntimeBoundingBox? BoundingBox { get; set; }

    /// <summary>Whether the local mesh appears manifold.</summary>
    public bool? IsManifold { get; set; }

    /// <summary>Number of non-manifold edges found locally.</summary>
    public double? NonManifoldEdgeCount { get; set; }

    /// <summary>Runtime complexity bucket for the mesh.</summary>
    public string? Complexity { get; set; }
}

/// <summary>
/// Local browser bounding box dimensions accepted from runtime telemetry.
/// </summary>
public sealed class BrowserGeometryRuntimeBoundingBox
{
    /// <summary>Width of the bounding box in millimeters.</summary>
    public double? X { get; set; }

    /// <summary>Depth of the bounding box in millimeters.</summary>
    public double? Y { get; set; }

    /// <summary>Height of the bounding box in millimeters.</summary>
    public double? Z { get; set; }
}
