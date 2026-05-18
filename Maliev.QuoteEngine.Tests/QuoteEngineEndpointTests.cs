using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Shared.Account;
using Maliev.QuoteEngine.Shared.Chatbot;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteEngineEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task ReferenceData_exposes_customer_visible_processes_and_materials()
    {
        using var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<QuoteReferenceDataResponse>("/quote/v1/reference-data");

        Assert.NotNull(response);
        Assert.Contains(response.Processes, process => process.Id == "fdm");
        Assert.Contains(response.Materials, material => material.ProcessId == "cnc");
        Assert.Contains("step", response.SupportedExtensions);
    }

    [Fact]
    public async Task DemoProject_is_non_mutating_sample_journey()
    {
        using var client = factory.CreateClient();

        var demo = await client.GetFromJsonAsync<QuoteEngineDemoProjectResponse>("/quote/v1/demo/project");

        Assert.NotNull(demo);
        Assert.Contains("sample", demo.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Single(demo.Parts);
        Assert.Contains("does not create", demo.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResumableUpload_allows_anonymous_temporary_workspace_upload()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            QuoteSessionId = "session-unsigned",
            FileName = "part.stl",
            ContentType = "model/stl",
            FileSizeBytes = 12
        });

        response.EnsureSuccessStatusCode();
        var upload = await response.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();
        Assert.NotNull(upload);
        Assert.StartsWith("quotes/temp/session-unsigned/", upload.StoragePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadHandoff_imports_web_uploaded_files_into_active_workspace()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/uploads/handoff", new QuoteUploadHandoffRequest
        {
            QuoteSessionId = "web-session-1",
            Files =
            [
                new QuoteUploadHandoffFileDto
                {
                    UploadId = "web-upload-1",
                    FileId = Guid.NewGuid(),
                    FileName = "web-dropped-part.step",
                    StoragePath = "quotes/temp/web-session-1/123/web-dropped-part.step",
                    ContentType = "application/step",
                    FileSizeBytes = 420_000,
                    Status = "Completed"
                }
            ]
        });

        response.EnsureSuccessStatusCode();
        var handoff = await response.Content.ReadFromJsonAsync<QuoteUploadHandoffResponse>();
        Assert.NotNull(handoff);
        Assert.Equal("web-session-1", handoff.QuoteSessionId);
        var part = Assert.Single(handoff.Parts);
        Assert.Equal("web-upload-1", part.UploadId);
        Assert.Equal("Analyzed", part.Status);
        Assert.True(part.VolumeCc > 0);
    }

    [Fact]
    public async Task ResumableUpload_requires_content_range_and_returns_analysis_metrics()
    {
        using var client = await CreateSignedInClientAsync();
        var initiation = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            QuoteSessionId = "session-1",
            FileName = "part.stl",
            ContentType = "model/stl",
            FileSizeBytes = 12
        });
        initiation.EnsureSuccessStatusCode();
        var upload = await initiation.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();
        Assert.NotNull(upload);

        using var badContent = new ByteArrayContent([1, 2, 3]);
        var badPut = await client.PutAsync(upload.ProxyUploadUrl, badContent);
        Assert.Equal(HttpStatusCode.BadRequest, badPut.StatusCode);

        using var goodContent = new ByteArrayContent([1, 2, 3, 4]);
        goodContent.Headers.ContentType = MediaTypeHeaderValue.Parse("model/stl");
        goodContent.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 4);
        var goodPut = await client.PutAsync(upload.ProxyUploadUrl, goodContent);
        Assert.Equal(HttpStatusCode.NoContent, goodPut.StatusCode);

        var complete = await client.PostAsJsonAsync($"/quote/v1/uploads/resumable/{upload.UploadId}/complete", new { });
        complete.EnsureSuccessStatusCode();

        var status = await client.GetFromJsonAsync<QuoteAnalysisStatusResponse>($"/quote/v1/uploads/{upload.UploadId}/analysis-status");
        Assert.NotNull(status);
        Assert.Equal("Analyzed", status.Status);
        Assert.True(status.VolumeCc > 0);
        Assert.NotEmpty(status.Findings);
    }

    [Fact]
    public async Task Estimate_uses_part_geometry_and_requires_sign_in_for_formal_quote()
    {
        using var client = factory.CreateClient();
        var estimate = await client.PostAsJsonAsync("/quote/v1/estimate", new QuoteEstimateRequest
        {
            QuoteSessionId = "session-2",
            LeadTimeCode = "STANDARD",
            Parts =
            [
                new QuotePartDraftDto
                {
                    FileId = Guid.NewGuid(),
                    UploadId = "upload-1",
                    FileName = "bracket.step",
                    ProcessId = "cnc",
                    MaterialId = "al6061",
                    Quantity = 2,
                    VolumeCc = 8.5m,
                    DfmAcknowledged = true
                }
            ]
        });
        estimate.EnsureSuccessStatusCode();

        var body = await estimate.Content.ReadFromJsonAsync<QuoteEstimateResponse>();
        Assert.NotNull(body);
        Assert.True(body.Total > 0);
        Assert.True(body.RequiresSignIn);
        Assert.Single(body.Lines);
    }

    [Fact]
    public async Task Account_profile_is_resolved_server_side_from_session_boundary()
    {
        using var client = await CreateSignedInClientAsync("profile-owner@example.com");

        var profile = await client.GetFromJsonAsync<CustomerProfileResponse>("/quote/v1/account/profile");

        Assert.NotNull(profile);
        Assert.NotEqual(Guid.Empty, profile.CustomerId);
        Assert.Equal("profile-owner@example.com", profile.Email);
    }

    [Fact]
    public async Task Account_quote_and_order_history_is_scoped_to_signed_in_customer()
    {
        using var customerA = await CreateSignedInClientAsync("quote-owner-a@example.com");
        var quoteResponse = await customerA.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-a", [], "Customer A quote."));
        quoteResponse.EnsureSuccessStatusCode();
        var quote = await quoteResponse.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var orderResponse = await customerA.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, string.Empty, "Customer A accepted."));
        orderResponse.EnsureSuccessStatusCode();

        var customerAQuotes = await customerA.GetFromJsonAsync<CustomerQuoteSummaryDto[]>("/quote/v1/account/quotes");
        var customerAOrders = await customerA.GetFromJsonAsync<CustomerOrderSummaryDto[]>("/quote/v1/account/orders");
        Assert.NotNull(customerAQuotes);
        Assert.NotNull(customerAOrders);
        Assert.Contains(customerAQuotes, item => item.QuoteId == quote.QuoteId);
        Assert.Single(customerAOrders);

        using var customerB = await CreateSignedInClientAsync("quote-owner-b@example.com");
        var customerBQuotes = await customerB.GetFromJsonAsync<CustomerQuoteSummaryDto[]>("/quote/v1/account/quotes");
        var customerBOrders = await customerB.GetFromJsonAsync<CustomerOrderSummaryDto[]>("/quote/v1/account/orders");
        Assert.NotNull(customerBQuotes);
        Assert.NotNull(customerBOrders);
        Assert.Empty(customerBQuotes);
        Assert.Empty(customerBOrders);

        var crossCustomerOrder = await customerB.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, string.Empty, "Cross-customer attempt."));
        Assert.Equal(HttpStatusCode.NotFound, crossCustomerOrder.StatusCode);
    }

    [Fact]
    public async Task Chatbot_message_routes_through_quote_boundary()
    {
        using var chatbotFactory = CreateChatbotFactory();
        using var client = chatbotFactory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/chatbot/messages", new CustomerChatbotRequest
        {
            Message = "Can you help with CNC aluminum fixtures?",
            Language = "en"
        });
        var body = await response.Content.ReadFromJsonAsync<CustomerChatbotResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("assistant", body.Role);
        Assert.NotEqual(Guid.Empty, body.SessionId);
    }

    [Fact]
    public async Task Chatbot_session_endpoint_reports_quote_auth_state()
    {
        using var client = await CreateSignedInClientAsync("quote-chat@example.com");

        var session = await client.GetFromJsonAsync<CustomerChatbotSessionResponse>("/quote/v1/chatbot/session");

        Assert.NotNull(session);
        Assert.True(session.IsAuthenticated);
        Assert.Equal("quote-chat@example.com", session.Email);
        Assert.NotNull(session.CustomerId);
    }

    [Fact]
    public async Task Chatbot_hydrate_validates_shared_session_request()
    {
        using var client = factory.CreateClient();
        var knownSessionId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync("/quote/v1/chatbot/hydrate", new CustomerChatbotHydrateRequest
        {
            SessionId = knownSessionId
        });
        var body = await response.Content.ReadFromJsonAsync<CustomerChatbotHydrateResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(knownSessionId, body.SessionId);
        Assert.False(body.Hydrated);
        Assert.NotNull(body.ContinuationMessage);
    }

    private async Task<HttpClient> CreateSignedInClientAsync(string email = "customer@example.com")
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var signIn = await client.PostAsJsonAsync("/quote/v1/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = "PrototypeOnly123!"
        });
        signIn.EnsureSuccessStatusCode();
        return client;
    }

    private WebApplicationFactory<Program> CreateChatbotFactory()
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddScoped<IChatbotServiceClient, FakeChatbotServiceClient>();
            });
        });
    }

    private sealed class FakeChatbotServiceClient : IChatbotServiceClient
    {
        private static readonly Guid SessionId = Guid.Parse("50d1d515-4c9b-4de2-ad10-840a19f4f64a");

        public Task<ChatbotSessionResponse?> InitiateSessionAsync(ChatbotInitiateSessionRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult<ChatbotSessionResponse?>(new ChatbotSessionResponse
            {
                SessionId = SessionId,
                Language = request.Language,
                WelcomeMessage = "Mali is ready.",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });
        }

        public Task<ChatbotMessageResponse?> SendMessageAsync(ChatbotSendMessageRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult<ChatbotMessageResponse?>(new ChatbotMessageResponse
            {
                MessageId = Guid.Parse("20c5a8da-7a10-46da-bcf3-83f757987846"),
                Content = "Mali can help with CNC aluminum fixture quotes in Quote Engine.",
                Role = "assistant",
                Language = "en",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        public Task<ChatbotConversationMessagesResponse?> GetConversationMessagesAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            return Task.FromResult<ChatbotConversationMessagesResponse?>(new ChatbotConversationMessagesResponse
            {
                SessionId = sessionId,
                Language = "en",
                Messages =
                [
                    new ChatbotConversationMessageResponse
                    {
                        Role = "assistant",
                        Content = "Hydrated assistant message.",
                        CreatedAt = DateTimeOffset.UtcNow
                    }
                ]
            });
        }
    }
}
