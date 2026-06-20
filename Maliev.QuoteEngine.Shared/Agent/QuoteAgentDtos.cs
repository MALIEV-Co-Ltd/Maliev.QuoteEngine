using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Shared.Agent;

/// <summary>
/// Text limits for customer-facing QuoteEngine agent requests.
/// </summary>
public static class QuoteAgentTextLimits
{
    /// <summary>
    /// Maximum customer text characters accepted in one conversational turn.
    /// </summary>
    public const int MaxMessageCharacters = 1200;

    /// <summary>
    /// Maximum customer-visible context characters accepted with an agent request.
    /// </summary>
    public const int MaxCustomerContextCharacters = 1600;
}

/// <summary>
/// Customer-safe health state for the QuoteEngine assistant backend boundary.
/// </summary>
public sealed class QuoteAgentHealthResponse
{
    /// <summary>Gets or sets the aggregate agent status.</summary>
    public string Status { get; set; } = "checking";

    /// <summary>Gets or sets whether ChatbotService is reachable and ready.</summary>
    public bool ChatbotServiceAvailable { get; set; }

    /// <summary>Gets or sets a customer-safe status message.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Customer message request for the chat-based QuoteEngine agent.
/// </summary>
public sealed class QuoteAgentMessageRequest
{
    /// <summary>Gets or sets the QuoteEngine agent session ID.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>Gets or sets the customer message.</summary>
    [Required]
    [StringLength(QuoteAgentTextLimits.MaxMessageCharacters, MinimumLength = 1)]
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the preferred response language.</summary>
    [RegularExpression("^(en|th)?$", ErrorMessage = "Language must be 'en' or 'th'.")]
    public string? Language { get; set; }

    /// <summary>Gets or sets an optional model override for narrow utility turns.</summary>
    [StringLength(80)]
    public string? ModelName { get; set; }

    /// <summary>Gets or sets optional browser-visible customer context.</summary>
    [StringLength(QuoteAgentTextLimits.MaxCustomerContextCharacters)]
    public string? CustomerContext { get; set; }

    /// <summary>Gets or sets supplemental attachments already available to the browser.</summary>
    public List<QuoteAgentAttachmentDto> Attachments { get; set; } = [];

    /// <summary>Gets or sets the ID of a prior message the customer is quoting/replying to.</summary>
    public Guid? ReplyToMessageId { get; set; }

    /// <summary>Gets or sets a short snippet of the replied-to message, used for agent context and the reply chip.</summary>
    [StringLength(400)]
    public string? ReplyToPreview { get; set; }

    /// <summary>Gets or sets whether the previous customer turn should be removed before this message is submitted.</summary>
    public bool EditLastTurn { get; set; }
}

/// <summary>
/// Request to register uploaded browser files with an existing QuoteEngine agent session.
/// </summary>
public sealed class QuoteAgentAttachmentRegisterRequest
{
    /// <summary>Gets or sets optional customer-visible context used to infer requirements.</summary>
    [StringLength(1600)]
    public string? Message { get; set; }

    /// <summary>Gets or sets the language for generated session state.</summary>
    [RegularExpression("^(en|th)?$", ErrorMessage = "Language must be 'en' or 'th'.")]
    public string? Language { get; set; }

    /// <summary>Gets or sets the uploaded attachments to register.</summary>
    public List<QuoteAgentAttachmentDto> Attachments { get; set; } = [];
}

/// <summary>
/// Attachment metadata for an agent turn.
/// </summary>
public sealed class QuoteAgentAttachmentDto
{
    /// <summary>Gets or sets the attachment ID.</summary>
    public Guid AttachmentId { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the original filename.</summary>
    [Required]
    [StringLength(260)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the MIME content type.</summary>
    [StringLength(100)]
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Gets or sets the file size in bytes.</summary>
    [Range(0, 10_737_418_240L)]
    public long FileSizeBytes { get; set; }

    /// <summary>Gets or sets a browser-visible URL or data reference.</summary>
    /// <remarks>Sketches are uploaded as image files via UploadSketch endpoint and referenced by StoragePath.</remarks>
    [StringLength(10_000)]
    public string? Url { get; set; }

    /// <summary>Gets or sets the attachment kind such as cad, drawing, photo, sketch, or supplemental.</summary>
    [StringLength(40)]
    public string Kind { get; set; } = "supplemental";

    /// <summary>Gets or sets an existing quote upload ID, when available.</summary>
    [StringLength(120)]
    public string? UploadId { get; set; }

    /// <summary>Gets or sets an existing storage path, when available.</summary>
    [StringLength(500)]
    public string? StoragePath { get; set; }

    /// <summary>Gets or sets whether this file can satisfy the geometry gate.</summary>
    public bool SatisfiesGeometryGate { get; set; }
}

/// <summary>
/// Agent response for one customer turn.
/// </summary>
public sealed class QuoteAgentTurnResponse
{
    /// <summary>Gets or sets the QuoteEngine agent session ID.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets the downstream assistant message ID.</summary>
    public Guid? MessageId { get; set; }

    /// <summary>Gets or sets the assistant text.</summary>
    public string AssistantText { get; set; } = string.Empty;

    /// <summary>Gets or sets the response role.</summary>
    public string Role { get; set; } = "assistant";

    /// <summary>Gets or sets the response language.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Gets or sets the response creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets current visible artifacts.</summary>
    public List<QuoteAgentArtifactDto> Artifacts { get; set; } = [];

    /// <summary>Gets or sets current workflow gates.</summary>
    public List<QuoteAgentGateDto> Gates { get; set; } = [];

    /// <summary>Gets or sets proposed confirmation actions.</summary>
    public List<QuoteAgentProposedActionDto> ProposedActions { get; set; } = [];

    /// <summary>Gets or sets a trusted authentication handoff when customer authentication blocks the next step.</summary>
    public QuoteAgentAuthHandoffResponse? AuthHandoff { get; set; }

    /// <summary>Gets or sets thinking steps returned by the agent harness.</summary>
    public List<QuoteAgentThinkingStepDto> ThinkingSteps { get; set; } = [];

    /// <summary>Gets or sets customer-safe UI focus directives the client may apply.</summary>
    public List<QuoteAgentUiDirectiveDto> UiDirectives { get; set; } = [];

    /// <summary>Gets or sets the desired UI culture to apply immediately, such as en-US or th-TH, set by agent-initiated language change.</summary>
    public string? UiCulture { get; set; }

    /// <summary>Gets or sets the project name set by the agent via quote_set_project_name tool.</summary>
    [StringLength(120)]
    public string? ProjectName { get; set; }

    /// <summary>Gets or sets a focused question with answer options presented to the customer by the agent.</summary>
    public QuoteAgentCustomerQuestionDto? CustomerQuestion { get; set; }

    /// <summary>Gets or sets the current chatbot usage snapshot.</summary>
    public QuoteAgentUsageSnapshotDto? UsageSnapshot { get; set; }
}

/// <summary>
/// Customer-safe daily chatbot usage snapshot.
/// </summary>
public sealed class QuoteAgentUsageSnapshotDto
{
    /// <summary>Gets or sets whether daily token budgeting is enabled.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Gets or sets tokens used in the rolling daily window.</summary>
    public long UsedTokens { get; set; }

    /// <summary>Gets or sets the configured daily token budget.</summary>
    public long DailyTokenBudget { get; set; }

    /// <summary>Gets or sets tokens remaining in the rolling daily window.</summary>
    public long RemainingTokens { get; set; }

    /// <summary>Gets or sets the usage ratio from 0 to 1.</summary>
    public double UsedRatio { get; set; }

    /// <summary>Gets or sets whether the budget has been reached.</summary>
    public bool IsExceeded { get; set; }
}

/// <summary>
/// A focused clarifying question with discrete answer options presented to the customer by the agent.
/// </summary>
public sealed class QuoteAgentCustomerQuestionDto
{
    /// <summary>Gets or sets the question text.</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>Gets or sets the 2–4 discrete answer options.</summary>
    public List<string> Options { get; set; } = [];
}

/// <summary>
/// Incremental event returned by the QuoteEngine agent streaming endpoint.
/// </summary>
public sealed class QuoteAgentStreamEvent
{
    /// <summary>Gets or sets the event type: started, delta, thought, final, or error.</summary>
    [Required]
    [StringLength(40)]
    public string Type { get; set; } = "delta";

    /// <summary>Gets or sets the text delta for assistant content.</summary>
    public string? Delta { get; set; }

    /// <summary>Gets or sets incremental thought text for thought-type events.</summary>
    public string? Thought { get; set; }

    /// <summary>Gets or sets the final complete turn response.</summary>
    public QuoteAgentTurnResponse? Response { get; set; }

    /// <summary>Gets or sets a customer-safe error message.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Current QuoteEngine agent state.
/// </summary>
public sealed class QuoteAgentStateResponse
{
    /// <summary>Gets or sets the QuoteEngine agent session ID.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets the latest assistant-safe state summary.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the customer is authenticated.</summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>Gets or sets the signed-in customer ID, when known.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>Gets or sets known attachments.</summary>
    public List<QuoteAgentAttachmentDto> Attachments { get; set; } = [];

    /// <summary>Gets or sets quote parts available to the agent session.</summary>
    public List<QuotePartDraftDto> Parts { get; set; } = [];

    /// <summary>Gets or sets visible artifacts.</summary>
    public List<QuoteAgentArtifactDto> Artifacts { get; set; } = [];

    /// <summary>Gets or sets workflow gates.</summary>
    public List<QuoteAgentGateDto> Gates { get; set; } = [];

    /// <summary>Gets or sets pending proposed actions.</summary>
    public List<QuoteAgentProposedActionDto> ProposedActions { get; set; } = [];

    /// <summary>Gets or sets the current estimate.</summary>
    public QuoteEstimateResponse? Estimate { get; set; }

    /// <summary>Gets or sets customer-safe UI focus directives for the current state.</summary>
    public List<QuoteAgentUiDirectiveDto> UiDirectives { get; set; } = [];

    /// <summary>Gets or sets the project name set by the agent via quote_set_project_name tool.</summary>
    [StringLength(120)]
    public string? ProjectName { get; set; }
}

/// <summary>
/// Ordered customer-safe chat messages for a QuoteEngine agent session.
/// </summary>
public sealed class QuoteAgentMessageHistoryResponse
{
    /// <summary>Gets or sets the QuoteEngine agent session ID.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets the conversation language.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Gets or sets the ordered chat messages.</summary>
    public List<QuoteAgentMessageHistoryItemDto> Messages { get; set; } = [];
}

/// <summary>
/// One customer-safe chat message restored from ChatbotService.
/// </summary>
public sealed class QuoteAgentMessageHistoryItemDto
{
    /// <summary>Gets or sets the message role.</summary>
    [Required]
    [StringLength(40)]
    public string Role { get; set; } = "assistant";

    /// <summary>Gets or sets the message content.</summary>
    [Required]
    [StringLength(QuoteAgentTextLimits.MaxMessageCharacters)]
    public string Content { get; set; } = string.Empty;

    /// <summary>Gets or sets the message creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Customer-safe instruction for the Make Studio UI to open a panel and highlight a referenced item.
/// </summary>
public sealed class QuoteAgentUiDirectiveDto
{
    /// <summary>Gets or sets the directive ID.</summary>
    public Guid DirectiveId { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the panel to open, such as workbench, artifacts, summary, or none.</summary>
    [StringLength(40)]
    public string Panel { get; set; } = "none";

    /// <summary>Gets or sets the target type, such as artifact, uploaded_file, dfm_issue, summary, or canvas_location.</summary>
    [StringLength(60)]
    public string TargetType { get; set; } = string.Empty;

    /// <summary>Gets or sets the target ID when the UI can map directly to an artifact, part, file, or issue.</summary>
    [StringLength(160)]
    public string? TargetId { get; set; }

    /// <summary>Gets or sets a stable highlight key used by the client to pulse the target.</summary>
    [StringLength(160)]
    public string HighlightKey { get; set; } = string.Empty;

    /// <summary>Gets or sets customer-safe text describing why the target is being highlighted.</summary>
    [StringLength(280)]
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets a normalized X coordinate for canvas highlights, when available.</summary>
    [Range(0, 1)]
    public double? CanvasX { get; set; }

    /// <summary>Gets or sets a normalized Y coordinate for canvas highlights, when available.</summary>
    [Range(0, 1)]
    public double? CanvasY { get; set; }

    /// <summary>Gets or sets an optional normalized Z coordinate or depth hint for 3D canvas highlights.</summary>
    [Range(0, 1)]
    public double? CanvasZ { get; set; }
}

/// <summary>
/// Workflow gate state.
/// </summary>
public sealed record QuoteAgentGateDto(
    string Code,
    string Label,
    string Status,
    string Detail,
    bool Required = true);

/// <summary>
/// Visible artifact produced or referenced by the agent workflow.
/// </summary>
public sealed class QuoteAgentArtifactDto
{
    /// <summary>Gets or sets the artifact ID.</summary>
    public Guid ArtifactId { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the artifact type.</summary>
    public string ArtifactType { get; set; } = string.Empty;

    /// <summary>Gets or sets the artifact title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the artifact status.</summary>
    public string Status { get; set; } = "ready";

    /// <summary>Gets or sets optional related part ID.</summary>
    public Guid? PartId { get; set; }

    /// <summary>Gets or sets optional artifact URL.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets compact artifact metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Explicit customer confirmation action proposed by the agent.
/// </summary>
public sealed class QuoteAgentProposedActionDto
{
    /// <summary>Gets or sets the action ID.</summary>
    public Guid ActionId { get; set; }

    /// <summary>Gets or sets the action type.</summary>
    public string ActionType { get; set; } = string.Empty;

    /// <summary>Gets or sets the action title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the action summary.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Gets or sets whether a signed-in customer session is required.</summary>
    public bool RequiresAuthentication { get; set; }

    /// <summary>Gets or sets whether explicit confirmation is required.</summary>
    public bool RequiresConfirmation { get; set; } = true;

    /// <summary>Gets or sets the action status.</summary>
    public string Status { get; set; } = "pending_confirmation";
}

/// <summary>
/// Agent thinking step streamed or returned by ChatbotService.
/// </summary>
public sealed class QuoteAgentThinkingStepDto
{
    /// <summary>Gets or sets the step number.</summary>
    public int StepNumber { get; set; }

    /// <summary>Gets or sets the step type.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the step title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the step detail.</summary>
    public string? Detail { get; set; }

    /// <summary>Gets or sets a human-readable one-line summary synthesized by the BFF.</summary>
    public string? Summary { get; set; }

    /// <summary>Gets or sets the step timestamp.</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets the step duration in milliseconds.</summary>
    public long? DurationMs { get; set; }
}

/// <summary>
/// Internal tool execution request from ChatbotService to QuoteEngine BFF.
/// </summary>
public sealed class QuoteAgentToolRequest
{
    /// <summary>Gets or sets model-supplied tool arguments.</summary>
    public Dictionary<string, JsonElement> Arguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Customer feedback for a generated 3D preview artifact.
/// </summary>
public sealed class QuoteAgentPreviewFeedbackRequest
{
    /// <summary>Gets or sets the customer rating from 1 (poor) to 5 (excellent).</summary>
    [Range(1, 5)]
    public int Rating { get; set; }

    /// <summary>Gets or sets customer comments that should improve future generated drafts.</summary>
    [Required]
    [StringLength(1200, MinimumLength = 1)]
    public string Comment { get; set; } = string.Empty;
}

/// <summary>
/// Result of recording customer feedback for a generated 3D preview artifact.
/// </summary>
public sealed class QuoteAgentPreviewFeedbackResponse
{
    /// <summary>Gets or sets the artifact ID that received feedback.</summary>
    public Guid ArtifactId { get; set; }

    /// <summary>Gets or sets the recording status.</summary>
    public string Status { get; set; } = "recorded";

    /// <summary>Gets or sets whether feedback was observed into durable customer memory.</summary>
    public bool MemoryObserved { get; set; }

    /// <summary>Gets or sets updated agent state.</summary>
    public QuoteAgentStateResponse? State { get; set; }
}

/// <summary>
/// Customer-scoped search response returned to the QuoteEngine agent.
/// </summary>
public sealed class QuoteAgentSearchResponse
{
    /// <summary>Gets or sets the QuoteEngine agent session ID that requested the search.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets the normalized query used for search.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the search used a signed-in customer boundary.</summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>Gets or sets the matched customer data results.</summary>
    public List<QuoteAgentSearchResultDto> Results { get; set; } = [];
}

/// <summary>
/// Customer-safe search result for projects, quotes, orders, documents, files, and artifacts.
/// </summary>
public sealed class QuoteAgentSearchResultDto
{
    /// <summary>Gets or sets the resource type such as project, quote, order, document, or artifact.</summary>
    public string ResourceType { get; set; } = string.Empty;

    /// <summary>Gets or sets the stable resource ID.</summary>
    public string ResourceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the customer-visible title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets compact customer-visible detail text.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Gets or sets the action the agent can take next, such as resume_project.</summary>
    public string ActionHint { get; set; } = string.Empty;

    /// <summary>Gets or sets optional resource URL for browser navigation or download.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets optional compact metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Customer-safe compact project summary for the QuoteEngine agent.
/// </summary>
public sealed class QuoteAgentProjectSummaryResponse
{
    /// <summary>Gets or sets the QuoteEngine agent session ID.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets the latest assistant-safe state summary.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the current request has a signed-in customer session.</summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>Gets or sets the number of known attachments.</summary>
    public int AttachmentCount { get; set; }

    /// <summary>Gets or sets the number of quote parts in the session.</summary>
    public int PartCount { get; set; }

    /// <summary>Gets or sets the number of visible artifacts.</summary>
    public int ArtifactCount { get; set; }

    /// <summary>Gets or sets the current estimate total, when pricing exists.</summary>
    public decimal? EstimateTotal { get; set; }

    /// <summary>Gets or sets the current estimate currency, when pricing exists.</summary>
    public string? EstimateCurrency { get; set; }

    /// <summary>Gets or sets the current manufacturing order number, when an order exists.</summary>
    public string? CurrentOrderNumber { get; set; }

    /// <summary>Gets or sets the latest customer-visible order status, when available.</summary>
    public string? CurrentOrderStatus { get; set; }

    /// <summary>Gets or sets the latest customer-visible payment status, when available.</summary>
    public string? CurrentPaymentStatus { get; set; }

    /// <summary>Gets or sets the customer-facing order detail URL, when an order exists.</summary>
    public string? CurrentOrderUrl { get; set; }

    /// <summary>Gets or sets the current or next customer-visible manufacturing milestone label.</summary>
    public string? CurrentOrderMilestoneLabel { get; set; }

    /// <summary>Gets or sets the current or next customer-visible manufacturing milestone description.</summary>
    public string? CurrentOrderMilestoneDescription { get; set; }

    /// <summary>Gets or sets the current or next customer-visible manufacturing milestone state.</summary>
    public string? CurrentOrderMilestoneState { get; set; }

    /// <summary>Gets or sets the current or next customer-visible manufacturing milestone percent.</summary>
    public int? CurrentOrderMilestonePercent { get; set; }

    /// <summary>Gets or sets passed gate codes for agent reasoning.</summary>
    public List<string> PassedGateCodes { get; set; } = [];

    /// <summary>Gets or sets blocked gate codes for agent reasoning.</summary>
    public List<string> BlockingGateCodes { get; set; } = [];

    /// <summary>Gets or sets currently pending confirmation action types.</summary>
    public List<string> PendingActionTypes { get; set; } = [];

    /// <summary>Gets or sets extracted customer-safe requirement facts from drawings, sketches, photos, or chat text.</summary>
    public Dictionary<string, string> RequirementFacts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets concise next actions the assistant can explain to the customer.</summary>
    public List<string> NextActions { get; set; } = [];
}

/// <summary>
/// Customer-safe Make Studio settings for the current quote agent session.
/// </summary>
public sealed class QuoteAgentSettingsResponse
{
    /// <summary>Gets or sets the QuoteEngine agent session ID.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets the current response/input language.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Gets or sets the preferred dimensional unit system.</summary>
    public string Units { get; set; } = "mm";

    /// <summary>Gets or sets the preferred quote currency.</summary>
    public string Currency { get; set; } = "THB";

    /// <summary>Gets or sets the preferred Make Studio interaction mode.</summary>
    public string InteractionMode { get; set; } = "chat";

    /// <summary>Gets or sets whether the customer wants the artifact panel available.</summary>
    public bool AllowArtifactPanel { get; set; } = true;

    /// <summary>Gets or sets whether bilingual/multilingual responses are enabled for the session.</summary>
    public bool Multilingual { get; set; } = true;

    /// <summary>Gets or sets customer-safe next actions after reading or changing settings.</summary>
    public List<string> NextActions { get; set; } = [];
}

/// <summary>
/// Customer-safe connector registry response for QuoteEngine plugins and integrations.
/// </summary>
public sealed class QuoteAgentConnectorRegistryResponse
{
    /// <summary>Gets or sets the QuoteEngine agent session ID that requested the registry.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets whether a customer must sign in just to list connector capabilities.</summary>
    public bool RequiresAuthenticationToList { get; set; }

    /// <summary>Gets or sets available, planned, and future connector definitions.</summary>
    public List<QuoteAgentConnectorDto> Connectors { get; set; } = [];
}

/// <summary>
/// Customer-safe connector definition for planned file import and CAD sender integrations.
/// </summary>
public sealed class QuoteAgentConnectorDto
{
    /// <summary>Gets or sets the stable connector ID.</summary>
    public string ConnectorId { get; set; } = string.Empty;

    /// <summary>Gets or sets the customer-visible connector name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the connector category such as file_import or cad_sender.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Gets or sets the connector status such as planned, future, available, or connected.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets a customer-safe summary of what the connector will do.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets whether signing in is required before connecting this integration.</summary>
    public bool RequiresAuthenticationToConnect { get; set; }

    /// <summary>Gets or sets whether this connector is currently connected for the customer.</summary>
    public bool IsConnected { get; set; }

    /// <summary>Gets or sets supported file types or payload types.</summary>
    public List<string> SupportedFileTypes { get; set; } = [];

    /// <summary>Gets or sets the action hint the agent can use for next steps.</summary>
    public string ActionHint { get; set; } = string.Empty;
}

/// <summary>
/// Customer-safe connector handoff response for planned file import and CAD sender integrations.
/// </summary>
public sealed class QuoteAgentConnectorHandoffResponse
{
    /// <summary>Gets or sets the QuoteEngine agent session ID that requested the connector handoff.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets the requested connector ID.</summary>
    public string ConnectorId { get; set; } = string.Empty;

    /// <summary>Gets or sets the customer-visible connector name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the connector status such as planned, future, authentication_required, or ready_to_connect.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the current request has a signed-in customer session.</summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>Gets or sets whether this connector can be connected in the current product version.</summary>
    public bool IsAvailableToConnect { get; set; }

    /// <summary>Gets or sets the local trusted handoff URL for authentication or connector setup.</summary>
    public string? HandoffUrl { get; set; }

    /// <summary>Gets or sets a customer-safe connector setup message for the agent.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the next customer-safe action hint.</summary>
    public string ActionHint { get; set; } = string.Empty;
}

/// <summary>
/// Customer-safe authentication handoff response for agent-assisted sign-in and sign-up.
/// </summary>
public sealed class QuoteAgentAuthHandoffResponse
{
    /// <summary>Gets or sets the QuoteEngine agent session ID that requested auth help.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets whether the current request already has a customer session.</summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>Gets or sets the signed-in customer ID, when known.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>Gets or sets the requested auth intent, such as sign-in or sign-up.</summary>
    public string Intent { get; set; } = "sign-in";

    /// <summary>Gets or sets the local return URL after authentication completes.</summary>
    public string ReturnUrl { get; set; } = "/quotes";

    /// <summary>Gets or sets the current auth handoff status.</summary>
    public string Status { get; set; } = "authentication_required";

    /// <summary>Gets or sets the gate code satisfied by a completed customer session.</summary>
    public string RequiredGateCode { get; set; } = "customer_authenticated";

    /// <summary>Gets or sets customer-safe authentication methods the agent may present.</summary>
    public List<QuoteAgentAuthMethodDto> Methods { get; set; } = [];
}

/// <summary>
/// Customer-safe sign-in or sign-up method available through the trusted auth surface.
/// </summary>
public sealed class QuoteAgentAuthMethodDto
{
    /// <summary>Gets or sets the stable auth method ID.</summary>
    public string MethodId { get; set; } = string.Empty;

    /// <summary>Gets or sets the customer-visible auth method name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the method status, such as preferred, fallback, or available_when_supported.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the safe navigation URL for this auth handoff.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets a short customer-safe description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the method depends on browser platform support.</summary>
    public bool RequiresBrowserSupport { get; set; }

    /// <summary>Gets or sets the fallback method ID when this method is not available.</summary>
    public string? FallbackMethodId { get; set; }
}

/// <summary>
/// Request to confirm a server-stored proposed action.
/// </summary>
public sealed class QuoteAgentConfirmActionRequest
{
    /// <summary>Gets or sets optional customer confirmation note.</summary>
    [StringLength(1000)]
    public string? ConfirmationNote { get; set; }
}

/// <summary>
/// Result of confirming a proposed action.
/// </summary>
public sealed class QuoteAgentActionResultResponse
{
    /// <summary>Gets or sets the action ID.</summary>
    public Guid ActionId { get; set; }

    /// <summary>Gets or sets the action status.</summary>
    public string Status { get; set; } = "completed";

    /// <summary>Gets or sets a customer-safe result message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets updated agent state.</summary>
    public QuoteAgentStateResponse? State { get; set; }
}

/// <summary>
/// Result of uploading a sketch image to the upload service.
/// </summary>
public sealed class UploadSketchResponse
{
    /// <summary>Gets or sets the upload service ID.</summary>
    public string UploadId { get; set; } = string.Empty;

    /// <summary>Gets or sets the storage path for the uploaded sketch.</summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>Gets or sets a browser-safe signed URL for previewing the uploaded sketch.</summary>
    public string? Url { get; set; }
}

/// <summary>
/// Request to clean up raw dictated speech text.
/// </summary>
public sealed class QuoteAgentCleanSpeechRequest
{
    /// <summary>Gets or sets the raw dictated speech text.</summary>
    [Required]
    [StringLength(2000, MinimumLength = 1)]
    public string Speech { get; set; } = string.Empty;

    /// <summary>Gets or sets the speech language code (en or th).</summary>
    [RegularExpression("^(en|th)?$", ErrorMessage = "Language must be 'en' or 'th'.")]
    public string Language { get; set; } = "en";
}

/// <summary>
/// Response containing the cleaned speech text.
/// </summary>
public sealed class QuoteAgentCleanSpeechResponse
{
    /// <summary>Gets or sets the cleaned speech text.</summary>
    public string CleanedText { get; set; } = string.Empty;
}

/// <summary>
/// A CAD command in the parametric construction sequence.
/// </summary>
public sealed class CadCommandDto
{
    /// <summary>Operation: box, cylinder, sphere, cone, extrude, revolve,
    /// fuse, cut, intersect, fillet, chamfer, sweep, loft, translate, rotate.</summary>
    public string Op { get; set; } = string.Empty;

    /// <summary>Operation alias often emitted by model tool calls; normalized into <see cref="Op"/>.</summary>
    public string? Operation { get; set; }

    /// <summary>Operation type alias often emitted by model tool calls; normalized into <see cref="Op"/>.</summary>
    public string? Type { get; set; }

    /// <summary>Shape alias often emitted by model tool calls for primitive operations; normalized into <see cref="Op"/>.</summary>
    public string? Shape { get; set; }

    /// <summary>Identifier for referencing this shape in subsequent commands.</summary>
    public string? Id { get; set; }

    /// <summary>Target shape reference (for boolean ops, fillet, translate, etc.).</summary>
    public string? TargetId { get; set; }

    /// <summary>Snake-case target shape reference alias; normalized into <see cref="TargetId"/>.</summary>
    [JsonPropertyName("target_id")]
    public string? TargetIdSnake { get; set; }

    /// <summary>Target shape reference alias often emitted by model tool calls; normalized into <see cref="TargetId"/>.</summary>
    public string? Target { get; set; }

    /// <summary>Tool shape reference (for boolean ops, sweep, loft).</summary>
    public string? ToolId { get; set; }

    /// <summary>Snake-case tool shape reference alias; normalized into <see cref="ToolId"/>.</summary>
    [JsonPropertyName("tool_id")]
    public string? ToolIdSnake { get; set; }

    /// <summary>Tool shape reference alias often emitted by model tool calls; normalized into <see cref="ToolId"/>.</summary>
    public string? Tool { get; set; }

    /// <summary>Result identifier — alias for the output of this command.</summary>
    public string? ResultId { get; set; }

    /// <summary>Snake-case result identifier alias; normalized into <see cref="ResultId"/>.</summary>
    [JsonPropertyName("result_id")]
    public string? ResultIdSnake { get; set; }

    /// <summary>Result identifier alias often emitted by model tool calls; normalized into <see cref="ResultId"/>.</summary>
    public string? Result { get; set; }

    /// <summary>Numeric parameters, op-dependent:
    /// box: [w, d, h]; cylinder: [radius, height]; sphere: [radius];
    /// cone: [radiusBottom, radiusTop, height]; extrude/revolve: [height/angle];
    /// fillet/chamfer: [radius].</summary>
    public double[]? Params { get; set; }

    /// <summary>Numeric parameter alias often emitted by model tool calls; normalized into <see cref="Params"/>.</summary>
    public double[]? Parameters { get; set; }

    /// <summary>Nested primitive dimensions often emitted by model tool calls; normalized into <see cref="Params"/>.</summary>
    public CadDimensionsDto? Dimensions { get; set; }

    /// <summary>Named box/profile width or X dimension, normalized to params when present.</summary>
    public double? Width { get; set; }

    /// <summary>Named box depth or Y dimension, normalized to params when present.</summary>
    public double? Depth { get; set; }

    /// <summary>Named box depth/length fallback, normalized to params when depth is omitted.</summary>
    public double? Length { get; set; }

    /// <summary>Named primitive height or Z dimension, normalized to params when present.</summary>
    public double? Height { get; set; }

    /// <summary>Named X translation component, normalized to offset when present.</summary>
    public double? X { get; set; }

    /// <summary>Named Y translation component, normalized to offset when present.</summary>
    public double? Y { get; set; }

    /// <summary>Named Z translation component, normalized to offset when present.</summary>
    public double? Z { get; set; }

    /// <summary>Translation offset [x, y, z] for the translate op.</summary>
    public double[]? Offset { get; set; }

    /// <summary>Named rotation axis X component, normalized to axis when present.</summary>
    public double? AxisX { get; set; }

    /// <summary>Named rotation axis Y component, normalized to axis when present.</summary>
    public double? AxisY { get; set; }

    /// <summary>Named rotation axis Z component, normalized to axis when present.</summary>
    public double? AxisZ { get; set; }

    /// <summary>Rotation axis [x, y, z] for the rotate or revolve op.</summary>
    public double[]? Axis { get; set; }

    /// <summary>Rotation angle in radians.</summary>
    public double? Angle { get; set; }

    /// <summary>Fillet or chamfer radius.</summary>
    public double? Radius { get; set; }

    /// <summary>Diameter shorthand for cylinder and sphere primitives.</summary>
    public double? Diameter { get; set; }

    /// <summary>Named cone bottom radius, normalized to params when present.</summary>
    public double? RadiusBottom { get; set; }

    /// <summary>Named cone top radius, normalized to params when present.</summary>
    public double? RadiusTop { get; set; }

    /// <summary>Named cone bottom radius alias, normalized to params when radiusBottom is omitted.</summary>
    public double? BottomRadius { get; set; }

    /// <summary>Named cone top radius alias, normalized to params when radiusTop is omitted.</summary>
    public double? TopRadius { get; set; }

    /// <summary>2D profile definition for extrude/revolve operations.</summary>
    public CadProfileDto? Profile { get; set; }
}

/// <summary>
/// Nested primitive dimensions for generated CAD commands.
/// </summary>
public sealed class CadDimensionsDto
{
    /// <summary>Box/profile width or X dimension.</summary>
    public double? Width { get; set; }

    /// <summary>Box depth or Y dimension.</summary>
    public double? Depth { get; set; }

    /// <summary>Box depth/length fallback.</summary>
    public double? Length { get; set; }

    /// <summary>Primitive height or Z dimension.</summary>
    public double? Height { get; set; }

    /// <summary>Primitive radius.</summary>
    public double? Radius { get; set; }

    /// <summary>Primitive diameter shorthand.</summary>
    public double? Diameter { get; set; }

    /// <summary>Cone bottom radius.</summary>
    public double? RadiusBottom { get; set; }

    /// <summary>Cone top radius.</summary>
    public double? RadiusTop { get; set; }

    /// <summary>Cone bottom radius alias.</summary>
    public double? BottomRadius { get; set; }

    /// <summary>Cone top radius alias.</summary>
    public double? TopRadius { get; set; }
}

/// <summary>
/// A 2D profile / sketch for extrude or revolve operations.
/// </summary>
public sealed class CadProfileDto
{
    /// <summary>Optional shorthand profile type, such as circle, rect, or rectangle.</summary>
    public string? Type { get; set; }

    /// <summary>Sketch plane: "XY", "XZ", or "YZ". Default "XY".</summary>
    public string Plane { get; set; } = "XY";

    /// <summary>Sketch segments defining the 2D profile.</summary>
    public List<CadSegmentDto> Segments { get; set; } = [];

    /// <summary>Profile shorthand parameters: rectangle [width, height], circle [radius].</summary>
    public double[]? Params { get; set; }

    /// <summary>Profile shorthand parameter alias often emitted by model tool calls; normalized into <see cref="Params"/>.</summary>
    public double[]? Parameters { get; set; }

    /// <summary>Whether to auto-close the profile. Default true.</summary>
    public bool Close { get; set; } = true;

    /// <summary>Circle radius (shorthand — sets a full circle profile).</summary>
    public double? Radius { get; set; }

    /// <summary>Rectangle width (shorthand — sets a full rect profile).</summary>
    public double? Width { get; set; }

    /// <summary>Rectangle height (shorthand — sets a full rect profile).</summary>
    public double? Height { get; set; }
}

/// <summary>
/// A single segment in a 2D sketch profile.
/// </summary>
public sealed class CadSegmentDto
{
    /// <summary>Segment type: move, line, hLine, vLine, arc, bezier.</summary>
    public string Type { get; set; } = "line";

    /// <summary>Segment parameters, type-dependent.</summary>
    public double[]? Params { get; set; }

    /// <summary>Segment parameter alias often emitted by model tool calls; normalized into <see cref="Params"/>.</summary>
    public double[]? Parameters { get; set; }

    /// <summary>Named X coordinate for move/line sketch segments.</summary>
    public double? X { get; set; }

    /// <summary>Named Y coordinate for move/line sketch segments.</summary>
    public double? Y { get; set; }

    /// <summary>Named horizontal delta for hLine sketch segments.</summary>
    public double? Dx { get; set; }

    /// <summary>Named vertical delta for vLine sketch segments.</summary>
    public double? Dy { get; set; }

    /// <summary>Named line length fallback for hLine/vLine sketch segments.</summary>
    public double? Length { get; set; }
}

/// <summary>
/// Request to export the Make Studio chat transcript as a PDF.
/// </summary>
public sealed class QuoteAgentExportPdfRequest
{
    /// <summary>Gets or sets the QuoteEngine agent session ID.</summary>
    [Required]
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets the preferred language for the PDF.</summary>
    [RegularExpression("^(en|th)?$")]
    public string? Language { get; set; }
}

/// <summary>
/// Response containing the PDF download URL for an exported chat transcript.
/// </summary>
public sealed class QuoteAgentExportPdfResponse
{
    /// <summary>Gets or sets the PDF download URL.</summary>
    public string PdfUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the PDF generation request ID for tracking.</summary>
    public Guid? RequestId { get; set; }
}
