using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

public sealed class PaymentServiceClientContractTests
{
    private static readonly Guid BillingAddressId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ShippingAddressId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

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
                status = "pending",
                customerId = "customer-1",
                orderId = "order-1",
                description = "Manufacturing order ORD-2026-0001",
                selectedProvider = "omise",
                providerTransactionId = "chrg_test_123",
                paymentUrl = "https://pay.omise.co/payments/paym_test_123"
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
            "https://quote.example.com/payment/success?orderNumber=ORD-2026-0001",
            "https://quote.example.com/payment/cancel?orderNumber=ORD-2026-0001",
            "customer-1:order-1",
            BillingAddressId,
            ShippingAddressId,
            "MALIEV Test Buyer Co., Ltd.",
            "TH-0123456789012",
            "Receiving",
            "+66810000002",
            "receiving@example.com",
            acceptedTerms: true);

        Assert.NotNull(result);
        Assert.Equal(transactionId, result.TransactionId);
        Assert.Equal("https://pay.omise.co/payments/paym_test_123", result.PaymentUrl);
        Assert.Equal("pending", result.Status);

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
        Assert.Equal("https://quote.example.com/payment/success?orderNumber=ORD-2026-0001", body.GetProperty("returnUrl").GetString());
        Assert.Equal("https://quote.example.com/payment/cancel?orderNumber=ORD-2026-0001", body.GetProperty("cancelUrl").GetString());
        Assert.False(body.TryGetProperty("preferredProvider", out _));
        Assert.False(body.TryGetProperty("selectedProvider", out _));
        Assert.Equal("ORD-2026-0001", body.GetProperty("metadata").GetProperty("orderNumber").GetString());
        Assert.Equal(BillingAddressId.ToString("D"), body.GetProperty("metadata").GetProperty("billingAddressId").GetString());
        Assert.Equal(ShippingAddressId.ToString("D"), body.GetProperty("metadata").GetProperty("shippingAddressId").GetString());
        Assert.Equal("MALIEV Test Buyer Co., Ltd.", body.GetProperty("metadata").GetProperty("billingCompanyName").GetString());
        Assert.Equal("TH-0123456789012", body.GetProperty("metadata").GetProperty("billingVatNumber").GetString());
        Assert.Equal("Receiving", body.GetProperty("metadata").GetProperty("deliveryContactName").GetString());
        Assert.Equal("+66810000002", body.GetProperty("metadata").GetProperty("deliveryContactPhone").GetString());
        Assert.Equal("receiving@example.com", body.GetProperty("metadata").GetProperty("deliveryContactEmail").GetString());
        Assert.Equal("true", body.GetProperty("metadata").GetProperty("acceptedTerms").GetString());
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
