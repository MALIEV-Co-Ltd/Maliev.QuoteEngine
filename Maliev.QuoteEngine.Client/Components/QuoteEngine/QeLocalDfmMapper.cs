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

        var expectedProcessCode = ToRuntimeProcessCode(part.ProcessId);
        if (string.IsNullOrWhiteSpace(result.ProcessCode)
            || !string.Equals(result.ProcessCode, expectedProcessCode, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var issues = result.Issues
            .Select(issue => new QeDfmIssueItem(
                issue.Severity ?? "warning",
                issue.Category ?? "local_geometry",
                issue.Description ?? issue.Title ?? "Detected by local browser DFM."))
            .ToList();

        if (part.ProcessId.Equals("sla", StringComparison.OrdinalIgnoreCase))
        {
            part.SlaDfmReport = new QeSlaDfmReport(
                CountIssues(result, "thin_wall"),
                HasIssue(result, "resin_trap") || HasIssue(result, "escape_hole"),
                HasIssue(result, "overhang"),
                HasIssue(result, "hollow"),
                issues);
        }
        else if (part.ProcessId.Equals("cnc", StringComparison.OrdinalIgnoreCase))
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

        part.Status = "DfmAnalysisReady";
        return true;
    }

    /// <summary>
    /// Returns true when the quote part has a DFM report for its currently selected process.
    /// </summary>
    public static bool HasCurrentProcessReport(QuotePartViewModel part) =>
        part.ProcessId.Equals("sla", StringComparison.OrdinalIgnoreCase)
            ? part.SlaDfmReport is not null
            : part.ProcessId.Equals("cnc", StringComparison.OrdinalIgnoreCase)
                ? part.CncDfmReport is not null
                : part.FdmDfmReport is not null;

    private static string ToRuntimeProcessCode(string processId) =>
        processId.Equals("sla", StringComparison.OrdinalIgnoreCase)
            ? "sla"
            : processId.Equals("cnc", StringComparison.OrdinalIgnoreCase)
                ? "cnc"
                : "fdm";

    private static int CountIssues(LocalGeometryRuntimeResult result, params string[] categories) =>
        result.Issues.Count(issue => categories.Any(category => IsIssue(issue, category)));

    private static bool HasIssue(LocalGeometryRuntimeResult result, params string[] categories) =>
        result.Issues.Any(issue => categories.Any(category => IsIssue(issue, category)));

    private static bool IsIssue(LocalGeometryRuntimeIssue issue, string category) =>
        string.Equals(issue.Category, category, StringComparison.OrdinalIgnoreCase);
}
