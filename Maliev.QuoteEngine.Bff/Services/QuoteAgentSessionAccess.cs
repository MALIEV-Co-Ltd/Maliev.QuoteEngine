using Maliev.QuoteEngine.Bff.Security;

namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>Validates a BFF-signed agent context against the durable session owner.</summary>
public interface IQuoteAgentServerContextAuthorizer
{
    /// <summary>Returns true only when the signed context matches the current durable owner.</summary>
    Task<bool> IsAuthorizedAsync(QuoteAgentContext context, CancellationToken cancellationToken);
}

/// <summary>Result of binding a browser request to a durable QuoteEngine agent-session owner.</summary>
public enum QuoteAgentSessionAccessDecision
{
    /// <summary>The current signed visitor or customer owns the session.</summary>
    Authorized,

    /// <summary>The supplied visitor credential is invalid or missing for this operation.</summary>
    Unauthorized,

    /// <summary>The session does not exist or belongs to another principal.</summary>
    NotFound
}

/// <summary>Authorizes browser requests against durable QuoteEngine agent-session ownership.</summary>
public interface IQuoteAgentSessionRequestAuthorizer
{
    /// <summary>Authorizes an existing session or atomically creates one for an allowed write.</summary>
    Task<QuoteAgentSessionAccessDecision> AuthorizeAsync(
        Guid sessionId,
        bool allowCreate,
        CancellationToken cancellationToken);
}

internal sealed record QuoteAgentCallerIdentity(
    Guid? CustomerId,
    Guid? VisitorId,
    AnonymousVisitorCredentialStatus VisitorCredentialStatus)
{
    public bool HasUsableVisitor => VisitorId.HasValue && VisitorId != Guid.Empty;
}

/// <summary>
/// Authorizes public agent sessions against a durable customer-or-visitor owner. Session ids are
/// locators only and never grant access by themselves.
/// </summary>
internal sealed class QuoteAgentSessionAccess(
    IHttpContextAccessor httpContextAccessor,
    CustomerSessionResolver customerSessionResolver,
    AnonymousVisitorCookie anonymousVisitorCookie,
    IQuoteAgentSessionOwnerStore ownerStore,
    IQuoteAgentConversationMap conversationMap,
    QuoteAgentSessionStore sessionStore,
    QuoteEnginePrototypeStore uploadStore) : IQuoteAgentServerContextAuthorizer, IQuoteAgentSessionRequestAuthorizer
{
    async Task<bool> IQuoteAgentServerContextAuthorizer.IsAuthorizedAsync(
        QuoteAgentContext context,
        CancellationToken cancellationToken) =>
        await AuthorizeServerContextAsync(context, cancellationToken) ==
        QuoteAgentSessionAccessDecision.Authorized;

    public QuoteAgentCallerIdentity GetCurrentCaller()
    {
        var context = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("An HTTP request is required to authorize an agent session.");
        var visitor = anonymousVisitorCookie.ResolveForRequest(context);
        Guid? customerId = customerSessionResolver.TryResolveCustomerId(out var resolvedCustomerId)
            ? resolvedCustomerId
            : null;
        return new QuoteAgentCallerIdentity(customerId, visitor.VisitorId, visitor.Status);
    }

    public Task<QuoteAgentSessionAccessDecision> AuthorizeExistingAsync(
        Guid sessionId,
        CancellationToken cancellationToken) =>
        AuthorizeAsync(sessionId, allowCreate: false, cancellationToken);

    public async Task<QuoteAgentSessionAccessDecision> AuthorizeServerContextAsync(
        QuoteAgentContext context,
        CancellationToken cancellationToken)
    {
        var owner = await ownerStore.GetAsync(context.QuoteSessionId, cancellationToken);
        if (owner is null)
        {
            return QuoteAgentSessionAccessDecision.NotFound;
        }

        var matches = owner.CustomerId.HasValue
            ? context.CustomerId == owner.CustomerId
            : !context.CustomerId.HasValue;
        if (!matches)
        {
            return QuoteAgentSessionAccessDecision.Unauthorized;
        }

        var mapping = await conversationMap.GetMappingAsync(context.QuoteSessionId, cancellationToken);
        if (mapping is null ||
            mapping.ChatbotSessionId != context.ChatbotSessionId ||
            mapping.CustomerId != context.CustomerId)
        {
            return QuoteAgentSessionAccessDecision.Unauthorized;
        }

        BindInMemoryState(context.QuoteSessionId, owner, mapping.ChatbotSessionId);
        return QuoteAgentSessionAccessDecision.Authorized;
    }

    public async Task<QuoteAgentSessionAccessDecision> AuthorizeAsync(
        Guid sessionId,
        bool allowCreate,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
        {
            return QuoteAgentSessionAccessDecision.NotFound;
        }

        var caller = GetCurrentCaller();
        if (caller.VisitorCredentialStatus == AnonymousVisitorCredentialStatus.Invalid &&
            !caller.CustomerId.HasValue)
        {
            return QuoteAgentSessionAccessDecision.Unauthorized;
        }

        var owner = await ownerStore.GetAsync(sessionId, cancellationToken);
        if (owner is null)
        {
            if (allowCreate && caller.HasUsableVisitor)
            {
                var survivingMapping = await conversationMap.GetMappingAsync(sessionId, cancellationToken);
                if (sessionStore.TryGet(sessionId, out _) || survivingMapping is not null)
                {
                    return QuoteAgentSessionAccessDecision.NotFound;
                }

                var createdOwner = new QuoteAgentSessionOwner(caller.CustomerId, caller.VisitorId!.Value);
                _ = await ownerStore.TryCreateAsync(sessionId, createdOwner, cancellationToken);
                owner = await ownerStore.GetAsync(sessionId, cancellationToken);
            }
        }

        if (owner is null)
        {
            return QuoteAgentSessionAccessDecision.NotFound;
        }

        if (owner.CustomerId.HasValue)
        {
            if (caller.CustomerId != owner.CustomerId)
            {
                return QuoteAgentSessionAccessDecision.NotFound;
            }

            if (!await AlignConversationOwnerAsync(sessionId, owner.CustomerId, cancellationToken))
            {
                return QuoteAgentSessionAccessDecision.NotFound;
            }

            uploadStore.PromoteSessionUploads(sessionId, owner.VisitorId, owner.CustomerId.Value);
            BindInMemoryState(sessionId, owner);
            return QuoteAgentSessionAccessDecision.Authorized;
        }

        if (caller.VisitorCredentialStatus == AnonymousVisitorCredentialStatus.Invalid)
        {
            return QuoteAgentSessionAccessDecision.Unauthorized;
        }

        if (!caller.HasUsableVisitor || caller.VisitorId != owner.VisitorId)
        {
            return QuoteAgentSessionAccessDecision.NotFound;
        }

        if (caller.CustomerId.HasValue)
        {
            if (!await CanAlignConversationOwnerAsync(
                    sessionId,
                    caller.CustomerId.Value,
                    cancellationToken))
            {
                return QuoteAgentSessionAccessDecision.NotFound;
            }

            var promoted = await ownerStore.TryPromoteAsync(
                sessionId,
                owner.VisitorId,
                caller.CustomerId.Value,
                cancellationToken);
            if (!promoted)
            {
                return QuoteAgentSessionAccessDecision.NotFound;
            }

            owner = new QuoteAgentSessionOwner(caller.CustomerId, owner.VisitorId);
            if (!await AlignConversationOwnerAsync(sessionId, owner.CustomerId, cancellationToken))
            {
                return QuoteAgentSessionAccessDecision.NotFound;
            }

            uploadStore.PromoteSessionUploads(sessionId, owner.VisitorId, caller.CustomerId.Value);
        }

        BindInMemoryState(sessionId, owner);
        return QuoteAgentSessionAccessDecision.Authorized;
    }

    private async Task<bool> CanAlignConversationOwnerAsync(
        Guid sessionId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        var mapping = await conversationMap.GetMappingAsync(sessionId, cancellationToken);
        return mapping?.CustomerId is null || mapping.CustomerId == customerId;
    }

    private async Task<bool> AlignConversationOwnerAsync(
        Guid sessionId,
        Guid? customerId,
        CancellationToken cancellationToken)
    {
        var mapping = await conversationMap.GetMappingAsync(sessionId, cancellationToken);
        if (mapping is null)
        {
            return true;
        }

        if (mapping.CustomerId.HasValue && mapping.CustomerId != customerId)
        {
            return false;
        }

        if (customerId.HasValue && mapping.CustomerId != customerId)
        {
            await conversationMap.StoreMappingAsync(
                sessionId,
                mapping.ChatbotSessionId,
                customerId,
                cancellationToken);
        }

        return true;
    }

    private void BindInMemoryState(
        Guid sessionId,
        QuoteAgentSessionOwner owner,
        Guid? chatbotSessionId = null)
    {
        var state = sessionStore.GetOrCreate(sessionId);
        lock (state.SyncRoot)
        {
            state.CustomerId = owner.CustomerId;
            state.VisitorId = owner.VisitorId;
            if (chatbotSessionId.HasValue)
            {
                state.ChatbotSessionId = chatbotSessionId;
            }
        }
    }
}
