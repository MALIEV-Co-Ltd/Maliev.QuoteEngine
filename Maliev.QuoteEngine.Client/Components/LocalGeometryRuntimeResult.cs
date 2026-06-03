namespace Maliev.QuoteEngine.Client.Components;

/// <summary>
/// Result produced by the GeometryService-owned browser runtime running in the customer viewer.
/// </summary>
public sealed class LocalGeometryRuntimeResult
{
    /// <summary>Manufacturing process code used by the browser runtime.</summary>
    public string? ProcessCode { get; set; }

    /// <summary>Runtime package version served by GeometryService.</summary>
    public string? RuntimeVersion { get; set; }

    /// <summary>DFM algorithm version used by the browser runtime.</summary>
    public string? AlgorithmVersion { get; set; }

    /// <summary>Authority marker for the result, expected to be local_primary.</summary>
    public string? Authority { get; set; }

    /// <summary>Execution mode marker for the result, expected to be primary_interactive.</summary>
    public string? ExecutionMode { get; set; }

    /// <summary>Whether the result is authoritative. Browser results must be false.</summary>
    public bool? IsAuthoritative { get; set; }

    /// <summary>Hash of the mesh input analyzed by the browser runtime.</summary>
    public string? InputHash { get; set; }

    /// <summary>Mesh metrics computed locally from the viewer geometry.</summary>
    public LocalGeometryRuntimeMetrics? Metrics { get; set; }

    /// <summary>DFM issues computed locally from the viewer geometry.</summary>
    public List<LocalGeometryRuntimeIssue> Issues { get; set; } = [];
}

/// <summary>
/// Mesh metrics produced by the browser geometry runtime.
/// </summary>
public sealed class LocalGeometryRuntimeMetrics
{
    /// <summary>Number of mesh vertices analyzed.</summary>
    public double? VertexCount { get; set; }

    /// <summary>Number of mesh triangle faces analyzed.</summary>
    public double? FaceCount { get; set; }

    /// <summary>Computed volume in cubic millimeters.</summary>
    public double? VolumeMm3 { get; set; }

    /// <summary>Computed surface area in square millimeters.</summary>
    public double? SurfaceAreaMm2 { get; set; }

    /// <summary>Whether the local mesh appears manifold.</summary>
    public bool? IsManifold { get; set; }

    /// <summary>Number of non-manifold edges found locally.</summary>
    public double? NonManifoldEdgeCount { get; set; }

    /// <summary>Runtime complexity bucket for the mesh.</summary>
    public string? Complexity { get; set; }
}

/// <summary>
/// DFM issue produced by the browser geometry runtime.
/// </summary>
public sealed class LocalGeometryRuntimeIssue
{
    /// <summary>Issue category, such as thin_wall or overhang.</summary>
    public string? Category { get; set; }

    /// <summary>Issue severity, such as warning or error.</summary>
    public string? Severity { get; set; }

    /// <summary>Short issue title.</summary>
    public string? Title { get; set; }

    /// <summary>Detailed issue description.</summary>
    public string? Description { get; set; }

    /// <summary>Measured issue value.</summary>
    public double? Value { get; set; }

    /// <summary>Threshold used for the local check.</summary>
    public double? Threshold { get; set; }

    /// <summary>Triangle face indices associated with the issue.</summary>
    public List<int> FaceIndices { get; set; } = [];

    /// <summary>Issue centroid in model coordinates.</summary>
    public List<double> Centroid { get; set; } = [];
}
