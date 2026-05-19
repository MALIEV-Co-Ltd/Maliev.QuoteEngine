// Maliev.QuoteEngine.Bff/Options/DemoModeOptions.cs
namespace Maliev.QuoteEngine.Bff.Options;

/// <summary>
/// Controls the demo short-circuit path for the MALIEV sample bracket file.
/// When <see cref="IsConfigured"/> is true, uploading a file whose name matches
/// <see cref="SampleFileName"/> returns pre-computed geometry results immediately
/// without triggering the real geometry pipeline.
/// </summary>
public sealed class DemoModeOptions
{
    public const string Section = "DemoMode";

    public string SampleFileName { get; set; } = "maliev-sample-bracket.step";

    /// <summary>Pre-signed or long-lived GLB URL for the sample bracket.</summary>
    public string? GlbUrl { get; set; }

    /// <summary>Pre-signed or long-lived thumbnail URL for the sample bracket (optional).</summary>
    public string? ThumbnailUrl { get; set; }

    /// <summary>True only when <see cref="GlbUrl"/> is configured; falls through to live pipeline otherwise.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(GlbUrl);
}
