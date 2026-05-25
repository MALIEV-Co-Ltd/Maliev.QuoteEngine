namespace Maliev.QuoteEngine.Client.Models;

/// <summary>
/// Customer-safe DFM issue projected into the quote workspace viewer overlay.
/// </summary>
public sealed record QeDfmOverlayIssue(
    string Icon,
    string Title,
    string Description,
    string Severity,
    string? Category,
    string? OverlayKey,
    string? OverlayUrl);
