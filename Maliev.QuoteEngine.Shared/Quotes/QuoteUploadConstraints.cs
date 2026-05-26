namespace Maliev.QuoteEngine.Shared.Quotes;

/// <summary>
/// Central customer-facing upload limits for the Quote Engine.
/// </summary>
public static class QuoteUploadConstraints
{
    /// <summary>
    /// Maximum customer upload size accepted by the Quote Engine.
    /// </summary>
    public const long MaxFileSizeBytes = 200L * 1024 * 1024;

    /// <summary>
    /// Maximum customer upload size in megabytes for UI copy and validation messages.
    /// </summary>
    public const int MaxFileSizeMegabytes = 200;

    /// <summary>
    /// File extensions routed to mesh-based additive processes.
    /// </summary>
    public static IReadOnlyList<string> MeshFileExtensions { get; } =
    [
        "stl",
        "3mf",
        "obj",
        "gltf",
        "glb",
        "ply",
        "off",
        "amf",
        "wrl",
        "x3d"
    ];

    /// <summary>
    /// File extensions routed to machining processes.
    /// </summary>
    public static IReadOnlyList<string> MachiningFileExtensions { get; } =
    [
        "step",
        "stp",
        "iges",
        "igs",
        "x_t",
        "x_b",
        "sat",
        "sab",
        "sldprt",
        "sldasm",
        "prt",
        "asm",
        "catpart",
        "catproduct",
        "jt",
        "3dxml",
        "3dm",
        "brep"
    ];

    /// <summary>
    /// All CAD file extensions accepted by the Quote Engine upload entry point.
    /// </summary>
    public static IReadOnlyList<string> SupportedCadExtensions { get; } =
    [
        "stl",
        "step",
        "stp",
        "3mf",
        "obj",
        "igs",
        "iges",
        "gltf",
        "glb",
        "ply",
        "off",
        "amf",
        "wrl",
        "x3d",
        "x_t",
        "x_b",
        "sat",
        "sab",
        "sldprt",
        "sldasm",
        "prt",
        "asm",
        "catpart",
        "catproduct",
        "jt",
        "3dxml",
        "3dm",
        "brep"
    ];

    /// <summary>
    /// Human-readable extension list for upload helper text.
    /// </summary>
    public static string SupportedCadExtensionLabel =>
        string.Join(", ", SupportedCadExtensions.Select(extension => extension.ToUpperInvariant()));

    /// <summary>
    /// Browser file picker accept value for supported CAD extensions.
    /// </summary>
    public static string SupportedCadAccept =>
        string.Join(",", SupportedCadExtensions.Select(extension => $".{extension}"));

    /// <summary>
    /// Determines whether the supplied file name has an extension supported by Quote Engine uploads.
    /// </summary>
    public static bool IsSupportedCadFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.');
        return SupportedCadExtensions.Any(supported =>
            supported.Equals(extension, StringComparison.OrdinalIgnoreCase));
    }
}
