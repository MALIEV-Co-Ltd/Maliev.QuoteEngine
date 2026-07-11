using System.Net;
using System.Text;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Shared.Account;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

public sealed class CustomerServiceClientContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task LookupCustomerDocumentsAsync_FiltersMixedOwnershipAndMapsOrderNumber()
    {
        var customerId = Guid.NewGuid();
        var ownedDocumentId = Guid.NewGuid();
        using var handler = new StaticResponseHandler(JsonResponse(new[]
        {
            Document(ownedDocumentId, "Customer", customerId, "order.pdf", "ORD-2026-00422"),
            Document(Guid.NewGuid(), "Customer", Guid.NewGuid(), "foreign.pdf", "ORD-FOREIGN"),
            Document(Guid.NewGuid(), "Company", customerId, "company.pdf", "ORD-COMPANY")
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://customer.test") };
        var client = new CustomerServiceClient(http, NullLogger<CustomerServiceClient>.Instance);

        var result = await client.LookupCustomerDocumentsAsync(customerId, CancellationToken.None);

        Assert.True(result.IsAvailable);
        var document = Assert.Single(result.Value);
        Assert.Equal(ownedDocumentId, document.DocumentId);
        Assert.Equal("order.pdf", document.FileName);
        Assert.Equal("ORD-2026-00422", document.OrderNumber);
        Assert.Equal(
            $"/customer/v1/documents?ownerType=Customer&ownerId={customerId:D}",
            handler.PathAndQuery);
    }

    [Fact]
    public async Task LookupCustomerDocumentsAsync_DistinguishesSuccessfulEmptyFromUnavailableResponses()
    {
        var customerId = Guid.NewGuid();
        using var emptyHandler = new StaticResponseHandler(JsonResponse(Array.Empty<object>()));
        using var emptyHttp = new HttpClient(emptyHandler) { BaseAddress = new Uri("https://customer.test") };
        var emptyClient = new CustomerServiceClient(emptyHttp, NullLogger<CustomerServiceClient>.Instance);
        using var outageHandler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var outageHttp = new HttpClient(outageHandler) { BaseAddress = new Uri("https://customer.test") };
        var outageClient = new CustomerServiceClient(outageHttp, NullLogger<CustomerServiceClient>.Instance);
        using var malformedHandler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json")
        });
        using var malformedHttp = new HttpClient(malformedHandler) { BaseAddress = new Uri("https://customer.test") };
        var malformedClient = new CustomerServiceClient(malformedHttp, NullLogger<CustomerServiceClient>.Instance);

        var empty = await emptyClient.LookupCustomerDocumentsAsync(customerId, CancellationToken.None);
        var outage = await outageClient.LookupCustomerDocumentsAsync(customerId, CancellationToken.None);
        var malformed = await malformedClient.LookupCustomerDocumentsAsync(customerId, CancellationToken.None);

        Assert.True(empty.IsAvailable);
        Assert.Empty(empty.Value);
        Assert.False(outage.IsAvailable);
        Assert.Empty(outage.Value);
        Assert.False(malformed.IsAvailable);
        Assert.Empty(malformed.Value);
        Assert.Empty((await emptyClient.GetCustomerDocumentsAsync(customerId, CancellationToken.None))!);
        Assert.Null(await outageClient.GetCustomerDocumentsAsync(customerId, CancellationToken.None));
    }

    [Fact]
    public async Task CreateCustomerDocumentAsync_PostsAndMapsOrderNumber()
    {
        var customerId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        using var handler = new CreateDocumentHandler(documentId, customerId);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://customer.test") };
        var client = new CustomerServiceClient(http, NullLogger<CustomerServiceClient>.Instance);

        var result = await client.CreateCustomerDocumentAsync(customerId, new CustomerDocumentUploadRequest
        {
            FileName = "receipt.pdf",
            Kind = "Receipt",
            StoragePath = "customer-documents/receipt.pdf",
            ContentType = "application/pdf",
            FileSizeBytes = 2048,
            OrderNumber = "ORD-2026-00422"
        }, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(documentId, result.DocumentId);
        Assert.Equal("ORD-2026-00422", result.OrderNumber);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/customer/v1/documents", handler.PathAndQuery);
        using var body = JsonDocument.Parse(handler.Body);
        Assert.Equal("Customer", body.RootElement.GetProperty("ownerType").GetString());
        Assert.Equal(customerId, body.RootElement.GetProperty("ownerId").GetGuid());
        Assert.Equal("ORD-2026-00422", body.RootElement.GetProperty("orderNumber").GetString());
    }

    private static object Document(
        Guid id,
        string ownerType,
        Guid ownerId,
        string filename,
        string orderNumber) => new
        {
            id,
            ownerType,
            ownerId,
            orderNumber,
            documentType = "Receipt",
            fileReference = $"customer-documents/{filename}",
            filename,
            fileSize = 2048,
            mimeType = "application/pdf",
            status = "Complete",
            createdAt = "2026-07-10T02:00:00Z",
            updatedAt = "2026-07-10T02:00:00Z"
        };

    private static HttpResponseMessage JsonResponse(object value, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json")
        };

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string PathAndQuery { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            PathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            return Task.FromResult(Clone(response));
        }

        private static HttpResponseMessage Clone(HttpResponseMessage source) => new(source.StatusCode)
        {
            Content = source.Content is null
                ? null
                : new StringContent(source.Content.ReadAsStringAsync().GetAwaiter().GetResult(), Encoding.UTF8, "application/json")
        };
    }

    private sealed class CreateDocumentHandler(Guid documentId, Guid customerId) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string PathAndQuery { get; private set; } = string.Empty;
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            PathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return JsonResponse(Document(documentId, "Customer", customerId, "receipt.pdf", "ORD-2026-00422"), HttpStatusCode.Created);
        }
    }
}
