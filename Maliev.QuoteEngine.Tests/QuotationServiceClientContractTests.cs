using System.Net;
using System.Net.Http.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuotationServiceClientContractTests
{
    [Fact]
    public async Task LookupBySourceProjectAsync_SelectsLatestOwnedQuoteInsteadOfNewerForeignQuote()
    {
        var customerId = Guid.NewGuid();
        var foreignCustomerId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var ownedQuoteId = Guid.NewGuid();
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                data = new[]
                {
                    CreateQuotation(
                        Guid.NewGuid(),
                        foreignCustomerId,
                        projectId,
                        "QT-FOREIGN",
                        DateTime.UtcNow),
                    CreateQuotation(
                        ownedQuoteId,
                        customerId,
                        projectId,
                        "QT-OWNED",
                        DateTime.UtcNow.AddMinutes(-5))
                }
            })
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://quotation-service.test")
        };
        var client = new QuotationServiceClient(http, NullLogger<QuotationServiceClient>.Instance);

        var lookup = await client.LookupBySourceProjectAsync(customerId, projectId);

        Assert.True(lookup.IsAvailable);
        Assert.NotNull(lookup.Value);
        Assert.Equal(ownedQuoteId, lookup.Value.Id);
        Assert.Equal(customerId, lookup.Value.CustomerId);
        Assert.Equal("QT-OWNED", lookup.Value.QuotationNumber);
    }

    [Fact]
    public async Task LookupBySourceProjectAsync_EmptyPageIsAvailable()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                data = Array.Empty<object>()
            })
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://quotation-service.test")
        };
        var client = new QuotationServiceClient(http, NullLogger<QuotationServiceClient>.Instance);

        var lookup = await client.LookupBySourceProjectAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.True(lookup.IsAvailable);
        Assert.Null(lookup.Value);
    }

    [Fact]
    public async Task LookupBySourceProjectAsync_NonSuccessIsUnavailable()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://quotation-service.test")
        };
        var client = new QuotationServiceClient(http, NullLogger<QuotationServiceClient>.Instance);

        var lookup = await client.LookupBySourceProjectAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.False(lookup.IsAvailable);
        Assert.Null(lookup.Value);
    }

    [Fact]
    public async Task LookupBySourceProjectAsync_MalformedPayloadIsUnavailable()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ not-json")
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://quotation-service.test")
        };
        var client = new QuotationServiceClient(http, NullLogger<QuotationServiceClient>.Instance);

        var lookup = await client.LookupBySourceProjectAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.False(lookup.IsAvailable);
        Assert.Null(lookup.Value);
    }

    [Fact]
    public async Task GetByCustomerAsync_DropsRowsThatDoNotBelongToTheRequestedCustomer()
    {
        var customerId = Guid.NewGuid();
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                data = new[]
                {
                    CreateQuotation(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        "QT-FOREIGN",
                        DateTime.UtcNow),
                    CreateQuotation(
                        Guid.NewGuid(),
                        customerId,
                        Guid.NewGuid(),
                        "QT-OWNED",
                        DateTime.UtcNow)
                }
            })
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://quotation-service.test")
        };
        var client = new QuotationServiceClient(http, NullLogger<QuotationServiceClient>.Instance);

        var quotes = await client.GetByCustomerAsync(customerId);

        var quote = Assert.Single(quotes);
        Assert.Equal("QT-OWNED", quote.QuoteNumber);
    }

    private static object CreateQuotation(
        Guid id,
        Guid customerId,
        Guid projectId,
        string quotationNumber,
        DateTime updatedAt) =>
        new
        {
            id,
            customerId,
            sourceProjectId = projectId,
            sourceProjectNumber = "PRJ-001",
            currentVersionNumber = 1,
            quotationNumber,
            status = "Draft",
            validityPeriodEnd = DateTime.UtcNow.AddDays(30),
            total = 535m,
            currencyCode = "THB",
            createdAt = updatedAt.AddMinutes(-1),
            updatedAt,
            versions = Array.Empty<object>()
        };

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
