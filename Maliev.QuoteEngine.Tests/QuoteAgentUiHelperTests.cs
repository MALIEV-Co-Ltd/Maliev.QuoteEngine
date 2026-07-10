using Maliev.QuoteEngine.Client.Components.QuoteAgent;
using Maliev.QuoteEngine.Client.Models;
using Maliev.QuoteEngine.Shared.Agent;
using System.Globalization;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteAgentUiHelperTests
{
    [Fact]
    public void Visible_artifacts_exclude_uploaded_viewers_and_project_summaries_but_keep_generated_previews()
    {
        var artifacts = new[]
        {
            new QuoteAgentArtifactDto { ArtifactType = "viewer", Title = "3D viewer - Ring1.stl", Status = "Ready" },
            new QuoteAgentArtifactDto
            {
                ArtifactType = "viewer",
                Title = "Generated hand keychain preview",
                Status = "Ready",
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["generated"] = "true",
                    ["cad_commands"] = "[]"
                }
            },
            new QuoteAgentArtifactDto { ArtifactType = "dfm", Title = "DFM analysis - Ring1.stl", Status = "Ready" },
            new QuoteAgentArtifactDto { ArtifactType = "requirements_summary", Title = "Project summary", Status = "Ready" }
        };

        var visible = artifacts.Where(QuoteAgentUiHelpers.IsVisibleArtifact).ToList();

        Assert.Equal(2, visible.Count);
        Assert.DoesNotContain(visible, artifact =>
            string.Equals(artifact.ArtifactType, "viewer", StringComparison.OrdinalIgnoreCase) &&
            !artifact.Metadata.TryGetValue("generated", out _));
        Assert.DoesNotContain(visible, artifact =>
            string.Equals(artifact.ArtifactType, "requirements_summary", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(visible, artifact =>
            string.Equals(artifact.ArtifactType, "viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(visible, artifact => artifact.ArtifactType == "dfm");
    }

    [Fact]
    public void Chat_scroll_helper_reports_bottom_position()
    {
        Assert.True(QuoteAgentUiHelpers.IsAtScrollBottom(scrollTop: 100, scrollHeight: 200, clientHeight: 100));
        Assert.True(QuoteAgentUiHelpers.IsAtScrollBottom(scrollTop: 99, scrollHeight: 200, clientHeight: 100));
        Assert.False(QuoteAgentUiHelpers.IsAtScrollBottom(scrollTop: 50, scrollHeight: 200, clientHeight: 100));
    }

    [Fact]
    public void Reasoning_label_extracts_bold_reasoning_header()
    {
        var label = QuoteAgentUiHelpers.ExtractReasoningLabel("** Understanding the customer intent **\nI need to inspect the sketch.");

        Assert.Equal("Understanding the customer intent", label);
    }

    [Fact]
    public void Reasoning_label_uses_latest_bold_reasoning_header()
    {
        var label = QuoteAgentUiHelpers.ExtractReasoningLabel(
            """
            ** Understanding the customer intent **
            I need to infer the manufacturing requirements.

            ** Planning CAD operations **
            I will create construction geometry before extruding.
            """);

        Assert.Equal("Planning CAD operations", label);
    }

    [Fact]
    public void Reasoning_label_falls_back_to_cleaned_first_reasoning_line()
    {
        var label = QuoteAgentUiHelpers.ExtractReasoningLabel(
            "I am checking the uploaded sketch before choosing CAD operations.\nThe drawing has two visible holes.");

        Assert.Equal("I am checking the uploaded sketch before choosing CAD operations.", label);
    }

    [Fact]
    public void Reasoning_label_returns_null_for_empty_reasoning()
    {
        Assert.Null(QuoteAgentUiHelpers.ExtractReasoningLabel(null));
        Assert.Null(QuoteAgentUiHelpers.ExtractReasoningLabel(""));
        Assert.Null(QuoteAgentUiHelpers.ExtractReasoningLabel("  \r\n  "));
    }

    [Fact]
    public void Uploaded_part_metadata_formats_size_volume_and_bounding_box()
    {
        var part = new QuotePartViewModel
        {
            FileName = "Ring1.stl",
            ContentType = "model/stl",
            FileSizeBytes = 1_234_567,
            VolumeCc = 12.34m,
            BoundingBox = new QuoteBoundingBoxDto(10.5m, 8.2m, 4.1m)
        };

        var metadata = QuoteAgentUiHelpers.FormatUploadedPartMetadata(part);

        Assert.Equal("1.2 MB · 10.5 × 8.2 × 4.1 mm · 12.34 cm³", metadata);
    }

    [Theory]
    [InlineData(null, "Attached")]
    [InlineData("", "Attached")]
    [InlineData("Uploading", "Uploading")]
    [InlineData("WaitingForUpload", "Waiting to upload")]
    [InlineData("Processing", "Preparing preview")]
    [InlineData("GlbReady", "3D preview ready")]
    [InlineData("DfmAnalysisReady", "DFM analysis ready")]
    [InlineData("ModelGenerated", "3D preview ready")]
    [InlineData("Ready", "Ready")]
    [InlineData("Upload failed", "File unavailable")]
    [InlineData("InternalPipelineStage42", "In progress")]
    public void Customer_status_replaces_internal_file_states_with_safe_labels(string? status, string expected)
    {
        Assert.Equal(expected, QuoteAgentUiHelpers.FormatCustomerStatus(status));
    }

    [Fact]
    public void Volume_formatter_uses_units_appropriate_to_stored_cubic_centimetres()
    {
        Assert.Equal("0.1 mm³", QuoteAgentUiHelpers.FormatVolume(0.0001m));
        Assert.Equal("999 mm³", QuoteAgentUiHelpers.FormatVolume(0.999m));
        Assert.Equal("1 cm³", QuoteAgentUiHelpers.FormatVolume(1m));
        Assert.Equal("999999.999 cm³", QuoteAgentUiHelpers.FormatVolume(999_999.999m));
        Assert.Equal("1 m³", QuoteAgentUiHelpers.FormatVolume(1_000_000m));
    }

    [Fact]
    public void Volume_formatter_uses_invariant_decimal_output_and_sensible_precision()
    {
        var previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            Assert.Equal("12.346 cm³", QuoteAgentUiHelpers.FormatVolume(12.34567m));
            Assert.Equal("1.235 m³", QuoteAgentUiHelpers.FormatVolume(1_234_567.89m));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
