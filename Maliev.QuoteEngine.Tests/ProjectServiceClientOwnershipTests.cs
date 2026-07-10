using System.Net;
using System.Net.Http.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

public sealed class ProjectServiceClientOwnershipTests
{
    [Fact]
    public async Task GetProjectNavigationAsync_DropsProjectsNotOwnedByRequestedCustomer()
    {
        var ownerId = Guid.NewGuid();
        var ownerProjectId = Guid.NewGuid();
        var otherProjectId = Guid.NewGuid();
        using var response = BuildPagedResponse(
            Project(ownerProjectId, ownerId, "Owned fixture"),
            Project(otherProjectId, Guid.NewGuid(), "Other customer's fixture"));
        var handler = new RecordingHandler(response);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://project.test") };
        var client = new ProjectServiceClient(http, NullLogger<ProjectServiceClient>.Instance);

        var projects = await client.GetProjectNavigationAsync(ownerId, CancellationToken.None);

        Assert.Equal(
            $"/project/v1/projects?customerId={ownerId:D}&pageSize=100",
            handler.Request!.RequestUri!.PathAndQuery);
        var project = Assert.Single(projects);
        Assert.Equal(ownerProjectId, project.ProjectId);
    }

    [Fact]
    public async Task SearchProjectResultsAsync_DropsProjectsNotOwnedByRequestedCustomer()
    {
        var ownerId = Guid.NewGuid();
        var ownerProjectId = Guid.NewGuid();
        using var response = BuildPagedResponse(
            Project(ownerProjectId, ownerId, "Owned fixture"),
            Project(Guid.NewGuid(), Guid.NewGuid(), "Other customer's fixture"));
        var handler = new RecordingHandler(response);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://project.test") };
        var client = new ProjectServiceClient(http, NullLogger<ProjectServiceClient>.Instance);

        var projects = await client.SearchProjectResultsAsync(ownerId, "fixture", 20, CancellationToken.None);

        var project = Assert.Single(projects);
        Assert.Equal(ownerProjectId.ToString("D"), project.ResourceId);
    }

    private static HttpResponseMessage BuildPagedResponse(params object[] projects) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { data = projects })
    };

    private static object Project(Guid id, Guid customerId, string title) => new
    {
        id,
        projectNumber = $"PRJ-{id:N}"[..12],
        customerId,
        title,
        status = "Draft",
        isPinned = false,
        isArchived = false,
        createdAt = DateTime.UtcNow.AddDays(-1),
        updatedAt = DateTime.UtcNow,
        parts = Array.Empty<object>()
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
