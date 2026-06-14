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
    private static readonly Guid CheckoutBillingAddressId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid CheckoutShippingAddressId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

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
    public async Task Agent_message_with_cad_attachment_does_not_pre_acknowledge_dfm_review()
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
            Message = "Quote this STEP as 10 pieces in aluminum.",
            Language = "en",
            Attachments =
            [
                new QuoteAgentAttachmentDto
                {
                    FileName = "bracket.step",
                    ContentType = "model/step",
                    FileSizeBytes = 120_000,
                    Kind = "cad",
                    StoragePath = "quotes/temp/bracket.step"
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
        Assert.False(part.DfmAcknowledged);
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
    public async Task Agent_register_uploads_tool_materializes_geometry_and_supplemental_context()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var state = await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_register_uploads",
            new Dictionary<string, JsonElement>
            {
                ["requirements"] = JsonSerializer.SerializeToElement("Quote this CNC housing as 10 aluminum pieces.", JsonOptions),
                ["files"] = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        file_name = "housing.step",
                        content_type = "model/step",
                        file_size_bytes = 240_000,
                        kind = "cad",
                        upload_id = "agent-upload-cad",
                        storage_path = "quotes/temp/session/agent-upload-cad/housing.step"
                    },
                    new
                    {
                        file_name = "housing-drawing.pdf",
                        content_type = "application/pdf",
                        file_size_bytes = 88_000,
                        kind = "drawing",
                        upload_id = "agent-upload-drawing",
                        storage_path = "quotes/temp/session/agent-upload-drawing/housing-drawing.pdf"
                    }
                }, JsonOptions)
            });

        Assert.Equal(sessionId, state.SessionId);
        Assert.Equal(2, state.Attachments.Count);
        var part = Assert.Single(state.Parts);
        Assert.Equal("housing.step", part.FileName);
        Assert.Equal("agent-upload-cad", part.UploadId);
        Assert.Single(part.DrawingFiles);
        Assert.Contains(state.Gates, gate => gate.Code == "geometry_required" && gate.Status == "passed");
        Assert.Contains(state.Gates, gate => gate.Code == "analysis_complete" && gate.Status == "passed");
        Assert.Contains(state.Gates, gate => gate.Code == "priced" && gate.Status == "passed");
        Assert.Contains(state.Artifacts, artifact => artifact.ArtifactType == "viewer" && artifact.Status == "ready");
        Assert.Contains(state.Artifacts, artifact => artifact.ArtifactType == "dfm" && artifact.Status == "ready");
        Assert.NotNull(state.Estimate);
    }

    [Fact]
    public async Task Agent_register_uploads_tool_accepts_supplemental_files_without_satisfying_geometry()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var state = await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_register_uploads",
            new Dictionary<string, JsonElement>
            {
                ["requirements"] = JsonSerializer.SerializeToElement("Need 50 aluminum brackets from these sketches and photos.", JsonOptions),
                ["files"] = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        file_name = "bracket-sketch.jpg",
                        content_type = "image/jpeg",
                        file_size_bytes = 1_500_000,
                        kind = "sketch",
                        upload_id = "agent-upload-sketch",
                        storage_path = "",
                        url = "https://files.example.test/bracket-sketch.jpg"
                    },
                    new
                    {
                        file_name = "bracket-notes.pdf",
                        content_type = "application/pdf",
                        file_size_bytes = 210_000,
                        kind = "drawing",
                        upload_id = "agent-upload-drawing",
                        storage_path = "quotes/temp/session/agent-upload-drawing/bracket-notes.pdf",
                        url = ""
                    }
                }, JsonOptions)
            });

        Assert.Equal(sessionId, state.SessionId);
        Assert.Equal(2, state.Attachments.Count);
        Assert.All(state.Attachments, attachment => Assert.False(attachment.SatisfiesGeometryGate));
        Assert.Empty(state.Parts);
        Assert.Null(state.Estimate);
        Assert.Contains(state.Gates, gate => gate.Code == "geometry_required" && gate.Status == "blocked");
        Assert.Contains(state.Gates, gate => gate.Code == "analysis_complete" && gate.Status == "blocked");
        var analysisArtifact = Assert.Single(state.Artifacts, artifact => artifact.ArtifactType == "analysis");
        Assert.Equal("needs_geometry", analysisArtifact.Status);
        Assert.Equal("not_satisfied_by_supplemental_files", analysisArtifact.Metadata["geometryGate"]);
        Assert.Contains("sketch", analysisArtifact.Metadata["summary"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("sketch, drawing", analysisArtifact.Metadata["fileKinds"]);
        Assert.Equal("50", analysisArtifact.Metadata["inferredQuantity"]);
        Assert.Equal("cnc", analysisArtifact.Metadata["inferredProcess"]);
        Assert.Equal("al6061", analysisArtifact.Metadata["inferredMaterial"]);
        Assert.Equal("STANDARD", analysisArtifact.Metadata["inferredLeadTime"]);
        Assert.Equal("true", analysisArtifact.Metadata["needsCadGeometry"]);
        Assert.Equal("false", analysisArtifact.Metadata["usableForFinalPricing"]);
    }

    [Fact]
    public async Task Agent_project_summary_tool_reports_progress_blockers_and_next_actions()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_register_uploads",
            new Dictionary<string, JsonElement>
            {
                ["requirements"] = JsonSerializer.SerializeToElement("Quote this CNC housing as 10 aluminum pieces.", JsonOptions),
                ["files"] = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        file_name = "housing.step",
                        content_type = "model/step",
                        file_size_bytes = 240_000,
                        kind = "cad",
                        upload_id = "summary-upload-cad",
                        storage_path = "quotes/temp/session/summary-upload-cad/housing.step"
                    }
                }, JsonOptions)
            });

        var json = await ExecuteToolAsync(client, sessionId, "quote_get_project_summary");
        var summary = JsonSerializer.Deserialize<QuoteAgentProjectSummaryResponse>(json, JsonOptions);

        Assert.NotNull(summary);
        Assert.Equal(sessionId, summary.SessionId);
        Assert.Equal(1, summary.AttachmentCount);
        Assert.Equal(1, summary.PartCount);
        Assert.True(summary.ArtifactCount >= 3);
        Assert.NotNull(summary.EstimateTotal);
        Assert.Equal("THB", summary.EstimateCurrency);
        Assert.Contains("geometry_required", summary.PassedGateCodes);
        Assert.Contains("priced", summary.PassedGateCodes);
        Assert.Contains("customer_authenticated", summary.BlockingGateCodes);
        Assert.Contains(summary.NextActions, action => action.Contains("sign-in", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Agent_register_uploads_tool_rejects_unsupported_attachment_extension()
    {
        using var client = factory.CreateClient();

        var json = await ExecuteToolAsync(
            client,
            Guid.NewGuid(),
            "quote_register_uploads",
            new Dictionary<string, JsonElement>
            {
                ["files"] = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        file_name = "macro.xlsm",
                        content_type = "application/vnd.ms-excel.sheet.macroEnabled.12",
                        file_size_bytes = 42_000,
                        kind = "supplemental"
                    }
                }, JsonOptions)
            });
        using var document = JsonDocument.Parse(json);

        Assert.Equal("unsupported_upload_type", document.RootElement.GetProperty("requiredGateCode").GetString());
        Assert.Equal("register_uploads", document.RootElement.GetProperty("actionType").GetString());
        Assert.Contains("STEP", document.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_calculate_estimate_tool_reports_geometry_gate_before_pricing()
    {
        using var client = factory.CreateClient();

        var json = await ExecuteToolAsync(client, Guid.NewGuid(), "quote_calculate_estimate");
        using var document = JsonDocument.Parse(json);

        Assert.Equal("geometry_required", document.RootElement.GetProperty("requiredGateCode").GetString());
        Assert.Equal("calculate_estimate", document.RootElement.GetProperty("actionType").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("state").GetProperty("estimate").ValueKind);
        Assert.Contains("Upload STEP", document.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_calculate_estimate_tool_prices_after_geometry_analysis_and_configuration()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_register_uploads",
            new Dictionary<string, JsonElement>
            {
                ["requirements"] = JsonSerializer.SerializeToElement("Quote this STEP as 10 aluminum pieces.", JsonOptions),
                ["files"] = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        file_name = "priced-housing.step",
                        content_type = "model/step",
                        file_size_bytes = 240_000,
                        kind = "cad",
                        upload_id = "estimate-upload-cad",
                        storage_path = "quotes/temp/session/estimate-upload-cad/priced-housing.step"
                    }
                }, JsonOptions)
            });

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_calculate_estimate");

        Assert.NotNull(state.Estimate);
        Assert.True(state.Estimate.Total > 0);
        Assert.Equal("THB", state.Estimate.Currency);
        Assert.Contains(state.Gates, gate => gate.Code == "priced" && gate.Status == "passed");
        Assert.Contains(state.Artifacts, artifact => artifact.ArtifactType == "pricing" && artifact.Status == "ready");
    }

    [Fact]
    public async Task Agent_dfm_issue_blocks_pricing_until_exact_issue_is_acknowledged()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var uploadedState = await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_register_uploads",
            new Dictionary<string, JsonElement>
            {
                ["requirements"] = JsonSerializer.SerializeToElement(
                    "Quote this STEP as 10 aluminum pieces. Local DFM found a thin wall risk.",
                    JsonOptions),
                ["files"] = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        file_name = "thin-wall-bracket.step",
                        content_type = "model/step",
                        file_size_bytes = 240_000,
                        kind = "cad",
                        upload_id = "dfm-risk-upload-cad",
                        storage_path = "quotes/temp/session/dfm-risk-upload-cad/thin-wall-bracket.step"
                    }
                }, JsonOptions)
            });

        Assert.Contains(uploadedState.Parts, part =>
            part.Findings.Any(finding => finding.Code == "THIN_WALL"));
        Assert.Contains(uploadedState.Gates, gate => gate.Code == "dfm_reviewed" && gate.Status == "blocked");

        var blockedEstimateJson = await ExecuteToolAsync(client, sessionId, "quote_calculate_estimate");
        using (var blockedEstimate = JsonDocument.Parse(blockedEstimateJson))
        {
            Assert.Equal("dfm_reviewed", blockedEstimate.RootElement.GetProperty("requiredGateCode").GetString());
            Assert.Equal("calculate_estimate", blockedEstimate.RootElement.GetProperty("actionType").GetString());
        }

        var vagueAcknowledgementJson = await ExecuteToolAsync(client, sessionId, "quote_acknowledge_dfm");
        using (var vagueAcknowledgement = JsonDocument.Parse(vagueAcknowledgementJson))
        {
            Assert.Equal("dfm_reviewed", vagueAcknowledgement.RootElement.GetProperty("requiredGateCode").GetString());
            Assert.Equal("dfm_acknowledgement", vagueAcknowledgement.RootElement.GetProperty("actionType").GetString());
            Assert.Contains(
                vagueAcknowledgement.RootElement.GetProperty("requiredIssueIds").EnumerateArray(),
                issue => issue.GetString() == "THIN_WALL");
        }

        var acknowledgementState = await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_acknowledge_dfm",
            new Dictionary<string, JsonElement>
            {
                ["issue_ids"] = JsonSerializer.SerializeToElement(new[] { "THIN_WALL" }, JsonOptions),
                ["note"] = JsonSerializer.SerializeToElement("I reviewed and accept the thin-wall DFM risk.", JsonOptions)
            });
        var acknowledgementAction = Assert.Single(acknowledgementState.ProposedActions);
        Assert.Equal("dfm_acknowledgement", acknowledgementAction.ActionType);

        var acknowledgementResult = await ConfirmActionAsync(client, acknowledgementAction.ActionId);
        Assert.NotNull(acknowledgementResult.State);
        Assert.Contains(acknowledgementResult.State.Gates, gate =>
            gate.Code == "dfm_reviewed" && gate.Status == "passed");

        var pricedState = await ExecuteToolForStateAsync(client, sessionId, "quote_calculate_estimate");

        Assert.NotNull(pricedState.Estimate);
        Assert.Contains(pricedState.Gates, gate => gate.Code == "priced" && gate.Status == "passed");
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
    public async Task Agent_auth_required_gate_error_includes_trusted_auth_handoff()
    {
        using var client = factory.CreateClient();
        using var prepareRequest = new HttpRequestMessage(HttpMethod.Post, "/quote/v1/agent/tools/quote_prepare_formal_quote")
        {
            Content = JsonContent.Create(new QuoteAgentToolRequest
            {
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["requirements"] = JsonSerializer.SerializeToElement("Need a formal quote.", JsonOptions),
                    ["return_url"] = JsonSerializer.SerializeToElement("/quote/new?checkout=1", JsonOptions)
                }
            }, options: JsonOptions)
        };
        prepareRequest.Headers.TryAddWithoutValidation(
            "X-Maliev-Agent-Context",
            CreateSignedAgentContextToken(Guid.NewGuid(), Guid.NewGuid(), null));

        using var prepareResponse = await client.SendAsync(prepareRequest);
        using var document = await JsonDocument.ParseAsync(await prepareResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, prepareResponse.StatusCode);
        Assert.Equal("customer_authenticated", document.RootElement.GetProperty("requiredGateCode").GetString());

        var authHandoff = document.RootElement.GetProperty("authHandoff");
        Assert.False(authHandoff.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal("/quote/new?checkout=1", authHandoff.GetProperty("returnUrl").GetString());
        Assert.Equal("authentication_required", authHandoff.GetProperty("status").GetString());

        var google = Assert.Single(authHandoff.GetProperty("methods").EnumerateArray(), method =>
            method.GetProperty("methodId").GetString() == "google");
        Assert.Equal("/auth/sign-in?returnUrl=%2Fquote%2Fnew%3Fcheckout%3D1", google.GetProperty("url").GetString());
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

        var checkoutState = await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_update_checkout_details",
            new Dictionary<string, JsonElement>
            {
                ["billing_address_id"] = JsonSerializer.SerializeToElement(CheckoutBillingAddressId.ToString("D"), JsonOptions),
                ["shipping_address_id"] = JsonSerializer.SerializeToElement(CheckoutShippingAddressId.ToString("D"), JsonOptions),
                ["phone"] = JsonSerializer.SerializeToElement("+66 2 555 0100", JsonOptions),
                ["company"] = JsonSerializer.SerializeToElement("MALIEV Buyer Co.", JsonOptions),
                ["vat_number"] = JsonSerializer.SerializeToElement("TH1234567890", JsonOptions),
                ["accepted_terms"] = JsonSerializer.SerializeToElement(true, JsonOptions),
                ["consent"] = JsonSerializer.SerializeToElement(true, JsonOptions)
            });
        Assert.Contains(checkoutState.Gates, gate => gate.Code == "checkout_ready" && gate.Status == "passed");

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

    [Fact]
    public async Task Agent_start_payment_blocks_until_checkout_details_are_collected()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-payment-checkout@example.com");
        var sessionId = await StartPricedCadSessionAsync(client);

        var formalQuoteState = await ExecuteToolForStateAsync(client, sessionId, "quote_prepare_formal_quote");
        await ConfirmActionAsync(client, Assert.Single(formalQuoteState.ProposedActions).ActionId);
        var approvalState = await ExecuteToolForStateAsync(client, sessionId, "quote_approve_quote");
        await ConfirmActionAsync(client, Assert.Single(approvalState.ProposedActions).ActionId);
        var orderState = await ExecuteToolForStateAsync(client, sessionId, "quote_create_order");
        await ConfirmActionAsync(client, Assert.Single(orderState.ProposedActions).ActionId);

        var json = await ExecuteToolAsync(client, sessionId, "quote_start_payment");
        using var document = JsonDocument.Parse(json);

        Assert.Equal("checkout_ready", document.RootElement.GetProperty("requiredGateCode").GetString());
        Assert.Equal("start_payment", document.RootElement.GetProperty("actionType").GetString());
        Assert.Contains(
            "Billing",
            document.RootElement.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_start_payment_rejects_mismatched_amount_before_confirmation()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-payment-amount@example.com");
        var sessionId = await StartPricedCadSessionAsync(client);

        var currentState = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        Assert.NotNull(currentState.Estimate);

        var formalQuoteState = await ExecuteToolForStateAsync(client, sessionId, "quote_prepare_formal_quote");
        await ConfirmActionAsync(client, Assert.Single(formalQuoteState.ProposedActions).ActionId);
        var approvalState = await ExecuteToolForStateAsync(client, sessionId, "quote_approve_quote");
        await ConfirmActionAsync(client, Assert.Single(approvalState.ProposedActions).ActionId);
        var orderState = await ExecuteToolForStateAsync(client, sessionId, "quote_create_order");
        var orderResult = await ConfirmActionAsync(client, Assert.Single(orderState.ProposedActions).ActionId);
        Assert.NotNull(orderResult.State);

        await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_update_checkout_details",
            new Dictionary<string, JsonElement>
            {
                ["billing_address_id"] = JsonSerializer.SerializeToElement(CheckoutBillingAddressId.ToString("D"), JsonOptions),
                ["shipping_address_id"] = JsonSerializer.SerializeToElement(CheckoutShippingAddressId.ToString("D"), JsonOptions),
                ["accepted_terms"] = JsonSerializer.SerializeToElement(true, JsonOptions),
                ["consent"] = JsonSerializer.SerializeToElement(true, JsonOptions)
            });

        var mismatchedPaymentJson = await ExecuteToolAsync(
            client,
            sessionId,
            "quote_start_payment",
            new Dictionary<string, JsonElement>
            {
                ["order_number"] = JsonSerializer.SerializeToElement(orderResult.State.Artifacts.Single(artifact => artifact.ArtifactType == "order").Title, JsonOptions),
                ["amount"] = JsonSerializer.SerializeToElement(currentState.Estimate.Total + 100m, JsonOptions),
                ["currency"] = JsonSerializer.SerializeToElement(currentState.Estimate.Currency, JsonOptions)
            });
        using (var mismatchedPayment = JsonDocument.Parse(mismatchedPaymentJson))
        {
            Assert.Equal("payment_amount_verified", mismatchedPayment.RootElement.GetProperty("requiredGateCode").GetString());
            Assert.Equal("start_payment", mismatchedPayment.RootElement.GetProperty("actionType").GetString());
            Assert.Equal(currentState.Estimate.Total, mismatchedPayment.RootElement.GetProperty("expectedAmount").GetDecimal());
            Assert.Equal(currentState.Estimate.Currency, mismatchedPayment.RootElement.GetProperty("expectedCurrency").GetString());
            Assert.Equal(0, mismatchedPayment.RootElement.GetProperty("state").GetProperty("proposedActions").GetArrayLength());
        }

        var paymentState = await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_start_payment",
            new Dictionary<string, JsonElement>
            {
                ["amount"] = JsonSerializer.SerializeToElement(currentState.Estimate.Total, JsonOptions),
                ["currency"] = JsonSerializer.SerializeToElement(currentState.Estimate.Currency, JsonOptions)
            });

        Assert.Single(paymentState.ProposedActions);
        Assert.Equal("start_payment", paymentState.ProposedActions[0].ActionType);
    }

    [Fact]
    public async Task Agent_update_checkout_details_records_required_payment_context()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-checkout-details@example.com");
        var sessionId = await StartPricedCadSessionAsync(client);

        var state = await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_update_checkout_details",
            new Dictionary<string, JsonElement>
            {
                ["billing_address_id"] = JsonSerializer.SerializeToElement(CheckoutBillingAddressId.ToString("D"), JsonOptions),
                ["shipping_address_id"] = JsonSerializer.SerializeToElement(CheckoutShippingAddressId.ToString("D"), JsonOptions),
                ["phone"] = JsonSerializer.SerializeToElement("+66 2 555 0100", JsonOptions),
                ["company"] = JsonSerializer.SerializeToElement("MALIEV Buyer Co.", JsonOptions),
                ["vat_number"] = JsonSerializer.SerializeToElement("TH1234567890", JsonOptions),
                ["accepted_terms"] = JsonSerializer.SerializeToElement(true, JsonOptions),
                ["consent"] = JsonSerializer.SerializeToElement(true, JsonOptions)
            });

        Assert.Contains(state.Gates, gate => gate.Code == "checkout_ready" && gate.Status == "pending");
        var checkout = Assert.Single(state.Artifacts, artifact => artifact.ArtifactType == "checkout");
        Assert.Equal("ready", checkout.Status);
        Assert.Equal(CheckoutBillingAddressId.ToString("D"), checkout.Metadata["billingAddressId"]);
        Assert.Equal(CheckoutShippingAddressId.ToString("D"), checkout.Metadata["shippingAddressId"]);
        Assert.Equal("+66 2 555 0100", checkout.Metadata["phone"]);
        Assert.Equal("MALIEV Buyer Co.", checkout.Metadata["company"]);
        Assert.Equal("TH1234567890", checkout.Metadata["vatNumber"]);
        Assert.Equal("true", checkout.Metadata["acceptedTerms"]);
        Assert.Equal("true", checkout.Metadata["consent"]);
    }

    [Fact]
    public async Task Agent_duplicate_project_uses_confirmed_customer_draft_project()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-duplicate@example.com");
        var sessionId = await StartPricedCadSessionAsync(client);

        var draftState = await ExecuteToolForStateAsync(client, sessionId, "quote_prepare_draft_project");
        var draftAction = Assert.Single(draftState.ProposedActions);
        Assert.Equal("draft_project", draftAction.ActionType);

        var draftResult = await ConfirmActionAsync(client, draftAction.ActionId);
        Assert.NotNull(draftResult.State);
        var draftArtifact = Assert.Single(draftResult.State.Artifacts, artifact => artifact.ArtifactType == "draft_project");
        Assert.True(Guid.TryParse(draftArtifact.Metadata["projectId"], out var sourceProjectId));

        var duplicateState = await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_duplicate_project",
            new Dictionary<string, JsonElement>
            {
                ["title"] = JsonSerializer.SerializeToElement("Duplicate from agent", JsonOptions)
            });
        var duplicateAction = Assert.Single(duplicateState.ProposedActions);
        Assert.Equal("duplicate_project", duplicateAction.ActionType);

        var duplicateResult = await ConfirmActionAsync(client, duplicateAction.ActionId);

        Assert.NotNull(duplicateResult.State);
        Assert.Contains("duplicated", duplicateResult.Message, StringComparison.OrdinalIgnoreCase);
        var duplicateArtifact = Assert.Single(duplicateResult.State.Artifacts, artifact => artifact.ArtifactType == "duplicate_project");
        Assert.Equal(sourceProjectId.ToString("D"), duplicateArtifact.Metadata["sourceProjectId"]);
        Assert.True(Guid.TryParse(duplicateArtifact.Metadata["projectId"], out var duplicateProjectId));
        Assert.NotEqual(sourceProjectId, duplicateProjectId);
        Assert.Equal("Duplicate from agent", duplicateArtifact.Title);
    }

    [Fact]
    public async Task Agent_duplicate_project_blocks_until_draft_project_exists()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-duplicate-blocked@example.com");
        var sessionId = await StartPricedCadSessionAsync(client);

        var json = await ExecuteToolAsync(client, sessionId, "quote_duplicate_project");
        using var document = JsonDocument.Parse(json);

        Assert.Equal("draft_project", document.RootElement.GetProperty("requiredGateCode").GetString());
        Assert.Equal("duplicate_project", document.RootElement.GetProperty("actionType").GetString());
        Assert.Contains(
            "draft project",
            document.RootElement.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("quote_pin_project", "pin_project", "project_pin", "pinned", "isPinned", "true")]
    [InlineData("quote_unpin_project", "unpin_project", "project_unpin", "unpinned", "isPinned", "false")]
    [InlineData("quote_archive_project", "archive_project", "project_archive", "archived", "isArchived", "true")]
    public async Task Agent_project_management_tool_requires_confirmation_and_updates_customer_project(
        string toolName,
        string actionType,
        string artifactType,
        string expectedStatus,
        string metadataKey,
        string metadataValue)
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, $"agent-{actionType}@example.com");
        var sessionId = await StartPricedCadSessionAsync(client);

        var draftState = await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_prepare_draft_project",
            new Dictionary<string, JsonElement>
            {
                ["title"] = JsonSerializer.SerializeToElement($"Project {actionType}", JsonOptions)
            });
        var draftResult = await ConfirmActionAsync(client, Assert.Single(draftState.ProposedActions).ActionId);
        Assert.NotNull(draftResult.State);
        var draftArtifact = Assert.Single(draftResult.State.Artifacts, artifact => artifact.ArtifactType == "draft_project");
        Assert.True(Guid.TryParse(draftArtifact.Metadata["projectId"], out var projectId));

        var pendingState = await ExecuteToolForStateAsync(
            client,
            sessionId,
            toolName,
            new Dictionary<string, JsonElement>
            {
                ["project_id"] = JsonSerializer.SerializeToElement(projectId.ToString("D"), JsonOptions)
            });
        var pendingAction = Assert.Single(pendingState.ProposedActions);
        Assert.Equal(actionType, pendingAction.ActionType);

        var result = await ConfirmActionAsync(client, pendingAction.ActionId);

        Assert.NotNull(result.State);
        var artifact = Assert.Single(result.State.Artifacts, item => item.ArtifactType == artifactType);
        Assert.Equal(expectedStatus, artifact.Status);
        Assert.Equal(projectId.ToString("D"), artifact.Metadata["projectId"]);
        Assert.Equal(metadataValue, artifact.Metadata[metadataKey]);

        var searchJson = await ExecuteToolAsync(
            client,
            sessionId,
            "quote_search_customer_data",
            new Dictionary<string, JsonElement>
            {
                ["query"] = JsonSerializer.SerializeToElement($"Project {actionType}", JsonOptions)
            });
        using var document = JsonDocument.Parse(searchJson);
        var projectResult = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray(), result =>
            result.GetProperty("resourceType").GetString() == "project" &&
            result.GetProperty("resourceId").GetString() == projectId.ToString("D"));
        Assert.Equal(metadataValue, projectResult.GetProperty("metadata").GetProperty(metadataKey).GetString());
    }

    [Fact]
    public async Task Agent_confirm_action_retry_returns_completed_result_without_reexecuting()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-confirm-retry@example.com");
        var sessionId = await StartPricedCadSessionAsync(client);

        var draftState = await ExecuteToolForStateAsync(client, sessionId, "quote_prepare_draft_project");
        var action = Assert.Single(draftState.ProposedActions);

        var first = await ConfirmActionAsync(client, action.ActionId);
        Assert.NotNull(first.State);
        var firstArtifact = Assert.Single(first.State.Artifacts, artifact => artifact.ArtifactType == "draft_project");
        var firstProjectId = firstArtifact.Metadata["projectId"];

        var retryResponse = await client.PostAsJsonAsync(
            $"/quote/v1/agent/actions/{action.ActionId:D}/confirm",
            new QuoteAgentConfirmActionRequest
            {
                ConfirmationNote = "Retry after a transient network failure."
            });

        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        var retry = await retryResponse.Content.ReadFromJsonAsync<QuoteAgentActionResultResponse>();
        Assert.NotNull(retry);
        Assert.Equal(action.ActionId, retry.ActionId);
        Assert.Equal("completed", retry.Status);
        Assert.NotNull(retry.State);
        var retryArtifact = Assert.Single(retry.State.Artifacts, artifact => artifact.ArtifactType == "draft_project");
        Assert.Equal(firstProjectId, retryArtifact.Metadata["projectId"]);
    }

    [Fact]
    public async Task Agent_project_management_tool_rejects_project_owned_by_another_customer()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var ownerClient = await CreateSignedInClientAsync(scopedFactory, "agent-pin-owner@example.com");
        var ownerSessionId = await StartPricedCadSessionAsync(ownerClient);
        var draftState = await ExecuteToolForStateAsync(ownerClient, ownerSessionId, "quote_prepare_draft_project");
        var draftResult = await ConfirmActionAsync(ownerClient, Assert.Single(draftState.ProposedActions).ActionId);
        Assert.NotNull(draftResult.State);
        var draftArtifact = Assert.Single(draftResult.State.Artifacts, artifact => artifact.ArtifactType == "draft_project");
        Assert.True(Guid.TryParse(draftArtifact.Metadata["projectId"], out var projectId));

        using var otherClient = await CreateSignedInClientAsync(scopedFactory, "agent-pin-other@example.com");
        var json = await ExecuteToolAsync(
            otherClient,
            Guid.NewGuid(),
            "quote_pin_project",
            new Dictionary<string, JsonElement>
            {
                ["project_id"] = JsonSerializer.SerializeToElement(projectId.ToString("D"), JsonOptions)
            });
        using var document = JsonDocument.Parse(json);

        Assert.Equal("project_access", document.RootElement.GetProperty("requiredGateCode").GetString());
        Assert.Equal("pin_project", document.RootElement.GetProperty("actionType").GetString());
        Assert.Contains(
            "not found",
            document.RootElement.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_resume_project_hydrates_state_from_customer_project()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-resume@example.com");
        var sourceSessionId = Guid.NewGuid();
        await ExecuteToolForStateAsync(
            client,
            sourceSessionId,
            "quote_register_uploads",
            new Dictionary<string, JsonElement>
            {
                ["requirements"] = JsonSerializer.SerializeToElement("Quote this CNC housing with the attached drawing as 10 aluminum pieces.", JsonOptions),
                ["files"] = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        file_name = "resume-housing.step",
                        content_type = "model/step",
                        file_size_bytes = 240_000,
                        kind = "cad",
                        upload_id = "resume-upload-cad",
                        storage_path = "quotes/temp/session/resume-upload-cad/resume-housing.step"
                    },
                    new
                    {
                        file_name = "resume-housing-drawing.pdf",
                        content_type = "application/pdf",
                        file_size_bytes = 88_000,
                        kind = "drawing",
                        upload_id = "resume-upload-drawing",
                        storage_path = "quotes/temp/session/resume-upload-drawing/resume-housing-drawing.pdf"
                    }
                }, JsonOptions)
            });

        var draftState = await ExecuteToolForStateAsync(client, sourceSessionId, "quote_prepare_draft_project");
        var draftAction = Assert.Single(draftState.ProposedActions);
        var draftResult = await ConfirmActionAsync(client, draftAction.ActionId);
        Assert.NotNull(draftResult.State);
        var draftArtifact = Assert.Single(draftResult.State.Artifacts, artifact => artifact.ArtifactType == "draft_project");
        Assert.True(Guid.TryParse(draftArtifact.Metadata["projectId"], out var projectId));

        var resumedSessionId = Guid.NewGuid();
        var resumed = await ExecuteToolForStateAsync(
            client,
            resumedSessionId,
            "quote_resume_project",
            new Dictionary<string, JsonElement>
            {
                ["project_id"] = JsonSerializer.SerializeToElement(projectId.ToString("D"), JsonOptions)
            });

        Assert.Equal(resumedSessionId, resumed.SessionId);
        Assert.Single(resumed.Parts);
        Assert.Contains("lead time STANDARD", resumed.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(resumed.Gates, gate => gate.Code == "geometry_required" && gate.Status == "passed");
        Assert.Contains(resumed.Gates, gate => gate.Code == "analysis_complete" && gate.Status == "passed");
        Assert.Contains(resumed.Artifacts, artifact =>
            artifact.ArtifactType == "resumed_project" &&
            artifact.Metadata["projectId"] == projectId.ToString("D"));
        var resumedPart = Assert.Single(resumed.Parts);
        var resumedDrawing = Assert.Single(resumedPart.DrawingFiles);
        Assert.Equal("resume-housing-drawing.pdf", resumedDrawing.FileName);
        var resumedAttachment = Assert.Single(resumed.Attachments);
        Assert.Equal("resume-housing-drawing.pdf", resumedAttachment.FileName);
        Assert.False(resumedAttachment.SatisfiesGeometryGate);
        Assert.Contains(resumed.Artifacts, artifact =>
            artifact.ArtifactType == "analysis" &&
            artifact.Status == "ready" &&
            artifact.Metadata["geometryGate"] == "satisfied_by_resumed_cad" &&
            artifact.Metadata["needsCadGeometry"] == "false" &&
            artifact.Metadata["usableForFinalPricing"] == "true");
        Assert.Contains(resumed.Artifacts, artifact => artifact.ArtifactType == "viewer" && artifact.Status == "ready");
        Assert.NotNull(resumed.Estimate);
    }

    [Fact]
    public async Task Agent_resume_project_rejects_project_owned_by_another_customer()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var ownerClient = await CreateSignedInClientAsync(scopedFactory, "agent-resume-owner@example.com");
        var ownerSessionId = await StartPricedCadSessionAsync(ownerClient);
        var draftState = await ExecuteToolForStateAsync(ownerClient, ownerSessionId, "quote_prepare_draft_project");
        var draftAction = Assert.Single(draftState.ProposedActions);
        var draftResult = await ConfirmActionAsync(ownerClient, draftAction.ActionId);
        Assert.NotNull(draftResult.State);
        var draftArtifact = Assert.Single(draftResult.State.Artifacts, artifact => artifact.ArtifactType == "draft_project");
        Assert.True(Guid.TryParse(draftArtifact.Metadata["projectId"], out var projectId));

        using var otherClient = await CreateSignedInClientAsync(scopedFactory, "agent-resume-other@example.com");
        var json = await ExecuteToolAsync(
            otherClient,
            Guid.NewGuid(),
            "quote_resume_project",
            new Dictionary<string, JsonElement>
            {
                ["project_id"] = JsonSerializer.SerializeToElement(projectId.ToString("D"), JsonOptions)
            });
        using var document = JsonDocument.Parse(json);

        Assert.Equal("project_access", document.RootElement.GetProperty("requiredGateCode").GetString());
        Assert.Equal("resume_project", document.RootElement.GetProperty("actionType").GetString());
        Assert.Contains(
            "not found",
            document.RootElement.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_search_endpoint_requires_customer_session()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/quote/v1/agent/sessions/{Guid.NewGuid():D}/search?query=fixture");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("Sign in", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_search_endpoint_returns_customer_scoped_results()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var ownerClient = await CreateSignedInClientAsync(scopedFactory, "agent-search-endpoint-owner@example.com");
        var ownerSessionId = await StartPricedCadSessionAsync(ownerClient);

        var draftState = await ExecuteToolForStateAsync(
            ownerClient,
            ownerSessionId,
            "quote_prepare_draft_project",
            new Dictionary<string, JsonElement>
            {
                ["title"] = JsonSerializer.SerializeToElement("Endpoint fixture search project", JsonOptions)
            });
        await ConfirmActionAsync(ownerClient, Assert.Single(draftState.ProposedActions).ActionId);

        using var otherClient = await CreateSignedInClientAsync(scopedFactory, "agent-search-endpoint-other@example.com");
        var otherSessionId = await StartPricedCadSessionAsync(otherClient);
        var otherDraftState = await ExecuteToolForStateAsync(
            otherClient,
            otherSessionId,
            "quote_prepare_draft_project",
            new Dictionary<string, JsonElement>
            {
                ["title"] = JsonSerializer.SerializeToElement("Other endpoint fixture project", JsonOptions)
            });
        await ConfirmActionAsync(otherClient, Assert.Single(otherDraftState.ProposedActions).ActionId);

        var response = await ownerClient.GetAsync($"/quote/v1/agent/sessions/{ownerSessionId:D}/search?query=fixture&limit=20");
        var body = await response.Content.ReadFromJsonAsync<QuoteAgentSearchResponse>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(ownerSessionId, body.SessionId);
        Assert.True(body.IsAuthenticated);
        Assert.Equal("fixture", body.Query);
        Assert.Contains(body.Results, result =>
            result.ResourceType == "project" &&
            result.Title == "Endpoint fixture search project" &&
            result.ActionHint == "resume_project");
        Assert.Contains(body.Results, result =>
            result.ResourceType == "file" &&
            result.Title == "fixture.step" &&
            result.Metadata["source"] == "session");
        Assert.DoesNotContain(body.Results, result =>
            result.Title == "Other endpoint fixture project");
    }

    [Fact]
    public async Task Agent_search_customer_data_returns_customer_scoped_resources()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var ownerClient = await CreateSignedInClientAsync(scopedFactory, "agent-search-owner@example.com");
        var ownerSessionId = await StartPricedCadSessionAsync(ownerClient);

        var draftState = await ExecuteToolForStateAsync(
            ownerClient,
            ownerSessionId,
            "quote_prepare_draft_project",
            new Dictionary<string, JsonElement>
            {
                ["title"] = JsonSerializer.SerializeToElement("Fixture search project", JsonOptions)
            });
        await ConfirmActionAsync(ownerClient, Assert.Single(draftState.ProposedActions).ActionId);

        var quoteState = await ExecuteToolForStateAsync(ownerClient, ownerSessionId, "quote_prepare_formal_quote");
        await ConfirmActionAsync(ownerClient, Assert.Single(quoteState.ProposedActions).ActionId);

        var approvalState = await ExecuteToolForStateAsync(ownerClient, ownerSessionId, "quote_approve_quote");
        await ConfirmActionAsync(ownerClient, Assert.Single(approvalState.ProposedActions).ActionId);

        var orderState = await ExecuteToolForStateAsync(ownerClient, ownerSessionId, "quote_create_order");
        await ConfirmActionAsync(ownerClient, Assert.Single(orderState.ProposedActions).ActionId);

        using var otherClient = await CreateSignedInClientAsync(scopedFactory, "agent-search-other@example.com");
        var otherSessionId = await StartPricedCadSessionAsync(otherClient);
        var otherDraftState = await ExecuteToolForStateAsync(
            otherClient,
            otherSessionId,
            "quote_prepare_draft_project",
            new Dictionary<string, JsonElement>
            {
                ["title"] = JsonSerializer.SerializeToElement("Other customer hidden fixture", JsonOptions)
            });
        await ConfirmActionAsync(otherClient, Assert.Single(otherDraftState.ProposedActions).ActionId);

        var json = await ExecuteToolAsync(
            ownerClient,
            ownerSessionId,
            "quote_search_customer_data",
            new Dictionary<string, JsonElement>
            {
                ["query"] = JsonSerializer.SerializeToElement(string.Empty, JsonOptions),
                ["limit"] = JsonSerializer.SerializeToElement(20, JsonOptions)
            });
        using var document = JsonDocument.Parse(json);
        var results = document.RootElement.GetProperty("results").EnumerateArray().ToArray();

        Assert.Equal(ownerSessionId, document.RootElement.GetProperty("sessionId").GetGuid());
        Assert.Contains(results, result =>
            result.GetProperty("resourceType").GetString() == "project" &&
            result.GetProperty("title").GetString() == "Fixture search project");
        Assert.Contains(results, result => result.GetProperty("resourceType").GetString() == "quote");
        Assert.Contains(results, result => result.GetProperty("resourceType").GetString() == "order");
        Assert.Contains(results, result =>
            result.GetProperty("resourceType").GetString() == "document" &&
            result.GetProperty("title").GetString() == "manufacturing-requirements.pdf");
        Assert.DoesNotContain(results, result =>
            result.GetProperty("title").GetString() == "Other customer hidden fixture");
    }

    [Fact]
    public async Task Agent_search_customer_data_includes_current_session_files_and_parts()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-search-session@example.com");
        var sessionId = Guid.NewGuid();

        await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_register_uploads",
            new Dictionary<string, JsonElement>
            {
                ["requirements"] = JsonSerializer.SerializeToElement(
                    "Quote this CNC housing as 12 aluminum parts with the matching drawing.",
                    JsonOptions),
                ["files"] = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        file_name = "session-housing.step",
                        content_type = "model/step",
                        file_size_bytes = 420_000,
                        kind = "cad",
                        upload_id = "session-housing-cad",
                        storage_path = "quotes/temp/session/session-housing.step"
                    },
                    new
                    {
                        file_name = "session-housing-drawing.pdf",
                        content_type = "application/pdf",
                        file_size_bytes = 92_000,
                        kind = "drawing",
                        upload_id = "session-housing-drawing",
                        storage_path = "quotes/temp/session/session-housing-drawing.pdf"
                    }
                }, JsonOptions)
            });

        var json = await ExecuteToolAsync(
            client,
            sessionId,
            "quote_search_customer_data",
            new Dictionary<string, JsonElement>
            {
                ["query"] = JsonSerializer.SerializeToElement("session-housing", JsonOptions),
                ["limit"] = JsonSerializer.SerializeToElement(20, JsonOptions)
            });
        using var document = JsonDocument.Parse(json);
        var results = document.RootElement.GetProperty("results").EnumerateArray().ToArray();

        Assert.Equal(sessionId, document.RootElement.GetProperty("sessionId").GetGuid());
        var cadFile = Assert.Single(results, result =>
            result.GetProperty("resourceType").GetString() == "file" &&
            result.GetProperty("title").GetString() == "session-housing.step");
        Assert.Equal("session", cadFile.GetProperty("metadata").GetProperty("source").GetString());
        Assert.Equal("true", cadFile.GetProperty("metadata").GetProperty("satisfiesGeometryGate").GetString());

        var drawingFile = Assert.Single(results, result =>
            result.GetProperty("resourceType").GetString() == "file" &&
            result.GetProperty("title").GetString() == "session-housing-drawing.pdf");
        Assert.Equal("drawing", drawingFile.GetProperty("metadata").GetProperty("kind").GetString());
        Assert.Equal("false", drawingFile.GetProperty("metadata").GetProperty("satisfiesGeometryGate").GetString());

        var part = Assert.Single(results, result =>
            result.GetProperty("resourceType").GetString() == "part" &&
            result.GetProperty("title").GetString() == "session-housing.step");
        Assert.Equal("open_part", part.GetProperty("actionHint").GetString());
        Assert.Equal("session", part.GetProperty("metadata").GetProperty("source").GetString());
        Assert.Equal("12", part.GetProperty("metadata").GetProperty("quantity").GetString());
    }

    [Fact]
    public async Task Agent_search_customer_data_requires_customer_session()
    {
        using var client = factory.CreateClient();
        var json = await ExecuteToolAsync(
            client,
            Guid.NewGuid(),
            "quote_search_customer_data",
            new Dictionary<string, JsonElement>
            {
                ["query"] = JsonSerializer.SerializeToElement("fixture", JsonOptions)
            });
        using var document = JsonDocument.Parse(json);

        Assert.Equal("customer_authenticated", document.RootElement.GetProperty("requiredGateCode").GetString());
        Assert.Equal("search_customer_data", document.RootElement.GetProperty("actionType").GetString());
        Assert.Contains(
            "Sign in",
            document.RootElement.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_connector_registry_lists_safe_planned_customer_connectors()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var json = await ExecuteToolAsync(client, sessionId, "quote_get_connectors");
        using var document = JsonDocument.Parse(json);
        var connectors = document.RootElement.GetProperty("connectors").EnumerateArray().ToArray();

        Assert.Equal(sessionId, document.RootElement.GetProperty("sessionId").GetGuid());
        Assert.False(document.RootElement.GetProperty("requiresAuthenticationToList").GetBoolean());

        var googleDrive = Assert.Single(connectors, connector =>
            connector.GetProperty("connectorId").GetString() == "google-drive");
        Assert.Equal("Google Drive", googleDrive.GetProperty("displayName").GetString());
        Assert.Equal("planned", googleDrive.GetProperty("status").GetString());
        Assert.Equal("file_import", googleDrive.GetProperty("category").GetString());
        Assert.True(googleDrive.GetProperty("requiresAuthenticationToConnect").GetBoolean());
        Assert.False(googleDrive.GetProperty("isConnected").GetBoolean());
        Assert.Contains("STEP", googleDrive.GetProperty("supportedFileTypes").EnumerateArray().Select(item => item.GetString()));

        Assert.Contains(connectors, connector =>
            connector.GetProperty("connectorId").GetString() == "blender" &&
            connector.GetProperty("category").GetString() == "cad_sender" &&
            connector.GetProperty("status").GetString() == "future");
        Assert.Contains(connectors, connector =>
            connector.GetProperty("connectorId").GetString() == "freecad" &&
            connector.GetProperty("category").GetString() == "cad_sender" &&
            connector.GetProperty("status").GetString() == "future");
    }

    [Fact]
    public async Task Agent_connector_registry_endpoint_lists_plugins_without_tool_context()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var response = await client.GetAsync($"/quote/v1/agent/sessions/{sessionId:D}/connectors");

        response.EnsureSuccessStatusCode();
        var registry = await response.Content.ReadFromJsonAsync<QuoteAgentConnectorRegistryResponse>();
        Assert.NotNull(registry);
        Assert.Equal(sessionId, registry.SessionId);
        Assert.False(registry.RequiresAuthenticationToList);
        Assert.Contains(registry.Connectors, connector =>
            connector.ConnectorId == "google-drive" &&
            connector.DisplayName == "Google Drive" &&
            connector.Status == "planned" &&
            connector.Category == "file_import" &&
            connector.RequiresAuthenticationToConnect &&
            connector.SupportedFileTypes.Contains("STEP"));
        Assert.Contains(registry.Connectors, connector => connector.ConnectorId == "blender" && connector.Status == "future");
        Assert.Contains(registry.Connectors, connector => connector.ConnectorId == "freecad" && connector.Status == "future");
    }

    [Fact]
    public async Task Agent_auth_handoff_lists_google_passkey_and_email_fallback_for_anonymous_customer()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var json = await ExecuteToolAsync(
            client,
            sessionId,
            "quote_get_auth_handoff",
            new Dictionary<string, JsonElement>
            {
                ["intent"] = JsonSerializer.SerializeToElement("sign-up", JsonOptions),
                ["return_url"] = JsonSerializer.SerializeToElement("/quotes", JsonOptions)
            });
        using var document = JsonDocument.Parse(json);
        var methods = document.RootElement.GetProperty("methods").EnumerateArray().ToArray();

        Assert.Equal(sessionId, document.RootElement.GetProperty("sessionId").GetGuid());
        Assert.False(document.RootElement.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal("sign-up", document.RootElement.GetProperty("intent").GetString());
        Assert.Equal("/quotes", document.RootElement.GetProperty("returnUrl").GetString());
        Assert.Equal("customer_authenticated", document.RootElement.GetProperty("requiredGateCode").GetString());

        var google = Assert.Single(methods, method => method.GetProperty("methodId").GetString() == "google");
        Assert.Equal("Google", google.GetProperty("displayName").GetString());
        Assert.Equal("preferred", google.GetProperty("status").GetString());
        Assert.Equal("/auth/sign-up?returnUrl=%2Fquotes", google.GetProperty("url").GetString());

        var passkey = Assert.Single(methods, method => method.GetProperty("methodId").GetString() == "passkey");
        Assert.Equal("available_when_supported", passkey.GetProperty("status").GetString());
        Assert.True(passkey.GetProperty("requiresBrowserSupport").GetBoolean());

        var email = Assert.Single(methods, method => method.GetProperty("methodId").GetString() == "email-password");
        Assert.Equal("fallback", email.GetProperty("status").GetString());
        Assert.Equal("/auth/sign-up?returnUrl=%2Fquotes", email.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Agent_auth_handoff_reports_existing_authenticated_customer()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-auth-handoff@example.com");
        var sessionId = Guid.NewGuid();

        var json = await ExecuteToolAsync(client, sessionId, "quote_get_auth_handoff");
        using var document = JsonDocument.Parse(json);

        Assert.Equal(sessionId, document.RootElement.GetProperty("sessionId").GetGuid());
        Assert.True(document.RootElement.GetProperty("isAuthenticated").GetBoolean());
        Assert.NotEqual(Guid.Empty, document.RootElement.GetProperty("customerId").GetGuid());
        Assert.Equal("already_authenticated", document.RootElement.GetProperty("status").GetString());
        Assert.Empty(document.RootElement.GetProperty("methods").EnumerateArray());
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
        string toolName,
        Dictionary<string, JsonElement>? arguments = null)
    {
        var json = await ExecuteToolAsync(client, sessionId, toolName, arguments);
        var state = JsonSerializer.Deserialize<QuoteAgentStateResponse>(json, JsonOptions);
        Assert.NotNull(state);
        return state;
    }

    private static async Task<string> ExecuteToolAsync(
        HttpClient client,
        Guid sessionId,
        string toolName,
        Dictionary<string, JsonElement>? arguments = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/quote/v1/agent/tools/{toolName}")
        {
            Content = JsonContent.Create(new QuoteAgentToolRequest
            {
                Arguments = arguments ?? []
            }, options: JsonOptions)
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
