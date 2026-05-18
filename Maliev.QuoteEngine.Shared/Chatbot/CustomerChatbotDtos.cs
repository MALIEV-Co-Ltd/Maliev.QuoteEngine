using System.ComponentModel.DataAnnotations;

namespace Maliev.QuoteEngine.Shared.Chatbot;

/// <summary>
/// Customer-facing chatbot message request.
/// </summary>
public sealed class CustomerChatbotRequest
{
    /// <summary>Gets or sets the existing conversation session ID, when one is active.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>Gets or sets the customer message.</summary>
    [Required]
    [StringLength(1000, MinimumLength = 1)]
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets optional browser/account personalization notes for the assistant.</summary>
    [StringLength(1600)]
    public string? CustomerContext { get; set; }

    /// <summary>Gets or sets the preferred language code, either en or th.</summary>
    [RegularExpression("^(en|th)?$", ErrorMessage = "Language must be 'en' or 'th'.")]
    public string? Language { get; set; }
}

/// <summary>
/// Customer-facing chatbot response.
/// </summary>
public sealed class CustomerChatbotResponse
{
    /// <summary>Gets or sets the active conversation session ID.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>Gets or sets the downstream assistant message ID, when available.</summary>
    public Guid? MessageId { get; set; }

    /// <summary>Gets or sets the assistant response content.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Gets or sets the response role.</summary>
    public string Role { get; set; } = "assistant";

    /// <summary>Gets or sets the response language code.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Gets or sets whether the BFF rejected the message as outside MALIEV service topics.</summary>
    public bool IsOutOfScope { get; set; }

    /// <summary>Gets or sets the response creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets suggested follow-up actions returned by the chatbot.</summary>
    public List<CustomerChatbotActionDto> SuggestedActions { get; set; } = [];
}

/// <summary>
/// Customer-facing chatbot suggested action.
/// </summary>
public sealed class CustomerChatbotActionDto
{
    /// <summary>Gets or sets the visible action label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the action type.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Gets or sets optional action data.</summary>
    public string? Data { get; set; }
}

/// <summary>
/// Request to hydrate a prior customer-assistant session.
/// </summary>
public sealed class CustomerChatbotHydrateRequest
{
    /// <summary>Gets or sets the browser-visible session ID to hydrate.</summary>
    public Guid? SessionId { get; set; }
}

/// <summary>
/// Response containing hydrated customer-assistant messages.
/// </summary>
public sealed class CustomerChatbotHydrateResponse
{
    /// <summary>Gets or sets the active conversation session ID.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>Gets or sets whether persisted message history was hydrated.</summary>
    public bool Hydrated { get; set; }

    /// <summary>Gets or sets the current authentication state for the QuoteEngine browser session.</summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>Gets or sets the display name for the signed-in customer, when available.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets the customer email for the signed-in customer, when available.</summary>
    public string? Email { get; set; }

    /// <summary>Gets or sets the response language code.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Gets or sets a fallback continuation message when history could not be loaded.</summary>
    public string? ContinuationMessage { get; set; }

    /// <summary>Gets or sets the ordered hydrated messages.</summary>
    public List<CustomerChatbotHydratedMessageDto> Messages { get; set; } = [];
}

/// <summary>
/// Hydrated chatbot message row.
/// </summary>
public sealed class CustomerChatbotHydratedMessageDto
{
    /// <summary>Gets or sets the sender role.</summary>
    public string Role { get; set; } = "assistant";

    /// <summary>Gets or sets the message content.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Gets or sets the message creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Current QuoteEngine customer-assistant session state.
/// </summary>
public sealed class CustomerChatbotSessionResponse
{
    /// <summary>Gets or sets the active customer-assistant session ID, when known.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>Gets or sets whether the browser has a signed-in QuoteEngine customer session.</summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>Gets or sets the signed-in customer ID, when available.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>Gets or sets the signed-in customer display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets the signed-in customer email.</summary>
    public string? Email { get; set; }
}
