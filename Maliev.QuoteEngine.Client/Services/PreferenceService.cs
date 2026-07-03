using System.Text.Json;
using Maliev.QuoteEngine.Shared.Localization;
using Microsoft.JSInterop;

namespace Maliev.QuoteEngine.Client.Services;

/// <summary>
/// Browser-backed display preferences shared by the Quote Engine shell and pages.
/// </summary>
public sealed class PreferenceService(IJSRuntime js)
{
    public const string SystemTheme = "system";
    public const string LightTheme = "light";
    public const string DarkTheme = "dark";
    public const string DefaultCurrency = "THB";
    public const string MotionReducedPreferenceKey = "maliev.quote.motion-reduced";
    public const string AutoCultureMode = "auto";
    public const string ManualCultureMode = "manual";
    public const string LightThemeProfileKey = "maliev.quote.theme.light";
    public const string DarkThemeProfileKey = "maliev.quote.theme.dark";
    public const string DefaultUiFont = "-apple-system, BlinkMacSystemFont, \"Segoe UI Variable Text\", \"Segoe UI\", \"Noto Sans\", \"Noto Sans Thai\", sans-serif";
    public const string DefaultCodeFont = "ui-monospace, \"SFMono-Regular\", \"SF Mono\", Consolas, \"Liberation Mono\", Menlo, monospace";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ThemeProfilePreference DefaultLightThemeProfile { get; } = new()
    {
        Accent = "#339CFF",
        Background = "#FFFFFF",
        Foreground = "#1A1C1F",
        UiFont = DefaultUiFont,
        CodeFont = DefaultCodeFont,
        TranslucentSidebar = true,
        Contrast = 45
    };

    public static ThemeProfilePreference DefaultDarkThemeProfile { get; } = new()
    {
        Accent = "#339CFF",
        Background = "#181818",
        Foreground = "#FFFFFF",
        UiFont = DefaultUiFont,
        CodeFont = DefaultCodeFont,
        TranslucentSidebar = true,
        Contrast = 68
    };

    private bool _initialized;

    public event Action? Changed;

    public string Culture { get; private set; } = SupportedCultures.DefaultCulture;

    public string CultureMode { get; private set; } = AutoCultureMode;

    public bool IsCultureAuto => string.Equals(CultureMode, AutoCultureMode, StringComparison.OrdinalIgnoreCase);

    public string Theme { get; private set; } = SystemTheme;

    public string ResolvedTheme { get; private set; } = LightTheme;

    public string Currency { get; private set; } = DefaultCurrency;

    public bool IsMotionReduced { get; private set; }

    public bool IsInitialized => _initialized;

    public bool IsDarkMode => string.Equals(ResolvedTheme, DarkTheme, StringComparison.OrdinalIgnoreCase);

    public ThemeProfilePreference LightThemeProfile { get; private set; } = DefaultLightThemeProfile;

    public ThemeProfilePreference DarkThemeProfile { get; private set; } = DefaultDarkThemeProfile;

    public ThemeProfilePreference ActiveThemeProfile => IsDarkMode ? DarkThemeProfile : LightThemeProfile;

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        Culture = SupportedCultures.Apply(await js.InvokeAsync<string?>(
            "quoteEnginePreferences.resolveCulture",
            SupportedCultures.DefaultCulture));

        CultureMode = NormalizeCultureMode(await js.InvokeAsync<string?>(
            "quoteEnginePreferences.resolveCultureMode",
            AutoCultureMode));

        Theme = NormalizeTheme(await js.InvokeAsync<string?>(
            "quoteEnginePreferences.resolveTheme",
            SystemTheme));

        LightThemeProfile = await LoadThemeProfileAsync(LightTheme);
        DarkThemeProfile = await LoadThemeProfileAsync(DarkTheme);

        Currency = NormalizeCurrency(await js.InvokeAsync<string?>(
            "quoteEnginePreferences.getPreference",
            "maliev.quote.currency"));

        IsMotionReduced = await js.InvokeAsync<bool>(
            "quoteEnginePreferences.resolveMotion",
            false);

        await js.InvokeVoidAsync("quoteEnginePreferences.setCulture", Culture);
        ResolvedTheme = NormalizeResolvedTheme(await js.InvokeAsync<string?>("quoteEnginePreferences.setTheme", Theme), Theme);
        await ApplyThemeProfileAsync();
        await js.InvokeVoidAsync("quoteEnginePreferences.setMotion", IsMotionReduced);

        _initialized = true;
        Changed?.Invoke();
    }

    public async Task SetCultureAsync(string? cultureName)
    {
        var normalized = SupportedCultures.Apply(cultureName);
        if (string.Equals(Culture, normalized, StringComparison.Ordinal) && string.Equals(CultureMode, ManualCultureMode, StringComparison.Ordinal))
        {
            return;
        }

        Culture = normalized;
        CultureMode = ManualCultureMode;
        await js.InvokeVoidAsync("quoteEnginePreferences.setCulture", normalized);
        Changed?.Invoke();
    }

    public async Task SetCultureAutoAsync()
    {
        var resolved = SupportedCultures.Apply(await js.InvokeAsync<string?>("quoteEnginePreferences.setCultureAuto"));
        Culture = resolved;
        CultureMode = AutoCultureMode;
        Changed?.Invoke();
    }

    public async Task SetThemeAsync(string? theme)
    {
        var normalized = NormalizeTheme(theme);
        if (string.Equals(Theme, normalized, StringComparison.Ordinal))
        {
            return;
        }

        Theme = normalized;
        ResolvedTheme = NormalizeResolvedTheme(await js.InvokeAsync<string?>("quoteEnginePreferences.setTheme", normalized), normalized);
        await ApplyThemeProfileAsync();
        Changed?.Invoke();
    }

    public async Task SetThemeProfileAsync(string theme, ThemeProfilePreference profile)
    {
        var normalizedTheme = NormalizeThemeProfileName(theme);
        var normalizedProfile = NormalizeThemeProfile(
            profile,
            normalizedTheme == DarkTheme ? DefaultDarkThemeProfile : DefaultLightThemeProfile);

        if (normalizedTheme == DarkTheme)
        {
            DarkThemeProfile = normalizedProfile;
        }
        else
        {
            LightThemeProfile = normalizedProfile;
        }

        await js.InvokeVoidAsync(
            "quoteEnginePreferences.setThemeProfile",
            normalizedTheme,
            normalizedProfile);

        if (string.Equals(ResolvedTheme, normalizedTheme, StringComparison.Ordinal))
        {
            await ApplyThemeProfileAsync();
        }

        Changed?.Invoke();
    }

    public Task ResetThemeProfileAsync(string theme)
    {
        var normalizedTheme = NormalizeThemeProfileName(theme);
        return SetThemeProfileAsync(
            normalizedTheme,
            normalizedTheme == DarkTheme ? DefaultDarkThemeProfile : DefaultLightThemeProfile);
    }

    public ThemeProfilePreference GetThemeProfile(string theme)
    {
        return NormalizeThemeProfileName(theme) == DarkTheme ? DarkThemeProfile : LightThemeProfile;
    }

    public async Task SetCurrencyAsync(string? currency)
    {
        Currency = NormalizeCurrency(currency);
        await js.InvokeVoidAsync("quoteEnginePreferences.setPreference", "maliev.quote.currency", Currency);
        Changed?.Invoke();
    }

    public async Task SetMotionReducedAsync(bool reduced)
    {
        if (IsMotionReduced == reduced)
        {
            return;
        }

        IsMotionReduced = reduced;
        await js.InvokeVoidAsync("quoteEnginePreferences.setMotion", IsMotionReduced);
        Changed?.Invoke();
    }

    public Task<string?> GetPreferenceAsync(string key)
    {
        return js.InvokeAsync<string?>("quoteEnginePreferences.getPreference", key).AsTask();
    }

    public async Task SetPreferenceAsync(string key, string? value)
    {
        await js.InvokeVoidAsync("quoteEnginePreferences.setPreference", key, value ?? string.Empty);
        Changed?.Invoke();
    }

    public string Text(string en, string th)
    {
        return SupportedCultures.Normalize(Culture) == SupportedCultures.ThaiCulture ? th : en;
    }

    private static string NormalizeCultureMode(string? mode)
    {
        return string.Equals(mode, ManualCultureMode, StringComparison.OrdinalIgnoreCase)
            ? ManualCultureMode
            : AutoCultureMode;
    }

    private static string NormalizeTheme(string? theme)
    {
        if (string.Equals(theme, DarkTheme, StringComparison.OrdinalIgnoreCase))
        {
            return DarkTheme;
        }

        if (string.Equals(theme, LightTheme, StringComparison.OrdinalIgnoreCase))
        {
            return LightTheme;
        }

        return SystemTheme;
    }

    private static string NormalizeResolvedTheme(string? resolvedTheme, string requestedTheme)
    {
        if (string.Equals(resolvedTheme, DarkTheme, StringComparison.OrdinalIgnoreCase))
        {
            return DarkTheme;
        }

        if (string.Equals(resolvedTheme, LightTheme, StringComparison.OrdinalIgnoreCase))
        {
            return LightTheme;
        }

        return string.Equals(requestedTheme, DarkTheme, StringComparison.Ordinal) ? DarkTheme : LightTheme;
    }

    private static string NormalizeThemeProfileName(string? theme)
    {
        return string.Equals(theme, DarkTheme, StringComparison.OrdinalIgnoreCase) ? DarkTheme : LightTheme;
    }

    private async Task<ThemeProfilePreference> LoadThemeProfileAsync(string theme)
    {
        var fallback = theme == DarkTheme ? DefaultDarkThemeProfile : DefaultLightThemeProfile;
        var key = theme == DarkTheme ? DarkThemeProfileKey : LightThemeProfileKey;
        var json = await GetPreferenceAsync(key);
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback;
        }

        try
        {
            return NormalizeThemeProfile(JsonSerializer.Deserialize<ThemeProfilePreference>(json, JsonOptions), fallback);
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private Task ApplyThemeProfileAsync()
    {
        return js.InvokeVoidAsync("quoteEnginePreferences.applyThemeProfile", ActiveThemeProfile).AsTask();
    }

    private static ThemeProfilePreference NormalizeThemeProfile(
        ThemeProfilePreference? profile,
        ThemeProfilePreference fallback)
    {
        return new ThemeProfilePreference
        {
            Accent = NormalizeHex(profile?.Accent, fallback.Accent),
            Background = NormalizeHex(profile?.Background, fallback.Background),
            Foreground = NormalizeHex(profile?.Foreground, fallback.Foreground),
            UiFont = NormalizeFontStack(profile?.UiFont, fallback.UiFont),
            CodeFont = NormalizeFontStack(profile?.CodeFont, fallback.CodeFont),
            TranslucentSidebar = profile?.TranslucentSidebar ?? fallback.TranslucentSidebar,
            Contrast = Math.Clamp(profile?.Contrast ?? fallback.Contrast, 0, 100)
        };
    }

    private static string NormalizeHex(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 4 &&
            trimmed[0] == '#' &&
            trimmed[1..].All(Uri.IsHexDigit))
        {
            return $"#{trimmed[1]}{trimmed[1]}{trimmed[2]}{trimmed[2]}{trimmed[3]}{trimmed[3]}".ToUpperInvariant();
        }

        return trimmed.Length == 7 &&
               trimmed[0] == '#' &&
               trimmed[1..].All(Uri.IsHexDigit)
            ? trimmed.ToUpperInvariant()
            : fallback;
    }

    private static string NormalizeFontStack(string? value, string fallback)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Length > 180 ||
            trimmed.Contains('<', StringComparison.Ordinal) ||
            trimmed.Contains('>', StringComparison.Ordinal) ||
            trimmed.Contains('{', StringComparison.Ordinal) ||
            trimmed.Contains('}', StringComparison.Ordinal))
        {
            return fallback;
        }

        return trimmed;
    }

    private static string NormalizeCurrency(string? currency)
    {
        return string.IsNullOrWhiteSpace(currency)
            ? DefaultCurrency
            : currency.Trim().ToUpperInvariant();
    }
}

public sealed record ThemeProfilePreference
{
    public string Accent { get; init; } = "#339CFF";

    public string Background { get; init; } = "#FFFFFF";

    public string Foreground { get; init; } = "#1A1C1F";

    public string UiFont { get; init; } = PreferenceService.DefaultUiFont;

    public string CodeFont { get; init; } = PreferenceService.DefaultCodeFont;

    public bool TranslucentSidebar { get; init; } = true;

    public int Contrast { get; init; } = 45;
}
