using MassTransit;
using Maliev.MessagingContracts.Contracts.Payments;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.SignalR;

namespace Maliev.QuoteEngine.Bff.Consumers;

/// <summary>
/// Consumes <see cref="PaymentExpiredEvent"/> and pushes a provider-neutral
/// expiry notification to customers watching the affected order.
/// </summary>
public sealed class QuotePaymentExpiredConsumer(
    IHubContext<QuoteNotificationsHub> hub,
    ILogger<QuotePaymentExpiredConsumer> logger) : IConsumer<PaymentExpiredEvent>
{
    public async Task Consume(ConsumeContext<PaymentExpiredEvent> context)
    {
        var payload = context.Message.Payload;
        if (payload is null)
        {
            logger.LogWarning("PaymentExpiredEvent received with null payload; skipping");
            return;
        }

        var signalRPayload = new QePaymentExpiredPayload(
            OrderNumber: payload.OrderId,
            PaymentId: payload.TransactionId,
            Amount: (decimal)payload.Amount,
            Currency: payload.Currency,
            Reason: payload.Reason,
            ProviderEventCode: payload.ProviderEventCode,
            ExpiredAt: payload.ExpiredAt);

        await hub.Clients
            .Group(QuoteNotificationsHub.OrderGroup(payload.OrderId))
            .SendAsync("PaymentExpired", signalRPayload, context.CancellationToken);

        logger.LogInformation(
            "Pushed PaymentExpired to SignalR group for order {OrderNumber}, payment {PaymentId}: {EventCode}",
            payload.OrderId,
            payload.TransactionId,
            payload.ProviderEventCode);
    }
}
