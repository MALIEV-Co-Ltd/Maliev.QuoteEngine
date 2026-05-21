using Microsoft.AspNetCore.SignalR;

namespace Maliev.QuoteEngine.Bff.Hubs;

public sealed class QuoteNotificationsHub : Hub
{
    public Task JoinFileGroup(string storagePath)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, FileGroup(storagePath));
    }

    public Task LeaveFileGroup(string storagePath)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, FileGroup(storagePath));
    }

    public Task JoinOrderGroup(string orderNumber)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, OrderGroup(orderNumber));
    }

    public Task LeaveOrderGroup(string orderNumber)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, OrderGroup(orderNumber));
    }

    internal static string FileGroup(string storagePath)
    {
        return $"quote-file:{storagePath}";
    }

    internal static string OrderGroup(string orderNumber)
    {
        return $"quote-order:{orderNumber}";
    }
}
