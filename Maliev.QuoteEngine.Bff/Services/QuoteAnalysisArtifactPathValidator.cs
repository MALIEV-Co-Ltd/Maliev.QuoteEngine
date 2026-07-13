namespace Maliev.QuoteEngine.Bff.Services;

internal static class QuoteAnalysisArtifactPathValidator
{
    internal static bool IsCanonicalUploadPath(string? path) =>
        path is not null &&
        path.Length > 0 &&
        string.Equals(path, path.Trim(), StringComparison.Ordinal) &&
        !path.StartsWith('/') &&
        !path.Contains('\\') &&
        !path.Contains("://", StringComparison.Ordinal) &&
        !path.Any(char.IsControl) &&
        path.Split('/').All(segment => segment is not "" and not "." and not "..");

    internal static bool IsHttpsCapability(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    internal static bool IsCanonicalViewerPath(string sourcePath, string? path) =>
        path is null ||
        string.Equals(path, sourcePath, StringComparison.Ordinal) ||
        string.Equals(path, sourcePath + "_viewer.glb", StringComparison.Ordinal);

    internal static bool IsCanonicalThumbnailPath(string sourcePath, string? path) =>
        path is null ||
        string.Equals(path, sourcePath + "_thumbnail_small.webp", StringComparison.Ordinal) ||
        string.Equals(path, sourcePath + "_thumbnail_large.webp", StringComparison.Ordinal);

    internal static bool IsCanonicalOverlayPath(string sourcePath, string path)
    {
        var prefix = sourcePath + "_";
        const string suffix = "_overlay.glb";
        return path.StartsWith(prefix, StringComparison.Ordinal) &&
            path.EndsWith(suffix, StringComparison.Ordinal) &&
            path.Length > prefix.Length + suffix.Length &&
            path[prefix.Length..^suffix.Length]
                .All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');
    }

    internal static bool AreCanonicalGeometryPaths(
        string sourcePath,
        string? viewerPath,
        string? thumbnailPath) =>
        IsCanonicalUploadPath(sourcePath) &&
        (viewerPath is null || IsCanonicalUploadPath(viewerPath)) &&
        (thumbnailPath is null || IsCanonicalUploadPath(thumbnailPath)) &&
        IsCanonicalViewerPath(sourcePath, viewerPath) &&
        IsCanonicalThumbnailPath(sourcePath, thumbnailPath);

    internal static bool AreCanonicalOverlayPaths(
        string sourcePath,
        IReadOnlyList<string> paths) =>
        IsCanonicalUploadPath(sourcePath) &&
        paths.All(path => IsCanonicalUploadPath(path) && IsCanonicalOverlayPath(sourcePath, path));

    internal static void RequireCanonicalGeometryPaths(
        string sourcePath,
        string? viewerPath,
        string? thumbnailPath)
    {
        if (!AreCanonicalGeometryPaths(sourcePath, viewerPath, thumbnailPath))
            throw new ArgumentException("Analysis preview paths must be exact derivatives of the upload storage path.");
    }

    internal static IReadOnlyList<string> RequireCanonicalOverlayPaths(
        string sourcePath,
        IEnumerable<string>? paths)
    {
        var exact = paths?.ToArray() ?? [];
        if (!AreCanonicalOverlayPaths(sourcePath, exact))
            throw new ArgumentException("Analysis overlay paths must be exact derivatives of the upload storage path.");
        return exact.Distinct(StringComparer.Ordinal).ToArray();
    }
}
