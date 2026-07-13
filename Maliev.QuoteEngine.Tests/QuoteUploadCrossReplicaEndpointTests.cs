using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DotNet.Testcontainers.Configurations;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Security;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteUploadCrossReplicaEndpointTests : IAsyncLifetime
{
    static QuoteUploadCrossReplicaEndpointTests()
    {
        TestcontainersSettings.ResourceReaperEnabled = false;
    }

    private readonly RedisContainer _redis = new RedisBuilder(
        "redis:7.0-alpine@sha256:c9d92d840fd011c908f040592857c724ae6d877f2aba5c40ad963276507386b2")
        .Build();

    public Task InitializeAsync() => _redis.StartAsync();

    public Task DisposeAsync() => _redis.DisposeAsync().AsTask();

    [Fact]
    public async Task UploadCreatedOnHostA_CanCompletePromoteAndReadOnHostB()
    {
        var downstream = new QuoteEngineWebApplicationFactory.FixedCompletedMetadataUploadClient(
            metadataMatches: true);
        await using var redisA = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var redisB = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await using var rootA = new QuoteEngineWebApplicationFactory();
        await using var rootB = new QuoteEngineWebApplicationFactory();
        await using var hostA = CreateHost(rootA, downstream, redisA);
        await using var hostB = CreateHost(rootB, downstream, redisB);
        using var clientA = hostA.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var clientB = hostB.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var sessionId = Guid.NewGuid();

        var initiation = await clientA.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            FileName = "cross-replica.step",
            ContentType = "model/step",
            FileSizeBytes = 8,
            QuoteSessionId = sessionId.ToString("D")
        });
        initiation.EnsureSuccessStatusCode();
        var initiated = await initiation.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();
        Assert.NotNull(initiated);
        var visitorCookie = ReadCookie(initiation, AnonymousVisitorCookie.CookieName);
        var recoveredOnB = await hostB.Services.GetRequiredService<IQuoteUploadStateStore>()
            .GetAsync(initiated.UploadId, CancellationToken.None);
        Assert.NotNull(recoveredOnB);
        using (var scope = hostB.Services.CreateScope())
        {
            var context = new DefaultHttpContext();
            context.Request.Headers.Cookie = visitorCookie;
            var visitorOnB = scope.ServiceProvider.GetRequiredService<AnonymousVisitorCookie>()
                .ReadVisitorId(context.Request);
            Assert.Equal(recoveredOnB.VisitorId, visitorOnB);
        }

        using (var chunk = new HttpRequestMessage(HttpMethod.Put, initiated.ProxyUploadUrl)
        {
            Content = new ByteArrayContent(new byte[8])
        })
        {
            chunk.Headers.Add("Cookie", visitorCookie);
            chunk.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 7, 8);
            Assert.Equal(HttpStatusCode.NoContent, (await clientB.SendAsync(chunk)).StatusCode);
        }

        using (var complete = new HttpRequestMessage(
            HttpMethod.Post,
            $"/quote/v1/uploads/resumable/{initiated.UploadId}/complete"))
        {
            complete.Headers.Add("Cookie", visitorCookie);
            var completion = await clientB.SendAsync(complete);
            completion.EnsureSuccessStatusCode();
            var completed = await completion.Content.ReadFromJsonAsync<CompleteQuoteUploadResponse>();
            Assert.NotNull(completed);
            Assert.Equal(downstream.CanonicalFileId, completed.FileId);
        }

        var foreign = await clientA.GetAsync($"/quote/v1/uploads/{initiated.UploadId}/analysis-status");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Empty(await foreign.Content.ReadAsByteArrayAsync());

        const string email = "cross-replica@example.com";
        var signIn = await clientB.GetAsync($"/test/sign-in?email={Uri.EscapeDataString(email)}");
        signIn.EnsureSuccessStatusCode();
        var authCookie = ReadCookie(signIn, "__Secure-Maliev.Identity");
        using (var promote = new HttpRequestMessage(
            HttpMethod.Get,
            $"/quote/v1/agent/sessions/{sessionId:D}"))
        {
            promote.Headers.Add("Cookie", $"{visitorCookie}; {authCookie}");
            (await clientB.SendAsync(promote)).EnsureSuccessStatusCode();
        }

        var reconnectSignIn = await clientA.GetAsync($"/test/sign-in?email={Uri.EscapeDataString(email)}");
        reconnectSignIn.EnsureSuccessStatusCode();
        var reconnectAuthCookie = ReadCookie(reconnectSignIn, "__Secure-Maliev.Identity");
        using var status = new HttpRequestMessage(
            HttpMethod.Get,
            $"/quote/v1/uploads/{initiated.UploadId}/analysis-status");
        status.Headers.Add("Cookie", reconnectAuthCookie);
        var recovered = await clientA.SendAsync(status);

        recovered.EnsureSuccessStatusCode();
        var analysis = await recovered.Content.ReadFromJsonAsync<QuoteAnalysisStatusResponse>();
        Assert.NotNull(analysis);
        Assert.Equal("Processing", analysis.Status);
    }

    private WebApplicationFactory<Program> CreateHost(
        QuoteEngineWebApplicationFactory root,
        QuoteUploadServiceClient downstream,
        IConnectionMultiplexer redis) =>
        root.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:redis"] = _redis.GetConnectionString()
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IConnectionMultiplexer>();
                services.AddSingleton(redis);
                services.RemoveAll<IQuoteUploadStateStore>();
                services.AddSingleton<IQuoteUploadStateStore>(new RedisQuoteUploadStateStore(redis));
                services.RemoveAll<IQuoteAgentSessionOwnerStore>();
                services.AddSingleton<IQuoteAgentSessionOwnerStore>(new RedisQuoteAgentSessionOwnerStore(redis));
                services.RemoveAll<IQuoteFileAnalysisStatusService>();
                services.AddSingleton<IQuoteFileAnalysisStatusService>(new RedisQuoteFileAnalysisStatusService(redis));
                services.RemoveAll<QuoteUploadServiceClient>();
                services.AddSingleton(downstream);
            });
        });

    private static string ReadCookie(HttpResponseMessage response, string cookieName)
    {
        var value = response.Headers.GetValues("Set-Cookie")
            .Select(header => header.Split(';', 2)[0])
            .Single(cookie => cookie.StartsWith(cookieName + "=", StringComparison.Ordinal));
        return value;
    }
}
