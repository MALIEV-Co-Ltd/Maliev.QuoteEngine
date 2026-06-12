using MassTransit;
using Maliev.MessagingContracts.Contracts.Payments;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.SignalR;

namespace Maliev.QuoteEngine.Bff.Consumers;

/// <summary>
/// Consumes <see cref="PaymentFailedEvent"/> and pushes a provider-neutral payment
/// failure notification to customers watching the affected order.
/// </summary>
public sealed class QuotePaymentFailedConsumer(
    IHubContext<QuoteNotificationsHub> hub,
    ILogger<QuotePaymentFailedConsumer> logger) : IConsumer<PaymentFailedEvent>
{
    public async Task Consume(ConsumeContext<PaymentFailedEvent> context)
    {
        var payload = context.Message.Payload;
        if (payload is null)
        {
            logger.LogWarning("PaymentFailedEvent received with null payload; skipping");
            return;
        }

        var signalRPayload = new QePaymentFailedPayload(
            OrderNumber: payload.OrderId,
            PaymentId: payload.TransactionId,
            Amount: (decimal)payload.Amount,
            Currency: payload.Currency,
            ErrorMessage: payload.ErrorMessage,
            ProviderErrorCode: payload.ProviderErrorCode,
            FailedAt: payload.FailedAt);

        await hub.Clients
            .Group(QuoteNotificationsHub.OrderGroup(payload.OrderId))
            .SendAsync("PaymentFailed", signalRPayload, context.CancellationToken);

        logger.LogWarning(
            "Pushed PaymentFailed to SignalR group for order {OrderNumber}, payment {PaymentId}: {ErrorCode}",
            payload.OrderId,
            payload.TransactionId,
            payload.ProviderErrorCode);
    }
}
