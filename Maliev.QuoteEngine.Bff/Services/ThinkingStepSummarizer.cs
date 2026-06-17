using System.Text.Json;
using System.Text.RegularExpressions;
using Maliev.QuoteEngine.Shared.Agent;

namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>
/// Synthesizes human-readable one-line summaries for agent thinking steps from structured tool arguments.
/// </summary>
internal static class ThinkingStepSummarizer
{
    public static void Summarize(QuoteAgentThinkingStepDto step)
    {
        if (!string.IsNullOrWhiteSpace(step.Summary))
            return;

        var args = TryParseArguments(step.Detail);
        step.Summary = BuildSummary(step.Type, args) ?? BuildSummary(step.Title, args);
    }

    private static string? BuildSummary(string? toolName, Dictionary<string, string>? args)
    {
        var tool = NormalizeTool(toolName);
        if (string.IsNullOrWhiteSpace(tool))
            return null;

        if (args is not null)
        {
            args.TryGetValue("quantity", out var qty);
            args.TryGetValue("material", out var material);
            args.TryGetValue("process", out var process);
            args.TryGetValue("currency", out var currency);
            args.TryGetValue("query", out var query);
            args.TryGetValue("language", out var language);

            // Configuration / RFQ extraction: quantity + material are the key signals
            if (!string.IsNullOrWhiteSpace(qty) && !string.IsNullOrWhiteSpace(material))
            {
                var qtyLabel = int.TryParse(qty, out var qtyNum) ? $"{qtyNum:N0}" : qty;
                var summary = $"{qtyLabel} × {material.ToUpperInvariant()}";
                if (!string.IsNullOrWhiteSpace(process))
                    summary += $" · {process.ToUpperInvariant()}";
                return summary;
            }

            if (!string.IsNullOrWhiteSpace(currency) &&
                (tool.Contains("calculat") || tool.Contains("estimate") || tool.Contains("quote") || tool.Contains("rfq")))
                return $"Estimating price in {currency.ToUpperInvariant()}";

            if (!string.IsNullOrWhiteSpace(query))
                return $"Searching for \"{query}\"";

            if (!string.IsNullOrWhiteSpace(language))
                return $"Switched to {HumanizeLanguage(language)}";
        }

        return tool switch
        {
            _ when tool.Contains("rfq") || tool.Contains("get_param") => "Reading quote requirements",
            _ when tool.Contains("calculat") || tool.Contains("estimate") => "Calculating price estimate",
            _ when tool.Contains("register_upload") || (tool.Contains("upload") && tool.Contains("register")) => "Registered uploaded files",
            _ when tool.Contains("get_state") => "Checked session state",
            _ when tool.Contains("get_project") || tool.Contains("project_summary") => "Reviewed project summary",
            _ when tool.Contains("get_ref") => "Loaded manufacturing catalog",
            _ when tool.Contains("get_account") || tool.Contains("account_context") => "Verified customer account",
            _ when tool.Contains("update_part") || tool.Contains("part_config") => "Updated part configuration",
            _ when tool.Contains("focus_ui") => "Updated workspace view",
            _ when tool.Contains("get_settings") => "Checked session settings",
            _ when tool.Contains("update_settings") => "Updated session settings",
            _ when tool.Contains("prepare_quote") || tool.Contains("formal_quote") => "Prepared formal quote",
            _ when tool.Contains("approve_quote") || tool.Contains("quote_approval") => "Approved formal quote",
            _ when tool.Contains("acknowledge") || tool.Contains("dfm") => "Reviewed DFM findings",
            _ when tool.Contains("checkout") => "Saved checkout details",
            _ when tool.Contains("create_order") => "Created manufacturing order",
            _ when tool.Contains("start_payment") || tool.Contains("payment") => "Started payment handoff",
            _ when tool.Contains("get_auth") || tool.Contains("auth_handoff") => "Prepared sign-in handoff",
            _ when tool.Contains("resume_project") => "Resumed customer project",
            _ when tool.Contains("draft_project") => "Prepared draft project",
            _ when tool.Contains("duplicate_project") => "Duplicated project",
            _ when tool.Contains("pin_project") => "Pinned project",
            _ when tool.Contains("archive_project") => "Archived project",
            _ when tool.Contains("search") => "Searched customer data",
            _ when tool.Contains("connector") => "Checked integrations",
            _ when tool.Contains("language") => "Switched interface language",
            _ => null
        };
    }

    private static string NormalizeTool(string? raw) =>
        (raw ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    private static string HumanizeLanguage(string code) => code.ToLowerInvariant() switch
    {
        "th" => "Thai",
        "en" => "English",
        _ => code
    };

    private static Dictionary<string, string>? TryParseArguments(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return null;

        string? jsonStr = null;
        if (detail.StartsWith("Arguments:", StringComparison.OrdinalIgnoreCase))
            jsonStr = detail["Arguments:".Length..].Trim();
        else if (detail.TrimStart() is { } t && (t.StartsWith('{') || t.StartsWith('[')))
            jsonStr = t;

        if (jsonStr is null)
            return null;

        try
        {
            var normalized = NormalizeJsonLiterals(jsonStr);
            using var doc = JsonDocument.Parse(normalized);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var value = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.ToString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null
                };
                if (value is not null)
                    result[prop.Name] = value;
            }
            return result.Count > 0 ? result : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeJsonLiterals(string json)
    {
        json = Regex.Replace(json, @"\bTrue\b", "true");
        json = Regex.Replace(json, @"\bFalse\b", "false");
        json = Regex.Replace(json, @"\bNull\b", "null");
        json = Regex.Replace(json, @"\bNone\b", "null");
        return json;
    }
}
