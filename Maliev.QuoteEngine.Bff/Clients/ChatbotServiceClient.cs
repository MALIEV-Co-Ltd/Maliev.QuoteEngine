using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Maliev.QuoteEngine.Shared.Agent;
using Maliev.QuoteEngine.Shared.Chatbot;

namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>
/// Client for ChatbotService customer-assistant operations.
/// </summary>
public interface IChatbotServiceClient
{
    /// <summary>Checks whether ChatbotService is ready to receive assistant traffic.</summary>
    Task<bool> CheckReadinessAsync(CancellationToken cancellationToken);

    /// <summary>Initiates a chatbot session.</summary>
    Task<ChatbotSessionResponse?> InitiateSessionAsync(ChatbotInitiateSessionRequest request, CancellationToken cancellationToken);

    /// <summary>Sends a message to a chatbot session.</summary>
    Task<ChatbotMessageResponse?> SendMessageAsync(ChatbotSendMessageRequest request, CancellationToken cancellationToken);

    /// <summary>Streams a message response from a chatbot session.</summary>
    IAsyncEnumerable<ChatbotMessageStreamEvent> SendMessageStreamAsync(
        ChatbotSendMessageRequest request,
        CancellationToken cancellationToken);

    /// <summary>Gets internal session messages for BFF-owned handoff hydration.</summary>
    Task<ChatbotConversationMessagesResponse?> GetConversationMessagesAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Deletes the last turn of a conversation session so an edited message can be resubmitted.</summary>
    Task<bool> TruncateLastTurnAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Cleans up raw dictated speech text using Gemini directly.</summary>
    Task<string?> CleanSpeechAsync(string speech, string language, CancellationToken cancellationToken);
}

internal sealed class ChatbotServiceClient(HttpClient httpClient, ILogger<ChatbotServiceClient> logger) : IChatbotServiceClient
{
    private static readonly TimeSpan ChatbotReadinessTimeout = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions SnakeCaseJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<bool> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var readinessCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readinessCts.CancelAfter(ChatbotReadinessTimeout);
            using var response = await httpClient.GetAsync("/chatbot/readiness", readinessCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "ChatbotService readiness returned {StatusCode}.",
                    response.StatusCode);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "ChatbotService readiness check timed out.");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "ChatbotService readiness check failed.");
            return false;
        }
    }

    public Task<ChatbotSessionResponse?> InitiateSessionAsync(ChatbotInitiateSessionRequest request, CancellationToken cancellationToken)
    {
        return SendAsync<ChatbotInitiateSessionRequest, ChatbotSessionResponse>(
            HttpMethod.Post,
            "/chatbot/v1/sessions/initiate",
            request,
            "initiating chatbot session",
            cancellationToken);
    }

    public Task<ChatbotMessageResponse?> SendMessageAsync(ChatbotSendMessageRequest request, CancellationToken cancellationToken)
    {
        return SendAsync<ChatbotSendMessageRequest, ChatbotMessageResponse>(
            HttpMethod.Post,
            "/chatbot/v1/messages",
            request,
            "sending chatbot message",
            cancellationToken);
    }

    public async IAsyncEnumerable<ChatbotMessageStreamEvent> SendMessageStreamAsync(
        ChatbotSendMessageRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await SendAsync<ChatbotSendMessageRequest, ChatbotMessageResponse>(
            HttpMethod.Post,
            "/chatbot/v1/messages",
            request,
            "sending chatbot message for streamed quote agent turn",
            cancellationToken);
        if (response is null)
        {
            yield return new ChatbotMessageStreamEvent
            {
                Type = "error",
                Error = "ChatbotService message request failed."
            };
            yield break;
        }

        yield return new ChatbotMessageStreamEvent
        {
            Type = "final",
            Message = response
        };
    }

    public async Task<ChatbotConversationMessagesResponse?> GetConversationMessagesAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                $"/chatbot/v1/internal/sessions/{sessionId:D}/messages",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "ChatbotService history hydration failed with {StatusCode} for session {SessionId}",
                    response.StatusCode,
                    sessionId);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ChatbotConversationMessagesResponse>(SnakeCaseJson, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ChatbotService history hydration failed for session {SessionId}", sessionId);
            return null;
        }
    }

    public async Task<bool> TruncateLastTurnAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.DeleteAsync(
                $"/chatbot/v1/internal/sessions/{sessionId:D}/messages/last-turn",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "ChatbotService last-turn truncation failed with {StatusCode} for session {SessionId}",
                    response.StatusCode,
                    sessionId);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ChatbotService last-turn truncation failed for session {SessionId}", sessionId);
            return false;
        }
    }

    public async Task<string?> CleanSpeechAsync(string speech, string language, CancellationToken cancellationToken)
    {
        var result = await SendAsync<CleanSpeechRequestBody, CleanSpeechResponseBody>(
            HttpMethod.Post,
            "/chatbot/v1/extraction/clean-speech",
            new CleanSpeechRequestBody { Speech = speech, Language = language },
            "cleaning speech text",
            cancellationToken);
        return result?.CleanedText;
    }

    private sealed class CleanSpeechRequestBody
    {
        public string Speech { get; set; } = string.Empty;
        public string Language { get; set; } = "en";
    }

    private sealed class CleanSpeechResponseBody
    {
        public string CleanedText { get; set; } = string.Empty;
    }

    private async Task<TResponse?> SendAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest request,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(method, path)
            {
                Content = JsonContent.Create(request, options: SnakeCaseJson)
            };
            using var response = await httpClient.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "ChatbotService returned {StatusCode} while {Operation}.",
                    response.StatusCode,
                    operation);
                return default;
            }

            return await response.Content.ReadFromJsonAsync<TResponse>(SnakeCaseJson, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ChatbotService failed while {Operation}.", operation);
            return default;
        }
    }
}

/// <summary>ChatbotService session initiation request.</summary>
public sealed class ChatbotInitiateSessionRequest
{
    /// <summary>Gets or sets the conversation channel.</summary>
    public string Channel { get; set; } = "website";

    /// <summary>Gets or sets the preferred language.</summary>
    public string Language { get; set; } = "en";
}

/// <summary>ChatbotService session initiation response.</summary>
public sealed class ChatbotSessionResponse
{
    /// <summary>Gets or sets the created session ID.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets the welcome message.</summary>
    public string WelcomeMessage { get; set; } = string.Empty;

    /// <summary>Gets or sets the session language.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Gets or sets the session expiration timestamp.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>ChatbotService message request.</summary>
public sealed class ChatbotSendMessageRequest
{
    /// <summary>Gets or sets the target session ID.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets the message content.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Gets or sets the requested response language.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets an optional model override for utility calls.</summary>
    public string? ModelName { get; set; }

    /// <summary>Gets or sets message attachments.</summary>
    public List<ChatbotMessageAttachmentRequest>? Attachments { get; set; }

    /// <summary>Gets or sets the optional requested response MIME type.</summary>
    public string? ResponseMimeType { get; set; }

    /// <summary>Gets or sets an optional thinking-step callback URL.</summary>
    public string? CallbackUrl { get; set; }

    /// <summary>Gets or sets the signed QuoteEngine agent context token.</summary>
    public string? QuoteAgentContextToken { get; set; }
}

/// <summary>ChatbotService message attachment request.</summary>
public sealed class ChatbotMessageAttachmentRequest
{
    /// <summary>Gets or sets the attachment type.</summary>
    public string Type { get; set; } = "image";

    /// <summary>Gets or sets the attachment URL or data reference.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the MIME type.</summary>
    public string? MimeType { get; set; }

    /// <summary>Gets or sets the filename.</summary>
    public string? Filename { get; set; }

    /// <summary>Gets or sets the size in bytes.</summary>
    public long? SizeBytes { get; set; }
}

/// <summary>ChatbotService message response.</summary>
public sealed class ChatbotMessageResponse
{
    /// <summary>Gets or sets the assistant message ID.</summary>
    public Guid MessageId { get; set; }

    /// <summary>Gets or sets the assistant content.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Gets or sets the response role.</summary>
    public string Role { get; set; } = "assistant";

    /// <summary>Gets or sets the language code.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Gets or sets the response creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets suggested actions.</summary>
    public List<CustomerChatbotActionDto> SuggestedActions { get; set; } = [];

    /// <summary>Gets or sets agent thinking steps returned by ChatbotService.</summary>
    public List<QuoteAgentThinkingStepDto> ThinkingSteps { get; set; } = [];

    /// <summary>Gets or sets the current daily token usage snapshot.</summary>
    public QuoteAgentUsageSnapshotDto? UsageSnapshot { get; set; }
}

/// <summary>ChatbotService streamed message event.</summary>
public sealed class ChatbotMessageStreamEvent
{
    /// <summary>Gets or sets the event type: started, delta, thought, final, or error.</summary>
    public string Type { get; set; } = "delta";

    /// <summary>Gets or sets the assistant text delta for delta events.</summary>
    public string? Delta { get; set; }

    /// <summary>Gets or sets incremental thought text for thought-type events.</summary>
    public string? Thought { get; set; }

    /// <summary>Gets or sets the final assistant message for final events.</summary>
    public ChatbotMessageResponse? Message { get; set; }

    /// <summary>Gets or sets the customer-safe error message for error events.</summary>
    public string? Error { get; set; }
}

/// <summary>ChatbotService conversation message history response.</summary>
public sealed class ChatbotConversationMessagesResponse
{
    /// <summary>Gets or sets the session ID.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets the session language.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Gets or sets the ordered messages.</summary>
    public List<ChatbotConversationMessageResponse> Messages { get; set; } = [];
}

/// <summary>ChatbotService conversation message row.</summary>
public sealed class ChatbotConversationMessageResponse
{
    /// <summary>Gets or sets the message role.</summary>
    public string Role { get; set; } = "assistant";

    /// <summary>Gets or sets the message content.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Gets or sets the message creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
