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
    /// Applies a server analysis status refresh without erasing accepted browser-local DFM.
    /// </summary>
    /// <param name="part">The quote part to update.</param>
    /// <param name="status">The latest server analysis status.</param>
    public static void ApplyAnalysisStatus(
        QuotePartViewModel part,
        QuoteAnalysisStatusResponse status)
    {
        var hasCurrentBrowserReport = HasCurrentProcessReport(part);
        var hasIncomingServerReport = status.FdmReport is not null
            || status.SlaReport is not null
            || status.CncReport is not null;
        var preserveCurrentBrowserReport = hasCurrentBrowserReport && !hasIncomingServerReport;

        part.Status = preserveCurrentBrowserReport
            ? "DfmAnalysisReady"
            : status.Status;
        if (!preserveCurrentBrowserReport || status.VolumeCc > 0m)
        {
            part.VolumeCc = status.VolumeCc;
        }

        if (!preserveCurrentBrowserReport || status.SurfaceAreaCm2 > 0m)
        {
            part.SurfaceAreaCm2 = status.SurfaceAreaCm2;
        }

        part.GlbUrl = status.ViewerGlbUrl ?? part.GlbUrl;
        part.ViewerStoragePath = status.ViewerStoragePath ?? part.ViewerStoragePath;
        part.ViewerFileExtension = NormalizeViewerFileExtension(
            status.ViewerFileExtension,
            part.ViewerStoragePath ?? part.StoragePath);
        part.ThumbnailUrl = status.ThumbnailUrl ?? part.ThumbnailUrl;
        if (!preserveCurrentBrowserReport || status.Findings.Count > 0)
        {
            part.Findings = status.Findings;
        }

        if (!preserveCurrentBrowserReport)
        {
            part.IsManifold = status.IsManifold;
            part.BodyCount = status.BodyCount;
            part.NonManifoldReason = status.NonManifoldReason;
        }

        part.FdmDfmReport = status.FdmReport ?? part.FdmDfmReport;
        part.SlaDfmReport = status.SlaReport ?? part.SlaDfmReport;
        part.CncDfmReport = status.CncReport ?? part.CncDfmReport;
        if (!preserveCurrentBrowserReport || status.OverlayGlbUrls.Count > 0)
        {
            part.OverlayGlbUrls = status.OverlayGlbUrls;
        }
    }

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

        if (metrics.BoundingBoxMm is not null &&
            IsPositiveFinite(metrics.BoundingBoxMm.X) &&
            IsPositiveFinite(metrics.BoundingBoxMm.Y) &&
            IsPositiveFinite(metrics.BoundingBoxMm.Z))
        {
            part.BoundingBox = metrics.BoundingBoxMm;
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

    private static bool IsPositiveFinite(decimal value) => value > 0;

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

    private static string? NormalizeViewerFileExtension(
        string? fileExtension,
        string? storagePath)
    {
        var ext = !string.IsNullOrWhiteSpace(fileExtension)
            ? fileExtension.Trim()
            : Path.GetExtension(storagePath);

        if (string.IsNullOrWhiteSpace(ext))
        {
            return null;
        }

        return ext.StartsWith('.') ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant();
    }

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
