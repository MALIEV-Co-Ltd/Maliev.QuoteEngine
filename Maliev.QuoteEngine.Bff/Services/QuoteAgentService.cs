using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Bff.Security;
using Maliev.QuoteEngine.Shared.Agent;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.SignalR;

namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>
/// Coordinates QuoteEngine agent sessions, tool calls, and confirmation actions.
/// </summary>
public interface IQuoteAgentService
{
    /// <summary>Sends one customer turn through the QuoteEngine agent channel.</summary>
    Task<QuoteAgentTurnResponse> SendAsync(QuoteAgentMessageRequest request, CancellationToken cancellationToken);

    /// <summary>Gets the current agent state.</summary>
    QuoteAgentStateResponse GetState(Guid sessionId);

    /// <summary>Executes an allowlisted internal tool call.</summary>
    Task<object> ExecuteToolAsync(string toolName, QuoteAgentToolRequest request, QuoteAgentContext context, CancellationToken cancellationToken);

    /// <summary>Confirms and executes a server-stored pending action.</summary>
    Task<QuoteAgentActionResultResponse?> ConfirmActionAsync(
        Guid actionId,
        QuoteAgentConfirmActionRequest request,
        CancellationToken cancellationToken);

    /// <summary>Relays a thinking step to the quote notifications hub.</summary>
    Task RelayThinkingStepAsync(Guid sessionId, QuoteAgentThinkingStepDto step, CancellationToken cancellationToken);
}

internal sealed class QuoteAgentService(
    IChatbotServiceClient chatbotClient,
    QuoteEnginePrototypeStore prototypeStore,
    QuoteAgentSessionStore sessionStore,
    QuoteAgentContextToken contextToken,
    CustomerSessionResolver sessionResolver,
    IHttpContextAccessor httpContextAccessor,
    IHubContext<QuoteNotificationsHub> hubContext,
    IConfiguration configuration) : IQuoteAgentService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<QuoteAgentTurnResponse> SendAsync(
        QuoteAgentMessageRequest request,
        CancellationToken cancellationToken)
    {
        var language = NormalizeLanguage(request.Language, request.Message);
        var sessionId = request.SessionId.GetValueOrDefault(Guid.NewGuid());
        var state = sessionStore.GetOrCreate(sessionId, language);
        state.Language = language;
        var customerId = ResolveCustomerId();
        state.CustomerId = customerId ?? state.CustomerId;
        sessionStore.AddAttachments(state, request.Attachments);
        MaterializeSupplementalAnalysis(state, request);
        MaterializePrototypeParts(state, request);

        var chatbotSessionId = await EnsureChatbotSessionAsync(state, language, cancellationToken);
        var token = contextToken.Create(state.SessionId, chatbotSessionId, customerId);
        var chatbotResponse = await chatbotClient.SendMessageAsync(new ChatbotSendMessageRequest
        {
            SessionId = chatbotSessionId,
            Content = ComposeAgentMessage(request.Message, request.CustomerContext, state),
            Language = language,
            Attachments = BuildChatbotAttachments(request.Attachments),
            CallbackUrl = BuildThinkingCallbackUrl(state.SessionId),
            QuoteAgentContextToken = token
        }, cancellationToken);

        var currentState = ToStateResponse(state);
        return new QuoteAgentTurnResponse
        {
            SessionId = state.SessionId,
            MessageId = chatbotResponse?.MessageId,
            AssistantText = string.IsNullOrWhiteSpace(chatbotResponse?.Content)
                ? FallbackAgentAnswer(currentState)
                : chatbotResponse.Content,
            Role = string.IsNullOrWhiteSpace(chatbotResponse?.Role) ? "assistant" : chatbotResponse.Role,
            Language = NormalizeLanguage(chatbotResponse?.Language, request.Message),
            CreatedAt = chatbotResponse?.CreatedAt == default ? DateTimeOffset.UtcNow : chatbotResponse!.CreatedAt,
            Artifacts = currentState.Artifacts,
            Gates = currentState.Gates,
            ProposedActions = currentState.ProposedActions,
            ThinkingSteps = chatbotResponse?.ThinkingSteps ?? []
        };
    }

    public QuoteAgentStateResponse GetState(Guid sessionId)
    {
        return ToStateResponse(sessionStore.GetOrCreate(sessionId));
    }

    public Task<object> ExecuteToolAsync(
        string toolName,
        QuoteAgentToolRequest request,
        QuoteAgentContext context,
        CancellationToken cancellationToken)
    {
        var state = sessionStore.GetOrCreate(context.QuoteSessionId);
        state.ChatbotSessionId = context.ChatbotSessionId;
        state.CustomerId = context.CustomerId ?? state.CustomerId;

        var result = toolName switch
        {
            "quote_get_state" => ToStateResponse(state),
            "quote_get_reference_data" => prototypeStore.ReferenceData,
            "quote_get_account_context" => BuildAccountContext(state),
            "quote_resume_project" => ResumeProjectOrGateError(state, request.Arguments),
            "quote_update_part_configuration" => UpdatePartConfiguration(state, request.Arguments),
            "quote_calculate_estimate" => CalculateEstimate(state),
            "quote_prepare_draft_project" => PrepareActionOrGateError(
                state,
                "draft_project",
                "Create draft project",
                ReadString(request.Arguments, "title") ?? "Create a customer draft project from this quote session.",
                requiresAuthentication: true,
                request.Arguments),
            "quote_duplicate_project" => PrepareActionOrGateError(
                state,
                "duplicate_project",
                "Duplicate project",
                ReadString(request.Arguments, "title") ?? "Duplicate the current customer draft project.",
                requiresAuthentication: true,
                request.Arguments),
            "quote_prepare_formal_quote" => PrepareActionOrGateError(
                state,
                "formal_quote",
                "Generate formal quote",
                ReadString(request.Arguments, "requirements") ?? "Generate a formal quote artifact from the reviewed quote session.",
                requiresAuthentication: true,
                request.Arguments),
            "quote_approve_quote" => PrepareActionOrGateError(
                state,
                "quote_approval",
                "Approve formal quote",
                ReadString(request.Arguments, "note") ?? "Approve the formal quote and allow order creation.",
                requiresAuthentication: true,
                request.Arguments),
            "quote_acknowledge_dfm" => PrepareActionOrGateError(
                state,
                "dfm_acknowledgement",
                "Acknowledge DFM review",
                ReadString(request.Arguments, "note") ?? "Record customer acknowledgement of DFM risks.",
                requiresAuthentication: false,
                request.Arguments),
            "quote_create_order" => PrepareActionOrGateError(
                state,
                "create_order",
                "Create manufacturing order",
                ReadString(request.Arguments, "requirements") ?? "Create a manufacturing order from the approved quote.",
                requiresAuthentication: true,
                request.Arguments),
            "quote_start_payment" => PrepareActionOrGateError(
                state,
                "start_payment",
                "Start payment",
                "Start a PaymentService handoff after checkout ownership, amount, and terms are verified.",
                requiresAuthentication: true,
                request.Arguments),
            _ => new { error = $"Unknown QuoteEngine tool: {toolName}" }
        };
        return Task.FromResult<object>(result);
    }

    public Task<QuoteAgentActionResultResponse?> ConfirmActionAsync(
        Guid actionId,
        QuoteAgentConfirmActionRequest request,
        CancellationToken cancellationToken)
    {
        if (!sessionStore.TryGetAction(actionId, out var action))
        {
            return Task.FromResult<QuoteAgentActionResultResponse?>(null);
        }

        var customerId = ResolveCustomerId();
        if (action.RequiresAuthentication && !customerId.HasValue)
        {
            throw new UnauthorizedAccessException("This action requires a signed-in customer session.");
        }

        var state = sessionStore.GetOrCreate(action.SessionId);
        state.CustomerId = customerId ?? state.CustomerId;
        var message = action.ActionType switch
        {
            "draft_project" => ExecuteDraftProject(state, customerId!.Value, action),
            "duplicate_project" => ExecuteDuplicateProject(state, customerId!.Value, action),
            "formal_quote" => ExecuteFormalQuote(state, customerId!.Value, action),
            "quote_approval" => ExecuteQuoteApproval(state),
            "dfm_acknowledgement" => ExecuteDfmAcknowledgement(state),
            "create_order" => ExecuteCreateOrder(state, customerId!.Value, action),
            "start_payment" => ExecuteStartPayment(state, customerId!.Value),
            _ => $"Action {action.ActionType} completed."
        };

        sessionStore.CompleteAction(state, actionId);
        return Task.FromResult<QuoteAgentActionResultResponse?>(new QuoteAgentActionResultResponse
        {
            ActionId = actionId,
            Status = "completed",
            Message = message,
            State = ToStateResponse(state)
        });
    }

    public Task RelayThinkingStepAsync(
        Guid sessionId,
        QuoteAgentThinkingStepDto step,
        CancellationToken cancellationToken)
    {
        return hubContext.Clients
            .Group(QuoteNotificationsHub.QuoteSessionGroup(sessionId))
            .SendAsync("QuoteAgentThinkingStep", step, cancellationToken);
    }

    private async Task<Guid> EnsureChatbotSessionAsync(
        QuoteAgentSessionState state,
        string language,
        CancellationToken cancellationToken)
    {
        if (state.ChatbotSessionId is { } existing && existing != Guid.Empty)
        {
            return existing;
        }

        var session = await chatbotClient.InitiateSessionAsync(new ChatbotInitiateSessionRequest
        {
            Channel = "quote-engine",
            Language = language
        }, cancellationToken);
        var sessionId = session?.SessionId is { } id && id != Guid.Empty ? id : Guid.NewGuid();
        state.ChatbotSessionId = sessionId;
        return sessionId;
    }

    private QuoteAgentStateResponse ToStateResponse(QuoteAgentSessionState state)
    {
        var customerId = ResolveCustomerId();
        return sessionStore.ToResponse(state, customerId.HasValue, customerId);
    }

    private Guid? ResolveCustomerId()
    {
        return sessionResolver.TryResolveCustomerId(out var customerId) ? customerId : null;
    }

    private QuoteAgentStateResponse PrepareAction(
        QuoteAgentSessionState state,
        string actionType,
        string title,
        string summary,
        bool requiresAuthentication,
        Dictionary<string, JsonElement> arguments)
    {
        sessionStore.AddAction(state, actionType, title, summary, requiresAuthentication, arguments);
        return ToStateResponse(state);
    }

    private object PrepareActionOrGateError(
        QuoteAgentSessionState state,
        string actionType,
        string title,
        string summary,
        bool requiresAuthentication,
        Dictionary<string, JsonElement> arguments)
    {
        var blocker = GetActionBlocker(state, actionType, requiresAuthentication);
        return blocker is null
            ? PrepareAction(state, actionType, title, summary, requiresAuthentication, arguments)
            : new
            {
                error = blocker.Detail,
                requiredGateCode = blocker.Code,
                actionType,
                state = ToStateResponse(state)
            };
    }

    private QuoteAgentGateDto? GetActionBlocker(
        QuoteAgentSessionState state,
        string actionType,
        bool requiresAuthentication)
    {
        var isAuthenticated = ResolveCustomerId().HasValue || state.CustomerId.HasValue;
        var gates = QuoteAgentSessionStore.BuildGates(state, isAuthenticated);
        if (requiresAuthentication)
        {
            var auth = gates.FirstOrDefault(gate => gate.Code == "customer_authenticated");
            if (auth is not null && !auth.Status.Equals("passed", StringComparison.OrdinalIgnoreCase))
            {
                return auth;
            }
        }

        return actionType switch
        {
            "draft_project" => FirstBlockingGate(gates, "geometry_required", "analysis_complete"),
            "duplicate_project" => TryGetCurrentDraftProjectId(state, out _) ? null : new QuoteAgentGateDto(
                "draft_project",
                "Draft project ready",
                "blocked",
                "Create a customer draft project before duplicating it."),
            "formal_quote" => FirstBlockingGate(
                gates,
                "geometry_required",
                "analysis_complete",
                "dfm_reviewed",
                "configuration_complete",
                "priced"),
            "quote_approval" => FirstBlockingGate(gates, "quote_artifact_ready"),
            "dfm_acknowledgement" => state.Parts.Count == 0
                ? gates.FirstOrDefault(gate => gate.Code == "geometry_required")
                : null,
            "create_order" => FirstBlockingGate(gates, "quote_artifact_ready", "quote_approved"),
            "start_payment" => FirstBlockingGate(gates, "order_created", "checkout_ready"),
            _ => null
        };
    }

    private static QuoteAgentGateDto? FirstBlockingGate(
        IReadOnlyCollection<QuoteAgentGateDto> gates,
        params string[] codes)
    {
        return codes
            .Select(code => gates.FirstOrDefault(gate => gate.Code == code))
            .FirstOrDefault(gate => gate is not null && !gate.Status.Equals("passed", StringComparison.OrdinalIgnoreCase));
    }

    private QuoteAgentStateResponse UpdatePartConfiguration(
        QuoteAgentSessionState state,
        Dictionary<string, JsonElement> arguments)
    {
        lock (state.SyncRoot)
        {
            var part = ResolvePart(state, arguments);
            if (part is not null)
            {
                SetIfPresent(arguments, "process", value => part.ProcessId = value);
                SetIfPresent(arguments, "material", value => part.MaterialId = value);
                SetIfPresent(arguments, "finish", value =>
                {
                    part.FinishId = value;
                    part.FinishCode = value;
                });
                SetIfPresent(arguments, "color", value => part.Color = value);
                SetIfPresent(arguments, "tolerance", value =>
                {
                    part.ToleranceId = value;
                    part.ToleranceCode = value;
                });
                SetIfPresent(arguments, "quantity", value =>
                {
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity))
                    {
                        part.Quantity = Math.Clamp(quantity, 1, 100_000);
                    }
                });
                SetIfPresent(arguments, "lead_time", value => state.LeadTimeCode = value);
            }
        }

        return ToStateResponse(state);
    }

    private QuoteAgentStateResponse CalculateEstimate(QuoteAgentSessionState state)
    {
        lock (state.SyncRoot)
        {
            if (state.Parts.Count > 0)
            {
                state.Estimate = prototypeStore.Estimate(new QuoteEstimateRequest
                {
                    QuoteSessionId = state.SessionId.ToString("N"),
                    LeadTimeCode = state.LeadTimeCode,
                    Parts = state.Parts
                });
                UpsertArtifact(state, "pricing", "Pricing estimate", "ready", null, null);
            }
        }

        return ToStateResponse(state);
    }

    private object BuildAccountContext(QuoteAgentSessionState state)
    {
        var customerId = ResolveCustomerId();
        return new
        {
            isAuthenticated = customerId.HasValue,
            customerId,
            signInUrl = "/auth/sign-in?returnUrl=/quote/new",
            signUpUrl = "/auth/sign-up?returnUrl=/quote/new",
            gates = QuoteAgentSessionStore.BuildGates(state, customerId.HasValue)
        };
    }

    private object ResumeProjectOrGateError(
        QuoteAgentSessionState state,
        Dictionary<string, JsonElement> arguments)
    {
        var customerId = ResolveCustomerId() ?? state.CustomerId;
        if (!customerId.HasValue)
        {
            return new
            {
                error = "Sign in before resuming customer projects.",
                requiredGateCode = "customer_authenticated",
                actionType = "resume_project",
                state = ToStateResponse(state)
            };
        }

        if (!TryReadGuid(arguments, "project_id", out var projectId) &&
            !TryReadGuid(arguments, "projectId", out projectId))
        {
            return new
            {
                error = "A project_id is required to resume a customer project.",
                requiredGateCode = "project_id",
                actionType = "resume_project",
                state = ToStateResponse(state)
            };
        }

        var project = prototypeStore.GetProject(customerId.Value, projectId);
        if (project is null)
        {
            return new
            {
                error = "The requested project was not found for the signed-in customer.",
                requiredGateCode = "project_access",
                actionType = "resume_project",
                state = ToStateResponse(state)
            };
        }

        lock (state.SyncRoot)
        {
            state.Parts.Clear();
            state.Attachments.Clear();
            state.Artifacts.Clear();
            state.ProposedActions.Clear();
            state.Estimate = null;
            state.FormalQuote = null;
            state.QuoteApproved = false;
            state.Order = null;
            state.Payment = null;

            foreach (var part in project.Parts)
            {
                state.Parts.Add(part);
                UpsertArtifact(state, "viewer", $"3D viewer - {part.FileName}", "ready", part.PartId, part.ViewerGlbUrl);
                UpsertArtifact(state, "dfm", $"DFM analysis - {part.FileName}", "ready", part.PartId, null);
            }

            UpsertArtifact(state, "requirements_summary", "Project summary", "ready", null, null);
            UpsertArtifact(state, "resumed_project", project.Title, project.Status, null, null);
            SetArtifactMetadata(state, "resumed_project", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["projectId"] = project.ProjectId.ToString("D"),
                ["projectNumber"] = project.ProjectNumber
            });

            if (HasPriceableConfiguration(state))
            {
                state.Estimate = prototypeStore.Estimate(new QuoteEstimateRequest
                {
                    QuoteSessionId = state.SessionId.ToString("N"),
                    LeadTimeCode = state.LeadTimeCode,
                    Parts = state.Parts
                });
                UpsertArtifact(state, "pricing", "Pricing estimate", "ready", null, null);
            }
        }

        return ToStateResponse(state);
    }

    private string ExecuteDraftProject(QuoteAgentSessionState state, Guid customerId, QuoteAgentPendingAction action)
    {
        var response = prototypeStore.CreateDraftProject(customerId, new CreateDraftProjectRequest(
            state.SessionId.ToString("N"),
            state.Parts,
            ReadString(action.Arguments, "requirements") ?? ReadString(action.Arguments, "notes") ?? string.Empty,
            ReadString(action.Arguments, "title") ?? "Chat-created quote"));
        UpsertArtifact(state, "draft_project", response.Title, response.Status, null, null);
        SetArtifactMetadata(state, "draft_project", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["projectId"] = response.ProjectId.ToString("D"),
            ["projectNumber"] = response.ProjectNumber
        });
        return $"Draft project {response.ProjectNumber} is ready.";
    }

    private string ExecuteDuplicateProject(QuoteAgentSessionState state, Guid customerId, QuoteAgentPendingAction action)
    {
        if (!TryGetCurrentDraftProjectId(state, out var sourceProjectId))
        {
            throw new InvalidOperationException("A draft project is required before duplicating it.");
        }

        var response = prototypeStore.DuplicateDraftProject(
            customerId,
            sourceProjectId,
            new DuplicateDraftProjectRequest(ReadString(action.Arguments, "title")));
        if (response is null)
        {
            throw new KeyNotFoundException("The current draft project was not found for the signed-in customer.");
        }

        UpsertArtifact(state, "duplicate_project", response.Title, response.Status, null, null);
        SetArtifactMetadata(state, "duplicate_project", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["projectId"] = response.ProjectId.ToString("D"),
            ["projectNumber"] = response.ProjectNumber,
            ["sourceProjectId"] = sourceProjectId.ToString("D")
        });
        return $"Project {response.ProjectNumber} was duplicated from the current draft.";
    }

    private string ExecuteFormalQuote(QuoteAgentSessionState state, Guid customerId, QuoteAgentPendingAction action)
    {
        state.FormalQuote = prototypeStore.GenerateQuote(customerId);
        UpsertArtifact(state, "formal_quote", state.FormalQuote.QuoteNumber, state.FormalQuote.Status, null, state.FormalQuote.PdfUrl);
        return $"Formal quote {state.FormalQuote.QuoteNumber} is ready.";
    }

    private static string ExecuteQuoteApproval(QuoteAgentSessionState state)
    {
        if (state.FormalQuote is null)
        {
            throw new InvalidOperationException("A formal quote is required before quote approval.");
        }

        state.QuoteApproved = true;
        UpsertArtifact(state, "quote_approval", "Quote approval", "approved", null, state.FormalQuote.PdfUrl);
        return $"Formal quote {state.FormalQuote.QuoteNumber} is approved.";
    }

    private static string ExecuteDfmAcknowledgement(QuoteAgentSessionState state)
    {
        foreach (var part in state.Parts)
        {
            part.DfmAcknowledged = true;
        }

        return "DFM risks were acknowledged for the current quote session.";
    }

    private string ExecuteCreateOrder(QuoteAgentSessionState state, Guid customerId, QuoteAgentPendingAction action)
    {
        if (state.FormalQuote is null)
        {
            throw new InvalidOperationException("A formal quote is required before creating an order.");
        }

        state.Order = prototypeStore.CreateOrder(customerId, state.FormalQuote.QuoteId);
        UpsertArtifact(state, "order", state.Order.OrderNumber, state.Order.Status, null, null);
        return $"Manufacturing order {state.Order.OrderNumber} is created.";
    }

    private string ExecuteStartPayment(QuoteAgentSessionState state, Guid customerId)
    {
        if (state.Order is null)
        {
            throw new InvalidOperationException("A manufacturing order is required before payment.");
        }

        state.Payment = prototypeStore.StartPayment(customerId, state.Order.OrderId);
        UpsertArtifact(state, "payment", "Payment handoff", state.Payment.Status, null, state.Payment.PaymentUrl);
        return $"Payment handoff is ready for {state.Order.OrderNumber}.";
    }

    private static QuotePartDraftDto? ResolvePart(
        QuoteAgentSessionState state,
        IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (state.Parts.Count == 0)
        {
            return null;
        }

        var rawPartId = ReadString(arguments, "part_id");
        return Guid.TryParse(rawPartId, out var partId)
            ? state.Parts.FirstOrDefault(part => part.PartId == partId) ?? state.Parts[0]
            : state.Parts[0];
    }

    private static void SetIfPresent(
        IReadOnlyDictionary<string, JsonElement> arguments,
        string key,
        Action<string> assign)
    {
        var value = ReadString(arguments, key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            assign(value.Trim());
        }
    }

    private static void UpsertArtifact(
        QuoteAgentSessionState state,
        string artifactType,
        string title,
        string status,
        Guid? partId,
        string? url)
    {
        state.Artifacts.RemoveAll(item => item.ArtifactType.Equals(artifactType, StringComparison.OrdinalIgnoreCase));
        state.Artifacts.Add(new QuoteAgentArtifactDto
        {
            ArtifactType = artifactType,
            Title = title,
            Status = status,
            PartId = partId,
            Url = url
        });
    }

    private static void SetArtifactMetadata(
        QuoteAgentSessionState state,
        string artifactType,
        IReadOnlyDictionary<string, string> metadata)
    {
        var artifact = state.Artifacts.FirstOrDefault(item =>
            item.ArtifactType.Equals(artifactType, StringComparison.OrdinalIgnoreCase));
        if (artifact is null)
        {
            return;
        }

        foreach (var (key, value) in metadata)
        {
            artifact.Metadata[key] = value;
        }
    }

    private static bool TryGetCurrentDraftProjectId(QuoteAgentSessionState state, out Guid projectId)
    {
        projectId = Guid.Empty;
        var artifact = state.Artifacts
            .LastOrDefault(item => item.ArtifactType.Equals("draft_project", StringComparison.OrdinalIgnoreCase));
        return artifact is not null &&
            artifact.Metadata.TryGetValue("projectId", out var rawProjectId) &&
            Guid.TryParse(rawProjectId, out projectId);
    }

    private void MaterializePrototypeParts(
        QuoteAgentSessionState state,
        QuoteAgentMessageRequest request)
    {
        var geometryAttachments = request.Attachments
            .Where(attachment => attachment.SatisfiesGeometryGate)
            .ToList();
        if (geometryAttachments.Count == 0)
        {
            return;
        }

        lock (state.SyncRoot)
        {
            foreach (var attachment in geometryAttachments)
            {
                var uploadId = ResolveUploadId(attachment);
                if (state.Parts.Any(part =>
                    part.UploadId.Equals(uploadId, StringComparison.OrdinalIgnoreCase) ||
                    part.FileName.Equals(attachment.FileName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var part = BuildPrototypeAnalyzedPart(attachment, uploadId, request.Message);
                AttachSupplementalFiles(part, state.Attachments);
                state.Parts.Add(part);
                UpsertArtifact(state, "viewer", $"3D viewer - {part.FileName}", "ready", part.PartId, part.ViewerGlbUrl);
                UpsertArtifact(state, "dfm", $"DFM analysis - {part.FileName}", "ready", part.PartId, null);
                UpsertArtifact(state, "requirements_summary", "Project summary", "ready", part.PartId, null);
                MarkSupplementalAnalysisGeometrySatisfied(state);
            }

            ApplyMessageConfiguration(state, request.Message);
            if (HasPriceableConfiguration(state))
            {
                state.Estimate = prototypeStore.Estimate(new QuoteEstimateRequest
                {
                    QuoteSessionId = state.SessionId.ToString("N"),
                    LeadTimeCode = state.LeadTimeCode,
                    Parts = state.Parts
                });
                UpsertArtifact(state, "pricing", "Pricing estimate", "ready", null, null);
            }
        }
    }

    private static void MaterializeSupplementalAnalysis(
        QuoteAgentSessionState state,
        QuoteAgentMessageRequest request)
    {
        var supplemental = request.Attachments
            .Where(IsSupplementalManufacturingAttachment)
            .ToList();
        if (supplemental.Count == 0)
        {
            return;
        }

        lock (state.SyncRoot)
        {
            var artifact = state.Artifacts.FirstOrDefault(item =>
                item.ArtifactType.Equals("analysis", StringComparison.OrdinalIgnoreCase));
            if (artifact is null)
            {
                artifact = new QuoteAgentArtifactDto
                {
                    ArtifactType = "analysis",
                    Title = "Supplemental requirement analysis",
                    Status = "needs_geometry"
                };
                state.Artifacts.Add(artifact);
            }

            artifact.Metadata["fileCount"] = supplemental.Count.ToString(CultureInfo.InvariantCulture);
            artifact.Metadata["fileNames"] = string.Join(", ", supplemental.Select(item => item.FileName).Take(5));
            artifact.Metadata["geometryGate"] = "not_satisfied_by_supplemental_files";
            artifact.Metadata["summary"] = BuildSupplementalSummary(request.Message, supplemental);

            foreach (var part in state.Parts)
            {
                AttachSupplementalFiles(part, supplemental);
            }
        }
    }

    private static void MarkSupplementalAnalysisGeometrySatisfied(QuoteAgentSessionState state)
    {
        var artifact = state.Artifacts.FirstOrDefault(item =>
            item.ArtifactType.Equals("analysis", StringComparison.OrdinalIgnoreCase));
        if (artifact is null)
        {
            return;
        }

        artifact.Status = "ready";
        artifact.Metadata["geometryGate"] = "satisfied_by_cad_attachment";
    }

    private static bool IsSupplementalManufacturingAttachment(QuoteAgentAttachmentDto attachment)
    {
        if (attachment.SatisfiesGeometryGate)
        {
            return false;
        }

        return attachment.Kind.Equals("drawing", StringComparison.OrdinalIgnoreCase) ||
            attachment.Kind.Equals("photo", StringComparison.OrdinalIgnoreCase) ||
            attachment.Kind.Equals("sketch", StringComparison.OrdinalIgnoreCase) ||
            attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
            attachment.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSupplementalSummary(
        string message,
        IReadOnlyCollection<QuoteAgentAttachmentDto> supplemental)
    {
        var kinds = supplemental
            .Select(item => string.IsNullOrWhiteSpace(item.Kind) ? item.ContentType : item.Kind)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4);
        var quantity = InferQuantity(message);
        var process = InferProcessFromMessage(message) ?? "unknown";
        return $"Captured {supplemental.Count} supplemental file(s): {string.Join(", ", kinds)}. Inferred quantity {quantity} and process {process}; CAD/3D geometry is still required for final DFM, pricing, order, and payment.";
    }

    private static void AttachSupplementalFiles(
        QuotePartDraftDto part,
        IEnumerable<QuoteAgentAttachmentDto> attachments)
    {
        foreach (var attachment in attachments.Where(IsSupplementalManufacturingAttachment))
        {
            if (part.DrawingFiles.Any(file =>
                file.StoragePath.Equals(attachment.StoragePath ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                file.FileName.Equals(attachment.FileName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            part.DrawingFiles.Add(new QuotePartAttachmentDto(
                attachment.FileName,
                attachment.StoragePath ?? attachment.Url ?? attachment.AttachmentId.ToString("N"),
                attachment.ContentType,
                attachment.FileSizeBytes,
                attachment.Kind));
        }
    }

    private static QuotePartDraftDto BuildPrototypeAnalyzedPart(
        QuoteAgentAttachmentDto attachment,
        string uploadId,
        string message)
    {
        var process = InferProcess(attachment, message);
        var material = InferMaterial(process, message);
        var quantity = InferQuantity(message);
        var volume = Math.Clamp(Math.Round(Math.Max(attachment.FileSizeBytes, 1) / 10_000m, 2), 4m, 500m);

        return new QuotePartDraftDto
        {
            PartId = Guid.NewGuid(),
            FileId = Guid.TryParse(attachment.UploadId, out var fileId) ? fileId : Guid.NewGuid(),
            UploadId = uploadId,
            FileName = string.IsNullOrWhiteSpace(attachment.FileName) ? "uploaded-part.stl" : attachment.FileName,
            ProcessId = process,
            MaterialId = material,
            FinishId = InferFinish(process, message),
            FinishCode = InferFinish(process, message),
            ToleranceId = InferTolerance(process, message),
            ToleranceCode = InferTolerance(process, message),
            InspectionLevel = "STANDARD",
            Quantity = quantity,
            VolumeCc = volume,
            SurfaceAreaCm2 = Math.Round(volume * 6m, 2),
            StoragePath = attachment.StoragePath,
            Status = "DfmAnalysisReady",
            ViewerGlbUrl = "/models/sample.glb",
            ViewerStoragePath = attachment.StoragePath,
            ViewerFileExtension = Path.GetExtension(attachment.FileName).TrimStart('.').ToLowerInvariant(),
            ThumbnailUrl = "/images/generated/sample-part.svg",
            Findings = [],
            IsManifold = true,
            DfmAcknowledged = false,
            PartNotes = "Prototype analysis generated from uploaded CAD/3D attachment metadata until GeometryService returns authoritative analysis.",
            BodyCount = 1,
            SelectedBodyIndex = 0
        };
    }

    private static void ApplyMessageConfiguration(QuoteAgentSessionState state, string message)
    {
        var leadTime = InferLeadTime(message);
        if (!string.IsNullOrWhiteSpace(leadTime))
        {
            state.LeadTimeCode = leadTime;
        }

        foreach (var part in state.Parts)
        {
            part.Quantity = Math.Max(part.Quantity, InferQuantity(message));
            part.ProcessId = InferProcessFromMessage(message) ?? part.ProcessId;
            part.MaterialId = InferMaterial(part.ProcessId, message);
            part.FinishId = InferFinish(part.ProcessId, message);
            part.FinishCode = part.FinishId;
            part.ToleranceId = InferTolerance(part.ProcessId, message);
            part.ToleranceCode = part.ToleranceId;
        }
    }

    private static bool HasPriceableConfiguration(QuoteAgentSessionState state)
    {
        return state.Parts.Count > 0 &&
            state.Parts.All(part =>
                !string.IsNullOrWhiteSpace(part.ProcessId) &&
                !string.IsNullOrWhiteSpace(part.MaterialId) &&
                part.Quantity > 0) &&
            !string.IsNullOrWhiteSpace(state.LeadTimeCode);
    }

    private static string ResolveUploadId(QuoteAgentAttachmentDto attachment)
    {
        return !string.IsNullOrWhiteSpace(attachment.UploadId)
            ? attachment.UploadId.Trim()
            : attachment.AttachmentId.ToString("N");
    }

    private static int InferQuantity(string message)
    {
        var match = Regex.Match(message, @"\b(?<quantity>\d{1,5})\s*(pcs?|pieces?|parts?|units?)?\b", RegexOptions.IgnoreCase);
        return match.Success &&
            int.TryParse(match.Groups["quantity"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity)
            ? Math.Clamp(quantity, 1, 100_000)
            : 1;
    }

    private static string InferProcess(QuoteAgentAttachmentDto attachment, string message)
    {
        return InferProcessFromMessage(message) ??
            (QuoteUploadConstraints.MachiningFileExtensions.Any(ext => attachment.FileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                ? "cnc"
                : "fdm");
    }

    private static string? InferProcessFromMessage(string message)
    {
        if (message.Contains("cnc", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("machin", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("aluminum", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("aluminium", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("6061", StringComparison.OrdinalIgnoreCase))
        {
            return "cnc";
        }

        if (message.Contains("sla", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("resin", StringComparison.OrdinalIgnoreCase))
        {
            return "sla";
        }

        if (message.Contains("3d print", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("fdm", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("nylon", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("pla", StringComparison.OrdinalIgnoreCase))
        {
            return "fdm";
        }

        return null;
    }

    private static string InferMaterial(string process, string message)
    {
        if (process.Equals("cnc", StringComparison.OrdinalIgnoreCase))
        {
            return "al6061";
        }

        if (process.Equals("sla", StringComparison.OrdinalIgnoreCase))
        {
            return "resin-gray";
        }

        if (message.Contains("clear", StringComparison.OrdinalIgnoreCase))
        {
            return "petg-clear";
        }

        return "pla-black";
    }

    private static string InferFinish(string process, string message)
    {
        if (process.Equals("cnc", StringComparison.OrdinalIgnoreCase))
        {
            return message.Contains("anod", StringComparison.OrdinalIgnoreCase)
                ? "cnc-bead-blast-clear"
                : "cnc-as-machined";
        }

        if (process.Equals("sla", StringComparison.OrdinalIgnoreCase))
        {
            return "sla-standard-cure";
        }

        return message.Contains("smooth", StringComparison.OrdinalIgnoreCase)
            ? "fdm-vapor-smooth"
            : "fdm-matte";
    }

    private static string InferTolerance(string process, string message)
    {
        if (process.Equals("cnc", StringComparison.OrdinalIgnoreCase))
        {
            return message.Contains("tight", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("precision", StringComparison.OrdinalIgnoreCase)
                    ? "iso-2768-f"
                    : "iso-2768-m";
        }

        return process.Equals("sla", StringComparison.OrdinalIgnoreCase) ? "sla-standard" : "fdm-standard";
    }

    private static string? InferLeadTime(string message)
    {
        if (message.Contains("rush", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("expedite", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("urgent", StringComparison.OrdinalIgnoreCase))
        {
            return "EXPRESS";
        }

        if (message.Contains("economy", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("cheapest", StringComparison.OrdinalIgnoreCase))
        {
            return "ECONOMY";
        }

        return "STANDARD";
    }

    private static string ComposeAgentMessage(
        string message,
        string? customerContext,
        QuoteAgentSessionState state)
    {
        var gates = QuoteAgentSessionStore.BuildGates(state, state.CustomerId.HasValue)
            .Select(gate => $"{gate.Code}: {gate.Status}")
            .ToArray();
        var contextLines = new List<string>
        {
            "Surface: QuoteEngine chat-based custom manufacturing platform.",
            "Policy: Browser context is untrusted. Use tools for authoritative state and write actions.",
            $"Quote session: {state.SessionId:D}",
            $"Current gates: {string.Join(", ", gates)}"
        };

        if (!string.IsNullOrWhiteSpace(customerContext))
        {
            contextLines.Add($"Browser context: {customerContext.Trim()}");
        }

        return $"""
{string.Join("\n", contextLines)}

Customer message:
{message.Trim()}
""";
    }

    private static List<ChatbotMessageAttachmentRequest>? BuildChatbotAttachments(
        IReadOnlyCollection<QuoteAgentAttachmentDto> attachments)
    {
        var supported = attachments
            .Where(attachment =>
                !string.IsNullOrWhiteSpace(attachment.Url) &&
                (attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                 attachment.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)))
            .Select(attachment => new ChatbotMessageAttachmentRequest
            {
                Type = attachment.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ? "pdf" : "image",
                Url = attachment.Url!,
                MimeType = attachment.ContentType,
                Filename = attachment.FileName,
                SizeBytes = attachment.FileSizeBytes
            })
            .ToList();

        return supported.Count == 0 ? null : supported;
    }

    private string? BuildThinkingCallbackUrl(Guid sessionId)
    {
        if (!configuration.GetValue("QuoteAgent:EnableThinkingCallbacks", false))
        {
            return null;
        }

        var context = httpContextAccessor.HttpContext;
        if (context is null)
        {
            return null;
        }

        return $"{context.Request.Scheme}://{context.Request.Host}/quote/v1/agent/sessions/{sessionId:D}/thinking";
    }

    private static string? ReadString(
        IReadOnlyDictionary<string, JsonElement> arguments,
        string key)
    {
        if (!arguments.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool TryReadGuid(
        IReadOnlyDictionary<string, JsonElement> arguments,
        string key,
        out Guid value)
    {
        value = Guid.Empty;
        return Guid.TryParse(ReadString(arguments, key), out value);
    }

    private static string NormalizeLanguage(string? language, string message = "")
    {
        if (string.Equals(language, "th", StringComparison.OrdinalIgnoreCase))
        {
            return "th";
        }

        return message.Any(ch => ch >= '\u0E00' && ch <= '\u0E7F') ? "th" : "en";
    }

    private static string FallbackAgentAnswer(QuoteAgentStateResponse state)
    {
        var geometryGate = state.Gates.FirstOrDefault(gate => gate.Code == "geometry_required");
        return geometryGate?.Status == "passed"
            ? "I can continue configuring this quote. Tell me the material, finish, tolerance, quantity, or lead time you want."
            : "Upload a CAD or 3D file and I can analyze geometry, DFM, materials, lead time, and pricing.";
    }
}
