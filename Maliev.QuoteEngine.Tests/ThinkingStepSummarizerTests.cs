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
}
