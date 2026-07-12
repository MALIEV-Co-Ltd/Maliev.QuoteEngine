namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>
/// Represents an agent dependency failure that is safe to translate at the customer-facing API boundary.
/// </summary>
internal sealed class QuoteAgentBackendUnavailableException : Exception
{
    public const string ChatbotSessionErrorCode = "chatbot_session_unavailable";
    public const string DefaultCustomerTitle = "Assistant backend is temporarily unavailable.";
    public const string DefaultCustomerDetail = "Please try again in a moment.";

    public QuoteAgentBackendUnavailableException()
        : base(DefaultCustomerTitle)
    {
    }

    /// <summary>Gets the stable customer-facing error code.</summary>
    public string ErrorCode => ChatbotSessionErrorCode;

    /// <summary>Gets the customer-safe error title.</summary>
    public string CustomerTitle => DefaultCustomerTitle;

    /// <summary>Gets the customer-safe recovery guidance.</summary>
    public string CustomerDetail => DefaultCustomerDetail;

    /// <summary>Gets the customer-safe combined message used by the streaming contract.</summary>
    public string CustomerMessage => $"{CustomerTitle} {CustomerDetail}";
}
