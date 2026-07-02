using System.Net;
using System.Text;
using Maliev.QuoteEngine.Bff.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

public sealed class ChatbotServiceClientContractTests
{
    [Fact]
    public async Task SendMessageStreamAsync_UsesNonStreamingMessageEndpointAndYieldsFinalEvent()
    {
        var responseJson = """
            {
              "message_id": "d127db4e-1106-4106-8f6b-32c6b467e8ad",
              "content": "Assistant response from the stable message endpoint.",
              "role": "assistant",
              "language": "en",
              "created_at": "2026-07-02T04:00:00Z",
              "suggested_actions": [],
              "thinking_steps": [],
              "usage_snapshot": {
                "is_enabled": true,
                "used_tokens": 42,
                "daily_token_budget": 2000000,
                "remaining_tokens": 1999958,
                "used_ratio": 0.000021,
                "is_exceeded": false
              }
            }
            """;
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://chatbot-service.test")
        };
        var client = new ChatbotServiceClient(http, NullLogger<ChatbotServiceClient>.Instance);

        var events = new List<ChatbotMessageStreamEvent>();
        await foreach (var streamEvent in client.SendMessageStreamAsync(new ChatbotSendMessageRequest
        {
            SessionId = Guid.Parse("6f9b89db-4e96-4c4b-ace5-908d2bb172b6"),
            Content = "Quote this PLA bracket.",
            Language = "en",
            QuoteAgentContextToken = "signed-context"
        }, CancellationToken.None))
        {
            events.Add(streamEvent);
        }

        Assert.Equal(HttpMethod.Post, handler.Request?.Method);
        Assert.Equal("/chatbot/v1/messages", handler.Request?.RequestUri?.AbsolutePath);
        var final = Assert.Single(events);
        Assert.Equal("final", final.Type);
        Assert.NotNull(final.Message);
        Assert.Equal("Assistant response from the stable message endpoint.", final.Message.Content);
        Assert.Equal(42, final.Message.UsageSnapshot?.UsedTokens);
    }

    [Fact]
    public async Task TruncateLastTurnAsync_SendsInternalDeleteRequest()
    {
        var sessionId = Guid.Parse("6f9b89db-4e96-4c4b-ace5-908d2bb172b6");
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://chatbot-service.test")
        };
        var client = new ChatbotServiceClient(http, NullLogger<ChatbotServiceClient>.Instance);

        var result = await client.TruncateLastTurnAsync(sessionId, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(HttpMethod.Delete, handler.Request?.Method);
        Assert.Equal(
            $"/chatbot/v1/internal/sessions/{sessionId:D}/messages/last-turn",
            handler.Request?.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task TruncateLastTurnAsync_WhenServiceRejects_ReturnsFalse()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://chatbot-service.test")
        };
        var client = new ChatbotServiceClient(http, NullLogger<ChatbotServiceClient>.Instance);

        var result = await client.TruncateLastTurnAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(result);
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
