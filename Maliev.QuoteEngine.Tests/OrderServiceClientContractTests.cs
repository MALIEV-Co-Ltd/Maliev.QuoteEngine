using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

public sealed class OrderServiceClientContractTests
{
    [Fact]
    public async Task CreateAsync_SendsOrderServiceWireContractWithQuoteTotal()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent.Create(new
            {
                orderId = "ORD-2026-00042",
                currentStatus = "Pending"
            })
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://order-service.test")
        };
        var client = new OrderServiceClient(http, NullLogger<OrderServiceClient>.Instance);

        var quoteId = Guid.NewGuid();
        var quoteVersionId = Guid.NewGuid();
        var result = await client.CreateAsync(new OrderCreateRequest
        {
            CustomerId = "customer-1",
            ServiceCategoryId = 1,
            ProcessTypeId = 1,
            OrderedQuantity = 2,
            CustomerPoNumber = "PO-QUOTE-TOTAL",
            Requirements = "Configured self-service quote.",
            QuotedAmount = 2140.00m,
            QuoteCurrency = "THB",
            QuoteId = quoteId,
            QuoteNumber = "QT-2026-00042",
            QuoteVersionId = quoteVersionId,
            QuoteVersionNumber = 2
        });

        Assert.NotNull(result);
        Assert.Equal("ORD-2026-00042", result.OrderNumber);

        Assert.Equal(HttpMethod.Post, handler.Request?.Method);
        Assert.Equal("/order/v1/orders", handler.Request?.RequestUri?.AbsolutePath);

        JsonElement body = await handler.ReadJsonBodyAsync();
        Assert.Equal("customer-1", body.GetProperty("customerId").GetString());
        Assert.Equal(2140.00m, body.GetProperty("quotedAmount").GetDecimal());
        Assert.Equal("THB", body.GetProperty("quoteCurrency").GetString());
        Assert.Equal(quoteId, body.GetProperty("quoteId").GetGuid());
        Assert.Equal("QT-2026-00042", body.GetProperty("quoteNumber").GetString());
        Assert.Equal(quoteVersionId, body.GetProperty("quoteVersionId").GetGuid());
        Assert.Equal(2, body.GetProperty("quoteVersionNumber").GetInt32());
        Assert.Equal("PO-QUOTE-TOTAL", body.GetProperty("customerPoNumber").GetString());
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
