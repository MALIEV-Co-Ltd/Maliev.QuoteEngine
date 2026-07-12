using Microsoft.AspNetCore.SignalR;
using Maliev.QuoteEngine.Bff.Services;

namespace Maliev.QuoteEngine.Bff.Hubs;

public sealed class QuoteNotificationsHub : Hub
{
    private const string SubscriptionGroupsKey = "quote-notifications:groups";
    private const string VisitorExpiryCancellationKey = "quote-notifications:visitor-expiry";

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        if (httpContext is null || !SubscriptionAuthorizer.CanConnect(httpContext))
        {
            Logger.LogWarning("Rejected unauthenticated QuoteEngine notification hub connection.");
            Context.Abort();
            return;
        }

        var expiry = SubscriptionAuthorizer.GetAnonymousConnectionExpiry(httpContext);
        if (expiry.HasValue)
        {
            ScheduleVisitorExpiry(expiry.Value);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        CancelVisitorExpiry();
        if (Context.Items.TryGetValue(SubscriptionGroupsKey, out var value) &&
            value is HashSet<string> groups)
        {
            foreach (var group in groups)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, CancellationToken.None);
            }

            groups.Clear();
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinFileGroup(string storagePath)
    {
        var group = await SubscriptionAuthorizer.AuthorizeFileAsync(
            RequireHttpContext(), storagePath, Context.ConnectionAborted);
        await JoinAuthorizedGroupAsync(group);
    }

    public async Task LeaveFileGroup(string storagePath)
    {
        var group = await SubscriptionAuthorizer.AuthorizeFileAsync(
            RequireHttpContext(), storagePath, Context.ConnectionAborted);
        await LeaveAuthorizedGroupAsync(group);
    }

    public async Task JoinOrderGroup(string orderNumber)
    {
        var group = await SubscriptionAuthorizer.AuthorizeOrderAsync(
            RequireHttpContext(), orderNumber, Context.ConnectionAborted);
        await JoinAuthorizedGroupAsync(group);
    }

    public async Task LeaveOrderGroup(string orderNumber)
    {
        var group = await SubscriptionAuthorizer.AuthorizeOrderAsync(
            RequireHttpContext(), orderNumber, Context.ConnectionAborted);
        await LeaveAuthorizedGroupAsync(group);
    }

    public async Task JoinQuoteSessionGroup(Guid sessionId)
    {
        var group = await SubscriptionAuthorizer.AuthorizeSessionAsync(
            RequireHttpContext(), sessionId, Context.ConnectionAborted);
        await JoinAuthorizedGroupAsync(group);
    }

    public async Task LeaveQuoteSessionGroup(Guid sessionId)
    {
        var group = await SubscriptionAuthorizer.AuthorizeSessionAsync(
            RequireHttpContext(), sessionId, Context.ConnectionAborted);
        await LeaveAuthorizedGroupAsync(group);
    }

    private HttpContext RequireHttpContext() => Context.GetHttpContext()
        ?? throw new HubException("Subscription is unavailable.");

    private QuoteNotificationSubscriptionAuthorizer SubscriptionAuthorizer => RequireHttpContext()
        .RequestServices.GetRequiredService<QuoteNotificationSubscriptionAuthorizer>();

    private ILogger<QuoteNotificationsHub> Logger => RequireHttpContext()
        .RequestServices.GetRequiredService<ILogger<QuoteNotificationsHub>>();

    private async Task JoinAuthorizedGroupAsync(string? group)
    {
        if (group is null)
        {
            throw new HubException("Subscription is unavailable.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        GetSubscriptionGroups().Add(group);
    }

    private async Task LeaveAuthorizedGroupAsync(string? group)
    {
        if (group is null)
        {
            throw new HubException("Subscription is unavailable.");
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        GetSubscriptionGroups().Remove(group);
    }

    private HashSet<string> GetSubscriptionGroups()
    {
        if (Context.Items.TryGetValue(SubscriptionGroupsKey, out var existing) &&
            existing is HashSet<string> groups)
        {
            return groups;
        }

        groups = new HashSet<string>(StringComparer.Ordinal);
        Context.Items[SubscriptionGroupsKey] = groups;
        return groups;
    }

    private void ScheduleVisitorExpiry(DateTimeOffset expiresAt)
    {
        var delay = expiresAt - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            Context.Abort();
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Context.ConnectionAborted);
        Context.Items[VisitorExpiryCancellationKey] = cancellation;
        _ = AbortWhenVisitorCredentialExpiresAsync(Context, delay, cancellation.Token);
    }

    private void CancelVisitorExpiry()
    {
        if (Context.Items.Remove(VisitorExpiryCancellationKey, out var value) &&
            value is CancellationTokenSource cancellation)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private static async Task AbortWhenVisitorCredentialExpiresAsync(
        HubCallerContext context,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            context.Abort();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A normal disconnect cancels the scheduled expiry callback.
        }
    }

    internal static string FileGroup(string storagePath)
    {
        return $"quote-file:{storagePath}";
    }

    internal static string OrderGroup(string orderNumber)
    {
        return $"quote-order:{orderNumber}";
    }

    internal static string QuoteSessionGroup(Guid sessionId)
    {
        return $"quote-agent:{sessionId:D}";
    }
}
