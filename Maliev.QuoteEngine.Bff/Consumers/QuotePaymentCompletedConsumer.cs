// Maliev.QuoteEngine.Bff/Consumers/QuotePaymentCompletedConsumer.cs
using MassTransit;
using Maliev.MessagingContracts.Contracts.Payments;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.SignalR;

namespace Maliev.QuoteEngine.Bff.Consumers;

/// <summary>
/// Consumes <see cref="PaymentCompletedEvent"/> from the payment pipeline and
/// pushes a SignalR "PaymentCompleted" notification to any client subscribed to
/// the order group, allowing the UI to update status without polling.
/// </summary>
public sealed class QuotePaymentCompletedConsumer(
    IOrderServiceClient orderClient,
    IHubContext<QuoteNotificationsHub> hub,
    ILogger<QuotePaymentCompletedConsumer> logger) : IConsumer<PaymentCompletedEvent>
{
    public async Task Consume(ConsumeContext<PaymentCompletedEvent> context)
    {
        var payload = context.Message.Payload;
        if (payload is null)
        {
            logger.LogWarning("PaymentCompletedEvent received with null payload — skipping");
            return;
        }

        var signalRPayload = new QePaymentCompletedPayload(
            OrderNumber: payload.OrderNumber,
            PaymentId: payload.PaymentId,
            Amount: (decimal)payload.Amount,
            Currency: payload.Currency);

        var orderStatusUpdated = await orderClient.AddStatusAsync(
            payload.OrderNumber,
            "Paid",
            context.CancellationToken);
        if (!orderStatusUpdated)
        {
            logger.LogWarning(
                "Could not advance order {OrderNumber} to Paid after payment {PaymentId}",
                payload.OrderNumber,
                payload.PaymentId);
        }

        await hub.Clients
            .Group(QuoteNotificationsHub.OrderGroup(payload.OrderNumber))
            .SendAsync("PaymentCompleted", signalRPayload, context.CancellationToken);

        logger.LogInformation(
            "Pushed PaymentCompleted to SignalR group for order {OrderNumber}, payment {PaymentId}",
            payload.OrderNumber, payload.PaymentId);
    }
}
