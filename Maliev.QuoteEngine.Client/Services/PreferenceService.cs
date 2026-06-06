using Maliev.QuoteEngine.Shared.Localization;
using Microsoft.JSInterop;

namespace Maliev.QuoteEngine.Client.Services;

/// <summary>
/// Browser-backed display preferences shared by the Quote Engine shell and pages.
/// </summary>
public sealed class PreferenceService(IJSRuntime js)
{
    public const string LightTheme = "light";
    public const string DarkTheme = "dark";
    public const string DefaultCurrency = "THB";

    private bool _initialized;

    public event Action? Changed;

    public string Culture { get; private set; } = SupportedCultures.DefaultCulture;

    public string Theme { get; private set; } = LightTheme;

    public string Currency { get; private set; } = DefaultCurrency;

    public bool IsInitialized => _initialized;

    public bool IsDarkMode => string.Equals(Theme, DarkTheme, StringComparison.OrdinalIgnoreCase);

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        Culture = SupportedCultures.Apply(await js.InvokeAsync<string?>(
            "quoteEnginePreferences.resolveCulture",
            SupportedCultures.DefaultCulture));

        Theme = NormalizeTheme(await js.InvokeAsync<string?>(
            "quoteEnginePreferences.resolveTheme",
            LightTheme));

        Currency = NormalizeCurrency(await js.InvokeAsync<string?>(
            "quoteEnginePreferences.getPreference",
            "maliev.quote.currency"));

        await js.InvokeVoidAsync("quoteEnginePreferences.setCulture", Culture);
        await js.InvokeVoidAsync("quoteEnginePreferences.setTheme", Theme);

        _initialized = true;
        Changed?.Invoke();
    }

    public async Task SetCultureAsync(string? cultureName)
    {
        var normalized = SupportedCultures.Apply(cultureName);
        if (string.Equals(Culture, normalized, StringComparison.Ordinal))
        {
            return;
        }

        Culture = normalized;
        await js.InvokeVoidAsync("quoteEnginePreferences.setCulture", normalized);
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
        await js.InvokeVoidAsync("quoteEnginePreferences.setTheme", normalized);
        Changed?.Invoke();
    }

    public async Task SetCurrencyAsync(string? currency)
    {
        Currency = NormalizeCurrency(currency);
        await js.InvokeVoidAsync("quoteEnginePreferences.setPreference", "maliev.quote.currency", Currency);
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

    private static string NormalizeTheme(string? theme)
    {
        return string.Equals(theme, DarkTheme, StringComparison.OrdinalIgnoreCase)
            ? DarkTheme
            : LightTheme;
    }

    private static string NormalizeCurrency(string? currency)
    {
        return string.IsNullOrWhiteSpace(currency)
            ? DefaultCurrency
            : currency.Trim().ToUpperInvariant();
    }
}
