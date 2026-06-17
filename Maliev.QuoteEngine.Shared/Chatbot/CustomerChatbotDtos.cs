namespace Maliev.QuoteEngine.Shared.Chatbot;

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
