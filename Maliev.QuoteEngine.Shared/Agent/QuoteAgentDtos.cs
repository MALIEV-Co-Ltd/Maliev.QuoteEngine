using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Shared.Agent;

/// <summary>
/// Customer message request for the chat-based QuoteEngine agent.
/// </summary>
public sealed class QuoteAgentMessageRequest
{
    /// <summary>Gets or sets the QuoteEngine agent session ID.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>Gets or sets the customer message.</summary>
    [Required]
    [StringLength(4000, MinimumLength = 1)]
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the preferred response language.</summary>
    [RegularExpression("^(en|th)?$", ErrorMessage = "Language must be 'en' or 'th'.")]
    public string? Language { get; set; }

    /// <summary>Gets or sets optional browser-visible customer context.</summary>
    [StringLength(1600)]
    public string? CustomerContext { get; set; }

    /// <summary>Gets or sets supplemental attachments already available to the browser.</summary>
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

    /// <summary>Gets or sets thinking steps returned by the agent harness.</summary>
    public List<QuoteAgentThinkingStepDto> ThinkingSteps { get; set; } = [];
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
