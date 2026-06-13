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
    public async Task Agent_message_with_supplemental_drawing_keeps_geometry_gate_blocked()
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
            Message = "I need 50 aluminum brackets from this hand sketch.",
            Language = "en",
            Attachments =
            [
                new QuoteAgentAttachmentDto
                {
                    FileName = "bracket-sketch.jpg",
                    ContentType = "image/jpeg",
                    FileSizeBytes = 1_500_000,
                    Kind = "sketch",
                    Url = "https://files.example.test/bracket-sketch.jpg"
                }
            ]
        });
        var body = await response.Content.ReadFromJsonAsync<QuoteAgentTurnResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Contains(body.Gates, gate => gate.Code == "geometry_required" && gate.Status == "blocked");
        Assert.Contains(body.Artifacts, artifact =>
            artifact.ArtifactType == "analysis" &&
            artifact.Status == "needs_geometry" &&
            artifact.Metadata["geometryGate"] == "not_satisfied_by_supplemental_files");

        var state = await client.GetFromJsonAsync<QuoteAgentStateResponse>(
            $"/quote/v1/agent/sessions/{body.SessionId:D}");
        Assert.NotNull(state);
        Assert.Empty(state.Parts);
        Assert.Single(state.Attachments);
    }

    [Fact]
    public async Task Agent_message_with_cad_and_drawing_attaches_supplemental_file_to_part()
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
            Message = "Quote this STEP with the attached drawing for 10 pieces in 6061.",
            Language = "en",
            Attachments =
            [
                new QuoteAgentAttachmentDto
                {
                    FileName = "housing.step",
                    ContentType = "model/step",
                    FileSizeBytes = 250_000,
                    Kind = "cad",
                    StoragePath = "quotes/temp/housing.step"
                },
                new QuoteAgentAttachmentDto
                {
                    FileName = "housing-drawing.pdf",
                    ContentType = "application/pdf",
                    FileSizeBytes = 300_000,
                    Kind = "drawing",
                    StoragePath = "quotes/temp/housing-drawing.pdf"
                }
            ]
        });
        var body = await response.Content.ReadFromJsonAsync<QuoteAgentTurnResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        var state = await client.GetFromJsonAsync<QuoteAgentStateResponse>(
            $"/quote/v1/agent/sessions/{body.SessionId:D}");

        Assert.NotNull(state);
        var part = Assert.Single(state.Parts);
        var drawing = Assert.Single(part.DrawingFiles);
        Assert.Equal("housing-drawing.pdf", drawing.FileName);
        Assert.Contains(body.Artifacts, artifact =>
            artifact.ArtifactType == "analysis" &&
            artifact.Status == "ready" &&
            artifact.Metadata["geometryGate"] == "satisfied_by_cad_attachment");
        Assert.Contains(body.Gates, gate => gate.Code == "priced" && gate.Status == "passed");
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
    public async Task Agent_formal_quote_tool_blocks_until_customer_is_authenticated()
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
        using var document = await JsonDocument.ParseAsync(await prepareResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, prepareResponse.StatusCode);
        Assert.Equal("customer_authenticated", document.RootElement.GetProperty("requiredGateCode").GetString());
        Assert.Equal("formal_quote", document.RootElement.GetProperty("actionType").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("state").GetProperty("proposedActions").GetArrayLength());
    }

    [Fact]
    public async Task Agent_formal_quote_tool_blocks_until_geometry_and_pricing_gates_pass()
    {
        using var client = factory.CreateClient();
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
            CreateSignedAgentContextToken(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        using var prepareResponse = await client.SendAsync(prepareRequest);
        using var document = await JsonDocument.ParseAsync(await prepareResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, prepareResponse.StatusCode);
        Assert.Equal("geometry_required", document.RootElement.GetProperty("requiredGateCode").GetString());
        Assert.Equal("formal_quote", document.RootElement.GetProperty("actionType").GetString());
        Assert.Contains(
            "Upload STEP",
            document.RootElement.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_quote_approval_requires_formal_quote_then_sets_quote_approved_gate()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-approval@example.com");
        var sessionId = await StartPricedCadSessionAsync(client);

        var blocked = await ExecuteToolAsync(client, sessionId, "quote_approve_quote");
        using (var blockedDocument = JsonDocument.Parse(blocked))
        {
            Assert.Equal("quote_artifact_ready", blockedDocument.RootElement.GetProperty("requiredGateCode").GetString());
            Assert.Equal("quote_approval", blockedDocument.RootElement.GetProperty("actionType").GetString());
        }

        var formalQuoteState = await ExecuteToolForStateAsync(client, sessionId, "quote_prepare_formal_quote");
        var formalQuoteAction = Assert.Single(formalQuoteState.ProposedActions);
        Assert.Equal("formal_quote", formalQuoteAction.ActionType);

        var formalQuoteResult = await ConfirmActionAsync(client, formalQuoteAction.ActionId);
        Assert.NotNull(formalQuoteResult.State);
        Assert.Contains(formalQuoteResult.State.Artifacts, artifact => artifact.ArtifactType == "formal_quote");
        Assert.Contains(formalQuoteResult.State.Gates, gate => gate.Code == "quote_artifact_ready" && gate.Status == "passed");
        Assert.Contains(formalQuoteResult.State.Gates, gate => gate.Code == "quote_approved" && gate.Status == "pending");

        var approvalState = await ExecuteToolForStateAsync(client, sessionId, "quote_approve_quote");
        var approvalAction = Assert.Single(approvalState.ProposedActions);
        Assert.Equal("quote_approval", approvalAction.ActionType);

        var approvalResult = await ConfirmActionAsync(client, approvalAction.ActionId);

        Assert.NotNull(approvalResult.State);
        Assert.Contains(approvalResult.State.Gates, gate => gate.Code == "quote_approved" && gate.Status == "passed");
        Assert.Contains(approvalResult.State.Artifacts, artifact =>
            artifact.ArtifactType == "quote_approval" &&
            artifact.Status == "approved");
    }

    [Fact]
    public async Task Agent_payment_confirmation_after_order_sets_payment_gate_and_artifact()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-payment@example.com");
        var sessionId = await StartPricedCadSessionAsync(client);

        var formalQuoteState = await ExecuteToolForStateAsync(client, sessionId, "quote_prepare_formal_quote");
        var formalQuoteAction = Assert.Single(formalQuoteState.ProposedActions);
        await ConfirmActionAsync(client, formalQuoteAction.ActionId);

        var approvalState = await ExecuteToolForStateAsync(client, sessionId, "quote_approve_quote");
        var approvalAction = Assert.Single(approvalState.ProposedActions);
        await ConfirmActionAsync(client, approvalAction.ActionId);

        var orderState = await ExecuteToolForStateAsync(client, sessionId, "quote_create_order");
        var orderAction = Assert.Single(orderState.ProposedActions);
        Assert.Equal("create_order", orderAction.ActionType);
        var orderResult = await ConfirmActionAsync(client, orderAction.ActionId);
        Assert.NotNull(orderResult.State);
        Assert.Contains(orderResult.State.Gates, gate => gate.Code == "order_created" && gate.Status == "passed");

        var paymentState = await ExecuteToolForStateAsync(client, sessionId, "quote_start_payment");
        var paymentAction = Assert.Single(paymentState.ProposedActions);
        Assert.Equal("start_payment", paymentAction.ActionType);

        var paymentResult = await ConfirmActionAsync(client, paymentAction.ActionId);

        Assert.NotNull(paymentResult.State);
        Assert.Contains("Payment handoff", paymentResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(paymentResult.State.Gates, gate => gate.Code == "payment_started_or_completed" && gate.Status == "passed");
        Assert.Contains(paymentResult.State.Artifacts, artifact =>
            artifact.ArtifactType == "payment" &&
            artifact.Status == "pending" &&
            !string.IsNullOrWhiteSpace(artifact.Url));
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

    private static async Task<HttpClient> CreateSignedInClientAsync(
        WebApplicationFactory<Program> scopedFactory,
        string email)
    {
        var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var signIn = await client.GetAsync($"/test/sign-in?email={Uri.EscapeDataString(email)}");
        signIn.EnsureSuccessStatusCode();
        return client;
    }

    private async Task<Guid> StartPricedCadSessionAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            Message = "Quote this STEP as 25 aluminum pieces with standard lead time.",
            Language = "en",
            Attachments =
            [
                new QuoteAgentAttachmentDto
                {
                    FileName = "fixture.step",
                    ContentType = "model/step",
                    FileSizeBytes = 250_000,
                    Kind = "cad",
                    StoragePath = "quotes/temp/fixture.step"
                }
            ]
        });
        var body = await response.Content.ReadFromJsonAsync<QuoteAgentTurnResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Contains(body.Gates, gate => gate.Code == "priced" && gate.Status == "passed");
        return body.SessionId;
    }

    private static async Task<QuoteAgentStateResponse> ExecuteToolForStateAsync(
        HttpClient client,
        Guid sessionId,
        string toolName)
    {
        var json = await ExecuteToolAsync(client, sessionId, toolName);
        var state = JsonSerializer.Deserialize<QuoteAgentStateResponse>(json, JsonOptions);
        Assert.NotNull(state);
        return state;
    }

    private static async Task<string> ExecuteToolAsync(
        HttpClient client,
        Guid sessionId,
        string toolName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/quote/v1/agent/tools/{toolName}")
        {
            Content = JsonContent.Create(new QuoteAgentToolRequest(), options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation(
            "X-Maliev-Agent-Context",
            CreateSignedAgentContextToken(sessionId, Guid.NewGuid(), null));

        using var response = await client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return json;
    }

    private static async Task<QuoteAgentActionResultResponse> ConfirmActionAsync(
        HttpClient client,
        Guid actionId)
    {
        var response = await client.PostAsJsonAsync(
            $"/quote/v1/agent/actions/{actionId:D}/confirm",
            new QuoteAgentConfirmActionRequest
            {
                ConfirmationNote = "Customer confirmed from the quote agent test."
            });
        var body = await response.Content.ReadFromJsonAsync<QuoteAgentActionResultResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        return body;
    }

    private WebApplicationFactory<Program> CreateAgentFactory()
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient, RecordingChatbotServiceClient>();
            });
        });
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
