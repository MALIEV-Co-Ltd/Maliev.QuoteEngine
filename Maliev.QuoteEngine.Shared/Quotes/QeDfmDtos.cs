// Maliev.QuoteEngine.Shared/Quotes/QeDfmDtos.cs
namespace Maliev.QuoteEngine.Shared.Quotes;

/// <summary>
/// A single DFM issue item within a process-specific report.
/// Distinct from <see cref="DfmFindingDto"/> (top-level legacy findings):
/// issue items are scoped to a specific manufacturing process and are populated
/// after full DFM analysis, not after initial geometry analysis.
/// </summary>
public sealed record QeDfmIssueItem(
    string Severity,   // "warning" | "error"
    string Code,
    string Message);

/// <summary>FDM-specific DFM report for customer display.</summary>
public sealed record QeFdmDfmReport(
    int ThinWallCount,
    int OverhangFaceCount,
    decimal OverhangAreaCm2,
    bool SupportRequired,
    int SmallDetailCount,
    IReadOnlyList<QeDfmIssueItem> Issues);

/// <summary>SLA-specific DFM report for customer display.</summary>
public sealed record QeSlaDfmReport(
    int ThinWallCount,
    bool ResinTrappingRisk,
    bool SuctionRisk,
    bool HollowRegions,
    IReadOnlyList<QeDfmIssueItem> Issues);

/// <summary>CNC-specific DFM report for customer display.</summary>
public sealed record QeCncDfmReport(
    int SharpCornerCount,
    bool HasUndercuts,
    bool HasDrillHoles,
    int DrillHoleCount,
    bool RequiresEdm,
    bool RequiresGrinding,
    bool IsTurnable,
    IReadOnlyList<QeDfmIssueItem> Issues);
