using System.Security.Claims;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Security;

namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>
/// Resolves the browser principal for QuoteEngine live updates and verifies resource ownership
/// before SignalR groups are joined. Resource locators supplied by the browser are never group
/// names or credentials.
/// </summary>
internal sealed class QuoteNotificationSubscriptionAuthorizer(
    AnonymousVisitorCookie anonymousVisitorCookie,
    IQuoteAgentSessionOwnerStore sessionOwnerStore,
    QuoteEnginePrototypeStore prototypeStore,
    IOrderServiceClient orderServiceClient,
    IHostEnvironment environment)
{
    /// <summary>Returns whether this connection presents a valid customer or visitor principal.</summary>
    internal bool CanConnect(HttpContext context) => ResolveCaller(context).IsAuthenticated;

    /// <summary>
    /// Gets the maximum anonymous connection lifetime. Authenticated customers rely on their
    /// authenticated session lifetime; visitor-only connections must end with their signed capability.
    /// </summary>
    internal DateTimeOffset? GetAnonymousConnectionExpiry(HttpContext context)
    {
        var caller = ResolveCaller(context);
        return caller.CustomerId.HasValue
            ? null
            : anonymousVisitorCookie.ReadVisitorCredential(context.Request)?.ExpiresAt;
    }

    /// <summary>Authorizes a session and returns its canonical server-derived group name.</summary>
    internal async Task<string?> AuthorizeSessionAsync(
        HttpContext context,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
        {
            return null;
        }

        var caller = ResolveCaller(context);
        var owner = await sessionOwnerStore.GetAsync(sessionId, cancellationToken);
        if (!caller.Matches(owner))
        {
            return null;
        }

        return Hubs.QuoteNotificationsHub.QuoteSessionGroup(sessionId);
    }

    /// <summary>Authorizes an upload and returns its canonical server-derived group name.</summary>
    internal async Task<string?> AuthorizeFileAsync(
        HttpContext context,
        string storagePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var upload = prototypeStore.FindUploadByStoragePath(storagePath);
        var caller = ResolveCaller(context);
        if (upload is null)
        {
            return null;
        }

        var ownsUpload = caller.Matches(upload.CustomerId, upload.VisitorId);
        if (Guid.TryParse(upload.QuoteSessionId, out var sessionId))
        {
            // The upload index is a locator only. Session-bound uploads inherit their durable
            // customer-or-visitor ownership from Redis so a stale local record cannot grant access.
            ownsUpload = caller.Matches(await sessionOwnerStore.GetAsync(sessionId, cancellationToken));
        }

        return ownsUpload
            ? Hubs.QuoteNotificationsHub.FileGroup(upload.StoragePath)
            : null;
    }

    /// <summary>Authorizes an authenticated customer's order and returns its canonical group name.</summary>
    internal async Task<string?> AuthorizeOrderAsync(
        HttpContext context,
        string orderNumber,
        CancellationToken cancellationToken)
    {
        var caller = ResolveCaller(context);
        if (!caller.CustomerId.HasValue || string.IsNullOrWhiteSpace(orderNumber))
        {
            return null;
        }

        var normalizedOrderNumber = orderNumber.Trim();
        var order = await orderServiceClient.GetDetailForCustomerAsync(
            caller.CustomerId.Value.ToString("D"),
            normalizedOrderNumber,
            cancellationToken);
        var owned = order is not null;

        // The prototype order store exists only for local development and integration testing. A
        // production hub fails closed when the owning OrderService cannot confirm ownership.
        if (!owned && (environment.IsDevelopment() || environment.IsEnvironment("Testing")))
        {
            owned = prototypeStore.GetOrderDetail(caller.CustomerId.Value, normalizedOrderNumber) is not null;
        }

        return owned
            ? Hubs.QuoteNotificationsHub.OrderGroup(normalizedOrderNumber)
            : null;
    }

    private QuoteNotificationCaller ResolveCaller(HttpContext context)
    {
        var rawCustomerId = context.User.FindFirstValue("customer_id")
            ?? context.User.FindFirstValue("customerId")
            ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var customerId = context.User.Identity?.IsAuthenticated == true &&
                         Guid.TryParse(rawCustomerId, out var parsedCustomerId) &&
                         parsedCustomerId != Guid.Empty
            ? parsedCustomerId
            : (Guid?)null;

        // Deliberately read-only: SignalR negotiation and reconnects must not bootstrap an
        // anonymous browser identity. A valid credential must have been issued by an HTTP agent flow.
        return new QuoteNotificationCaller(customerId, anonymousVisitorCookie.ReadVisitorId(context.Request));
    }
}

internal sealed record QuoteNotificationCaller(Guid? CustomerId, Guid? VisitorId)
{
    public bool IsAuthenticated => CustomerId.HasValue || VisitorId.HasValue;

    public bool Matches(QuoteAgentSessionOwner? owner) =>
        owner is not null && Matches(owner.CustomerId, owner.VisitorId);

    public bool Matches(Guid? resourceCustomerId, Guid? resourceVisitorId) =>
        resourceCustomerId.HasValue
            ? CustomerId == resourceCustomerId
            : VisitorId.HasValue && VisitorId == resourceVisitorId;
}
