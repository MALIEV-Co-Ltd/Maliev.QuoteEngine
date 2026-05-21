// Maliev.QuoteEngine.Bff/Consumers/QuoteOrderStatusChangedConsumer.cs
using MassTransit;
using Maliev.MessagingContracts.Contracts.Orders;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.SignalR;

namespace Maliev.QuoteEngine.Bff.Consumers;

/// <summary>
/// Consumes <see cref="OrderStatusChangedEvent"/> from the order pipeline and
/// pushes a SignalR "OrderStatusChanged" notification to any client subscribed
/// to the order group, allowing the customer portal to reflect status updates
/// in real time without polling.
/// </summary>
public sealed class QuoteOrderStatusChangedConsumer(
    IHubContext<QuoteNotificationsHub> hub,
    ILogger<QuoteOrderStatusChangedConsumer> logger) : IConsumer<OrderStatusChangedEvent>
{
    public async Task Consume(ConsumeContext<OrderStatusChangedEvent> context)
    {
        var payload = context.Message.Payload;
        if (payload is null)
        {
            logger.LogWarning("OrderStatusChangedEvent received with null payload — skipping");
            return;
        }

        var signalRPayload = new QeOrderStatusChangedPayload(
            OrderNumber: payload.OrderNumber,
            PreviousStatus: payload.PreviousStatus,
            NewStatus: payload.NewStatus,
            ChangedAt: payload.ChangedAt,
            Reason: payload.Reason);

        await hub.Clients
            .Group(QuoteNotificationsHub.OrderGroup(payload.OrderNumber))
            .SendAsync("OrderStatusChanged", signalRPayload, context.CancellationToken);

        logger.LogInformation(
            "Pushed OrderStatusChanged to SignalR group for order {OrderNumber}: {Previous} → {New}",
            payload.OrderNumber, payload.PreviousStatus, payload.NewStatus);
    }
}
