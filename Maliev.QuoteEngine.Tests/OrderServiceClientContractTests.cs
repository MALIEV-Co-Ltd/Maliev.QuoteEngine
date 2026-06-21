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

    [Fact]
    public async Task GetDetailAsync_InProgressOrderMarksManufacturingCurrent()
    {
        using var handler = new RouteHandler(
            ("GET", "/order/v1/orders/ORD-2026-00043", JsonContent.Create(new
            {
                orderId = "ORD-2026-00043",
                currentStatus = "InProgress",
                paymentStatus = "Paid",
                quotedAmount = 2140.00m,
                quoteCurrency = "THB",
                customerPoNumber = "PO-43",
                requirements = "Active production order.",
                createdAt = DateTime.UtcNow.AddDays(-2),
                updatedAt = DateTime.UtcNow,
                promisedDeliveryDate = (DateTime?)null,
                actualDeliveryDate = (DateTime?)null
            })),
            ("GET", "/order/v1/orders/ORD-2026-00043/statuses", JsonContent.Create(new[]
            {
                new
                {
                    status = "Paid",
                    customerNotes = "Payment confirmed.",
                    timestamp = DateTime.UtcNow.AddDays(-1)
                },
                new
                {
                    status = "InProgress",
                    customerNotes = "Production has started.",
                    timestamp = DateTime.UtcNow
                }
            })));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://order-service.test")
        };
        var client = new OrderServiceClient(http, NullLogger<OrderServiceClient>.Instance);

        var result = await client.GetDetailAsync("ORD-2026-00043");

        Assert.NotNull(result);
        Assert.Contains(result.ManufacturingMilestones, milestone =>
            milestone.Key == "manufacturing" &&
            milestone.State == "current");
        Assert.Contains(result.ManufacturingMilestones, milestone =>
            milestone.Key == "quality-inspection" &&
            milestone.State == "pending");
    }

    [Fact]
    public async Task GetDetailAsync_FinishedOrderMarksManufacturingCompleteAndQualityCurrent()
    {
        using var handler = new RouteHandler(
            ("GET", "/order/v1/orders/ORD-2026-00044", JsonContent.Create(new
            {
                orderId = "ORD-2026-00044",
                currentStatus = "Finished",
                paymentStatus = "Paid",
                quotedAmount = 2140.00m,
                quoteCurrency = "THB",
                customerPoNumber = "PO-44",
                requirements = "Finished production order.",
                createdAt = DateTime.UtcNow.AddDays(-3),
                updatedAt = DateTime.UtcNow,
                promisedDeliveryDate = (DateTime?)null,
                actualDeliveryDate = (DateTime?)null
            })),
            ("GET", "/order/v1/orders/ORD-2026-00044/statuses", JsonContent.Create(new[]
            {
                new
                {
                    status = "InProgress",
                    customerNotes = "Production has started.",
                    timestamp = DateTime.UtcNow.AddDays(-1)
                },
                new
                {
                    status = "Finished",
                    customerNotes = "Production completed; QC release is required before shipping.",
                    timestamp = DateTime.UtcNow
                }
            })));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://order-service.test")
        };
        var client = new OrderServiceClient(http, NullLogger<OrderServiceClient>.Instance);

        var result = await client.GetDetailAsync("ORD-2026-00044");

        Assert.NotNull(result);
        Assert.Contains(result.ManufacturingMilestones, milestone =>
            milestone.Key == "manufacturing" &&
            milestone.State == "complete");
        Assert.Contains(result.ManufacturingMilestones, milestone =>
            milestone.Key == "quality-inspection" &&
            milestone.State == "current");
        Assert.Contains(result.ManufacturingMilestones, milestone =>
            milestone.Key == "delivery" &&
            milestone.State == "pending");
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

    private sealed class RouteHandler(params (string Method, string Path, HttpContent Content)[] routes) : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<(string Method, string Path), HttpContent> _routes = routes.ToDictionary(
            route => (route.Method, route.Path),
            route => route.Content);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var key = (request.Method.Method, request.RequestUri?.AbsolutePath ?? string.Empty);
            if (_routes.TryGetValue(key, out var content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
