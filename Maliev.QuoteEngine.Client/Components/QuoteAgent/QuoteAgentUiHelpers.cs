using Maliev.QuoteEngine.Client.Models;
using Maliev.QuoteEngine.Shared.Agent;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Client.Components.QuoteAgent;

public static class QuoteAgentUiHelpers
{
    public static bool IsVisibleArtifact(QuoteAgentArtifactDto artifact)
    {
        return !string.Equals(artifact.ArtifactType, "viewer", StringComparison.OrdinalIgnoreCase) ||
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAtScrollBottom(double scrollTop, double scrollHeight, double clientHeight)
    {
        return scrollHeight - scrollTop - clientHeight <= 2;
    }

    public static string FormatUploadedPartMetadata(QuotePartViewModel part)
    {
        var parts = new List<string>();

        if (part.FileSizeBytes > 0)
        {
            parts.Add(FormatFileSize(part.FileSizeBytes));
        }

        if (part.BoundingBox is not null)
        {
            parts.Add(FormatBoundingBox(part.BoundingBox));
        }

        if (part.VolumeCc > 0)
        {
            parts.Add($"{part.VolumeCc:0.##} cc");
        }
        else if (part.SurfaceAreaCm2 > 0)
        {
            parts.Add($"{part.SurfaceAreaCm2:0.##} cm²");
        }

        return string.Join(" · ", parts);
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes < 1_000)
        {
            return $"{bytes} B";
        }

        const double megabyte = 1_000_000d;
        if (bytes >= megabyte)
        {
            return $"{bytes / megabyte:0.#} MB";
        }

        const double kilobyte = 1_000d;
        return $"{bytes / kilobyte:0.#} KB";
    }

    private static string FormatBoundingBox(QuoteBoundingBoxDto boundingBox)
    {
        return $"{boundingBox.X:0.#} × {boundingBox.Y:0.#} × {boundingBox.Z:0.#} mm";
    }
}
