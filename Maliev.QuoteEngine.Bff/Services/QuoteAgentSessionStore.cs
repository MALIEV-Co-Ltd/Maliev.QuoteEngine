using System.Collections.Concurrent;
using System.Text.Json;
using Maliev.QuoteEngine.Shared.Agent;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Bff.Services;

internal sealed class QuoteAgentSessionStore
{
    private readonly ConcurrentDictionary<Guid, QuoteAgentSessionState> _sessions = new();
    private readonly ConcurrentDictionary<Guid, QuoteAgentPendingAction> _actions = new();
    private readonly ConcurrentDictionary<Guid, QuoteAgentCompletedAction> _completedActions = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _actionLocks = new();

    public QuoteAgentSessionState GetOrCreate(Guid sessionId, string language = "en")
    {
        return _sessions.GetOrAdd(sessionId, id => new QuoteAgentSessionState
        {
            SessionId = id,
            Language = NormalizeLanguage(language),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    public bool TryGet(Guid sessionId, out QuoteAgentSessionState state)
    {
        return _sessions.TryGetValue(sessionId, out state!);
    }

    public void AddAttachments(QuoteAgentSessionState state, IEnumerable<QuoteAgentAttachmentDto> attachments)
    {
        lock (state.SyncRoot)
        {
            foreach (var attachment in attachments)
            {
                attachment.SatisfiesGeometryGate = IsGeometryAttachment(attachment);
                var existingIndex = state.Attachments.FindIndex(item =>
                    item.AttachmentId == attachment.AttachmentId ||
                    (!string.IsNullOrWhiteSpace(attachment.UploadId) &&
                     string.Equals(item.UploadId, attachment.UploadId, StringComparison.OrdinalIgnoreCase)));

                if (existingIndex >= 0)
                {
                    state.Attachments[existingIndex] = attachment;
                }
                else
                {
                    state.Attachments.Add(attachment);
                }
            }

            Touch(state);
        }
    }

    public void UpsertPart(QuoteAgentSessionState state, QuotePartDraftDto part)
    {
        lock (state.SyncRoot)
        {
            var index = state.Parts.FindIndex(item => item.PartId == part.PartId);
            if (index >= 0)
            {
                state.Parts[index] = part;
            }
            else
            {
                state.Parts.Add(part);
            }

            Touch(state);
        }
    }

    public QuoteAgentPendingAction AddAction(
        QuoteAgentSessionState state,
        string actionType,
        string title,
        string summary,
        bool requiresAuthentication,
        Dictionary<string, JsonElement> arguments,
        bool requiresConfirmation = true)
    {
        var action = new QuoteAgentPendingAction(
            Guid.NewGuid(),
            state.SessionId,
            actionType,
            title,
            summary,
            requiresAuthentication,
            requiresConfirmation,
            state.CustomerId,
            arguments,
            DateTimeOffset.UtcNow);
        _actions[action.ActionId] = action;

        lock (state.SyncRoot)
        {
            state.ProposedActions.RemoveAll(existing =>
                existing.ActionType.Equals(actionType, StringComparison.OrdinalIgnoreCase) &&
                existing.Status.Equals("pending_confirmation", StringComparison.OrdinalIgnoreCase));
            state.ProposedActions.Add(action.ToDto());
            Touch(state);
        }

        return action;
    }

    public bool TryGetAction(Guid actionId, out QuoteAgentPendingAction action)
    {
        return _actions.TryGetValue(actionId, out action!);
    }

    public bool TryGetCompletedAction(
        Guid actionId,
        Guid? customerId,
        out QuoteAgentActionResultResponse result)
    {
        result = default!;
        if (!_completedActions.TryGetValue(actionId, out var completed) ||
            !CanAccessAction(completed.CustomerId, customerId))
        {
            return false;
        }

        result = completed.Result;
        return true;
    }

    public SemaphoreSlim GetActionLock(Guid actionId)
    {
        return _actionLocks.GetOrAdd(actionId, _ => new SemaphoreSlim(1, 1));
    }

    public void ReleaseActionLock(Guid actionId)
    {
        _actionLocks.TryRemove(actionId, out _);
    }

    public void CompleteAction(
        QuoteAgentSessionState state,
        Guid actionId,
        QuoteAgentActionResultResponse result)
    {
        _actions.TryRemove(actionId, out var pendingAction);
        _completedActions[actionId] = new QuoteAgentCompletedAction(result, pendingAction?.CustomerId);
        lock (state.SyncRoot)
        {
            var action = state.ProposedActions.FirstOrDefault(item => item.ActionId == actionId);
            if (action is not null)
            {
                action.Status = "completed";
            }

            Touch(state);
        }
    }

    public void RemovePendingActions(
        QuoteAgentSessionState state,
        Func<QuoteAgentProposedActionDto, bool> predicate)
    {
        lock (state.SyncRoot)
        {
            var removed = false;
            foreach (var action in state.ProposedActions
                .Where(item =>
                    item.Status.Equals("pending_confirmation", StringComparison.OrdinalIgnoreCase) &&
                    predicate(item))
                .ToList())
            {
                state.ProposedActions.Remove(action);
                _actions.TryRemove(action.ActionId, out _);
                removed = true;
            }

            if (removed)
            {
                Touch(state);
            }
        }
    }

    public static bool CanAccessAction(Guid? actionCustomerId, Guid? customerId)
    {
        if (!actionCustomerId.HasValue)
        {
            return true;
        }

        return customerId == actionCustomerId;
    }

    public QuoteAgentStateResponse ToResponse(
        QuoteAgentSessionState state,
        bool isAuthenticated,
        Guid? customerId)
    {
        lock (state.SyncRoot)
        {
            state.CustomerId = customerId ?? state.CustomerId;
            return new QuoteAgentStateResponse
            {
                SessionId = state.SessionId,
                Summary = BuildSummary(state),
                IsAuthenticated = isAuthenticated,
                CustomerId = customerId,
                Attachments = state.Attachments.Select(CloneAttachment).ToList(),
                Parts = state.Parts.Select(ClonePart).ToList(),
                Artifacts = state.Artifacts.Select(CloneArtifact).ToList(),
                Gates = BuildGates(state, isAuthenticated),
                ProposedActions = state.ProposedActions
                    .Where(action => action.Status.Equals("pending_confirmation", StringComparison.OrdinalIgnoreCase))
                    .Select(CloneAction)
                    .ToList(),
                Estimate = state.Estimate,
                UiDirectives = state.UiDirectives.Select(CloneUiDirective).ToList(),
                ProjectName = state.ProjectName
            };
        }
    }

    public static List<QuoteAgentGateDto> BuildGates(QuoteAgentSessionState state, bool isAuthenticated)
    {
        var hasGeometry = state.Parts.Count > 0 || state.Attachments.Any(item => item.SatisfiesGeometryGate);
        var analysisComplete = state.Parts.Count > 0 &&
            state.Parts.All(part => IsTerminalAnalysisStatus(part.Status));
        var hasDfmIssues = state.Parts.Any(HasDfmIssues);
        var dfmReviewed = state.Parts.Count > 0 && state.Parts.All(part => part.DfmAcknowledged || !HasDfmIssues(part));
        var configurationComplete = state.ConfigurationConfirmed &&
            state.Parts.Count > 0 &&
            state.Parts.All(part =>
                !string.IsNullOrWhiteSpace(part.ProcessId) &&
                !string.IsNullOrWhiteSpace(part.MaterialId) &&
                part.Quantity > 0) &&
            !string.IsNullOrWhiteSpace(state.LeadTimeCode);
        var hasAuthoritativePricing = state.Estimate?.IsAuthoritative == true;

        var checkoutReady = state.Order is not null &&
            isAuthenticated &&
            state.CheckoutBillingAddressId.HasValue &&
            state.CheckoutShippingAddressId.HasValue &&
            state.CheckoutAcceptedTerms &&
            state.CheckoutConsent;

        return
        [
            Gate("geometry_required", "Usable 3D/CAD geometry", hasGeometry ? "passed" : "blocked",
                hasGeometry
                    ? "At least one geometry-capable CAD or 3D file is available."
                    : "Upload STEP, STL, 3MF, OBJ, GLB, or another supported CAD/3D file before DFM, pricing, order, or payment."),
            Gate("analysis_complete", "Geometry and DFM analysis complete", analysisComplete ? "passed" : hasGeometry ? "pending" : "blocked",
                analysisComplete ? "Analysis reached a terminal state." : "Analysis is waiting for usable geometry or completion."),
            Gate("dfm_reviewed", "DFM issues reviewed", dfmReviewed ? "passed" : hasDfmIssues ? "blocked" : "pending",
                dfmReviewed ? "Blocking manufacturing risks are resolved or acknowledged." : "Review and acknowledge DFM risks before formal quote or order actions."),
            Gate("configuration_complete", "Configuration complete", configurationComplete ? "passed" : "pending",
                configurationComplete ? "Process, material, quantity, and lead time are selected." : "Select process, material, quantity, finish/color, tolerance, and lead time."),
            Gate("priced", "Authoritative pricing available", hasAuthoritativePricing ? "passed" : "pending",
                hasAuthoritativePricing
                    ? "Authoritative pricing is available for the current configuration."
                    : state.Estimate is not null
                        ? "A prototype estimate is available, but authoritative PricingService pricing is required before a formal quote."
                        : "Pricing waits for geometry and required configuration."),
            Gate("customer_authenticated", "Customer authenticated", isAuthenticated ? "passed" : "blocked",
                isAuthenticated ? "Signed-in customer session is available." : "Sign in or sign up before durable quote, order, document, or payment actions."),
            Gate("quote_artifact_ready", "Quote artifacts ready", state.FormalQuote is not null ? "passed" : "pending",
                state.FormalQuote is not null ? "Formal quote artifact is ready." : "Generate a formal quote after required gates pass."),
            Gate("quote_approved", "Quote approved", state.QuoteApproved ? "passed" : "pending",
                state.QuoteApproved ? "Customer approved the quote." : "Customer approval is still pending."),
            Gate("order_created", "Order created", state.Order is not null ? "passed" : "pending",
                state.Order is not null ? "Manufacturing order exists." : "Order creation waits for quote approval and confirmation."),
            Gate("checkout_ready", "Checkout ready", checkoutReady ? "passed" : "pending",
                checkoutReady
                    ? "Billing, shipping, terms, consent, and ownership are ready for payment."
                    : "Billing and shipping addresses, phone, company/VAT if applicable, terms, and consent must be verified before payment."),
            Gate("payment_started_or_completed", "Payment started or completed", state.Payment is not null ? "passed" : "pending",
                state.Payment is not null ? "Payment handoff exists." : "Payment has not started.")
        ];
    }

    public static bool HasDfmIssues(QuotePartDraftDto part)
    {
        return part.Findings.Count > 0
            || part.FdmReport?.Issues.Count > 0
            || part.SlaReport?.Issues.Count > 0
            || part.CncReport?.Issues.Count > 0
            || (!part.IsManifold && !string.IsNullOrWhiteSpace(part.NonManifoldReason));
    }

    private static bool IsGeometryAttachment(QuoteAgentAttachmentDto attachment)
    {
        return QuoteUploadConstraints.IsSupportedCadFileName(attachment.FileName)
            || attachment.Kind.Equals("cad", StringComparison.OrdinalIgnoreCase)
            || attachment.Kind.Equals("geometry", StringComparison.OrdinalIgnoreCase)
            || attachment.Kind.Equals("3d", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminalAnalysisStatus(string? status)
    {
        return status is not null &&
            (status.Equals("Analyzed", StringComparison.OrdinalIgnoreCase) ||
             status.Equals("DfmAnalysisReady", StringComparison.OrdinalIgnoreCase) ||
             status.Equals("GlbReady", StringComparison.OrdinalIgnoreCase) ||
             status.Equals("ModelGenerated", StringComparison.OrdinalIgnoreCase) ||
             status.Equals("Failed", StringComparison.OrdinalIgnoreCase));
    }

    private static QuoteAgentGateDto Gate(string code, string label, string status, string detail)
    {
        return new QuoteAgentGateDto(code, label, status, detail);
    }

    private static string NormalizeLanguage(string? language)
    {
        return string.Equals(language, "th", StringComparison.OrdinalIgnoreCase) ? "th" : "en";
    }

    private static void Touch(QuoteAgentSessionState state)
    {
        state.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string BuildSummary(QuoteAgentSessionState state)
    {
        if (state.Parts.Count == 0 && state.Attachments.Count == 0)
        {
            return "No usable manufacturing files have been attached yet.";
        }

        return $"{state.Parts.Count} configured part(s), {state.Attachments.Count} attachment(s), lead time {state.LeadTimeCode}.";
    }

    private static QuoteAgentAttachmentDto CloneAttachment(QuoteAgentAttachmentDto source)
    {
        return new QuoteAgentAttachmentDto
        {
            AttachmentId = source.AttachmentId,
            FileName = source.FileName,
            ContentType = source.ContentType,
            FileSizeBytes = source.FileSizeBytes,
            Url = source.Url,
            Kind = source.Kind,
            UploadId = source.UploadId,
            StoragePath = source.StoragePath,
            SatisfiesGeometryGate = source.SatisfiesGeometryGate
        };
    }

    private static QuoteAgentArtifactDto CloneArtifact(QuoteAgentArtifactDto source)
    {
        return new QuoteAgentArtifactDto
        {
            ArtifactId = source.ArtifactId,
            ArtifactType = source.ArtifactType,
            Title = source.Title,
            Status = source.Status,
            PartId = source.PartId,
            Url = source.Url,
            Metadata = new(source.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static QuoteAgentProposedActionDto CloneAction(QuoteAgentProposedActionDto source)
    {
        return new QuoteAgentProposedActionDto
        {
            ActionId = source.ActionId,
            ActionType = source.ActionType,
            Title = source.Title,
            Summary = source.Summary,
            RequiresAuthentication = source.RequiresAuthentication,
            RequiresConfirmation = source.RequiresConfirmation,
            Status = source.Status
        };
    }

    private static QuoteAgentUiDirectiveDto CloneUiDirective(QuoteAgentUiDirectiveDto source)
    {
        return new QuoteAgentUiDirectiveDto
        {
            DirectiveId = source.DirectiveId,
            Panel = source.Panel,
            TargetType = source.TargetType,
            TargetId = source.TargetId,
            HighlightKey = source.HighlightKey,
            Label = source.Label,
            OpenPanel = source.OpenPanel,
            CanvasX = source.CanvasX,
            CanvasY = source.CanvasY,
            CanvasZ = source.CanvasZ
        };
    }

    private static QuotePartDraftDto ClonePart(QuotePartDraftDto source)
    {
        return new QuotePartDraftDto
        {
            PartId = source.PartId,
            FileId = source.FileId,
            UploadId = source.UploadId,
            FileName = source.FileName,
            ProcessId = source.ProcessId,
            MaterialId = source.MaterialId,
            FinishId = source.FinishId,
            FinishCode = source.FinishCode,
            ToleranceId = source.ToleranceId,
            ToleranceCode = source.ToleranceCode,
            InspectionLevel = source.InspectionLevel,
            RoughnessCode = source.RoughnessCode,
            Color = source.Color,
            ProcessOptionValues = new(source.ProcessOptionValues, StringComparer.OrdinalIgnoreCase),
            HasThreadedHoles = source.HasThreadedHoles,
            ThreadSpecification = source.ThreadSpecification,
            ThreadedHoleCount = source.ThreadedHoleCount,
            InsertType = source.InsertType,
            InsertCount = source.InsertCount,
            Quantity = source.Quantity,
            VolumeCc = source.VolumeCc,
            SurfaceAreaCm2 = source.SurfaceAreaCm2,
            BoundingBoxMm = source.BoundingBoxMm,
            StoragePath = source.StoragePath,
            Status = source.Status,
            ViewerGlbUrl = source.ViewerGlbUrl,
            ViewerStoragePath = source.ViewerStoragePath,
            ViewerFileExtension = source.ViewerFileExtension,
            ThumbnailUrl = source.ThumbnailUrl,
            Findings = source.Findings.Select(finding => finding with { }).ToArray(),
            IsManifold = source.IsManifold,
            NonManifoldReason = source.NonManifoldReason,
            FdmReport = source.FdmReport,
            SlaReport = source.SlaReport,
            CncReport = source.CncReport,
            OverlayGlbUrls = source.OverlayGlbUrls.ToArray(),
            DfmAcknowledged = source.DfmAcknowledged,
            PartNotes = source.PartNotes,
            BodyCount = source.BodyCount,
            SelectedBodyIndex = source.SelectedBodyIndex,
            DrawingFiles = source.DrawingFiles.Select(file => file with { }).ToList(),
            ViewerSettings = source.ViewerSettings with { }
        };
    }
}

internal sealed class QuoteAgentSessionState
{
    public object SyncRoot { get; } = new();

    public Guid SessionId { get; init; }

    public Guid? ChatbotSessionId { get; set; }

    public Guid? CustomerId { get; set; }

    public string Language { get; set; } = "en";

    public string? UiCulture { get; set; }

    public string Units { get; set; } = "mm";

    public string Currency { get; set; } = "THB";

    public string InteractionMode { get; set; } = "chat";

    public bool AllowArtifactPanel { get; set; } = true;

    public bool Multilingual { get; set; } = true;

    public string LeadTimeCode { get; set; } = "STANDARD";

    public bool ConfigurationConfirmed { get; set; }

    public List<QuoteAgentAttachmentDto> Attachments { get; } = [];

    public List<QuotePartDraftDto> Parts { get; } = [];

    public List<QuoteAgentArtifactDto> Artifacts { get; } = [];

    public List<QuoteAgentThinkingStepDto> LastAssistantThinkingSteps { get; } = [];

    public List<QuoteAgentProposedActionDto> ProposedActions { get; } = [];

    public List<QuoteAgentUiDirectiveDto> UiDirectives { get; } = [];

    public List<QuoteCadDesignSession> CadDesigns { get; } = [];

    public ShippingAddressDto? LastShippingDestination { get; set; }

    public List<ShippingRateOptionDto> ShippingRateOptions { get; } = [];

    public ShippingRateOptionDto? SelectedShippingRate { get; set; }

    public QuoteEstimateResponse? Estimate { get; set; }

    public GenerateFormalQuoteResponse? FormalQuote { get; set; }

    public bool QuoteApproved { get; set; }

    public CreateManufacturingOrderResponse? Order { get; set; }

    public string? ProjectName { get; set; }

    public QuoteAgentCustomerQuestionDto? PendingCustomerQuestion { get; set; }

    public Guid? CheckoutBillingAddressId { get; set; }

    public Guid? CheckoutShippingAddressId { get; set; }

    public string? CheckoutPhone { get; set; }

    public string? CheckoutCompany { get; set; }

    public string? CheckoutVatNumber { get; set; }

    public bool CheckoutAcceptedTerms { get; set; }

    public bool CheckoutConsent { get; set; }

    public InitiatePaymentResponse? Payment { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class QuoteCadDesignSession
{
    public Guid DesignId { get; init; }

    public string Description { get; set; } = string.Empty;

    public string ProcessHint { get; set; } = "fdm";

    public string Units { get; set; } = "mm";

    public string Stage { get; set; } = "requirements";

    public string Status { get; set; } = "planning";

    public int Revision { get; set; }

    public int ApplyIterations { get; set; }

    public List<CadCommandDto> Operations { get; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed record QuoteAgentPendingAction(
    Guid ActionId,
    Guid SessionId,
    string ActionType,
    string Title,
    string Summary,
    bool RequiresAuthentication,
    bool RequiresConfirmation,
    Guid? CustomerId,
    Dictionary<string, JsonElement> Arguments,
    DateTimeOffset CreatedAt)
{
    public QuoteAgentProposedActionDto ToDto()
    {
        return new QuoteAgentProposedActionDto
        {
            ActionId = ActionId,
            ActionType = ActionType,
            Title = Title,
            Summary = Summary,
            RequiresAuthentication = RequiresAuthentication,
            RequiresConfirmation = RequiresConfirmation,
            Status = "pending_confirmation"
        };
    }
}

internal sealed record QuoteAgentCompletedAction(
    QuoteAgentActionResultResponse Result,
    Guid? CustomerId);
