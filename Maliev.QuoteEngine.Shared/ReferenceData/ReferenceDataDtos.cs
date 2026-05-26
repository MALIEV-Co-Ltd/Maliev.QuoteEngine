namespace Maliev.QuoteEngine.Shared.ReferenceData;

/// <summary>
/// Customer-facing currency option loaded from CurrencyService.
/// </summary>
public sealed record CurrencyOptionDto(
    string Code,
    string Symbol,
    string Name,
    bool IsActive,
    bool IsPrimary);
