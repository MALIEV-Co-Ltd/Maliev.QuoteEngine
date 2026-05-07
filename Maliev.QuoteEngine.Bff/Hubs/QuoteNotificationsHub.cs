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

    internal static string FileGroup(string storagePath)
    {
        return $"quote-file:{storagePath}";
    }
}
