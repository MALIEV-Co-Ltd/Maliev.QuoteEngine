using MassTransit;
using Maliev.MessagingContracts.Contracts.Payments;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.SignalR;

namespace Maliev.QuoteEngine.Bff.Consumers;

/// <summary>
/// Consumes <see cref="PaymentCancelledEvent"/> and pushes a provider-neutral
/// cancellation notification to customers watching the affected order.
/// </summary>
public sealed class QuotePaymentCancelledConsumer(
    IHubContext<QuoteNotificationsHub> hub,
    ILogger<QuotePaymentCancelledConsumer> logger) : IConsumer<PaymentCancelledEvent>
{
    public async Task Consume(ConsumeContext<PaymentCancelledEvent> context)
    {
        var payload = context.Message.Payload;
        if (payload is null)
        {
            logger.LogWarning("PaymentCancelledEvent received with null payload; skipping");
            return;
        }

        var signalRPayload = new QePaymentCancelledPayload(
            OrderNumber: payload.OrderId,
            PaymentId: payload.TransactionId,
            Amount: (decimal)payload.Amount,
            Currency: payload.Currency,
            Reason: payload.Reason,
            ProviderEventCode: payload.ProviderEventCode,
            CancelledAt: payload.CancelledAt);

        await hub.Clients
            .Group(QuoteNotificationsHub.OrderGroup(payload.OrderId))
            .SendAsync("PaymentCancelled", signalRPayload, context.CancellationToken);

        logger.LogInformation(
            "Pushed PaymentCancelled to SignalR group for order {OrderNumber}, payment {PaymentId}: {EventCode}",
            payload.OrderId,
            payload.TransactionId,
            payload.ProviderEventCode);
    }
}
