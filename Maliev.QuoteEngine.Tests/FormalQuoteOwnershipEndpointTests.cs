using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Maliev.QuoteEngine.Tests;

[Collection(QuoteEngineEndpointTestCollection.Name)]
public sealed class FormalQuoteOwnershipEndpointTests(QuoteEngineWebApplicationFactory factory)
    : IClassFixture<QuoteEngineWebApplicationFactory>
{
    [Fact]
    public async Task GenerateFormalQuote_MissingOrForeignProject_ReturnsBareNotFoundWithoutQuotationCall()
    {
        var projectId = Guid.NewGuid();
        var projectClient = Substitute.For<IProjectServiceClient>();
        projectClient.LookupProjectDetailAsync(
                Arg.Any<Guid>(),
                projectId,
                Arg.Any<CancellationToken>())
            .Returns(new DownstreamLookupResult<CustomerProjectDetailResponse?>(true, null));
        var quotationClient = CreateSuccessfulQuotationClient();
        var materialClient = CreateMaterialClient();

        await using var scopedFactory = CreateFactory(projectClient, quotationClient, materialClient);
        using var client = scopedFactory.CreateClient();
        (await client.GetAsync("/test/sign-in?email=formal-owner-missing@example.com"))
            .EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(projectId, "formal-owner-missing", [], "Missing project."));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        await projectClient.Received(1).LookupProjectDetailAsync(
            Arg.Any<Guid>(),
            projectId,
            Arg.Any<CancellationToken>());
        await materialClient.DidNotReceiveWithAnyArgs()
            .ResolveMaterialIdAsync(default!, default!, default);
        await quotationClient.DidNotReceiveWithAnyArgs()
            .CreateOrReviseProjectQuoteAsync(default!, default);
    }

    [Fact]
    public async Task GenerateFormalQuote_ProjectServiceFailure_ReturnsServiceUnavailableWithoutQuotationCall()
    {
        var projectId = Guid.NewGuid();
        var projectClient = Substitute.For<IProjectServiceClient>();
        projectClient.LookupProjectDetailAsync(
                Arg.Any<Guid>(),
                projectId,
                Arg.Any<CancellationToken>())
            .Returns(new DownstreamLookupResult<CustomerProjectDetailResponse?>(false, null));
        var quotationClient = CreateSuccessfulQuotationClient();
        var materialClient = CreateMaterialClient();

        await using var scopedFactory = CreateFactory(projectClient, quotationClient, materialClient);
        using var client = scopedFactory.CreateClient();
        (await client.GetAsync("/test/sign-in?email=formal-owner-unavailable@example.com"))
            .EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(projectId, "formal-owner-unavailable", [], "Unavailable project lookup."));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Project service unavailable.", problem.RootElement.GetProperty("title").GetString());
        Assert.False(problem.RootElement.TryGetProperty("detail", out _));
        await projectClient.Received(1).LookupProjectDetailAsync(
            Arg.Any<Guid>(),
            projectId,
            Arg.Any<CancellationToken>());
        await materialClient.DidNotReceiveWithAnyArgs()
            .ResolveMaterialIdAsync(default!, default!, default);
        await quotationClient.DidNotReceiveWithAnyArgs()
            .CreateOrReviseProjectQuoteAsync(default!, default);
    }

    [Fact]
    public async Task GenerateFormalQuote_OwnedProject_UsesSessionCustomerAndVerifiedProject()
    {
        const string email = "formal-owner-success@example.com";
        var customerId = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(email)));
        var attackerCustomerId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var projectClient = Substitute.For<IProjectServiceClient>();
        projectClient.LookupProjectDetailAsync(customerId, projectId, Arg.Any<CancellationToken>())
            .Returns(new DownstreamLookupResult<CustomerProjectDetailResponse?>(
                true,
                new CustomerProjectDetailResponse(
                    projectId,
                    "PRJ-OWNED-001",
                    "Draft",
                    "Owned formal quote project",
                    IsPinned: false,
                    IsArchived: false,
                    DateTimeOffset.UtcNow,
                    [])));
        var quotationClient = CreateSuccessfulQuotationClient();
        var materialClient = CreateMaterialClient();

        await using var scopedFactory = CreateFactory(projectClient, quotationClient, materialClient);
        using var client = scopedFactory.CreateClient();
        (await client.GetAsync($"/test/sign-in?email={email}"))
            .EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new
            {
                projectId,
                quoteSessionId = "formal-owner-success",
                parts = Array.Empty<object>(),
                notes = "Owned project.",
                customerId = attackerCustomerId
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await projectClient.Received(1).LookupProjectDetailAsync(
            customerId,
            projectId,
            Arg.Any<CancellationToken>());
        await quotationClient.Received(1).CreateOrReviseProjectQuoteAsync(
            Arg.Is<QuotationCreateRequest>(request =>
                request.CustomerId == customerId &&
                request.SourceProjectId == projectId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateFormalQuote_AnonymousCaller_ReturnsUnauthorizedWithoutDownstreamCalls()
    {
        var projectClient = Substitute.For<IProjectServiceClient>();
        var quotationClient = CreateSuccessfulQuotationClient();
        var materialClient = CreateMaterialClient();

        await using var scopedFactory = CreateFactory(projectClient, quotationClient, materialClient);
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false
        });

        var response = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "formal-owner-anonymous", [], "Anonymous."));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await projectClient.DidNotReceiveWithAnyArgs()
            .LookupProjectDetailAsync(default, default, default);
        await materialClient.DidNotReceiveWithAnyArgs()
            .ResolveMaterialIdAsync(default!, default!, default);
        await quotationClient.DidNotReceiveWithAnyArgs()
            .CreateOrReviseProjectQuoteAsync(default!, default);
    }

    private WebApplicationFactory<Program> CreateFactory(
        IProjectServiceClient projectClient,
        IQuotationServiceClient quotationClient,
        IMaterialCatalogClient materialClient) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                Replace(services, projectClient);
                Replace(services, quotationClient);
                Replace(services, materialClient);
            }));

    private static IQuotationServiceClient CreateSuccessfulQuotationClient()
    {
        var quotationClient = Substitute.For<IQuotationServiceClient>();
        quotationClient.CreateOrReviseProjectQuoteAsync(
                Arg.Any<QuotationCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new QuotationCreatedResult
            {
                Id = Guid.NewGuid(),
                QuotationNumber = "QT-OWNERSHIP-RED",
                Status = "Draft"
            });
        return quotationClient;
    }

    private static IMaterialCatalogClient CreateMaterialClient()
    {
        var materialClient = Substitute.For<IMaterialCatalogClient>();
        materialClient.ResolveMaterialIdAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        return materialClient;
    }

    private static void Replace<TService>(IServiceCollection services, TService implementation)
        where TService : class
    {
        services.RemoveAll<TService>();
        services.AddSingleton(implementation);
    }
}
