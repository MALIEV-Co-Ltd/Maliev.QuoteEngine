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
    private const int MaxFailureBodyLogCharacters = 2048;
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
        var openResult = await OpenMessageStreamAsync(request, cancellationToken);
        if (openResult.ErrorEvent is not null)
        {
            yield return openResult.ErrorEvent;
            yield break;
        }

        using var response = openResult.Response!;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (true)
        {
            var readResult = await ReadMessageStreamEventAsync(reader, cancellationToken);
            if (readResult.ErrorEvent is not null)
            {
                yield return readResult.ErrorEvent;
                yield break;
            }

            if (readResult.EndOfStream)
            {
                yield break;
            }

            if (readResult.StreamEvent is not null)
            {
                yield return readResult.StreamEvent;
            }
        }
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

    private async Task<StreamOpenResult> OpenMessageStreamAsync(
        ChatbotSendMessageRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/chatbot/v1/messages/stream")
            {
                Content = JsonContent.Create(request, options: SnakeCaseJson)
            };
            var response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var failureBody = await ReadFailureBodyAsync(response, cancellationToken);
                logger.LogWarning(
                    "ChatbotService returned {StatusCode} while streaming chatbot message. Response body: {ResponseBody}",
                    response.StatusCode,
                    failureBody);
                response.Dispose();
                return StreamOpenResult.Failed(BuildStreamFailureError(failureBody));
            }

            return StreamOpenResult.Success(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ChatbotService failed while streaming chatbot message.");
            return StreamOpenResult.Failed("ChatbotService stream request failed.");
        }
    }

    private async Task<StreamReadResult> ReadMessageStreamEventAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ChatbotService stream failed while reading an event.");
                return StreamReadResult.Failed("ChatbotService stream response failed.");
            }

            if (line is null)
            {
                return StreamReadResult.End();
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var streamEvent = JsonSerializer.Deserialize<ChatbotMessageStreamEvent>(line, SnakeCaseJson);
                if (streamEvent is not null)
                {
                    return StreamReadResult.Event(streamEvent);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "ChatbotService stream emitted an invalid event payload.");
                return StreamReadResult.Failed("ChatbotService stream response was invalid.");
            }
        }
    }

    private static async Task<string> ReadFailureBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return "<empty>";
        }

        body = body.ReplaceLineEndings(" ");
        return body.Length <= MaxFailureBodyLogCharacters
            ? body
            : string.Concat(body.AsSpan(0, MaxFailureBodyLogCharacters), "...");
    }

    private static string BuildStreamFailureError(string failureBody)
    {
        const string prefix = "ChatbotService stream request failed.";
        if (string.IsNullOrWhiteSpace(failureBody) ||
            failureBody.Equals("<empty>", StringComparison.Ordinal))
        {
            return prefix;
        }

        var detail = ExtractFailureDetail(failureBody);
        return string.IsNullOrWhiteSpace(detail)
            ? prefix
            : $"{prefix.TrimEnd('.')}: {detail}";
    }

    private static string? ExtractFailureDetail(string failureBody)
    {
        try
        {
            using var document = JsonDocument.Parse(failureBody);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var propertyName in new[] { "error", "detail", "message", "title" })
                {
                    if (root.TryGetProperty(propertyName, out var property) &&
                        property.ValueKind == JsonValueKind.String)
                    {
                        return NormalizeFailureDetail(property.GetString());
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return NormalizeFailureDetail(failureBody);
    }

    private static string? NormalizeFailureDetail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= MaxFailureBodyLogCharacters
            ? normalized
            : string.Concat(normalized.AsSpan(0, MaxFailureBodyLogCharacters), "...");
    }

    private static ChatbotMessageStreamEvent CreateErrorEvent(string error)
    {
        return new ChatbotMessageStreamEvent
        {
            Type = "error",
            Error = error
        };
    }

    private sealed class StreamOpenResult
    {
        public HttpResponseMessage? Response { get; private init; }

        public ChatbotMessageStreamEvent? ErrorEvent { get; private init; }

        public static StreamOpenResult Success(HttpResponseMessage response)
        {
            return new StreamOpenResult
            {
                Response = response
            };
        }

        public static StreamOpenResult Failed(string error)
        {
            return new StreamOpenResult
            {
                ErrorEvent = CreateErrorEvent(error)
            };
        }
    }

    private sealed class StreamReadResult
    {
        public bool EndOfStream { get; private init; }

        public ChatbotMessageStreamEvent? StreamEvent { get; private init; }

        public ChatbotMessageStreamEvent? ErrorEvent { get; private init; }

        public static StreamReadResult End()
        {
            return new StreamReadResult
            {
                EndOfStream = true
            };
        }

        public static StreamReadResult Event(ChatbotMessageStreamEvent streamEvent)
        {
            return new StreamReadResult
            {
                StreamEvent = streamEvent
            };
        }

        public static StreamReadResult Failed(string error)
        {
            return new StreamReadResult
            {
                ErrorEvent = CreateErrorEvent(error)
            };
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

    /// <summary>Gets or sets an optional structured-output response schema.</summary>
    public object? ResponseSchema { get; set; }

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

    /// <summary>Gets or sets customer-safe grounding provenance returned by ChatbotService.</summary>
    public ChatbotGroundingProvenanceResponse? GroundingProvenance { get; set; }
}

/// <summary>ChatbotService grounding provenance response.</summary>
public sealed class ChatbotGroundingProvenanceResponse
{
    /// <summary>Gets or sets the bounded grounding purpose identifier.</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>Gets or sets the grounding provider identifier.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Gets or sets grounded, no_evidence, or unavailable.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets bounded provider-reported search queries.</summary>
    public List<string> Queries { get; set; } = [];

    /// <summary>Gets or sets bounded HTTPS sources used by the grounded turn.</summary>
    public List<ChatbotGroundingSourceResponse> Sources { get; set; } = [];

    /// <summary>Gets or sets a customer-safe failure code.</summary>
    public string? ErrorCode { get; set; }
}

/// <summary>ChatbotService grounding source response.</summary>
public sealed class ChatbotGroundingSourceResponse
{
    /// <summary>Gets or sets the source title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the canonical HTTPS source URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the normalized source host.</summary>
    public string Domain { get; set; } = string.Empty;
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

    /// <summary>Gets or sets persisted thinking steps, when ChatbotService includes them.</summary>
    public List<QuoteAgentThinkingStepDto> ThinkingSteps { get; set; } = [];

    /// <summary>Gets or sets persisted visible artifacts, when ChatbotService includes them.</summary>
    public List<QuoteAgentArtifactDto> Artifacts { get; set; } = [];
}
