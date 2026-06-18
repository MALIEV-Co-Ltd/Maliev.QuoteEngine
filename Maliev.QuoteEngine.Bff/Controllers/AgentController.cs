using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Security;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Agent;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Maliev.QuoteEngine.Bff.Controllers;

/// <summary>
/// Chat-based QuoteEngine agent boundary.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}/agent")]
public sealed class AgentController(
    IQuoteAgentService agentService,
    QuoteAgentContextToken contextToken,
    IChatbotServiceClient chatbotServiceClient,
    IPdfServiceClient pdfServiceClient,
    QuoteUploadServiceClient uploadClient) : ControllerBase
{
    private const string AgentContextHeader = "X-Maliev-Agent-Context";
    private static readonly JsonSerializerOptions StreamJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Gets the assistant backend readiness state, including downstream ChatbotService readiness.
    /// </summary>
    [HttpGet("health")]
    [ProducesResponseType(typeof(QuoteAgentHealthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(QuoteAgentHealthResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<QuoteAgentHealthResponse>> GetHealth(CancellationToken cancellationToken)
    {
        var chatbotAvailable = await chatbotServiceClient.CheckReadinessAsync(cancellationToken);
        var body = new QuoteAgentHealthResponse
        {
            Status = chatbotAvailable ? "ready" : "unavailable",
            ChatbotServiceAvailable = chatbotAvailable,
            Message = chatbotAvailable
                ? "Assistant backend is ready."
                : "Assistant backend is temporarily unavailable."
        };

        return chatbotAvailable
            ? Ok(body)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
    }

    /// <summary>
    /// Sends a customer message through the QuoteEngine agent workflow.
    /// </summary>
    [HttpPost("messages")]
    [ProducesResponseType(typeof(QuoteAgentTurnResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<QuoteAgentTurnResponse>> Send(
        [FromBody] QuoteAgentMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        return Ok(await agentService.SendAsync(request, cancellationToken));
    }

    /// <summary>
    /// Streams a customer message through the QuoteEngine agent workflow.
    /// </summary>
    [HttpPost("messages/stream")]
    [Produces("application/x-ndjson")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Stream(
        [FromBody] QuoteAgentMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers["X-Accel-Buffering"] = "no";
        Response.Headers["Cache-Control"] = "no-cache";
        var bufferingFeature = Response.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
        bufferingFeature?.DisableBuffering();
        await Response.StartAsync(cancellationToken);

        await foreach (var streamEvent in agentService.StreamAsync(request, cancellationToken))
        {
            await Response.WriteAsync(JsonSerializer.Serialize(streamEvent, StreamJsonOptions), cancellationToken);
            await Response.WriteAsync("\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }

        return new EmptyResult();
    }

    /// <summary>
    /// Cleans up raw dictated speech text via Gemini, bypassing the agent pipeline.
    /// </summary>
    [HttpPost("clean-speech")]
    [ProducesResponseType(typeof(QuoteAgentCleanSpeechResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<QuoteAgentCleanSpeechResponse>> CleanSpeech(
        [FromBody] QuoteAgentCleanSpeechRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var cleaned = await chatbotServiceClient.CleanSpeechAsync(request.Speech, request.Language, cancellationToken);
        return Ok(new QuoteAgentCleanSpeechResponse { CleanedText = cleaned ?? request.Speech });
    }

    /// <summary>
    /// Gets the current agent state for a QuoteEngine session.
    /// </summary>
    [HttpGet("sessions/{sessionId:guid}")]
    [ProducesResponseType(typeof(QuoteAgentStateResponse), StatusCodes.Status200OK)]
    public ActionResult<QuoteAgentStateResponse> GetState(Guid sessionId)
    {
        return Ok(agentService.GetState(sessionId));
    }

    /// <summary>
    /// Gets customer-safe connector definitions for the QuoteEngine agent workspace.
    /// </summary>
    [HttpGet("sessions/{sessionId:guid}/connectors")]
    [ProducesResponseType(typeof(QuoteAgentConnectorRegistryResponse), StatusCodes.Status200OK)]
    public ActionResult<QuoteAgentConnectorRegistryResponse> GetConnectors(Guid sessionId)
    {
        return Ok(agentService.GetConnectorRegistry(sessionId));
    }

    /// <summary>
    /// Gets customer-safe connector handoff details for the QuoteEngine agent workspace.
    /// </summary>
    [HttpGet("sessions/{sessionId:guid}/connectors/{connectorId}/handoff")]
    [ProducesResponseType(typeof(QuoteAgentConnectorHandoffResponse), StatusCodes.Status200OK)]
    public ActionResult<QuoteAgentConnectorHandoffResponse> GetConnectorHandoff(
        Guid sessionId,
        string connectorId,
        [FromQuery] string? returnUrl = null)
    {
        return Ok(agentService.GetConnectorHandoff(sessionId, connectorId, returnUrl));
    }

    /// <summary>
    /// Registers browser-uploaded files with a QuoteEngine agent session.
    /// </summary>
    [HttpPost("sessions/{sessionId:guid}/attachments")]
    [ProducesResponseType(typeof(QuoteAgentStateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public ActionResult<QuoteAgentStateResponse> RegisterAttachments(
        Guid sessionId,
        [FromBody] QuoteAgentAttachmentRegisterRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        return Ok(agentService.RegisterAttachments(sessionId, request));
    }

    /// <summary>
    /// Searches signed-in customer quote data for the QuoteEngine agent workspace.
    /// </summary>
    [HttpGet("sessions/{sessionId:guid}/search")]
    [ProducesResponseType(typeof(QuoteAgentSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<QuoteAgentSearchResponse> SearchCustomerData(
        Guid sessionId,
        [FromQuery] string? query,
        [FromQuery] int limit = 20)
    {
        try
        {
            return Ok(agentService.SearchCustomerData(sessionId, query, limit));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Sign-in required.",
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Executes an internal allowlisted QuoteEngine agent tool call.
    /// </summary>
    [HttpPost("tools/{toolName}")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<object>> ExecuteTool(
        string toolName,
        [FromBody] QuoteAgentToolRequest request,
        CancellationToken cancellationToken)
    {
        if (!contextToken.TryRead(Request.Headers[AgentContextHeader].ToString(), out var context))
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Signed QuoteEngine agent context is required.",
                Detail = "Tool calls must be made by ChatbotService with a BFF-issued agent context token."
            });
        }

        var result = await agentService.ExecuteToolAsync(toolName, request, context, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Confirms and executes a pending server-stored agent action.
    /// </summary>
    [HttpPost("actions/{actionId:guid}/confirm")]
    [ProducesResponseType(typeof(QuoteAgentActionResultResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<QuoteAgentActionResultResponse>> ConfirmAction(
        Guid actionId,
        [FromBody] QuoteAgentConfirmActionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await agentService.ConfirmActionAsync(actionId, request, cancellationToken);
            return result is null ? NotFound() : Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Sign-in required.",
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Uploads a sketch image attached to an agent message, returning a storage path reference.
    /// </summary>
    [HttpPost("sessions/{sessionId:guid}/sketches")]
    [RequestSizeLimit(10_000_000)]
    [ProducesResponseType(typeof(UploadSketchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UploadSketchResponse>> UploadSketch(
        Guid sessionId,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return ValidationProblem("Sketch file is required.");
        }

        if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationProblem("Sketch must be an image file.");
        }

        using var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream, cancellationToken);

        var result = await agentService.UploadSketchAsync(
            sessionId,
            file.FileName,
            file.ContentType,
            memoryStream.ToArray(),
            cancellationToken);

        return Ok(result);
    }

    /// <summary>
    /// Redirects a session-owned workbench artifact storage path to a signed download URL.
    /// </summary>
    [HttpGet("sessions/{sessionId:guid}/artifacts/download")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DownloadArtifact(
        Guid sessionId,
        [FromQuery] string path,
        CancellationToken cancellationToken)
    {
        if (!IsSafeSessionArtifactPath(sessionId, path))
        {
            return ValidationProblem("Artifact path is not available in this quote session.");
        }

        var signedUrl = await uploadClient.GetDownloadUrlByPathAsync(path, expirationMinutes: 60, ct: cancellationToken);
        return Redirect(signedUrl);
    }

    /// <summary>
    /// Receives thinking-step callbacks and relays them to the quote notification hub.
    /// </summary>
    [HttpPost("sessions/{sessionId:guid}/thinking")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> RelayThinkingStep(
        Guid sessionId,
        [FromBody] QuoteAgentThinkingStepDto step,
        CancellationToken cancellationToken)
    {
        await agentService.RelayThinkingStepAsync(sessionId, step, cancellationToken);
        return Accepted();
    }

    /// <summary>
    /// Exports the chat transcript for a Make Studio session as a PDF.
    /// </summary>
    [HttpPost("export-pdf")]
    [ProducesResponseType(typeof(QuoteAgentExportPdfResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<QuoteAgentExportPdfResponse>> ExportChatPdf(
        [FromBody] QuoteAgentExportPdfRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var conversation = await chatbotServiceClient.GetConversationMessagesAsync(request.SessionId, cancellationToken);
        if (conversation is null || conversation.Messages.Count == 0)
        {
            return NotFound(new ProblemDetails
            {
                Title = "No messages found.",
                Detail = "The specified session has no messages to export."
            });
        }

        var data = new
        {
            sessionId = request.SessionId.ToString("D"),
            language = request.Language ?? conversation.Language ?? "en",
            generatedAt = DateTimeOffset.UtcNow,
            messages = conversation.Messages.Select(m => new
            {
                role = m.Role,
                content = m.Content,
                timestamp = m.CreatedAt
            }).ToList()
        };

        var result = await pdfServiceClient.GeneratePdfAsync("ChatTranscript", request.SessionId.ToString("D"), data, cancellationToken);
        if (result is null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "PDF generation failed.",
                Detail = "The PDF service returned an error."
            });
        }

        return Ok(new QuoteAgentExportPdfResponse
        {
            PdfUrl = result.StorageUrl,
            RequestId = result.RequestId
        });
    }

    private static bool IsSafeSessionArtifactPath(Guid sessionId, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Trim().Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Contains("../", StringComparison.Ordinal) ||
            normalized.Contains("/..", StringComparison.Ordinal) ||
            normalized.Contains('%', StringComparison.Ordinal))
        {
            return false;
        }

        var compactSessionId = sessionId.ToString("N");
        var dashedSessionId = sessionId.ToString("D");
        return normalized.StartsWith($"agent/sketches/{compactSessionId}/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith($"quotes/temp/{compactSessionId}/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith($"quotes/temp/{dashedSessionId}/", StringComparison.OrdinalIgnoreCase);
    }
}
