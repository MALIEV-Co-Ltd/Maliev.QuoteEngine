using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maliev.QuoteEngine.Tests;

public sealed class ProjectSearchEndpointTests(QuoteEngineWebApplicationFactory factory)
    : IClassFixture<QuoteEngineWebApplicationFactory>
{
    [Fact]
    public async Task Project_search_requires_customer_session()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false
        });

        using var response = await client.GetAsync("/quote/v1/projects/search?query=fixture");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Project_search_rejects_queries_longer_than_boundary_limit()
    {
        using var client = await CreateSignedInClientAsync($"query-limit-{Guid.NewGuid():N}@example.com");
        var query = new string('x', 121);

        using var response = await client.GetAsync(
            $"/quote/v1/projects/search?query={Uri.EscapeDataString(query)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Project_search_local_fallback_returns_only_matching_owner_projects()
    {
        var token = $"bracket-{Guid.NewGuid():N}";
        using var owner = await CreateSignedInClientAsync($"owner-{Guid.NewGuid():N}@example.com");
        using var other = await CreateSignedInClientAsync($"other-{Guid.NewGuid():N}@example.com");
        var expected = await CreateDraftProjectAsync(owner, $"Precision {token}");
        _ = await CreateDraftProjectAsync(owner, "Unrelated owner project");
        _ = await CreateDraftProjectAsync(other, $"Private {token}");

        using var response = await owner.GetAsync(
            $"/quote/v1/projects/search?query={Uri.EscapeDataString($"  {token.ToUpperInvariant()}  ")}&limit=5");

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(token.ToUpperInvariant(), root.GetProperty("query").GetString());
        Assert.Equal(1, root.GetProperty("totalCount").GetInt32());
        var project = Assert.Single(root.GetProperty("projects").EnumerateArray());
        Assert.Equal(expected.ProjectServiceProjectId, project.GetProperty("projectId").GetGuid());
        Assert.Equal($"Precision {token}", project.GetProperty("title").GetString());
        Assert.Equal("Draft", project.GetProperty("status").GetString());
        Assert.False(project.TryGetProperty("customerId", out _));
    }

    [Fact]
    public async Task Project_search_intersects_index_candidates_with_session_owned_projects()
    {
        var indexedSearch = new RecordingSearchServiceClient();
        using var scopedFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISearchServiceClient>();
                services.AddSingleton<ISearchServiceClient>(indexedSearch);
            }));
        using var owner = await CreateSignedInClientAsync(
            scopedFactory,
            $"indexed-owner-{Guid.NewGuid():N}@example.com");
        using var other = await CreateSignedInClientAsync(
            scopedFactory,
            $"indexed-other-{Guid.NewGuid():N}@example.com");
        var first = await CreateDraftProjectAsync(owner, "Indexed first fixture");
        var second = await CreateDraftProjectAsync(owner, "Indexed second fixture");
        var privateProject = await CreateDraftProjectAsync(other, "Indexed private fixture");
        indexedSearch.ProjectIds =
        [
            Assert.IsType<Guid>(privateProject.ProjectServiceProjectId),
            Assert.IsType<Guid>(second.ProjectServiceProjectId),
            Assert.IsType<Guid>(first.ProjectServiceProjectId)
        ];

        using var response = await owner.GetAsync("/quote/v1/projects/search?query=indexed&limit=2");

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<CustomerProjectSearchResponse>();
        Assert.NotNull(payload);
        Assert.Equal("indexed", indexedSearch.Query);
        Assert.Equal(50, indexedSearch.Limit);
        Assert.Equal(
            [second.ProjectServiceProjectId, first.ProjectServiceProjectId],
            payload.Projects.Select(project => (Guid?)project.ProjectId));
        Assert.DoesNotContain(payload.Projects, project => project.ProjectId == privateProject.ProjectServiceProjectId);
    }

    private async Task<HttpClient> CreateSignedInClientAsync(string email)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var signIn = await client.GetAsync($"/test/sign-in?email={Uri.EscapeDataString(email)}");
        signIn.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<HttpClient> CreateSignedInClientAsync(
        WebApplicationFactory<Program> appFactory,
        string email)
    {
        var client = appFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var signIn = await client.GetAsync($"/test/sign-in?email={Uri.EscapeDataString(email)}");
        signIn.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<CreateDraftProjectResponse> CreateDraftProjectAsync(HttpClient client, string title)
    {
        using var response = await client.PostAsJsonAsync(
            "/quote/v1/projects/draft",
            new CreateDraftProjectRequest(
                QuoteSessionId: Guid.NewGuid().ToString("N"),
                Parts: [],
                Notes: "Project search endpoint test draft.",
                Title: title));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CreateDraftProjectResponse>()
            ?? throw new InvalidOperationException("QuoteEngine returned an empty draft project response.");
    }

    private sealed class RecordingSearchServiceClient : ISearchServiceClient
    {
        public string? Query { get; private set; }

        public int Limit { get; private set; }

        public IReadOnlyList<Guid> ProjectIds { get; set; } = [];

        public Task<ProjectSearchIndexResult> SearchProjectsAsync(
            string query,
            int limit,
            CancellationToken cancellationToken = default)
        {
            Query = query;
            Limit = limit;
            return Task.FromResult(ProjectSearchIndexResult.Available(ProjectIds));
        }
    }
}
