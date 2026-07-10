using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Agent;

namespace Maliev.QuoteEngine.Tests;

public sealed class ThinkingStepSummarizerTests
{
    [Fact]
    public void Summarize_skips_when_summary_already_set()
    {
        var step = new QuoteAgentThinkingStepDto { Summary = "Already set" };
        ThinkingStepSummarizer.Summarize(step);
        Assert.Equal("Already set", step.Summary);
    }

    [Fact]
    public void Summarize_configuration_args_produces_quantity_material_process()
    {
        var step = new QuoteAgentThinkingStepDto
        {
            Type = "quote_update_part_configuration",
            Detail = """Arguments: {"process": "fdm", "quantity": 5000, "material": "pla"}"""
        };
        ThinkingStepSummarizer.Summarize(step);
        Assert.NotNull(step.Summary);
        Assert.Contains("5,000", step.Summary);
        Assert.Contains("PLA", step.Summary);
        Assert.Contains("FDM", step.Summary);
    }

    [Fact]
    public void Summarize_currency_only_args_matches_estimate_tool()
    {
        var step = new QuoteAgentThinkingStepDto
        {
            Type = "calculate_estimate",
            Detail = """Arguments: {"currency": "THB"}"""
        };
        ThinkingStepSummarizer.Summarize(step);
        Assert.NotNull(step.Summary);
        Assert.Contains("THB", step.Summary);
    }

    [Fact]
    public void Summarize_tool_name_fallback_for_get_state()
    {
        var step = new QuoteAgentThinkingStepDto
        {
            Type = "quote_get_state",
            Detail = """Arguments: {"sessionId": "abc-123"}"""
        };
        ThinkingStepSummarizer.Summarize(step);
        Assert.NotNull(step.Summary);
        Assert.Contains("state", step.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summarize_unknown_tool_with_no_useful_args_returns_null()
    {
        var step = new QuoteAgentThinkingStepDto
        {
            Type = "unknown_future_tool",
            Detail = """Arguments: {"sessionId": "abc-123", "customerId": "xyz"}"""
        };
        ThinkingStepSummarizer.Summarize(step);
        Assert.Null(step.Summary);
    }

    [Fact]
    public void Summarize_no_detail_falls_back_to_tool_name_match()
    {
        var step = new QuoteAgentThinkingStepDto { Type = "quote_calculate_estimate" };
        ThinkingStepSummarizer.Summarize(step);
        Assert.NotNull(step.Summary);
        Assert.Contains("Calculat", step.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summarize_register_uploads_returns_readable_sentence()
    {
        var step = new QuoteAgentThinkingStepDto
        {
            Type = "quote_register_uploads",
            Detail = """Arguments: {"uploadId": "id-1", "storagePath": "/files/x"}"""
        };
        ThinkingStepSummarizer.Summarize(step);
        Assert.NotNull(step.Summary);
        Assert.Contains("file", step.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summarize_prepare_formal_quote_reports_that_confirmation_is_still_required()
    {
        var step = new QuoteAgentThinkingStepDto
        {
            Type = "quote_prepare_formal_quote",
            Detail = """Arguments: {"requirements":"Generate the reviewed formal quote."}"""
        };

        ThinkingStepSummarizer.Summarize(step);

        Assert.Equal("Awaiting confirmation to prepare formal quote", step.Summary);
    }

    [Fact]
    public void Summarize_quote_generate_3d_preview_request_is_readable_without_ids()
    {
        var step = new QuoteAgentThinkingStepDto
        {
            Type = "quote_generate_3d_preview",
            Detail = """Arguments: {"description":"Rectangular mounting plate 100x50x3mm with 4 holes","process_hint":"fdm","command_count":13}"""
        };
        ThinkingStepSummarizer.Summarize(step);
        Assert.Equal("Generate 3D preview · Rectangular mounting plate 100x50x3mm with 4 holes (13 command(s))", step.Summary);
    }

    [Fact]
    public void Summarize_quote_generate_3d_preview_result_includes_command_count_and_omits_artifact_ids()
    {
        var step = new QuoteAgentThinkingStepDto
        {
            Type = "quote_generate_3d_preview",
            Detail = @"Arguments: {""success"":true,""artifact_id"":""ebe20307-b475-40f9-ab95-8da14c6f928c"",""part_id"":""564e818e-0480-48ef-bef1-60d49561beb9"",""description"":""Rectangular mounting plate 100x50x3mm with 4 holes"",""command_count"":13,""message"":""Generated 3D preview with 13 command(s): Rectangular mounting plate 100x50x3mm with 4 holes""}"
        };
        ThinkingStepSummarizer.Summarize(step);
        Assert.Contains("3D preview generated", step.Summary);
        Assert.Contains("13 command(s)", step.Summary);
        Assert.Contains("Rectangular mounting plate 100x50x3mm with 4 holes", step.Summary);
        Assert.DoesNotContain("ebe20307-b475-40f9-ab95-8da14c6f928c", step.Summary);
        Assert.DoesNotContain("564e818e-0480-48ef-bef1-60d49561beb9", step.Summary);
    }

    [Theory]
    [InlineData("quote_cad_start_design", """Arguments: {"description":"flat bracket sketch","revision":0}""", "Started CAD design")]
    [InlineData("quote_cad_apply_operations", """Arguments: {"operation_count":5,"revision":1,"stage":"solid_features"}""", "Applied 5 CAD operation")]
    [InlineData("quote_cad_observe_design", """Arguments: {"operation_count":5,"revision":1,"status":"ready_for_preview"}""", "Observed CAD design")]
    [InlineData("quote_cad_finalize_preview", """Arguments: {"success":true,"command_count":5,"revision":1,"description":"flat bracket sketch"}""", "Finalized CAD preview")]
    public void Summarize_cad_workbench_tools_reports_progress(string tool, string detail, string expected)
    {
        var step = new QuoteAgentThinkingStepDto
        {
            Type = tool,
            Detail = detail
        };

        ThinkingStepSummarizer.Summarize(step);

        Assert.NotNull(step.Summary);
        Assert.Contains(expected, step.Summary, StringComparison.OrdinalIgnoreCase);
    }
}
