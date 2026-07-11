using System.Net;
using System.Net.Http.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

public sealed class PdfServiceClientContractTests
{
    [Fact]
    public async Task GetLatestAsync_QueriesVerifiedBusinessReferenceAndMapsStoragePath()
    {
        var invoiceId = Guid.NewGuid();
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                requestId = Guid.NewGuid(),
                storageUrl = "https://storage.example.test/invoice.pdf",
                storagePath = "pdfs/invoice/invoice.pdf"
            })
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://pdf.test") };
        var client = new PdfServiceClient(http, NullLogger<PdfServiceClient>.Instance);

        var result = await client.GetLatestAsync("Invoice", invoiceId);

        Assert.NotNull(result);
        Assert.Equal("pdfs/invoice/invoice.pdf", result.StoragePath);
        Assert.Equal(
            $"/pdf/v1/generations/latest?documentType=Invoice&referenceId={invoiceId:D}",
            handler.PathAndQuery);
    }

    [Theory]
    [InlineData("Quotation")]
    [InlineData("Unknown")]
    public async Task GetLatestAsync_RejectsUnsupportedDocumentType(string documentType)
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://pdf.test") };
        var client = new PdfServiceClient(http, NullLogger<PdfServiceClient>.Instance);

        var result = await client.GetLatestAsync(documentType, Guid.NewGuid());

        Assert.Null(result);
        Assert.Empty(handler.PathAndQuery);
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string PathAndQuery { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            PathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            return Task.FromResult(response);
        }
    }
}
