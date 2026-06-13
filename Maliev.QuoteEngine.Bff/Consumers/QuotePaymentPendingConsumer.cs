using MassTransit;
using Maliev.MessagingContracts.Contracts.Payments;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.SignalR;

namespace Maliev.QuoteEngine.Bff.Consumers;

/// <summary>
/// Consumes <see cref="PaymentPendingEvent"/> and pushes a provider-neutral
/// pending notification to customers watching the affected order.
/// </summary>
public sealed class QuotePaymentPendingConsumer(
    IHubContext<QuoteNotificationsHub> hub,
    ILogger<QuotePaymentPendingConsumer> logger) : IConsumer<PaymentPendingEvent>
{
    public async Task Consume(ConsumeContext<PaymentPendingEvent> context)
    {
        var payload = context.Message.Payload;
        if (payload is null)
        {
            logger.LogWarning("PaymentPendingEvent received with null payload; skipping");
            return;
        }

        var signalRPayload = new QePaymentPendingPayload(
            OrderNumber: payload.OrderId,
            PaymentId: payload.TransactionId,
            Amount: (decimal)payload.Amount,
            Currency: payload.Currency,
            ProviderEventCode: payload.ProviderEventCode,
            PendingAt: payload.PendingAt);

        await hub.Clients
            .Group(QuoteNotificationsHub.OrderGroup(payload.OrderId))
            .SendAsync("PaymentPending", signalRPayload, context.CancellationToken);

        logger.LogInformation(
            "Pushed PaymentPending to SignalR group for order {OrderNumber}, payment {PaymentId}: {EventCode}",
            payload.OrderId,
            payload.TransactionId,
            payload.ProviderEventCode);
    }
}
