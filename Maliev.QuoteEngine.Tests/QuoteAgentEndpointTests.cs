using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Shared.Agent;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteAgentEndpointTests(QuoteEngineWebApplicationFactory factory)
    : IClassFixture<QuoteEngineWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Agent_message_starts_anonymous_quote_engine_session_with_gate_state()
    {
        var chatbot = new RecordingChatbotServiceClient();
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
            });
        });
        using var client = scopedFactory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            Message = "I need a 3D printed bracket.",
            Language = "en"
        });
        var body = await response.Content.ReadFromJsonAsync<QuoteAgentTurnResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.SessionId);
        Assert.Contains("bracket", body.AssistantText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(body.Gates, gate =>
            gate.Code == "geometry_required" && gate.Status == "blocked");
        Assert.Contains(body.Gates, gate =>
            gate.Code == "customer_authenticated" && gate.Status == "blocked");
        Assert.Equal("quote-engine", chatbot.LastInitiateRequest?.Channel);
        Assert.False(string.IsNullOrWhiteSpace(chatbot.LastSendRequest?.QuoteAgentContextToken));
    }

    [Fact]
    public async Task Agent_message_with_cad_attachment_materializes_prototype_analysis_and_price()
    {
        var chatbot = new RecordingChatbotServiceClient();
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
            });
        });
        using var client = scopedFactory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            Message = "Quote this STL as 25 pieces, black PLA, standard lead time.",
            Language = "en",
            Attachments =
            [
                new QuoteAgentAttachmentDto
                {
                    FileName = "fixture.stl",
                    ContentType = "model/stl",
                    FileSizeBytes = 24_000,
                    Kind = "cad",
                    StoragePath = "quotes/temp/fixture.stl"
                }
            ]
        });
        var body = await response.Content.ReadFromJsonAsync<QuoteAgentTurnResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Contains(body.Gates, gate => gate.Code == "geometry_required" && gate.Status == "passed");
        Assert.Contains(body.Gates, gate => gate.Code == "analysis_complete" && gate.Status == "passed");
        Assert.Contains(body.Gates, gate => gate.Code == "dfm_reviewed" && gate.Status == "passed");
        Assert.Contains(body.Gates, gate => gate.Code == "configuration_complete" && gate.Status == "passed");
        Assert.Contains(body.Gates, gate => gate.Code == "priced" && gate.Status == "passed");
        Assert.Contains(body.Artifacts, artifact => artifact.ArtifactType == "viewer" && artifact.Status == "ready");
        Assert.Contains(body.Artifacts, artifact => artifact.ArtifactType == "dfm" && artifact.Status == "ready");
        Assert.Contains(body.Artifacts, artifact => artifact.ArtifactType == "pricing" && artifact.Status == "ready");

        var state = await client.GetFromJsonAsync<QuoteAgentStateResponse>(
            $"/quote/v1/agent/sessions/{body.SessionId:D}");
        Assert.NotNull(state);
        Assert.Single(state.Parts);
        Assert.NotNull(state.Estimate);
    }

    [Fact]
    public async Task Agent_tool_endpoint_rejects_missing_signed_context()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/quote/v1/agent/tools/quote_get_state",
            new QuoteAgentToolRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Agent_tool_endpoint_returns_state_for_signed_context()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/quote/v1/agent/tools/quote_get_state")
        {
            Content = JsonContent.Create(new QuoteAgentToolRequest(), options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation(
            "X-Maliev-Agent-Context",
            CreateSignedAgentContextToken(Guid.NewGuid(), Guid.NewGuid(), null));

        using var response = await client.SendAsync(request);
        var state = await response.Content.ReadFromJsonAsync<QuoteAgentStateResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(state);
        Assert.Contains(state.Gates, gate => gate.Code == "geometry_required");
        Assert.Contains(state.Gates, gate => gate.Code == "priced" && gate.Status == "pending");
    }

    [Fact]
    public async Task Agent_confirmation_requires_authentication_for_formal_quote_action()
    {
        using var client = factory.CreateClient();
        var quoteSessionId = Guid.NewGuid();
        using var prepareRequest = new HttpRequestMessage(HttpMethod.Post, "/quote/v1/agent/tools/quote_prepare_formal_quote")
        {
            Content = JsonContent.Create(new QuoteAgentToolRequest
            {
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["requirements"] = JsonSerializer.SerializeToElement("Need a formal quote.", JsonOptions)
                }
            }, options: JsonOptions)
        };
        prepareRequest.Headers.TryAddWithoutValidation(
            "X-Maliev-Agent-Context",
            CreateSignedAgentContextToken(quoteSessionId, Guid.NewGuid(), null));

        using var prepareResponse = await client.SendAsync(prepareRequest);
        var state = await prepareResponse.Content.ReadFromJsonAsync<QuoteAgentStateResponse>();
        var action = Assert.Single(state!.ProposedActions);

        var confirmResponse = await client.PostAsJsonAsync(
            $"/quote/v1/agent/actions/{action.ActionId:D}/confirm",
            new QuoteAgentConfirmActionRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, confirmResponse.StatusCode);
    }

    private static string CreateSignedAgentContextToken(Guid quoteSessionId, Guid chatbotSessionId, Guid? customerId)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new
        {
            quoteSessionId,
            chatbotSessionId,
            customerId,
            issuedAt = now,
            expiresAt = now.AddMinutes(15)
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var encodedPayload = Base64UrlTextEncoder.Encode(Encoding.UTF8.GetBytes(json));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("maliev-local-development-quote-agent-context-key"));
        var signature = Base64UrlTextEncoder.Encode(hmac.ComputeHash(Encoding.UTF8.GetBytes(encodedPayload)));
        return $"{encodedPayload}.{signature}";
    }

    private sealed class RecordingChatbotServiceClient : IChatbotServiceClient
    {
        public ChatbotInitiateSessionRequest? LastInitiateRequest { get; private set; }

        public ChatbotSendMessageRequest? LastSendRequest { get; private set; }

        public Task<ChatbotSessionResponse?> InitiateSessionAsync(
            ChatbotInitiateSessionRequest request,
            CancellationToken cancellationToken)
        {
            LastInitiateRequest = request;
            return Task.FromResult<ChatbotSessionResponse?>(new ChatbotSessionResponse
            {
                SessionId = Guid.Parse("3f35a7a7-1450-4b23-820a-0a97b85d5b0f"),
                WelcomeMessage = "Mali is ready for manufacturing quotes.",
                Language = request.Language,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
            });
        }

        public Task<ChatbotMessageResponse?> SendMessageAsync(
            ChatbotSendMessageRequest request,
            CancellationToken cancellationToken)
        {
            LastSendRequest = request;
            return Task.FromResult<ChatbotMessageResponse?>(new ChatbotMessageResponse
            {
                MessageId = Guid.Parse("d127db4e-1106-4106-8f6b-32c6b467e8ad"),
                Content = "Upload the bracket CAD file and I will check geometry, DFM, material, and price gates.",
                Role = "assistant",
                Language = "en",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        public Task<ChatbotConversationMessagesResponse?> GetConversationMessagesAsync(
            Guid sessionId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<ChatbotConversationMessagesResponse?>(null);
        }
    }
}
