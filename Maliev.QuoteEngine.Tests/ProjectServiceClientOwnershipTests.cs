using System.Net;
using System.Net.Http.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Shared.Quotes;
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
        Assert.NotNull(project.CreatedAt);
        Assert.True(project.CreatedAt < project.UpdatedAt);
    }

    [Fact]
    public async Task GetProjectDetailAsync_MapsCreatedDateAndOwnedFiles()
    {
        var ownerId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow.AddDays(-2);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                id = projectId,
                projectNumber = "PRJ-DATE-001",
                customerId = ownerId,
                title = "Created date fixture",
                status = "Draft",
                isPinned = false,
                isArchived = false,
                createdAt,
                updatedAt = createdAt.AddHours(4),
                parts = new[]
                {
                    new
                    {
                        id = Guid.NewGuid(),
                        fileName = "fixture.step",
                        process = "cnc",
                        material = "al6061",
                        quantity = 2,
                        status = "Ready"
                    }
                }
            })
        };
        var handler = new RecordingHandler(response);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://project.test") };
        var client = new ProjectServiceClient(http, NullLogger<ProjectServiceClient>.Instance);

        var detail = await client.GetProjectDetailAsync(ownerId, projectId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(new DateTimeOffset(createdAt, TimeSpan.Zero), detail.CreatedAt);
        Assert.Equal("fixture.step", Assert.Single(detail.Parts).FileName);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    public async Task GetProjectDetailAsync_MapsProjectServiceSupplementaryFilesAndPreservesUnknownVolume(string volumeJson)
    {
        var ownerId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var partId = Guid.NewGuid();
        var drawingId = Guid.NewGuid();
        var supplementaryId = Guid.NewGuid();
        var responseJson = $$"""
            {
              "id": "{{projectId:D}}",
              "projectNumber": "PRJ-FILES-001",
              "customerId": "{{ownerId:D}}",
              "title": "Attachment truth fixture",
              "status": "Draft",
              "createdAt": "2026-07-08T08:30:00Z",
              "updatedAt": "2026-07-10T09:45:00Z",
              "parts": [
                {
                  "id": "{{partId:D}}",
                  "fileName": "housing.step",
                  "processType": "CncMilling",
                  "materialCode": "AL6061",
                  "quantity": 1,
                  "volumeCm3": {{volumeJson}},
                  "status": "DfmAnalysisReady",
                  "drawingFiles": [
                    {
                      "fileId": "{{drawingId:D}}",
                      "fileName": "housing-drawing.pdf",
                      "storagePath": "customers/owner/drawings/housing-drawing.pdf",
                      "signedUrl": "https://files.example.test/drawing",
                      "sizeBytes": 4096,
                      "contentType": "application/pdf",
                      "uploadedAt": "2026-07-08T08:31:00Z"
                    }
                  ],
                  "supplementaryFiles": [
                    {
                      "fileId": "{{supplementaryId:D}}",
                      "fileName": "inspection-notes.txt",
                      "storagePath": "customers/owner/support/inspection-notes.txt",
                      "signedUrl": "https://files.example.test/notes",
                      "sizeBytes": 512,
                      "contentType": "text/plain",
                      "uploadedAt": "2026-07-08T08:32:00Z"
                    }
                  ]
                }
              ]
            }
            """;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
        };
        var handler = new RecordingHandler(response);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://project.test") };
        var client = new ProjectServiceClient(http, NullLogger<ProjectServiceClient>.Instance);

        var detail = await client.GetProjectDetailAsync(ownerId, projectId, CancellationToken.None);

        var part = Assert.Single(Assert.IsType<CustomerProjectDetailResponse>(detail).Parts);
        Assert.Equal(0m, part.VolumeCc);
        Assert.Collection(
            part.DrawingFiles,
            drawing =>
            {
                Assert.Equal("housing-drawing.pdf", drawing.FileName);
                Assert.Equal("customers/owner/drawings/housing-drawing.pdf", drawing.StoragePath);
                Assert.Equal("application/pdf", drawing.ContentType);
                Assert.Equal(4096, drawing.FileSizeBytes);
                Assert.Equal("Drawing", drawing.Kind);
            },
            supplementary =>
            {
                Assert.Equal("inspection-notes.txt", supplementary.FileName);
                Assert.Equal("customers/owner/support/inspection-notes.txt", supplementary.StoragePath);
                Assert.Equal("text/plain", supplementary.ContentType);
                Assert.Equal(512, supplementary.FileSizeBytes);
                Assert.Equal("Supplementary", supplementary.Kind);
            });
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
