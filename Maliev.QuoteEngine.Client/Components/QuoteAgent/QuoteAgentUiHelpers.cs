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

    public static string? ExtractReasoningLabel(string? reasoning)
    {
        if (string.IsNullOrWhiteSpace(reasoning))
        {
            return null;
        }

        var fallback = default(string);
        var latestHeader = default(string);
        var normalized = reasoning
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (TryExtractBoldReasoningHeader(line, out var header))
            {
                latestHeader = header;
                continue;
            }

            fallback ??= CleanReasoningLabel(line);
        }

        return latestHeader ?? fallback;
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

    private static bool TryExtractBoldReasoningHeader(string line, out string? header)
    {
        header = null;
        var candidate = StripLeadingMarkdownPrefix(line);
        if (!candidate.StartsWith("**", StringComparison.Ordinal))
        {
            return false;
        }

        var end = candidate.IndexOf("**", 2, StringComparison.Ordinal);
        if (end <= 2)
        {
            return false;
        }

        header = CleanReasoningLabel(candidate[2..end]);
        return !string.IsNullOrWhiteSpace(header);
    }

    private static string StripLeadingMarkdownPrefix(string line)
    {
        var value = line.TrimStart();
        while (value.StartsWith("#", StringComparison.Ordinal))
        {
            value = value[1..].TrimStart();
        }

        if (value.StartsWith("- ", StringComparison.Ordinal) ||
            value.StartsWith("* ", StringComparison.Ordinal))
        {
            value = value[2..].TrimStart();
        }

        return value;
    }

    private static string CleanReasoningLabel(string value)
    {
        var cleaned = StripLeadingMarkdownPrefix(value)
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Trim();

        return string.Join(" ", cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
