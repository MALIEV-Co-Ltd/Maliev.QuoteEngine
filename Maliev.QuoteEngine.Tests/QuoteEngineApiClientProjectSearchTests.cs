using System.Net;
using System.Net.Http.Json;
using Maliev.QuoteEngine.Client.Services;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteEngineApiClientProjectSearchTests
{
    [Fact]
    public async Task SearchProjectsAsync_NormalizesQueryLimitAndMapsSharedResponse()
    {
        var projectId = Guid.NewGuid();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new CustomerProjectSearchResponse(
                "bracket/blue",
                1,
                [
                    new CustomerProjectNavItemDto(
                        projectId,
                        "PRJ-1001",
                        "Blue bracket",
                        "Draft",
                        false,
                        false,
                        DateTimeOffset.UtcNow)
                ]))
        };
        var handler = new RecordingHandler(response);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://quote.test/") };
        var client = new QuoteEngineApiClient(http);

        var result = await client.SearchProjectsAsync("  bracket/blue  ", 100, CancellationToken.None);

        Assert.Equal(
            "/quote/v1/projects/search?query=bracket%2Fblue&limit=50",
            handler.Request!.RequestUri!.PathAndQuery);
        Assert.Equal("bracket/blue", result.Query);
        Assert.Equal(projectId, Assert.Single(result.Projects).ProjectId);
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(response);
        }
    }
}
