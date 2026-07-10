using System.Net;
using System.Net.Http.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

public sealed class SearchServiceClientContractTests
{
    [Fact]
    public async Task SearchProjectsAsync_UsesProjectFiltersAndMapsOnlyProjectServiceIds()
    {
        var firstProjectId = Guid.NewGuid();
        var secondProjectId = Guid.NewGuid();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                query = "precision bracket",
                totalCount = 6,
                results = new object[]
                {
                    SearchResult("ProjectService", "project", firstProjectId.ToString("D")),
                    SearchResult("ProjectService", "project", secondProjectId.ToString("D")),
                    SearchResult("ProjectService", "project", firstProjectId.ToString("D")),
                    SearchResult("OrderService", "project", Guid.NewGuid().ToString("D")),
                    SearchResult("ProjectService", "order", Guid.NewGuid().ToString("D")),
                    SearchResult("ProjectService", "project", "not-a-guid")
                }
            })
        };
        var handler = new RecordingHandler(response);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://search.test") };
        var client = new SearchServiceClient(http, NullLogger<SearchServiceClient>.Instance);

        var result = await client.SearchProjectsAsync("  precision bracket  ", 80, CancellationToken.None);

        Assert.Equal(
            "/search/v1/search?query=precision%20bracket&limit=50&type=project&area=ProjectService",
            handler.Request!.RequestUri!.PathAndQuery);
        Assert.True(result.IsAvailable);
        Assert.Equal([firstProjectId, secondProjectId], result.ProjectIds);
    }

    [Fact]
    public async Task SearchProjectsAsync_DependencyFailureReturnsUnavailable()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var handler = new RecordingHandler(response);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://search.test") };
        var client = new SearchServiceClient(http, NullLogger<SearchServiceClient>.Instance);

        var result = await client.SearchProjectsAsync("fixture", 20, CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Empty(result.ProjectIds);
    }

    private static object SearchResult(string sourceService, string resourceType, string resourceId) => new
    {
        sourceService,
        resourceType,
        resourceId,
        title = "Fixture project",
        subtitle = "PRJ-1001",
        summary = "Fixture",
        status = "Draft",
        requiredPermission = "project.projects.read",
        score = 1.0,
        updatedAtUtc = DateTimeOffset.UtcNow
    };

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
