using System.Net;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

public sealed class ReceiptServiceClientContractTests
{
    [Fact]
    public async Task GetByInvoiceAsync_QueriesReceiptServiceAndMapsCustomerSafeFields()
    {
        var invoiceId = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        using var handler = new RecordingHandler(new
        {
            data = new[]
            {
                new
                {
                    id = receiptId,
                    receiptNumber = "RCPT-20260710-000008",
                    invoiceId,
                    issueDate = "2026-07-10T03:00:00Z",
                    totalAmount = 535m,
                    currency = "THB",
                    status = "Active",
                    pdfReferenceId = Guid.NewGuid()
                }
            },
            pagination = new { page = 1, pageSize = 100, totalCount = 1, totalPages = 1 }
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://receipt.test") };
        var client = new ReceiptServiceClient(http, NullLogger<ReceiptServiceClient>.Instance);

        var result = await client.GetByInvoiceAsync(invoiceId);

        var receipt = Assert.Single(result);
        Assert.Equal(receiptId, receipt.ReceiptId);
        Assert.Equal("RCPT-20260710-000008", receipt.ReceiptNumber);
        Assert.Equal(invoiceId, receipt.InvoiceId);
        Assert.Equal("Active", receipt.Status);
        Assert.Equal(535m, receipt.TotalAmount);
        Assert.Equal("THB", receipt.Currency);
        Assert.NotNull(receipt.PdfReferenceId);
        Assert.Equal($"/receipt/v1/receipts?invoiceId={invoiceId:D}&page=1&pageSize=100", handler.PathAndQuery);
    }

    [Fact]
    public async Task LookupByInvoiceAsync_DistinguishesNoReceiptsFromServiceFailure()
    {
        var invoiceId = Guid.NewGuid();
        using var emptyHandler = new RecordingHandler(new { data = Array.Empty<object>() });
        using var emptyHttp = new HttpClient(emptyHandler) { BaseAddress = new Uri("https://receipt.test") };
        var emptyClient = new ReceiptServiceClient(emptyHttp, NullLogger<ReceiptServiceClient>.Instance);
        using var failedHandler = new RecordingHandler(new { error = "unavailable" }, HttpStatusCode.ServiceUnavailable);
        using var failedHttp = new HttpClient(failedHandler) { BaseAddress = new Uri("https://receipt.test") };
        var failedClient = new ReceiptServiceClient(failedHttp, NullLogger<ReceiptServiceClient>.Instance);

        var empty = await emptyClient.LookupByInvoiceAsync(invoiceId);
        var failed = await failedClient.LookupByInvoiceAsync(invoiceId);

        Assert.True(empty.IsAvailable);
        Assert.Empty(empty.Receipts);
        Assert.False(failed.IsAvailable);
        Assert.Empty(failed.Receipts);
    }

    private sealed class RecordingHandler(object response, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
}
