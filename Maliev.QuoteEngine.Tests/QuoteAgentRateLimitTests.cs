using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Security;
using Maliev.QuoteEngine.Shared.Agent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maliev.QuoteEngine.Tests;

/// <summary>
/// Defense-in-depth rate limiting + anonymous-visitor cookie for the public agent ingress (S1-extras).
/// </summary>
[Collection(QuoteEngineEndpointTestCollection.Name)]
public sealed class QuoteAgentRateLimitTests(QuoteEngineWebApplicationFactory factory)
    : IClassFixture<QuoteEngineWebApplicationFactory>
{
    [Fact]
    public async Task Agent_message_issues_signed_httponly_anonymous_visitor_cookie()
    {
        await using var scopedFactory = WithStubChatbot();
        // HandleCookies=false keeps the Set-Cookie header on the response so we can inspect it.
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            Message = "I need a 3D printed bracket.",
            Language = "en"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        var visitorCookie = Assert.Single(
            cookies!,
            c => c.StartsWith(AnonymousVisitorCookie.CookieName + "=", StringComparison.Ordinal));
        Assert.Contains("httponly", visitorCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_message_throttles_cookieless_caller_by_ip()
    {
        // The acceptance test for S1-extras: HandleCookies=false models a client that drops the visitor
        // cookie (the abuser). Every request presents no valid inbound cookie, so all share the IP
        // partition and the per-minute limit bites — proving the limit cannot be reset by dropping the
        // cookie (the mirror of "a new session must not reset the budget").
        await using var scopedFactory = WithStubChatbot(builder =>
            builder.UseSetting("QuoteAgent:RateLimit:PerMinute", "3"));
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
            {
                Message = "spam",
                Language = "en"
            });
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                break;
            }
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task Agent_message_is_not_throttled_when_limiter_disabled_by_default()
    {
        // Guards the suite-green invariant: with no configured limit, the policy must resolve to a
        // NoLimiter (not a zero-permit limiter that rejects everything).
        await using var scopedFactory = WithStubChatbot();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        for (var i = 0; i < 8; i++)
        {
            var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
            {
                Message = "ok",
                Language = "en"
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private WebApplicationFactory<Program> WithStubChatbot(Action<IWebHostBuilder>? configure = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            configure?.Invoke(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(new StubChatbotServiceClient());
            });
        });

    private sealed class StubChatbotServiceClient : IChatbotServiceClient
    {
        public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<ChatbotSessionResponse?> InitiateSessionAsync(
            ChatbotInitiateSessionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<ChatbotSessionResponse?>(new ChatbotSessionResponse
            {
                SessionId = Guid.NewGuid(),
                WelcomeMessage = "Ready.",
                Language = request.Language,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
            });

        public Task<ChatbotMessageResponse?> SendMessageAsync(
            ChatbotSendMessageRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<ChatbotMessageResponse?>(new ChatbotMessageResponse
            {
                MessageId = Guid.NewGuid(),
                Content = "Upload the bracket CAD file and I will check the gates.",
                Role = "assistant",
                Language = "en",
                CreatedAt = DateTimeOffset.UtcNow
            });

        public async IAsyncEnumerable<ChatbotMessageStreamEvent> SendMessageStreamAsync(
            ChatbotSendMessageRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new ChatbotMessageStreamEvent { Type = "started" };
            yield return new ChatbotMessageStreamEvent { Type = "delta", Delta = "Working on it." };
            yield return new ChatbotMessageStreamEvent
            {
                Type = "final",
                Message = new ChatbotMessageResponse
                {
                    MessageId = Guid.NewGuid(),
                    Content = "Working on it.",
                    Role = "assistant",
                    Language = "en",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            };
        }

        public Task<ChatbotConversationMessagesResponse?> GetConversationMessagesAsync(
            Guid sessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ChatbotConversationMessagesResponse?>(null);

        public Task<string?> CleanSpeechAsync(string speech, string language, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(speech);
    }
}
