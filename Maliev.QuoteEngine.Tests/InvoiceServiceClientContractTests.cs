using System.Net;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

public sealed class InvoiceServiceClientContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CreateAndFinalizeForOrderAsync_PostsInvoiceWithOrderPoNumberAndFinalizes()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://invoice.test")
        };
        var client = new InvoiceServiceClient(http, NullLogger<InvoiceServiceClient>.Instance);
        var customerId = Guid.NewGuid();

        var result = await client.CreateAndFinalizeForOrderAsync(new InvoiceCreateForOrderRequest
        {
            CustomerId = customerId,
            CustomerName = "MALIEV Buyer Co.",
            CustomerTaxId = "TH1234567890",
            BillingAddress = "Billing address aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            ShippingAddress = "Shipping address bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            OrderNumber = "ORD-1001",
            QuoteNumber = "MQ-1001",
            Currency = "THB",
            IssueDate = new DateTime(2026, 6, 19, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 6, 26, 0, 0, 0, DateTimeKind.Utc),
            PaymentTermsDays = 7,
            Lines =
            [
                new InvoiceCreateLineRequest
                {
                    LineNumber = 1,
                    Description = "Bracket; process FDM; material pla",
                    Quantity = 2m,
                    UnitPrice = 1500m
                }
            ]
        });

        Assert.NotNull(result);
        Assert.Equal(handler.InvoiceId, result.InvoiceId);
        Assert.Equal("INV-20260619-000001", result.InvoiceNumber);
        Assert.Equal("Finalized", result.Status);
        Assert.Equal(3210m, result.GrandTotal);
        Assert.Equal("THB", result.Currency);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/invoice/v1/invoices", handler.Requests[0].PathAndQuery);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal($"/invoice/v1/invoices/{handler.InvoiceId:D}/finalize", handler.Requests[1].PathAndQuery);

        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var root = body.RootElement;
        Assert.Equal(customerId, root.GetProperty("customerId").GetGuid());
        Assert.Equal(1, root.GetProperty("billingIdentityType").GetInt32());
        Assert.Equal("ORD-1001", root.GetProperty("poNumber").GetString());
        Assert.Equal("MQ-1001", root.GetProperty("quotationReference").GetString());
        Assert.Equal("MALIEV Buyer Co.", root.GetProperty("customerName").GetString());
        Assert.Equal("TH1234567890", root.GetProperty("customerTaxId").GetString());
        Assert.Equal("THB", root.GetProperty("currency").GetString());
        Assert.Equal(7, root.GetProperty("paymentTermsDays").GetInt32());
        var line = root.GetProperty("lines")[0];
        Assert.Equal(1, line.GetProperty("lineNumber").GetInt32());
        Assert.Equal(2m, line.GetProperty("quantity").GetDecimal());
        Assert.Equal(1500m, line.GetProperty("unitPrice").GetDecimal());
        Assert.Equal("VAT", line.GetProperty("taxCategory").GetString());
        Assert.Equal(7m, line.GetProperty("taxRate").GetDecimal());
    }

    [Fact]
    public async Task GetForOrderAsync_QueriesTheCustomerAndExactOrderNumber()
    {
        var invoiceId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        using var handler = new QueryHandler(new
        {
            items = new[]
            {
                new
                {
                    id = invoiceId,
                    invoiceNumber = "INV-20260710-000021",
                    customerId,
                    poNumber = "ORD-2100",
                    status = "FullyPaid",
                    grandTotal = 535m,
                    currency = "THB",
                    issueDate = "2026-07-10T02:00:00Z",
                    pdfFileReference = "pdf/invoices/21.pdf"
                }
            },
            page = 1,
            pageSize = 10,
            totalCount = 1
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://invoice.test") };
        var client = new InvoiceServiceClient(http, NullLogger<InvoiceServiceClient>.Instance);

        var result = await client.GetForOrderAsync(customerId, "ORD-2100");

        Assert.NotNull(result);
        Assert.Equal(invoiceId, result.InvoiceId);
        Assert.Equal("INV-20260710-000021", result.InvoiceNumber);
        Assert.Equal("FullyPaid", result.Status);
        Assert.Equal(535m, result.GrandTotal);
        Assert.Equal("THB", result.Currency);
        Assert.Equal("pdf/invoices/21.pdf", result.PdfFileReference);
        Assert.Equal(
            $"/invoice/v1/invoices?CustomerId={customerId:D}&PoNumber=ORD-2100&Page=1&PageSize=10",
            handler.PathAndQuery);
    }

    [Fact]
    public async Task LookupForOrderAsync_DistinguishesNoInvoiceFromServiceFailure()
    {
        var customerId = Guid.NewGuid();
        using var emptyHandler = new QueryHandler(new { items = Array.Empty<object>() });
        using var emptyHttp = new HttpClient(emptyHandler) { BaseAddress = new Uri("https://invoice.test") };
        var emptyClient = new InvoiceServiceClient(emptyHttp, NullLogger<InvoiceServiceClient>.Instance);
        using var failedHandler = new QueryHandler(new { error = "unavailable" }, HttpStatusCode.ServiceUnavailable);
        using var failedHttp = new HttpClient(failedHandler) { BaseAddress = new Uri("https://invoice.test") };
        var failedClient = new InvoiceServiceClient(failedHttp, NullLogger<InvoiceServiceClient>.Instance);

        var empty = await emptyClient.LookupForOrderAsync(customerId, "ORD-EMPTY");
        var failed = await failedClient.LookupForOrderAsync(customerId, "ORD-FAILED");

        Assert.True(empty.IsAvailable);
        Assert.Null(empty.Invoice);
        Assert.False(failed.IsAvailable);
        Assert.Null(failed.Invoice);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Guid InvoiceId { get; } = Guid.NewGuid();

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri?.PathAndQuery ?? string.Empty, body));

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/invoice/v1/invoices")
            {
                return Json(new
                {
                    id = InvoiceId,
                    invoiceNumber = string.Empty,
                    status = "Draft",
                    grandTotal = 3210m,
                    currency = "THB"
                }, HttpStatusCode.Created);
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == $"/invoice/v1/invoices/{InvoiceId:D}/finalize")
            {
                return Json(new
                {
                    id = InvoiceId,
                    invoiceNumber = "INV-20260619-000001",
                    status = "Finalized",
                    grandTotal = 3210m,
                    currency = "THB"
                }, HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object value, HttpStatusCode statusCode)
        {
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions), System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class QueryHandler(object response, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string PathAndQuery { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            PathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(response, JsonOptions),
                    System.Text.Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string PathAndQuery, string Body);
}
