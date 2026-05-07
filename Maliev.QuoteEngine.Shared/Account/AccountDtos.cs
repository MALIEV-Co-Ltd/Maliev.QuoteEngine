using System.ComponentModel.DataAnnotations;

namespace Maliev.QuoteEngine.Shared.Account;

public sealed record CustomerProfileResponse(
    Guid CustomerId,
    string DisplayName,
    string Email,
    string Phone,
    string CompanyName,
    string PreferredLanguage);

public sealed record CustomerQuoteSummaryDto(
    Guid QuoteId,
    string QuoteNumber,
    string Status,
    decimal Total,
    string Currency,
    DateTimeOffset UpdatedAt,
    string PdfUrl);

public sealed record CustomerOrderSummaryDto(
    Guid OrderId,
    string OrderNumber,
    string Status,
    DateTimeOffset UpdatedAt,
    string TrackingLabel);

public sealed record CustomerNdaDto(Guid NdaId, string Title, string Status, DateTimeOffset UpdatedAt);

public sealed record CustomerDocumentDto(Guid DocumentId, string FileName, string Kind, DateTimeOffset UploadedAt);

public sealed class SignInRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

public sealed class SignUpRequest
{
    [Required]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    public string LastName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;

    public string CompanyName { get; set; } = string.Empty;
}

public sealed record AuthSessionResponse(Guid CustomerId, string DisplayName, string Email, bool IsAuthenticated);
