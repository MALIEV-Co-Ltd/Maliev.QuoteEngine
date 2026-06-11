using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

public sealed class PaymentServiceClientContractTests
{
    [Fact]
    public async Task InitiateAsync_SendsPaymentServiceWireContractAndMapsResponse()
    {
        var transactionId = Guid.Parse("22b97be1-7c38-4da9-a963-02a6aa2ac346");
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent.Create(new
            {
                transactionId,
                amount = 1500.00m,
                currency = "THB",
                status = 1,
                customerId = "customer-1",
                orderId = "order-1",
                description = "Manufacturing order ORD-2026-0001",
                selectedProvider = "Stripe",
                providerTransactionId = "pi_test_123",
                paymentUrl = "https://checkout.stripe.com/c/pay/test"
            })
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://payment-service.test")
        };
        var client = new PaymentServiceClient(http, NullLogger<PaymentServiceClient>.Instance);

        var result = await client.InitiateAsync(
            "customer-1",
            "order-1",
            "ORD-2026-0001",
            1500.00m,
            "THB",
            "https://quote.example.com/payment/success?orderId=ORD-2026-0001",
            "https://quote.example.com/payment/cancel?orderId=ORD-2026-0001",
            "customer-1:order-1");

        Assert.NotNull(result);
        Assert.Equal(transactionId, result.TransactionId);
        Assert.Equal("https://checkout.stripe.com/c/pay/test", result.PaymentUrl);
        Assert.Equal("1", result.Status);

        Assert.Equal(HttpMethod.Post, handler.Request?.Method);
        Assert.Equal("/payment/v1/payments", handler.Request?.RequestUri?.AbsolutePath);
        IEnumerable<string>? idempotencyValues = null;
        var hasIdempotencyHeader = handler.Request?.Headers.TryGetValues(
            "Idempotency-Key",
            out idempotencyValues) == true;
        Assert.True(hasIdempotencyHeader);
        Assert.NotNull(idempotencyValues);
        Assert.Equal("customer-1:order-1", Assert.Single(idempotencyValues));

        var body = await handler.ReadJsonBodyAsync();
        Assert.Equal(1500.00m, body.GetProperty("amount").GetDecimal());
        Assert.Equal("THB", body.GetProperty("currency").GetString());
        Assert.Equal("customer-1", body.GetProperty("customerId").GetString());
        Assert.Equal("order-1", body.GetProperty("orderId").GetString());
        Assert.Equal("Manufacturing order ORD-2026-0001", body.GetProperty("description").GetString());
        Assert.Equal("https://quote.example.com/payment/success?orderId=ORD-2026-0001", body.GetProperty("returnUrl").GetString());
        Assert.Equal("https://quote.example.com/payment/cancel?orderId=ORD-2026-0001", body.GetProperty("cancelUrl").GetString());
        Assert.Equal("ORD-2026-0001", body.GetProperty("metadata").GetProperty("orderNumber").GetString());
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        private string? _body;

        public HttpRequestMessage? Request { get; private set; }

        public async Task<JsonElement> ReadJsonBodyAsync()
        {
            Assert.False(string.IsNullOrWhiteSpace(_body));
            return JsonDocument.Parse(_body).RootElement.Clone();
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            _body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
