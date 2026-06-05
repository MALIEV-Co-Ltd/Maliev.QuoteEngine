using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>
/// Serves a packaged browser geometry runtime when GeometryService runtime delivery is unavailable.
/// </summary>
public sealed class GeometryRuntimeFallbackProvider
{
    private const int ManifestVersion = 1;
    private const string RuntimeVersion = "1.0.0";
    private const string AlgorithmVersion = "browser-first-dfm-v1";
    private const string RuntimePrefix = "/geometry/client-runtime/assets/";
    private const string WorkerResourceSuffix = ".GeometryRuntimeFallback.client-geometry-runtime.worker.js";
    private static readonly byte[] WasmBytes = Convert.FromBase64String(
        "AGFzbQEAAAABEANgAAF/YAF/AX9gAn9/AX8DBAMAAQIHQwMPcnVudGltZV92ZXJzaW9u" +
        "AAAbdHJpYW5nbGVfY291bnRfZnJvbV9pbmRpY2VzAAEPaXNfd2l0aGluX2xpbWl0AAIK" +
        "FgMEAEEBCwcAIABBA24LBwAgACABTQs=");

    private readonly Lazy<byte[]> _workerBytes = new(LoadWorkerBytes);

    /// <summary>
    /// Builds the packaged runtime manifest response.
    /// </summary>
    /// <returns>The packaged runtime manifest asset.</returns>
    public GeometryRuntimeFallbackAsset GetManifest()
    {
        var manifest = new
        {
            manifestVersion = ManifestVersion,
            runtimeVersion = RuntimeVersion,
            algorithmVersion = AlgorithmVersion,
            runtimeKind = "browser-first-geometry",
            executionMode = "primary_interactive",
            authority = "local_primary",
            isAuthoritative = false,
            serverRole = "fallback_and_final_validation",
            minFrontendApiVersion = 1,
            deviceProfiles = new
            {
                mobile = new { maxInputBytes = 8 * 1024 * 1024, maxTriangles = 75_000, timeoutMs = 8_000 },
                tablet = new { maxInputBytes = 16 * 1024 * 1024, maxTriangles = 150_000, timeoutMs = 12_000 },
                desktop = new { maxInputBytes = 32 * 1024 * 1024, maxTriangles = 350_000, timeoutMs = 20_000 }
            },
            artifactPolicy = new
            {
                directBrowserViewerExtensions = new[] { ".glb", ".gltf", ".obj", ".stl" },
                browserViewableUploads = new
                {
                    viewerSource = "original_upload",
                    serverEagerMetrics = false,
                    serverGlbExport = false,
                    serverPreviewImages = false
                },
                serverGeneratedViewerUploads = new
                {
                    viewerSource = "generated_glb",
                    serverEagerMetrics = true,
                    serverGlbExport = true,
                    serverPreviewImages = true
                }
            },
            fallbackPolicy = new
            {
                fallbackOnUnsupportedDevice = true,
                fallbackOnTimeout = true,
                fallbackOnInputTooLarge = true,
                finalValidationRequired = true
            },
            assets = new
            {
                worker = $"{RuntimePrefix}{WorkerAssetName}",
                wasm = $"{RuntimePrefix}{WasmAssetName}"
            },
            capabilities = new
            {
                inputs = new
                {
                    meshBuffers = true,
                    binaryStl = true,
                    asciiStl = true,
                    obj = true,
                    glb = true,
                    gltf = true,
                    threeMf = true
                },
                localOperations = new[]
                {
                    "mesh_extraction",
                    "mesh_metrics",
                    "manifold_check",
                    "thin_feature_screening",
                    "process_dfm_screening",
                    "local_overlay_hints"
                },
                serverOperations = new[]
                {
                    "authoritative_dfm",
                    "durable_glb_artifacts",
                    "durable_preview_images",
                    "final_quote_validation"
                }
            }
        };

        return new GeometryRuntimeFallbackAsset(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)),
            "application/json; charset=utf-8",
            "no-cache");
    }

    /// <summary>
    /// Tries to resolve a packaged runtime asset by content-hashed file name.
    /// </summary>
    /// <param name="assetName">The requested asset file name.</param>
    /// <param name="asset">The packaged asset, when the file name is known.</param>
    /// <returns><c>true</c> when the asset name is part of the packaged runtime.</returns>
    public bool TryGetAsset(string assetName, out GeometryRuntimeFallbackAsset? asset)
    {
        if (string.Equals(assetName, WorkerAssetName, StringComparison.Ordinal))
        {
            asset = new GeometryRuntimeFallbackAsset(
                _workerBytes.Value,
                "text/javascript; charset=utf-8",
                "public, max-age=31536000, immutable");
            return true;
        }

        if (string.Equals(assetName, WasmAssetName, StringComparison.Ordinal))
        {
            asset = new GeometryRuntimeFallbackAsset(
                WasmBytes,
                "application/wasm",
                "public, max-age=31536000, immutable");
            return true;
        }

        asset = null;
        return false;
    }

    private string WorkerAssetName => $"client-geometry-runtime.{Hash(_workerBytes.Value)}.worker.js";

    private static string WasmAssetName => $"client-geometry-kernel.{Hash(WasmBytes)}.wasm";

    private static byte[] LoadWorkerBytes()
    {
        var assembly = typeof(GeometryRuntimeFallbackProvider).Assembly;
        var resourceName = assembly
            .GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(WorkerResourceSuffix, StringComparison.Ordinal));

        if (resourceName is null)
        {
            throw new InvalidOperationException("Packaged browser geometry runtime worker resource was not found.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Packaged browser geometry runtime worker resource could not be opened.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string Hash(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()[..16];
}

/// <summary>
/// Packaged browser geometry runtime response body plus delivery metadata.
/// </summary>
public sealed class GeometryRuntimeFallbackAsset
{
    /// <summary>
    /// Initializes a new packaged browser runtime response.
    /// </summary>
    /// <param name="content">The response payload bytes.</param>
    /// <param name="contentType">The HTTP content type.</param>
    /// <param name="cacheControl">The HTTP cache-control value.</param>
    public GeometryRuntimeFallbackAsset(byte[] content, string contentType, string cacheControl)
    {
        Content = content;
        ContentType = contentType;
        CacheControl = cacheControl;
    }

    /// <summary>The response payload bytes.</summary>
    public byte[] Content { get; }

    /// <summary>The HTTP content type.</summary>
    public string ContentType { get; }

    /// <summary>The HTTP cache-control value.</summary>
    public string CacheControl { get; }
}
