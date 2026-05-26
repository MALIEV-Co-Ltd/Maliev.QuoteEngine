namespace Maliev.QuoteEngine.Shared.Localization;

/// <summary>
/// Small bilingual text value used by customer UI and reference data.
/// </summary>
public sealed class LocalizedText
{
    /// <summary>
    /// English text.
    /// </summary>
    public string En { get; set; } = string.Empty;

    /// <summary>
    /// Thai text.
    /// </summary>
    public string Th { get; set; } = string.Empty;

    /// <summary>
    /// Returns the best text for the supplied culture.
    /// </summary>
    public string For(string? cultureName)
    {
        var normalized = SupportedCultures.Normalize(cultureName);
        return normalized == SupportedCultures.ThaiCulture && !string.IsNullOrWhiteSpace(Th)
            ? Th
            : En;
    }
}
