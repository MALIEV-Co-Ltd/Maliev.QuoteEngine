using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Controllers;
using Maliev.QuoteEngine.Bff.Security;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Agent;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maliev.QuoteEngine.Tests;

[Collection(QuoteEngineEndpointTestCollection.Name)]
public sealed class QuoteAgentSessionAuthorizationTests(QuoteEngineWebApplicationFactory factory)
    : IClassFixture<QuoteEngineWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static TheoryData<string, QuoteAgentSessionAccessMode> SessionRouteAccessModes => new()
    {
        { nameof(AgentController.Send), QuoteAgentSessionAccessMode.CreateOrResume },
        { nameof(AgentController.Stream), QuoteAgentSessionAccessMode.CreateOrResume },
        { nameof(AgentController.BootstrapSession), QuoteAgentSessionAccessMode.CreateOrResume },
        { nameof(AgentController.GetState), QuoteAgentSessionAccessMode.Existing },
        { nameof(AgentController.GetMessages), QuoteAgentSessionAccessMode.Existing },
        { nameof(AgentController.GetConnectors), QuoteAgentSessionAccessMode.Existing },
        { nameof(AgentController.GetConnectorHandoff), QuoteAgentSessionAccessMode.Existing },
        { nameof(AgentController.RegisterAttachments), QuoteAgentSessionAccessMode.Existing },
        { nameof(AgentController.SubmitLocalDfm), QuoteAgentSessionAccessMode.Existing },
        { nameof(AgentController.RecordPreviewFeedback), QuoteAgentSessionAccessMode.Existing },
        { nameof(AgentController.RecordPreviewBuild), QuoteAgentSessionAccessMode.Existing },
        { nameof(AgentController.SearchCustomerData), QuoteAgentSessionAccessMode.Existing },
        { nameof(AgentController.UploadSketch), QuoteAgentSessionAccessMode.CreateOrResume },
        { nameof(AgentController.DownloadArtifact), QuoteAgentSessionAccessMode.Existing },
        { nameof(AgentController.ExportChatPdf), QuoteAgentSessionAccessMode.Existing }
    };

    [Theory]
    [MemberData(nameof(SessionRouteAccessModes))]
    public void Every_browser_session_route_declares_central_access_metadata(
        string methodName,
        QuoteAgentSessionAccessMode expectedMode)
    {
        var method = Assert.Single(
            typeof(AgentController).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            candidate => candidate.Name == methodName);
        var attribute = Assert.Single(method.GetCustomAttributes<RequireQuoteAgentSessionAccessAttribute>());

        Assert.Equal(expectedMode, attribute.Mode);
    }

    [Fact]
    public void Every_session_bearing_browser_route_is_listed_in_the_central_access_manifest()
    {
        var declaredMethods = SessionRouteAccessModes
            .Select(row => Assert.IsType<string>(row[0]))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var discoveredMethods = typeof(AgentController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.Name != nameof(AgentController.RelayThinkingStep))
            .Where(method =>
                method.GetCustomAttributes<HttpMethodAttribute>().Any(attribute =>
                    attribute.Template?.Contains("sessions/{sessionId", StringComparison.OrdinalIgnoreCase) == true) ||
                method.GetParameters().Any(parameter =>
                    parameter.ParameterType == typeof(QuoteAgentMessageRequest) ||
                    parameter.ParameterType == typeof(QuoteAgentExportPdfRequest)))
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declaredMethods, discoveredMethods);
    }

    [Theory]
    [InlineData(nameof(AgentController.Send))]
    [InlineData(nameof(AgentController.Stream))]
    [InlineData(nameof(AgentController.RegisterAttachments))]
    [InlineData(nameof(AgentController.SubmitLocalDfm))]
    public void Public_agent_json_routes_have_a_bounded_request_body(string methodName)
    {
        var method = Assert.Single(
            typeof(AgentController).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            candidate => candidate.Name == methodName);
        var limit = Assert.Single(method.GetCustomAttributes<RequestSizeLimitAttribute>());
        var metadata = Assert.IsAssignableFrom<IRequestSizeLimitMetadata>(limit);

        Assert.InRange(metadata.MaxRequestBodySize ?? 0, 1, 512_000);
    }

    [Fact]
    public async Task Connector_reads_do_not_create_a_durable_session_owner()
    {
        await using var scopedFactory = CreateFactory();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = Guid.NewGuid();

        var response = await client.GetAsync($"/quote/v1/agent/sessions/{sessionId:D}/connectors");
        var owner = await scopedFactory.Services
            .GetRequiredService<IQuoteAgentSessionOwnerStore>()
            .GetAsync(sessionId, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Null(owner);
    }

    [Fact]
    public async Task Session_bootstrap_allows_connector_discovery_in_a_pristine_workspace()
    {
        await using var scopedFactory = CreateFactory();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = Guid.NewGuid();

        var bootstrap = await client.PostAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/bootstrap",
            content: null);
        var connectors = await client.GetAsync($"/quote/v1/agent/sessions/{sessionId:D}/connectors");
        var owner = await scopedFactory.Services
            .GetRequiredService<IQuoteAgentSessionOwnerStore>()
            .GetAsync(sessionId, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, bootstrap.StatusCode);
        Assert.Equal(HttpStatusCode.OK, connectors.StatusCode);
        Assert.NotNull(owner);
    }

    [Fact]
    public async Task Message_history_without_a_bound_chatbot_conversation_returns_not_found_without_downstream_access()
    {
        var chatbot = new StubChatbotServiceClient();
        await using var scopedFactory = CreateFactory(chatbot);
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = Guid.NewGuid();
        await EnsureSessionOwnerViaUploadAsync(client, sessionId);

        var response = await client.GetAsync($"/quote/v1/agent/sessions/{sessionId:D}/messages");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, chatbot.HistoryRequestCount);
    }

    [Fact]
    public async Task Empty_attachment_registration_does_not_create_a_durable_session_owner()
    {
        await using var scopedFactory = CreateFactory();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/attachments",
            new QuoteAgentAttachmentRegisterRequest { Attachments = [] });
        var owner = await scopedFactory.Services
            .GetRequiredService<IQuoteAgentSessionOwnerStore>()
            .GetAsync(sessionId, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Null(owner);
    }

    [Fact]
    public async Task Foreign_visitor_cannot_reuse_an_owned_session_id_for_upload_initiation()
    {
        await using var scopedFactory = CreateFactory();
        using var owner = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var otherVisitor = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = Guid.NewGuid();
        await EnsureSessionOwnerViaUploadAsync(owner, sessionId);
        Assert.Equal(HttpStatusCode.OK, (await otherVisitor.GetAsync("/quote/v1/agent/health")).StatusCode);

        var response = await otherVisitor.PostAsJsonAsync(
            "/quote/v1/uploads/resumable",
            new InitiateQuoteUploadRequest
            {
                FileName = "foreign-claim.step",
                ContentType = "application/step",
                FileSizeBytes = 1,
                QuoteSessionId = sessionId.ToString("D")
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Existing_session_rejects_an_empty_attachment_registration()
    {
        await using var scopedFactory = CreateFactory();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = await StartSessionAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/attachments",
            new QuoteAgentAttachmentRegisterRequest { Attachments = [] });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/quote/v1/agent/messages")]
    [InlineData("/quote/v1/agent/messages/stream")]
    public async Task Agent_message_rejects_more_than_twenty_attachments_before_downstream_call(
        string route)
    {
        var chatbot = new StubChatbotServiceClient();
        await using var scopedFactory = CreateFactory(chatbot);
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = Guid.NewGuid();
        await EnsureSessionOwnerViaUploadAsync(client, sessionId);
        var attachments = Enumerable.Range(1, 21)
            .Select(index => new QuoteAgentAttachmentDto
            {
                FileName = $"attachment-{index}.step",
                ContentType = "application/step",
                FileSizeBytes = 1,
                Kind = "cad"
            })
            .ToList();

        var response = await client.PostAsJsonAsync(route, new QuoteAgentMessageRequest
        {
            SessionId = sessionId,
            Message = "Quote these files.",
            Language = "en",
            Attachments = attachments
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, chatbot.SendRequestCount);
    }

    [Fact]
    public async Task Missing_owner_cannot_be_recreated_when_local_session_state_survives()
    {
        await using var scopedFactory = CreateFactory();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = Guid.NewGuid();
        _ = scopedFactory.Services.GetRequiredService<QuoteAgentSessionStore>().GetOrCreate(sessionId);

        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = sessionId,
            Message = "Attempt to claim surviving local state.",
            Language = "en"
        });
        var owner = await scopedFactory.Services
            .GetRequiredService<IQuoteAgentSessionOwnerStore>()
            .GetAsync(sessionId, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Null(owner);
    }

    [Fact]
    public async Task Missing_owner_cannot_be_recreated_when_durable_conversation_mapping_survives()
    {
        await using var scopedFactory = CreateFactory();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = Guid.NewGuid();
        await scopedFactory.Services.GetRequiredService<IQuoteAgentConversationMap>().StoreMappingAsync(
            sessionId,
            Guid.NewGuid(),
            customerId: null,
            CancellationToken.None);

        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = sessionId,
            Message = "Attempt to claim a surviving conversation.",
            Language = "en"
        });
        var owner = await scopedFactory.Services
            .GetRequiredService<IQuoteAgentSessionOwnerStore>()
            .GetAsync(sessionId, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Null(owner);
    }

    [Fact]
    public async Task Unknown_action_confirmation_does_not_allocate_a_permanent_action_lock()
    {
        await using var scopedFactory = CreateFactory();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var store = scopedFactory.Services.GetRequiredService<QuoteAgentSessionStore>();
        var lockField = typeof(QuoteAgentSessionStore).GetField(
            "_actionLocks",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(lockField);
        var locks = Assert.IsAssignableFrom<object>(lockField.GetValue(store));
        var countProperty = locks.GetType().GetProperty("Count");
        Assert.NotNull(countProperty);
        var before = Assert.IsType<int>(countProperty.GetValue(locks));

        var response = await client.PostAsJsonAsync(
            $"/quote/v1/agent/actions/{Guid.NewGuid():D}/confirm",
            new QuoteAgentConfirmActionRequest());
        var after = Assert.IsType<int>(countProperty.GetValue(locks));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Attachment_registration_rejects_a_completed_upload_bound_to_another_session()
    {
        await using var scopedFactory = CreateFactory();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var ownerSessionId = await StartSessionAsync(client);
        var targetSessionId = Guid.NewGuid();
        await EnsureSessionOwnerViaUploadAsync(client, targetSessionId);
        var owner = await scopedFactory.Services
            .GetRequiredService<IQuoteAgentSessionOwnerStore>()
            .GetAsync(ownerSessionId, CancellationToken.None);
        Assert.NotNull(owner);
        const string storagePath = "quotes/temp/owner-session/part.step";
        scopedFactory.Services.GetRequiredService<QuoteEnginePrototypeStore>().TrackAgentUpload(
            "cross-session-upload",
            Guid.NewGuid(),
            "part.step",
            "model/step",
            512,
            storagePath,
            ownerSessionId,
            owner.CustomerId,
            owner.VisitorId);

        var response = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{targetSessionId:D}/attachments",
            new QuoteAgentAttachmentRegisterRequest
            {
                Attachments =
                [
                    new QuoteAgentAttachmentDto
                    {
                        UploadId = "cross-session-upload",
                        StoragePath = storagePath,
                        FileName = "part.step",
                        ContentType = "model/step",
                        FileSizeBytes = 512,
                        Kind = "cad"
                    }
                ]
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Attachment_registration_rejects_an_upload_that_has_not_received_its_bytes()
    {
        await using var scopedFactory = CreateFactory();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = await StartSessionAsync(client);
        var owner = await scopedFactory.Services
            .GetRequiredService<IQuoteAgentSessionOwnerStore>()
            .GetAsync(sessionId, CancellationToken.None);
        Assert.NotNull(owner);
        var upload = scopedFactory.Services.GetRequiredService<QuoteEnginePrototypeStore>().InitiateUpload(
            new InitiateQuoteUploadRequest
            {
                FileName = "pending.step",
                ContentType = "model/step",
                FileSizeBytes = 512,
                QuoteSessionId = sessionId.ToString("N")
            },
            owner.CustomerId,
            owner.VisitorId);

        var response = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/attachments",
            new QuoteAgentAttachmentRegisterRequest
            {
                Attachments =
                [
                    new QuoteAgentAttachmentDto
                    {
                        UploadId = upload.UploadId,
                        StoragePath = upload.StoragePath,
                        FileName = upload.FileName,
                        ContentType = upload.ContentType,
                        FileSizeBytes = upload.ExpectedSizeBytes,
                        Kind = "cad"
                    }
                ]
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Anonymous_session_state_requires_the_owning_signed_visitor()
    {
        await using var scopedFactory = CreateFactory();
        using var owner = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var otherVisitor = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var sessionId = await StartSessionAsync(owner);
        var ownerRead = await owner.GetAsync($"/quote/v1/agent/sessions/{sessionId:D}");

        // Prime an independent, valid signed visitor cookie before attempting the foreign read.
        Assert.Equal(HttpStatusCode.OK, (await otherVisitor.GetAsync("/quote/v1/agent/health")).StatusCode);
        var foreignRead = await otherVisitor.GetAsync($"/quote/v1/agent/sessions/{sessionId:D}");

        Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        Assert.Empty(await foreignRead.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Customer_session_state_returns_empty_not_found_for_another_customer()
    {
        await using var scopedFactory = CreateFactory();
        using var owner = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var otherCustomer = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/test/sign-in?email=owner@example.com")).StatusCode);
        var sessionId = await StartSessionAsync(owner);
        Assert.Equal(HttpStatusCode.OK, (await otherCustomer.GetAsync("/test/sign-in?email=other@example.com")).StatusCode);

        var foreignRead = await otherCustomer.GetAsync($"/quote/v1/agent/sessions/{sessionId:D}");

        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        Assert.Empty(await foreignRead.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Session_state_rejects_a_tampered_visitor_cookie()
    {
        await using var scopedFactory = CreateFactory();
        using var owner = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var attacker = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var sessionId = await StartSessionAsync(owner);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/quote/v1/agent/sessions/{sessionId:D}");
        request.Headers.TryAddWithoutValidation("Cookie", $"{AnonymousVisitorCookie.CookieName}=tampered.invalid");

        var response = await attacker.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Session_state_rejects_an_expired_signed_visitor_cookie()
    {
        await using var scopedFactory = CreateFactory();
        using var owner = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var attacker = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var sessionId = await StartSessionAsync(owner);
        var now = DateTimeOffset.UtcNow;
        var expiredCookie = CreateSignedVisitorCookie(
            Guid.NewGuid(),
            now.AddDays(-2),
            now.AddDays(-1));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/quote/v1/agent/sessions/{sessionId:D}");
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            $"{AnonymousVisitorCookie.CookieName}={expiredCookie}");

        var response = await attacker.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Thinking_callback_requires_a_signed_server_context_token()
    {
        await using var scopedFactory = CreateFactory();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = await StartSessionAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/thinking",
            new QuoteAgentThinkingStepDto
            {
                StepNumber = 1,
                Type = "reasoning",
                Title = "Unsafe browser callback",
                Detail = "This must not be accepted from the browser."
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Foreign_visitor_is_denied_before_every_browser_session_action()
    {
        await using var scopedFactory = CreateFactory();
        using var owner = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var otherVisitor = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = await StartSessionAsync(owner);
        Assert.Equal(HttpStatusCode.OK, (await otherVisitor.GetAsync("/quote/v1/agent/health")).StatusCode);

        var requests = new List<(string Name, HttpRequestMessage Request)>
        {
            ("message", JsonRequest(HttpMethod.Post, "/quote/v1/agent/messages", new
            {
                sessionId,
                message = "Attempt to take over the session.",
                language = "en"
            })),
            ("stream", JsonRequest(HttpMethod.Post, "/quote/v1/agent/messages/stream", new
            {
                sessionId,
                message = "Attempt to stream into the session.",
                language = "en"
            })),
            ("state", new HttpRequestMessage(HttpMethod.Get, $"/quote/v1/agent/sessions/{sessionId:D}")),
            ("messages", new HttpRequestMessage(HttpMethod.Get, $"/quote/v1/agent/sessions/{sessionId:D}/messages")),
            ("connectors", new HttpRequestMessage(HttpMethod.Get, $"/quote/v1/agent/sessions/{sessionId:D}/connectors")),
            ("handoff", new HttpRequestMessage(HttpMethod.Get,
                $"/quote/v1/agent/sessions/{sessionId:D}/connectors/google-drive/handoff")),
            ("attachments", JsonRequest(HttpMethod.Post,
                $"/quote/v1/agent/sessions/{sessionId:D}/attachments", new { attachments = Array.Empty<object>() })),
            ("dfm", JsonRequest(HttpMethod.Post, $"/quote/v1/agent/sessions/{sessionId:D}/dfm", new { })),
            ("feedback", JsonRequest(HttpMethod.Post,
                $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/{Guid.NewGuid():D}/feedback",
                new { sentiment = "up" })),
            ("preview-build", JsonRequest(HttpMethod.Post,
                $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/{Guid.NewGuid():D}/preview-build",
                new { success = true })),
            ("search", new HttpRequestMessage(HttpMethod.Get,
                $"/quote/v1/agent/sessions/{sessionId:D}/search?query=quotes")),
            ("sketch", new HttpRequestMessage(HttpMethod.Post,
                $"/quote/v1/agent/sessions/{sessionId:D}/sketches")
            {
                Content = new MultipartFormDataContent()
            }),
            ("download", new HttpRequestMessage(HttpMethod.Get,
                $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/download?path=quotes%2Ftemp%2Ffile.glb")),
            ("export", JsonRequest(HttpMethod.Post, "/quote/v1/agent/export-pdf", new
            {
                sessionId,
                language = "en"
            }))
        };

        foreach (var (name, request) in requests)
        {
            using (request)
            using (var response = await otherVisitor.SendAsync(request))
            {
                Assert.True(
                    response.StatusCode == HttpStatusCode.NotFound,
                    $"{name} returned {(int)response.StatusCode} {response.StatusCode} instead of an empty 404.");
                Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            }
        }
    }

    [Fact]
    public async Task Anonymous_session_is_promoted_only_after_the_same_visitor_signs_in()
    {
        await using var scopedFactory = CreateFactory();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = await StartSessionAsync(client);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/test/sign-in?email=promoted@example.com")).StatusCode);
        var response = await client.GetAsync($"/quote/v1/agent/sessions/{sessionId:D}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Promoted_customer_can_reuse_pre_sign_in_uploads_from_a_new_browser()
    {
        await using var scopedFactory = CreateFactory();
        using var originalVisitor = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var sameCustomer = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var foreignCustomer = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = Guid.NewGuid();
        using var initiatedResponse = await originalVisitor.PostAsJsonAsync(
            "/quote/v1/uploads/resumable",
            new InitiateQuoteUploadRequest
            {
                FileName = "before-sign-in.step",
                ContentType = "application/step",
                FileSizeBytes = 1,
                QuoteSessionId = sessionId.ToString("D")
            });
        var initiated = await initiatedResponse.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();
        Assert.Equal(HttpStatusCode.OK, initiatedResponse.StatusCode);
        Assert.NotNull(initiated);
        using var uploadRequest = new HttpRequestMessage(HttpMethod.Put, initiated.ProxyUploadUrl)
        {
            Content = new ByteArrayContent([42])
        };
        uploadRequest.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 0-0/1");
        using var uploaded = await originalVisitor.SendAsync(uploadRequest);
        Assert.True(uploaded.StatusCode is HttpStatusCode.NoContent or (HttpStatusCode)308);

        Assert.Equal(
            HttpStatusCode.OK,
            (await originalVisitor.GetAsync("/test/sign-in?email=reopen@example.com")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await originalVisitor.GetAsync($"/quote/v1/agent/sessions/{sessionId:D}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await sameCustomer.GetAsync("/test/sign-in?email=reopen@example.com")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await foreignCustomer.GetAsync("/test/sign-in?email=foreign@example.com")).StatusCode);

        using var registered = await sameCustomer.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/attachments",
            new QuoteAgentAttachmentRegisterRequest
            {
                Attachments =
                [
                    new QuoteAgentAttachmentDto
                    {
                        FileName = "before-sign-in.step",
                        ContentType = "application/step",
                        FileSizeBytes = 1,
                        UploadId = initiated.UploadId,
                        StoragePath = initiated.StoragePath,
                        Kind = "cad"
                    }
                ]
            });
        using var foreignRead = await foreignCustomer.GetAsync(
            $"/quote/v1/uploads/{Uri.EscapeDataString(initiated.UploadId)}/analysis-status");

        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        Assert.Empty(await foreignRead.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_different_signed_in_visitor_cannot_promote_an_anonymous_session()
    {
        await using var scopedFactory = CreateFactory();
        using var owner = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var otherVisitor = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = await StartSessionAsync(owner);
        Assert.Equal(
            HttpStatusCode.OK,
            (await otherVisitor.GetAsync("/test/sign-in?email=foreign-promotion@example.com")).StatusCode);

        var response = await otherVisitor.GetAsync($"/quote/v1/agent/sessions/{sessionId:D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Promoted_session_refreshes_the_conversation_mapping_for_tools_and_thinking_callbacks()
    {
        var chatbot = new StubChatbotServiceClient();
        await using var scopedFactory = CreateFactory(chatbot);
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = await StartSessionAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/test/sign-in?email=next-turn@example.com")).StatusCode);
        var nextTurn = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = sessionId,
            Message = "Continue after sign-in.",
            Language = "en"
        });
        Assert.Equal(HttpStatusCode.OK, nextTurn.StatusCode);
        var promotedToken = Assert.IsType<string>(chatbot.LastSendRequest?.QuoteAgentContextToken);

        using var thinkingRequest = JsonRequest(
            HttpMethod.Post,
            $"/quote/v1/agent/sessions/{sessionId:D}/thinking",
            new QuoteAgentThinkingStepDto
            {
                StepNumber = 1,
                Type = "reasoning",
                Title = "Promoted callback",
                Detail = "The signed-in turn retained the active conversation."
            });
        thinkingRequest.Headers.TryAddWithoutValidation("X-Maliev-Agent-Context", promotedToken);
        var thinkingResponse = await client.SendAsync(thinkingRequest);
        using var toolRequest = JsonRequest(
            HttpMethod.Post,
            "/quote/v1/agent/tools/quote_get_state",
            new QuoteAgentToolRequest());
        toolRequest.Headers.TryAddWithoutValidation("X-Maliev-Agent-Context", promotedToken);
        var toolResponse = await client.SendAsync(toolRequest);

        Assert.Equal(HttpStatusCode.Accepted, thinkingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, toolResponse.StatusCode);
    }

    [Fact]
    public async Task Concurrent_first_turns_converge_on_one_durable_chatbot_conversation()
    {
        var chatbot = new StubChatbotServiceClient(synchronizeFirstTwoInitializations: true);
        await using var scopedFactory = CreateFactory(chatbot);
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/quote/v1/agent/health")).StatusCode);
        var sessionId = Guid.NewGuid();
        var first = client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = sessionId,
            Message = "First concurrent turn.",
            Language = "en"
        });
        var second = client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = sessionId,
            Message = "Second concurrent turn.",
            Language = "en"
        });

        var responses = await Task.WhenAll(first, second);
        var mapping = await scopedFactory.Services.GetRequiredService<IQuoteAgentConversationMap>()
            .GetMappingAsync(sessionId, CancellationToken.None);
        Assert.True(scopedFactory.Services.GetRequiredService<QuoteAgentSessionStore>()
            .TryGet(sessionId, out var state));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.NotNull(mapping);
        Assert.Equal(mapping.ChatbotSessionId, state.ChatbotSessionId);

        var followUp = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = sessionId,
            Message = "Continue on the durable winner.",
            Language = "en"
        });
        Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
    }

    [Fact]
    public async Task Internal_register_upload_tool_uses_the_owner_bound_by_signed_server_context()
    {
        var chatbot = new StubChatbotServiceClient();
        await using var scopedFactory = CreateFactory(chatbot);
        using var browser = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = await StartSessionAsync(browser);
        var owner = await scopedFactory.Services.GetRequiredService<IQuoteAgentSessionOwnerStore>()
            .GetAsync(sessionId, CancellationToken.None);
        Assert.NotNull(owner);
        const string uploadId = "internal-tool-upload";
        const string storagePath = "quotes/temp/internal-tool/session-part.step";
        scopedFactory.Services.GetRequiredService<QuoteEnginePrototypeStore>().TrackAgentUpload(
            uploadId,
            Guid.NewGuid(),
            "session-part.step",
            "application/step",
            512,
            storagePath,
            sessionId,
            owner.CustomerId,
            owner.VisitorId);
        var contextToken = Assert.IsType<string>(chatbot.LastSendRequest?.QuoteAgentContextToken);
        using var internalClient = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = JsonRequest(
            HttpMethod.Post,
            "/quote/v1/agent/tools/quote_register_uploads",
            new QuoteAgentToolRequest
            {
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["files"] = JsonSerializer.SerializeToElement(new[]
                    {
                        new
                        {
                            file_name = "session-part.step",
                            content_type = "application/step",
                            file_size_bytes = 512,
                            kind = "cad",
                            upload_id = uploadId,
                            storage_path = storagePath
                        }
                    }, JsonOptions)
                }
            });
        request.Headers.TryAddWithoutValidation("X-Maliev-Agent-Context", contextToken);

        var response = await internalClient.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body.RootElement.TryGetProperty("error", out _));
        Assert.Single(body.RootElement.GetProperty("attachments").EnumerateArray());
    }

    [Fact]
    public async Task Thinking_callback_accepts_the_matching_server_context_token()
    {
        var chatbot = new StubChatbotServiceClient();
        await using var scopedFactory = CreateFactory(chatbot);
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = await StartSessionAsync(client);
        var token = Assert.IsType<string>(chatbot.LastSendRequest?.QuoteAgentContextToken);
        using var request = JsonRequest(
            HttpMethod.Post,
            $"/quote/v1/agent/sessions/{sessionId:D}/thinking",
            new QuoteAgentThinkingStepDto
            {
                StepNumber = 1,
                Type = "reasoning",
                Title = "Validated callback",
                Detail = "This came from the trusted agent harness."
            });
        request.Headers.TryAddWithoutValidation("X-Maliev-Agent-Context", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_action_and_completed_replay_require_the_owning_visitor()
    {
        await using var scopedFactory = CreateFactory();
        using var owner = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var otherVisitor = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = await StartSessionAsync(owner);
        Assert.Equal(HttpStatusCode.OK, (await otherVisitor.GetAsync("/quote/v1/agent/health")).StatusCode);
        var store = scopedFactory.Services.GetRequiredService<QuoteAgentSessionStore>();
        Assert.True(store.TryGet(sessionId, out var state));
        var action = store.AddAction(
            state,
            "test_owner_action",
            "Owner-only action",
            "This action must remain bound to the visitor that created the session.",
            requiresAuthentication: false,
            new Dictionary<string, JsonElement>());

        var foreignAttempt = await otherVisitor.PostAsJsonAsync(
            $"/quote/v1/agent/actions/{action.ActionId:D}/confirm",
            new QuoteAgentConfirmActionRequest());

        Assert.Equal(HttpStatusCode.NotFound, foreignAttempt.StatusCode);
        Assert.True(store.TryGetAction(action.ActionId, out _));

        var ownerResult = await owner.PostAsJsonAsync(
            $"/quote/v1/agent/actions/{action.ActionId:D}/confirm",
            new QuoteAgentConfirmActionRequest());
        var foreignReplay = await otherVisitor.PostAsJsonAsync(
            $"/quote/v1/agent/actions/{action.ActionId:D}/confirm",
            new QuoteAgentConfirmActionRequest());

        Assert.Equal(HttpStatusCode.OK, ownerResult.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignReplay.StatusCode);
    }

    [Fact]
    public async Task Local_dfm_rejects_an_unregistered_storage_path_without_writing_analysis_state()
    {
        await using var scopedFactory = CreateFactory();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = await StartSessionAsync(client);
        const string registeredPath = "quotes/temp/owner/registered.stl";
        const string foreignPath = "customers/other/quotes/private-part.stl";
        var ownerStore = scopedFactory.Services.GetRequiredService<IQuoteAgentSessionOwnerStore>();
        var owner = await ownerStore.GetAsync(sessionId, CancellationToken.None);
        Assert.NotNull(owner);
        scopedFactory.Services.GetRequiredService<QuoteEnginePrototypeStore>().TrackAgentUpload(
            "owned-upload",
            Guid.NewGuid(),
            "registered.stl",
            "model/stl",
            100,
            registeredPath,
            sessionId,
            customerId: null,
            visitorId: owner.VisitorId);
        var registration = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/attachments",
            new QuoteAgentAttachmentRegisterRequest
            {
                Message = "Register the owned CAD file.",
                Language = "en",
                Attachments =
                [
                    new QuoteAgentAttachmentDto
                    {
                        FileName = "registered.stl",
                        ContentType = "model/stl",
                        FileSizeBytes = 100,
                        Kind = "cad",
                        UploadId = "owned-upload",
                        StoragePath = registeredPath
                    }
                ]
            });
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);

        var response = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/dfm",
            new QuoteAgentLocalDfmRequest
            {
                StoragePath = foreignPath,
                UploadId = "foreign-upload",
                ProcessCode = "FDM",
                IsManifold = true
            });
        var analysis = scopedFactory.Services.GetRequiredService<IQuoteFileAnalysisStatusService>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Null(await analysis.GetStatusAsync(foreignPath, CancellationToken.None));
    }

    [Fact]
    public async Task Artifact_download_rejects_an_unregistered_legacy_session_prefixed_path()
    {
        await using var scopedFactory = CreateFactory();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });
        var sessionId = await StartSessionAsync(client);
        var forgedPath = $"agent/sketches/{sessionId:N}/not-registered.png";

        var response = await client.GetAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/download?path={Uri.EscapeDataString(forgedPath)}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Browser_cannot_manufacture_file_membership_by_registering_an_arbitrary_storage_path()
    {
        await using var scopedFactory = CreateFactory();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = await StartSessionAsync(client);
        var forgedPath = $"customers/{Guid.NewGuid():D}/quotes/private/victim.stl";

        var registration = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/attachments",
            new QuoteAgentAttachmentRegisterRequest
            {
                Message = "Register a path that was never uploaded by this browser.",
                Attachments =
                [
                    new QuoteAgentAttachmentDto
                    {
                        FileName = "victim.stl",
                        ContentType = "model/stl",
                        FileSizeBytes = 512,
                        Kind = "cad",
                        UploadId = "forged-upload",
                        StoragePath = forgedPath,
                        SatisfiesGeometryGate = true
                    }
                ]
            });
        var download = await client.GetAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/download?path={Uri.EscapeDataString(forgedPath)}");

        Assert.Equal(HttpStatusCode.NotFound, registration.StatusCode);
        Assert.Empty(await registration.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        Assert.Empty(await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Browser_message_cannot_attach_an_arbitrary_storage_path()
    {
        await using var scopedFactory = CreateFactory();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = await StartSessionAsync(client);

        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = sessionId,
            Message = "Read this unverified file.",
            Attachments =
            [
                new QuoteAgentAttachmentDto
                {
                    FileName = "private.pdf",
                    ContentType = "application/pdf",
                    FileSizeBytes = 1024,
                    UploadId = "forged-message-upload",
                    StoragePath = $"customers/{Guid.NewGuid():D}/documents/private.pdf"
                }
            ]
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Internal_tool_context_cannot_claim_a_customer_that_conflicts_with_the_session_owner()
    {
        await using var scopedFactory = CreateFactory();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = await StartSessionAsync(client);
        using var request = JsonRequest(
            HttpMethod.Post,
            "/quote/v1/agent/tools/quote_get_state",
            new QuoteAgentToolRequest());
        request.Headers.TryAddWithoutValidation(
            "X-Maliev-Agent-Context",
            CreateSignedAgentContextToken(sessionId, Guid.NewGuid(), Guid.NewGuid()));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Internal_tool_context_must_match_the_current_chatbot_conversation()
    {
        var chatbot = new StubChatbotServiceClient();
        await using var scopedFactory = CreateFactory(chatbot);
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var sessionId = await StartSessionAsync(client);
        using var request = JsonRequest(
            HttpMethod.Post,
            "/quote/v1/agent/tools/quote_get_state",
            new QuoteAgentToolRequest());
        request.Headers.TryAddWithoutValidation(
            "X-Maliev-Agent-Context",
            CreateSignedAgentContextToken(sessionId, Guid.NewGuid(), null));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    private WebApplicationFactory<Program> CreateFactory() => CreateFactory(new StubChatbotServiceClient());

    private WebApplicationFactory<Program> CreateFactory(StubChatbotServiceClient chatbot) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
            });
        });

    private static HttpRequestMessage JsonRequest(HttpMethod method, string path, object body) =>
        new(method, path) { Content = JsonContent.Create(body) };

    private static string CreateSignedAgentContextToken(
        Guid quoteSessionId,
        Guid chatbotSessionId,
        Guid? customerId)
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
        using var hmac = new HMACSHA256(
            Encoding.UTF8.GetBytes("maliev-local-development-quote-agent-context-key"));
        var signature = Base64UrlTextEncoder.Encode(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(encodedPayload)));
        return $"{encodedPayload}.{signature}";
    }

    private static string CreateSignedVisitorCookie(
        Guid visitorId,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        var payload = new AnonymousVisitorPayload(visitorId, issuedAt, expiresAt);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var encodedPayload = Base64UrlTextEncoder.Encode(Encoding.UTF8.GetBytes(json));
        using var hmac = new HMACSHA256(
            Encoding.UTF8.GetBytes("maliev-local-development-anonymous-visitor-key"));
        var signature = Base64UrlTextEncoder.Encode(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(encodedPayload)));
        return $"{encodedPayload}.{signature}";
    }

    private static async Task<Guid> StartSessionAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            Message = "I need a machined bracket.",
            Language = "en"
        });
        var body = await response.Content.ReadFromJsonAsync<QuoteAgentTurnResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.SessionId);
        return body.SessionId;
    }

    private static async Task EnsureSessionOwnerViaUploadAsync(HttpClient client, Guid sessionId)
    {
        using var response = await client.PostAsJsonAsync(
            "/quote/v1/uploads/resumable",
            new InitiateQuoteUploadRequest
            {
                FileName = "session-owner.step",
                ContentType = "application/step",
                FileSizeBytes = 1,
                QuoteSessionId = sessionId.ToString("D")
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class StubChatbotServiceClient(bool synchronizeFirstTwoInitializations = false) : IChatbotServiceClient
    {
        private readonly TaskCompletionSource<bool> _secondInitializationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _initializationCount;

        public ChatbotSendMessageRequest? LastSendRequest { get; private set; }

        public int SendRequestCount { get; private set; }

        public int HistoryRequestCount { get; private set; }

        public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task<ChatbotSessionResponse?> InitiateSessionAsync(
            ChatbotInitiateSessionRequest request,
            CancellationToken cancellationToken)
        {
            var initializationNumber = Interlocked.Increment(ref _initializationCount);
            if (synchronizeFirstTwoInitializations)
            {
                if (initializationNumber == 1)
                {
                    _ = await Task.WhenAny(
                        _secondInitializationStarted.Task,
                        Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
                }
                else if (initializationNumber == 2)
                {
                    _secondInitializationStarted.TrySetResult(true);
                }
            }

            return new ChatbotSessionResponse
            {
                SessionId = Guid.NewGuid(),
                WelcomeMessage = "Ready.",
                Language = request.Language ?? "en",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
            };
        }

        public Task<ChatbotMessageResponse?> SendMessageAsync(
            ChatbotSendMessageRequest request,
            CancellationToken cancellationToken)
        {
            SendRequestCount++;
            LastSendRequest = request;
            return Task.FromResult<ChatbotMessageResponse?>(new ChatbotMessageResponse
            {
                MessageId = Guid.NewGuid(),
                Content = "Attach the CAD file and I will inspect it.",
                Role = "assistant",
                Language = request.Language ?? "en",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        public async IAsyncEnumerable<ChatbotMessageStreamEvent> SendMessageStreamAsync(
            ChatbotSendMessageRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new ChatbotMessageStreamEvent { Type = "started" };
            yield return new ChatbotMessageStreamEvent
            {
                Type = "final",
                Message = new ChatbotMessageResponse
                {
                    MessageId = Guid.NewGuid(),
                    Content = "Ready.",
                    Role = "assistant",
                    Language = request.Language ?? "en",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            };
        }

        public Task<ChatbotConversationMessagesResponse?> GetConversationMessagesAsync(
            Guid sessionId,
            CancellationToken cancellationToken)
        {
            HistoryRequestCount++;
            return Task.FromResult<ChatbotConversationMessagesResponse?>(null);
        }

        public Task<bool> TruncateLastTurnAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<string?> CleanSpeechAsync(string speech, string language, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(speech);
    }
}
