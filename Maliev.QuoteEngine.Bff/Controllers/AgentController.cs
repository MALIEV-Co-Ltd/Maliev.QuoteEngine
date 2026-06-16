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
    IChatbotServiceClient chatbotServiceClient) : ControllerBase
{
    private const string AgentContextHeader = "X-Maliev-Agent-Context";
    private static readonly JsonSerializerOptions StreamJsonOptions = new(JsonSerializerDefaults.Web);

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
}
