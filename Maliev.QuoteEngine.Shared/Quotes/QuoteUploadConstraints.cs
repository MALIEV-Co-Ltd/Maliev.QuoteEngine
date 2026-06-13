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
    /// All CAD/3D file extensions that can satisfy the geometry gate.
    /// </summary>
    public static IReadOnlyList<string> SupportedCadExtensions { get; } =
        MeshFileExtensions
            .Concat(MachiningFileExtensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Drawing and office document extensions accepted as supplemental quote context.
    /// </summary>
    public static IReadOnlyList<string> SupplementalDocumentExtensions { get; } =
    [
        "pdf",
        "dxf",
        "dwg"
    ];

    /// <summary>
    /// Image extensions accepted as supplemental quote context.
    /// </summary>
    public static IReadOnlyList<string> SupplementalImageExtensions { get; } =
    [
        "jpg",
        "jpeg",
        "png",
        "webp",
        "heic"
    ];

    /// <summary>
    /// Archive extensions accepted for bundled quote attachments.
    /// </summary>
    public static IReadOnlyList<string> SupplementalArchiveExtensions { get; } =
    [
        "zip"
    ];

    /// <summary>
    /// All file extensions accepted by the Quote Engine upload entry point.
    /// </summary>
    public static IReadOnlyList<string> SupportedAttachmentExtensions { get; } =
        SupportedCadExtensions
            .Concat(SupplementalDocumentExtensions)
            .Concat(SupplementalImageExtensions)
            .Concat(SupplementalArchiveExtensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Human-readable extension list for upload helper text.
    /// </summary>
    public static string SupportedCadExtensionLabel =>
        string.Join(", ", SupportedCadExtensions.Select(extension => extension.ToUpperInvariant()));

    /// <summary>
    /// Human-readable extension list for all accepted quote attachments.
    /// </summary>
    public static string SupportedAttachmentExtensionLabel =>
        string.Join(", ", SupportedAttachmentExtensions.Select(extension => extension.ToUpperInvariant()));

    /// <summary>
    /// Browser file picker accept value for supported CAD extensions.
    /// </summary>
    public static string SupportedCadAccept =>
        string.Join(",", SupportedCadExtensions.Select(extension => $".{extension}"));

    /// <summary>
    /// Browser file picker accept value for all supported quote attachments.
    /// </summary>
    public static string SupportedAttachmentAccept =>
        string.Join(",", SupportedAttachmentExtensions.Select(extension => $".{extension}"));

    /// <summary>
    /// Determines whether the supplied file name has an extension supported by Quote Engine uploads.
    /// </summary>
    public static bool IsSupportedCadFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.');
        return SupportedCadExtensions.Any(supported =>
            supported.Equals(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Determines whether the supplied file name has an extension supported by Quote Engine uploads.
    /// </summary>
    public static bool IsSupportedAttachmentFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.');
        return SupportedAttachmentExtensions.Any(supported =>
            supported.Equals(extension, StringComparison.OrdinalIgnoreCase));
    }
}
