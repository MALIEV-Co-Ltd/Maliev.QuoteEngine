using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Maliev.MessagingContracts.Contracts.Payments;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Consumers;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.Core;
using Xunit;

namespace Maliev.QuoteEngine.Tests;

/// <summary>
/// Unit tests for the QuoteEngine BFF payment-state SignalR relay consumers.
/// <para>
/// These pin the realtime payment-notification contract used by OrderDetail.razor: the client joins
/// exactly one group per order via <c>JoinOrderGroup(orderNumber)</c> and registers handlers for all
/// five payment states. Every consumer must therefore push to the SAME per-order group
/// (<c>quote-order:{orderNumber}</c>) with the correct method name and payload mapping. The completed
/// event carries a human-readable <c>OrderNumber</c>; the non-happy-path events carry the order number
/// in their string <c>OrderId</c> field. A divergence here silently stops customers from ever seeing
/// cancelled / failed / expired / pending updates in real time.
/// </para>
/// </summary>
public sealed class PaymentNotificationConsumerTests
{
    private const string OrderNumber = "MO-20260619-0001";
    private static readonly DateTimeOffset At = new(2026, 6, 19, 8, 30, 0, TimeSpan.Zero);

    private static (IHubContext<QuoteNotificationsHub> Hub, IHubClients Clients, IClientProxy Proxy) CreateHub(
        Action<string>? captureGroup = null)
    {
        var proxy = Substitute.For<IClientProxy>();
        var clients = Substitute.For<IHubClients>();
        if (captureGroup is null)
        {
            clients.Group(Arg.Any<string>()).Returns(proxy);
        }
        else
        {
            clients.Group(Arg.Do<string>(captureGroup)).Returns(proxy);
        }

        var hub = Substitute.For<IHubContext<QuoteNotificationsHub>>();
        hub.Clients.Returns(clients);
        return (hub, clients, proxy);
    }

    private static IOrderServiceClient CreateOrderClient(bool addStatusResult = true)
    {
        var orderClient = Substitute.For<IOrderServiceClient>();
        orderClient.AddStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(addStatusResult);
        return orderClient;
    }

    private static ConsumeContext<T> Context<T>(T message)
        where T : class
    {
        var ctx = Substitute.For<ConsumeContext<T>>();
        ctx.Message.Returns(message);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    /// <summary>Extracts the single payload argument sent via the one SendCoreAsync relay call.</summary>
    private static T SentPayload<T>(IClientProxy proxy, string expectedMethod)
    {
        var call = proxy.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync));
        var callArgs = call.GetArguments();
        Assert.Equal(expectedMethod, (string)callArgs[0]!);
        var sendArgs = (object?[])callArgs[1]!;
        Assert.Single(sendArgs);
        return Assert.IsType<T>(sendArgs[0]);
    }

    [Fact]
    public async Task Completed_pushes_PaymentCompleted_to_order_group()
    {
        var (hub, clients, proxy) = CreateHub();
        var orderClient = CreateOrderClient();
        var consumer = new QuotePaymentCompletedConsumer(orderClient, hub, Substitute.For<ILogger<QuotePaymentCompletedConsumer>>());
        var paymentId = Guid.NewGuid();
        var evt = new PaymentCompletedEvent() with
        {
            Payload = new PaymentCompletedEventPayload
            {
                OrderId = Guid.NewGuid(),
                OrderNumber = OrderNumber,
                CustomerId = "cust-1",
                PaymentId = paymentId,
                Amount = 12500d,
                Currency = "THB",
                ProviderName = "omise",
            },
        };

        await consumer.Consume(Context(evt));

        await orderClient.Received(1).AddStatusAsync(OrderNumber, "Paid", Arg.Any<CancellationToken>());
        clients.Received(1).Group($"quote-order:{OrderNumber}");
        var p = SentPayload<QePaymentCompletedPayload>(proxy, "PaymentCompleted");
        Assert.Equal(OrderNumber, p.OrderNumber);
        Assert.Equal(paymentId, p.PaymentId);
        Assert.Equal(12500m, p.Amount);
        Assert.Equal("THB", p.Currency);
    }

    [Fact]
    public async Task Cancelled_pushes_PaymentCancelled_to_order_group()
    {
        var (hub, clients, proxy) = CreateHub();
        var consumer = new QuotePaymentCancelledConsumer(hub, Substitute.For<ILogger<QuotePaymentCancelledConsumer>>());
        var txId = Guid.NewGuid();
        var evt = new PaymentCancelledEvent() with
        {
            Payload = new PaymentCancelledEventPayload(
                TransactionId: txId,
                IdempotencyKey: "idem-1",
                Amount: 12500d,
                Currency: "THB",
                CustomerId: "cust-1",
                OrderId: OrderNumber,
                ProviderName: "provider",
                Reason: "customer_cancelled",
                ProviderEventCode: "charge.cancelled",
                CancelledAt: At),
        };

        await consumer.Consume(Context(evt));

        clients.Received(1).Group($"quote-order:{OrderNumber}");
        var p = SentPayload<QePaymentCancelledPayload>(proxy, "PaymentCancelled");
        Assert.Equal(OrderNumber, p.OrderNumber);
        Assert.Equal(txId, p.PaymentId);
        Assert.Equal(12500m, p.Amount);
        Assert.Equal("THB", p.Currency);
        Assert.Equal("customer_cancelled", p.Reason);
        Assert.Equal("charge.cancelled", p.ProviderEventCode);
        Assert.Equal(At, p.CancelledAt);
    }

    [Fact]
    public async Task Failed_pushes_PaymentFailed_to_order_group()
    {
        var (hub, clients, proxy) = CreateHub();
        var consumer = new QuotePaymentFailedConsumer(hub, Substitute.For<ILogger<QuotePaymentFailedConsumer>>());
        var txId = Guid.NewGuid();
        var evt = new PaymentFailedEvent() with
        {
            Payload = new PaymentFailedEventPayload(
                TransactionId: txId,
                IdempotencyKey: "idem-1",
                Amount: 12500d,
                Currency: "THB",
                CustomerId: "cust-1",
                OrderId: OrderNumber,
                ProviderName: "provider",
                ErrorMessage: "card_declined",
                ProviderErrorCode: "card.declined",
                FailedAt: At),
        };

        await consumer.Consume(Context(evt));

        clients.Received(1).Group($"quote-order:{OrderNumber}");
        var p = SentPayload<QePaymentFailedPayload>(proxy, "PaymentFailed");
        Assert.Equal(OrderNumber, p.OrderNumber);
        Assert.Equal(txId, p.PaymentId);
        Assert.Equal("card_declined", p.ErrorMessage);
        Assert.Equal("card.declined", p.ProviderErrorCode);
        Assert.Equal(At, p.FailedAt);
    }

    [Fact]
    public async Task Expired_pushes_PaymentExpired_to_order_group()
    {
        var (hub, clients, proxy) = CreateHub();
        var consumer = new QuotePaymentExpiredConsumer(hub, Substitute.For<ILogger<QuotePaymentExpiredConsumer>>());
        var txId = Guid.NewGuid();
        var evt = new PaymentExpiredEvent() with
        {
            Payload = new PaymentExpiredEventPayload(
                TransactionId: txId,
                IdempotencyKey: "idem-1",
                Amount: 12500d,
                Currency: "THB",
                CustomerId: "cust-1",
                OrderId: OrderNumber,
                ProviderName: "provider",
                Reason: "session_expired",
                ProviderEventCode: "charge.expired",
                ExpiredAt: At),
        };

        await consumer.Consume(Context(evt));

        clients.Received(1).Group($"quote-order:{OrderNumber}");
        var p = SentPayload<QePaymentExpiredPayload>(proxy, "PaymentExpired");
        Assert.Equal(OrderNumber, p.OrderNumber);
        Assert.Equal(txId, p.PaymentId);
        Assert.Equal("session_expired", p.Reason);
        Assert.Equal("charge.expired", p.ProviderEventCode);
        Assert.Equal(At, p.ExpiredAt);
    }

    [Fact]
    public async Task Pending_pushes_PaymentPending_to_order_group()
    {
        var (hub, clients, proxy) = CreateHub();
        var consumer = new QuotePaymentPendingConsumer(hub, Substitute.For<ILogger<QuotePaymentPendingConsumer>>());
        var txId = Guid.NewGuid();
        var evt = new PaymentPendingEvent() with
        {
            Payload = new PaymentPendingEventPayload(
                TransactionId: txId,
                IdempotencyKey: "idem-1",
                Amount: 12500d,
                Currency: "THB",
                CustomerId: "cust-1",
                OrderId: OrderNumber,
                ProviderName: "provider",
                ProviderEventCode: "charge.pending",
                PendingAt: At),
        };

        await consumer.Consume(Context(evt));

        clients.Received(1).Group($"quote-order:{OrderNumber}");
        var p = SentPayload<QePaymentPendingPayload>(proxy, "PaymentPending");
        Assert.Equal(OrderNumber, p.OrderNumber);
        Assert.Equal(txId, p.PaymentId);
        Assert.Equal("charge.pending", p.ProviderEventCode);
        Assert.Equal(At, p.PendingAt);
    }

    [Fact]
    public async Task Completed_with_null_payload_is_skipped()
    {
        var (hub, clients, proxy) = CreateHub();
        var orderClient = CreateOrderClient();
        var consumer = new QuotePaymentCompletedConsumer(orderClient, hub, Substitute.For<ILogger<QuotePaymentCompletedConsumer>>());

        await consumer.Consume(Context(new PaymentCompletedEvent()));

        await orderClient.DidNotReceive().AddStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        clients.DidNotReceive().Group(Arg.Any<string>());
        await proxy.DidNotReceive().SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Completed_still_pushes_PaymentCompleted_when_order_status_update_fails()
    {
        var (hub, clients, proxy) = CreateHub();
        var orderClient = CreateOrderClient(addStatusResult: false);
        var consumer = new QuotePaymentCompletedConsumer(orderClient, hub, Substitute.For<ILogger<QuotePaymentCompletedConsumer>>());
        var paymentId = Guid.NewGuid();
        var evt = new PaymentCompletedEvent() with
        {
            Payload = new PaymentCompletedEventPayload
            {
                OrderId = Guid.NewGuid(),
                OrderNumber = OrderNumber,
                CustomerId = "cust-1",
                PaymentId = paymentId,
                Amount = 12500d,
                Currency = "THB",
                ProviderName = "omise",
            },
        };

        await consumer.Consume(Context(evt));

        await orderClient.Received(1).AddStatusAsync(OrderNumber, "Paid", Arg.Any<CancellationToken>());
        clients.Received(1).Group($"quote-order:{OrderNumber}");
        var p = SentPayload<QePaymentCompletedPayload>(proxy, "PaymentCompleted");
        Assert.Equal(OrderNumber, p.OrderNumber);
        Assert.Equal(paymentId, p.PaymentId);
    }

    [Fact]
    public async Task Cancelled_with_null_payload_is_skipped()
    {
        var (hub, clients, proxy) = CreateHub();
        var consumer = new QuotePaymentCancelledConsumer(hub, Substitute.For<ILogger<QuotePaymentCancelledConsumer>>());

        await consumer.Consume(Context(new PaymentCancelledEvent()));

        clients.DidNotReceive().Group(Arg.Any<string>());
        await proxy.DidNotReceive().SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_with_null_payload_is_skipped()
    {
        var (hub, clients, proxy) = CreateHub();
        var consumer = new QuotePaymentFailedConsumer(hub, Substitute.For<ILogger<QuotePaymentFailedConsumer>>());

        await consumer.Consume(Context(new PaymentFailedEvent()));

        clients.DidNotReceive().Group(Arg.Any<string>());
        await proxy.DidNotReceive().SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Expired_with_null_payload_is_skipped()
    {
        var (hub, clients, proxy) = CreateHub();
        var consumer = new QuotePaymentExpiredConsumer(hub, Substitute.For<ILogger<QuotePaymentExpiredConsumer>>());

        await consumer.Consume(Context(new PaymentExpiredEvent()));

        clients.DidNotReceive().Group(Arg.Any<string>());
        await proxy.DidNotReceive().SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pending_with_null_payload_is_skipped()
    {
        var (hub, clients, proxy) = CreateHub();
        var consumer = new QuotePaymentPendingConsumer(hub, Substitute.For<ILogger<QuotePaymentPendingConsumer>>());

        await consumer.Consume(Context(new PaymentPendingEvent()));

        clients.DidNotReceive().Group(Arg.Any<string>());
        await proxy.DidNotReceive().SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task All_payment_states_target_the_same_order_group_the_client_joins()
    {
        // OrderDetail.razor joins one group per order via JoinOrderGroup(orderNumber). Every payment
        // state must resolve to that same group or the customer misses updates. Completed carries
        // OrderNumber; the non-happy paths carry the order number in their string OrderId field.
        var groups = new List<string>();

        async Task Run(Func<IHubContext<QuoteNotificationsHub>, Task> consume)
        {
            string? captured = null;
            var (hub, _, _) = CreateHub(g => captured = g);
            await consume(hub);
            Assert.NotNull(captured);
            groups.Add(captured!);
        }

        await Run(h => new QuotePaymentCompletedConsumer(CreateOrderClient(), h, Substitute.For<ILogger<QuotePaymentCompletedConsumer>>())
            .Consume(Context(new PaymentCompletedEvent() with
            {
                Payload = new PaymentCompletedEventPayload
                {
                    OrderId = Guid.NewGuid(),
                    OrderNumber = OrderNumber,
                    CustomerId = "c",
                    PaymentId = Guid.NewGuid(),
                    Amount = 1d,
                    Currency = "THB",
                    ProviderName = "omise",
                },
            })));
        await Run(h => new QuotePaymentCancelledConsumer(h, Substitute.For<ILogger<QuotePaymentCancelledConsumer>>())
            .Consume(Context(new PaymentCancelledEvent() with
            {
                Payload = new PaymentCancelledEventPayload(Guid.NewGuid(), "i", 1d, "THB", "c", OrderNumber, "p", "r", "e", At),
            })));
        await Run(h => new QuotePaymentFailedConsumer(h, Substitute.For<ILogger<QuotePaymentFailedConsumer>>())
            .Consume(Context(new PaymentFailedEvent() with
            {
                Payload = new PaymentFailedEventPayload(Guid.NewGuid(), "i", 1d, "THB", "c", OrderNumber, "p", "m", "e", At),
            })));
        await Run(h => new QuotePaymentExpiredConsumer(h, Substitute.For<ILogger<QuotePaymentExpiredConsumer>>())
            .Consume(Context(new PaymentExpiredEvent() with
            {
                Payload = new PaymentExpiredEventPayload(Guid.NewGuid(), "i", 1d, "THB", "c", OrderNumber, "p", "r", "e", At),
            })));
        await Run(h => new QuotePaymentPendingConsumer(h, Substitute.For<ILogger<QuotePaymentPendingConsumer>>())
            .Consume(Context(new PaymentPendingEvent() with
            {
                Payload = new PaymentPendingEventPayload(Guid.NewGuid(), "i", 1d, "THB", "c", OrderNumber, "p", "e", At),
            })));

        Assert.Equal(5, groups.Count);
        Assert.Single(groups.Distinct());
        Assert.Equal($"quote-order:{OrderNumber}", groups[0]);
    }
}
