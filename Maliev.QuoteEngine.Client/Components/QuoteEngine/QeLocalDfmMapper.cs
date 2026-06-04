using System.Globalization;
using Maliev.QuoteEngine.Client.Models;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Client.Components.QuoteEngine;

/// <summary>
/// Maps browser-first local geometry runtime results into QuoteEngine DFM report models.
/// </summary>
public static class QeLocalDfmMapper
{
    /// <summary>
    /// Applies a browser local DFM result to the matching process report slot on a quote part.
    /// </summary>
    /// <param name="part">The quote part to update.</param>
    /// <param name="result">The browser local DFM result.</param>
    /// <returns>True when the result was accepted and applied.</returns>
    public static bool TryApply(QuotePartViewModel part, LocalGeometryRuntimeResult result)
    {
        if (result is not
            {
                Authority: "local_primary",
                ExecutionMode: "primary_interactive",
                IsAuthoritative: false
            })
        {
            return false;
        }

        if (!MatchesProcess(part, result.ProcessCode))
        {
            return false;
        }

        var issues = result.Issues
            .Select(issue => new QeDfmIssueItem(
                issue.Severity ?? "warning",
                issue.Category ?? "local_geometry",
                issue.Description ?? issue.Title ?? "Detected by local browser DFM."))
            .ToList();

        if (IsSlaProcess(part.ProcessId))
        {
            part.SlaDfmReport = new QeSlaDfmReport(
                CountIssues(result, "thin_wall"),
                HasIssue(result, "resin_trap") || HasIssue(result, "escape_hole"),
                HasIssue(result, "overhang"),
                HasIssue(result, "hollow"),
                issues);
        }
        else if (IsCncProcess(part.ProcessId))
        {
            part.CncDfmReport = new QeCncDfmReport(
                CountIssues(result, "sharp_corner", "internal_radius"),
                HasIssue(result, "undercut"),
                HasIssue(result, "hole", "drill_hole"),
                CountIssues(result, "hole", "drill_hole"),
                HasIssue(result, "edm"),
                HasIssue(result, "grinding"),
                !HasIssue(result, "not_turnable"),
                issues);
        }
        else
        {
            var overhangFaceCount = result.Issues
                .Where(issue => IsIssue(issue, "overhang"))
                .Sum(issue => issue.FaceIndices.Count > 0 ? issue.FaceIndices.Count : 1);
            var overhangAreaCm2 = result.Issues
                .Where(issue => IsIssue(issue, "overhang"))
                .Sum(issue => issue.Value.GetValueOrDefault());

            part.FdmDfmReport = new QeFdmDfmReport(
                CountIssues(result, "thin_wall"),
                overhangFaceCount,
                (decimal)overhangAreaCm2,
                overhangFaceCount > 0,
                CountIssues(result, "small_feature"),
                issues);
        }

        ApplyLocalGeometryRuntimeMetrics(part, result.Metrics);
        part.Status = "DfmAnalysisReady";
        part.LocalDfmRuntimeUnavailable = false;
        part.LocalDfmRuntimeUnavailableReason = null;
        part.LocalDfmRuntimeRunningProcessId = null;
        part.LocalDfmRuntimeStartedAtUtc = null;
        return true;
    }

    private static void ApplyLocalGeometryRuntimeMetrics(
        QuotePartViewModel part,
        LocalGeometryRuntimeMetrics? metrics)
    {
        if (metrics is null)
        {
            return;
        }

        if (TryGetFiniteNonNegative(metrics.VolumeMm3, out var volumeMm3)
            && volumeMm3 <= (double)decimal.MaxValue)
        {
            part.VolumeCc = (decimal)volumeMm3 / 1_000m;
        }

        if (TryGetFiniteNonNegative(metrics.SurfaceAreaMm2, out var surfaceAreaMm2)
            && surfaceAreaMm2 <= (double)decimal.MaxValue)
        {
            part.SurfaceAreaCm2 = (decimal)surfaceAreaMm2 / 100m;
        }

        if (metrics.IsManifold.HasValue)
        {
            part.IsManifold = metrics.IsManifold.Value;
        }

        if (TryGetFiniteNonNegative(metrics.NonManifoldEdgeCount, out var edgeCountValue)
            && edgeCountValue > 0
            && edgeCountValue <= int.MaxValue)
        {
            var edgeCount = Math.Max(1, (int)Math.Round(edgeCountValue, MidpointRounding.AwayFromZero));
            part.IsManifold = false;
            part.NonManifoldReason = string.Create(
                CultureInfo.InvariantCulture,
                $"Browser local DFM found {edgeCount:N0} non-manifold edge(s).");
        }
        else if (metrics.IsManifold == true)
        {
            part.NonManifoldReason = null;
        }
    }

    private static bool TryGetFiniteNonNegative(double? value, out double number)
    {
        number = value.GetValueOrDefault();
        return value.HasValue && double.IsFinite(number) && number >= 0;
    }

    /// <summary>
    /// Returns true when a browser runtime process code matches the part's selected process.
    /// </summary>
    public static bool MatchesProcess(QuotePartViewModel part, string? processCode)
    {
        var expectedProcessCode = ToRuntimeProcessCode(part.ProcessId);
        var actualProcessCode = ToRuntimeProcessCode(processCode);
        return !string.IsNullOrWhiteSpace(actualProcessCode)
            && string.Equals(actualProcessCode, expectedProcessCode, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when the quote part has a DFM report for its currently selected process.
    /// </summary>
    public static bool HasCurrentProcessReport(QuotePartViewModel part) =>
        IsSlaProcess(part.ProcessId)
            ? part.SlaDfmReport is not null
            : IsCncProcess(part.ProcessId)
                ? part.CncDfmReport is not null
                : part.FdmDfmReport is not null;

    internal static bool IsSlaProcess(string? processCode) =>
        string.Equals(ToRuntimeProcessCode(processCode), "sla", StringComparison.Ordinal);

    internal static bool IsCncProcess(string? processCode) =>
        string.Equals(ToRuntimeProcessCode(processCode), "cnc", StringComparison.Ordinal);

    private static string ToRuntimeProcessCode(string? processCode)
    {
        if (string.IsNullOrWhiteSpace(processCode))
        {
            return string.Empty;
        }

        var normalized = processCode.Trim()
            .Replace("-", "_", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .Replace(" ", "_", StringComparison.Ordinal)
            .ToUpperInvariant();

        return normalized switch
        {
            "CNC" or "CNC_MILL" or "CNC_MILLING" or "MILLING" => "cnc",
            "CNC_TURN" or "CNC_TURNING" or "TURNING" or "LATHE" => "cnc",
            "CNC_5AXIS" or "CNC_5_AXIS" or "CNC_5AXIS_MILLING" or "CNC_5_AXIS_MILLING" => "cnc",
            _ when normalized.StartsWith("CNC_", StringComparison.Ordinal) => "cnc",
            "SLA" or "DLP" or "SLA_DLP" => "sla",
            "FDM" or "FFF" or "FUSED_FILAMENT_FABRICATION" => "fdm",
            _ => normalized.ToLowerInvariant()
        };
    }

    private static int CountIssues(LocalGeometryRuntimeResult result, params string[] categories) =>
        result.Issues.Count(issue => categories.Any(category => IsIssue(issue, category)));

    private static bool HasIssue(LocalGeometryRuntimeResult result, params string[] categories) =>
        result.Issues.Any(issue => categories.Any(category => IsIssue(issue, category)));

    private static bool IsIssue(LocalGeometryRuntimeIssue issue, string category) =>
        string.Equals(issue.Category, category, StringComparison.OrdinalIgnoreCase);
}
