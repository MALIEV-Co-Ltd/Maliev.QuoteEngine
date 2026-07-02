using System.Net;
using System.Text;
using Maliev.QuoteEngine.Bff.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

public sealed class ChatbotServiceClientContractTests
{
    [Fact]
    public async Task SendMessageStreamAsync_UsesStreamingMessageEndpointAndYieldsStreamEvents()
    {
        var responseJson = """
            {"type":"started"}
            {"type":"delta","delta":"Assistant "}
            {"type":"thought","thought":"Checking uploaded files."}
            {"type":"final","message":{"message_id":"d127db4e-1106-4106-8f6b-32c6b467e8ad","content":"Assistant response from the streaming endpoint.","role":"assistant","language":"en","created_at":"2026-07-02T04:00:00Z","suggested_actions":[],"thinking_steps":[],"usage_snapshot":{"is_enabled":true,"used_tokens":42,"daily_token_budget":2000000,"remaining_tokens":1999958,"used_ratio":0.000021,"is_exceeded":false}}}
            """;
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/x-ndjson")
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
        Assert.Equal("/chatbot/v1/messages/stream", handler.Request?.RequestUri?.AbsolutePath);
        Assert.Contains("\"quote_agent_context_token\":\"signed-context\"", handler.RequestBody);
        Assert.Equal(4, events.Count);
        Assert.Equal("started", events[0].Type);
        Assert.Equal("delta", events[1].Type);
        Assert.Equal("Assistant ", events[1].Delta);
        Assert.Equal("thought", events[2].Type);
        Assert.Equal("Checking uploaded files.", events[2].Thought);
        var final = events[3];
        Assert.Equal("final", final.Type);
        Assert.NotNull(final.Message);
        Assert.Equal("Assistant response from the streaming endpoint.", final.Message.Content);
        Assert.Equal(42, final.Message.UsageSnapshot?.UsedTokens);
    }

    [Fact]
    public async Task SendMessageStreamAsync_WhenServiceRejects_YieldsErrorEvent()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"CallbackUrl must be same-origin."}""", Encoding.UTF8, "application/json")
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
            Language = "en"
        }, CancellationToken.None))
        {
            events.Add(streamEvent);
        }

        Assert.Equal("/chatbot/v1/messages/stream", handler.Request?.RequestUri?.AbsolutePath);
        var error = Assert.Single(events);
        Assert.Equal("error", error.Type);
        Assert.False(string.IsNullOrWhiteSpace(error.Error));
    }

    [Fact]
    public async Task SendMessageStreamAsync_MapsUsageCostBudgetFields()
    {
        var responseJson = """
            {"type":"final","message":{"message_id":"d127db4e-1106-4106-8f6b-32c6b467e8ad","content":"Assistant response.","role":"assistant","language":"en","created_at":"2026-07-02T04:00:00Z","suggested_actions":[],"thinking_steps":[],"usage_snapshot":{"is_enabled":true,"used_tokens":850000,"daily_token_budget":2000000,"remaining_tokens":1150000,"used_ratio":0.425,"used_cost_micro_usd":7250,"daily_cost_budget_micro_usd":5000000,"remaining_cost_micro_usd":4992750,"cost_used_ratio":0.00145,"is_token_exceeded":false,"is_cost_exceeded":true,"is_exceeded":true}}}
            """;
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/x-ndjson")
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
            Language = "en"
        }, CancellationToken.None))
        {
            events.Add(streamEvent);
        }

        var final = Assert.Single(events);
        Assert.NotNull(final.Message?.UsageSnapshot);
        var usage = final.Message.UsageSnapshot;
        Assert.Equal(7_250, ReadRequiredProperty<long>(usage, "UsedCostMicroUsd"));
        Assert.Equal(5_000_000, ReadRequiredProperty<long>(usage, "DailyCostBudgetMicroUsd"));
        Assert.Equal(4_992_750, ReadRequiredProperty<long>(usage, "RemainingCostMicroUsd"));
        Assert.Equal(0.00145, ReadRequiredProperty<double>(usage, "CostUsedRatio"));
        Assert.False(ReadRequiredProperty<bool>(usage, "IsTokenExceeded"));
        Assert.True(ReadRequiredProperty<bool>(usage, "IsCostExceeded"));
        Assert.True(usage.IsExceeded);
    }

    [Fact]
    public async Task SendMessageStreamAsync_ForwardsStructuredOutputSchema()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"type":"started"}""", Encoding.UTF8, "application/x-ndjson")
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://chatbot-service.test")
        };
        var client = new ChatbotServiceClient(http, NullLogger<ChatbotServiceClient>.Instance);

        await foreach (var _ in client.SendMessageStreamAsync(new ChatbotSendMessageRequest
        {
            SessionId = Guid.Parse("6f9b89db-4e96-4c4b-ace5-908d2bb172b6"),
            Content = "Extract quote requirements from this message.",
            Language = "en",
            ResponseMimeType = "application/json",
            ResponseSchema = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["required"] = new[] { "quoteSummary" },
                ["properties"] = new Dictionary<string, object?>
                {
                    ["quoteSummary"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string"
                    }
                }
            }
        }, CancellationToken.None))
        {
        }

        Assert.Contains("\"response_mime_type\":\"application/json\"", handler.RequestBody);
        Assert.Contains("\"response_schema\":", handler.RequestBody);
        Assert.Contains("\"quoteSummary\"", handler.RequestBody);
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

    [Fact]
    public async Task CleanSpeechAsync_UsesExtractionCleanSpeechEndpointAndSnakeCaseContract()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"cleaned_text":"Please quote the bracket."}""", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://chatbot-service.test")
        };
        var client = new ChatbotServiceClient(http, NullLogger<ChatbotServiceClient>.Instance);

        var result = await client.CleanSpeechAsync("um please quote the bracket", "en", CancellationToken.None);

        Assert.Equal("Please quote the bracket.", result);
        Assert.Equal(HttpMethod.Post, handler.Request?.Method);
        Assert.Equal("/chatbot/v1/extraction/clean-speech", handler.Request?.RequestUri?.AbsolutePath);
        Assert.Contains("\"speech\":\"um please quote the bracket\"", handler.RequestBody);
        Assert.Contains("\"language\":\"en\"", handler.RequestBody);
        Assert.DoesNotContain("cleanedText", handler.RequestBody);
    }

    [Fact]
    public async Task CleanSpeechAsync_WhenServiceRejects_ReturnsNull()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":"Daily Gemini token budget exceeded."}""", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://chatbot-service.test")
        };
        var client = new ChatbotServiceClient(http, NullLogger<ChatbotServiceClient>.Instance);

        var result = await client.CleanSpeechAsync("please clean this", "en", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal("/chatbot/v1/extraction/clean-speech", handler.Request?.RequestUri?.AbsolutePath);
    }

    private static T ReadRequiredProperty<T>(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        var value = property.GetValue(target);
        return Assert.IsType<T>(value);
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            if (request.Content is not null)
            {
                RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return response;
        }
    }
}
