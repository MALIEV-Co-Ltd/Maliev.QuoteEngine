using System.Net;
using Maliev.QuoteEngine.Bff.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

public sealed class ChatbotServiceClientContractTests
{
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
