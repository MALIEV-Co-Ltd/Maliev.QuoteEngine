using Maliev.QuoteEngine.Client.Components.QuoteAgent;
using Maliev.QuoteEngine.Client.Models;
using Maliev.QuoteEngine.Shared.Agent;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteAgentUiHelperTests
{
    [Fact]
    public void Visible_artifacts_exclude_redundant_viewer_entries()
    {
        var artifacts = new[]
        {
            new QuoteAgentArtifactDto { ArtifactType = "viewer", Title = "3D viewer - Ring1.stl", Status = "Ready" },
            new QuoteAgentArtifactDto { ArtifactType = "dfm", Title = "DFM analysis - Ring1.stl", Status = "Ready" },
            new QuoteAgentArtifactDto { ArtifactType = "summary", Title = "Project summary", Status = "Ready" }
        };

        var visible = artifacts.Where(QuoteAgentUiHelpers.IsVisibleArtifact).ToList();

        Assert.Equal(2, visible.Count);
        Assert.DoesNotContain(visible, artifact => string.Equals(artifact.ArtifactType, "viewer", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(visible, artifact => artifact.ArtifactType == "dfm");
        Assert.Contains(visible, artifact => artifact.ArtifactType == "summary");
    }

    [Fact]
    public void Chat_scroll_helper_reports_bottom_position()
    {
        Assert.True(QuoteAgentUiHelpers.IsAtScrollBottom(scrollTop: 100, scrollHeight: 200, clientHeight: 100));
        Assert.True(QuoteAgentUiHelpers.IsAtScrollBottom(scrollTop: 99, scrollHeight: 200, clientHeight: 100));
        Assert.False(QuoteAgentUiHelpers.IsAtScrollBottom(scrollTop: 50, scrollHeight: 200, clientHeight: 100));
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

        Assert.Equal("1.2 MB · 10.5 × 8.2 × 4.1 mm · 12.34 cc", metadata);
    }
}
