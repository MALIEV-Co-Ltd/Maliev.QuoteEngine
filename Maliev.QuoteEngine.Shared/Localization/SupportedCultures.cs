using System.Globalization;

namespace Maliev.QuoteEngine.Shared.Localization;

/// <summary>
/// Supported customer-facing cultures for the Quote Engine.
/// </summary>
public static class SupportedCultures
{
    /// <summary>
    /// Default English culture.
    /// </summary>
    public const string DefaultCulture = "en-US";

    /// <summary>
    /// Thai customer culture.
    /// </summary>
    public const string ThaiCulture = "th-TH";

    /// <summary>
    /// Supported culture names.
    /// </summary>
    public static readonly string[] Names = [DefaultCulture, ThaiCulture];

    /// <summary>
    /// Normalize a user or browser supplied culture into a supported culture.
    /// </summary>
    public static string Normalize(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return DefaultCulture;
        }

        return cultureName.StartsWith("th", StringComparison.OrdinalIgnoreCase)
            ? ThaiCulture
            : DefaultCulture;
    }

    /// <summary>
    /// Apply the normalized culture to the current thread.
    /// </summary>
    public static string Apply(string? cultureName)
    {
        var normalized = Normalize(cultureName);
        var culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        return normalized;
    }
}
