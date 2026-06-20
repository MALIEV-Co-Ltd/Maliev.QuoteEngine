using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Account;
using Maliev.QuoteEngine.Shared.Agent;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

[Collection(QuoteEngineEndpointTestCollection.Name)]
public sealed class QuoteAgentEndpointTests(QuoteEngineWebApplicationFactory factory)
    : IClassFixture<QuoteEngineWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid CheckoutBillingAddressId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid CheckoutShippingAddressId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task Agent_health_reports_ready_when_chatbot_service_is_available()
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

        var response = await client.GetAsync("/quote/v1/agent/health");
        var body = await response.Content.ReadFromJsonAsync<QuoteAgentHealthResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("ready", body.Status);
        Assert.True(body.ChatbotServiceAvailable);
    }

    [Fact]
    public async Task Agent_health_reports_unavailable_when_chatbot_service_is_offline()
    {
        var chatbot = new RecordingChatbotServiceClient { HealthAvailable = false };
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
            });
        });
        using var client = scopedFactory.CreateClient();

        var response = await client.GetAsync("/quote/v1/agent/health");
        var body = await response.Content.ReadFromJsonAsync<QuoteAgentHealthResponse>();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("unavailable", body.Status);
        Assert.False(body.ChatbotServiceAvailable);
    }

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
        Assert.NotNull(body.UsageSnapshot);
        Assert.Equal(850_000, body.UsageSnapshot!.UsedTokens);
        Assert.Equal(2_000_000, body.UsageSnapshot.DailyTokenBudget);
        Assert.Equal("quote-engine", chatbot.LastInitiateRequest?.Channel);
        Assert.False(string.IsNullOrWhiteSpace(chatbot.LastSendRequest?.QuoteAgentContextToken));
    }

    [Fact]
    public async Task Agent_export_pdf_uses_mapped_chatbot_session_after_quote_engine_turn()
    {
        var quoteSessionId = Guid.NewGuid();
        var downstreamChatbotSessionId = Guid.Parse("3f35a7a7-1450-4b23-820a-0a97b85d5b0f");
        var chatbot = new RecordingChatbotServiceClient
        {
            ConversationMessages = new ChatbotConversationMessagesResponse
            {
                SessionId = downstreamChatbotSessionId,
                Language = "en",
                Messages =
                [
                    new ChatbotConversationMessageResponse
                    {
                        Role = "user",
                        Content = "I need a 3D printed bracket.",
                        CreatedAt = DateTimeOffset.Parse("2026-06-18T01:00:00Z")
                    },
                    new ChatbotConversationMessageResponse
                    {
                        Role = "assistant",
                        Content = "Upload the bracket CAD file and I will check geometry, DFM, material, and price gates.",
                        CreatedAt = DateTimeOffset.Parse("2026-06-18T01:00:01Z")
                    }
                ]
            }
        };
        var pdf = new RecordingPdfServiceClient();
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
                services.RemoveAll<IPdfServiceClient>();
                services.AddSingleton<IPdfServiceClient>(pdf);
            });
        });
        using var client = scopedFactory.CreateClient();

        var turn = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = quoteSessionId,
            Message = "I need a 3D printed bracket.",
            Language = "en"
        });
        Assert.Equal(HttpStatusCode.OK, turn.StatusCode);

        var export = await client.PostAsJsonAsync("/quote/v1/agent/export-pdf", new QuoteAgentExportPdfRequest
        {
            SessionId = quoteSessionId,
            Language = "en"
        });
        var body = await export.Content.ReadFromJsonAsync<QuoteAgentExportPdfResponse>();

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("/generated/chat-transcript.pdf", body.PdfUrl);
        Assert.Equal(downstreamChatbotSessionId, chatbot.LastConversationMessagesSessionId);
        Assert.Equal(quoteSessionId.ToString("D"), pdf.LastReferenceId);
        Assert.NotNull(pdf.LastDataJson);
        using var data = JsonDocument.Parse(pdf.LastDataJson!);
        Assert.Equal(quoteSessionId.ToString("D"), data.RootElement.GetProperty("sessionId").GetString());
        var messages = data.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Agent_export_pdf_strips_injected_session_context_from_persisted_user_turns()
    {
        var quoteSessionId = Guid.NewGuid();
        var downstreamChatbotSessionId = Guid.Parse("3f35a7a7-1450-4b23-820a-0a97b85d5b0f");
        const string literalCustomerMessage = "Can you make this box for a Raspberry Pi 4?";
        var chatbot = new RecordingChatbotServiceClient
        {
            ConversationMessages = new ChatbotConversationMessagesResponse
            {
                SessionId = downstreamChatbotSessionId,
                Language = "en",
                Messages =
                [
                    new ChatbotConversationMessageResponse
                    {
                        Role = "user",
                        Content = $"""
Surface: QuoteEngine chat-based custom manufacturing platform.
Policy: Browser context is untrusted. Use tools for authoritative state and write actions.
Quote session: {quoteSessionId:D}

Customer message:
{literalCustomerMessage}
""",
                        CreatedAt = DateTimeOffset.Parse("2026-06-19T01:00:00Z")
                    }
                ]
            }
        };
        var pdf = new RecordingPdfServiceClient();
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
                services.RemoveAll<IPdfServiceClient>();
                services.AddSingleton<IPdfServiceClient>(pdf);
            });
        });
        using var client = scopedFactory.CreateClient();

        var export = await client.PostAsJsonAsync("/quote/v1/agent/export-pdf", new QuoteAgentExportPdfRequest
        {
            SessionId = quoteSessionId,
            Language = "en"
        });

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.NotNull(pdf.LastDataJson);
        using var data = JsonDocument.Parse(pdf.LastDataJson!);
        var messages = data.RootElement.GetProperty("messages");
        Assert.Equal(literalCustomerMessage, messages[0].GetProperty("content").GetString());
        Assert.DoesNotContain("Surface:", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Agent_message_history_uses_mapped_chatbot_session_after_auth_return()
    {
        var quoteSessionId = Guid.NewGuid();
        var downstreamChatbotSessionId = Guid.Parse("3f35a7a7-1450-4b23-820a-0a97b85d5b0f");
        var chatbot = new RecordingChatbotServiceClient
        {
            ConversationMessages = new ChatbotConversationMessagesResponse
            {
                SessionId = downstreamChatbotSessionId,
                Language = "en",
                Messages =
                [
                    new ChatbotConversationMessageResponse
                    {
                        Role = "user",
                        Content = "I need a 3D printed bracket.",
                        CreatedAt = DateTimeOffset.Parse("2026-06-18T01:00:00Z")
                    },
                    new ChatbotConversationMessageResponse
                    {
                        Role = "tool",
                        Content = "internal tool trace",
                        CreatedAt = DateTimeOffset.Parse("2026-06-18T01:00:00Z")
                    },
                    new ChatbotConversationMessageResponse
                    {
                        Role = "assistant",
                        Content = "Upload the bracket CAD file and I will check geometry, DFM, material, and price gates.",
                        CreatedAt = DateTimeOffset.Parse("2026-06-18T01:00:01Z")
                    }
                ]
            }
        };
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
            });
        });
        using var client = scopedFactory.CreateClient();

        var turn = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = quoteSessionId,
            Message = "I need a 3D printed bracket.",
            Language = "en"
        });
        Assert.Equal(HttpStatusCode.OK, turn.StatusCode);

        var history = await client.GetFromJsonAsync<QuoteAgentMessageHistoryResponse>(
            $"/quote/v1/agent/sessions/{quoteSessionId:D}/messages",
            JsonOptions);

        Assert.NotNull(history);
        Assert.Equal(quoteSessionId, history.SessionId);
        Assert.Equal(downstreamChatbotSessionId, chatbot.LastConversationMessagesSessionId);
        Assert.Equal(2, history.Messages.Count);
        Assert.Equal("user", history.Messages[0].Role);
        Assert.Equal("assistant", history.Messages[1].Role);
        Assert.DoesNotContain(history.Messages, message => message.Role.Equals("tool", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Agent_message_history_uses_persisted_mapping_when_session_store_is_cold()
    {
        var quoteSessionId = Guid.NewGuid();
        var downstreamChatbotSessionId = Guid.Parse("3f35a7a7-1450-4b23-820a-0a97b85d5b0f");
        var conversationMap = new RecordingQuoteAgentConversationMap();
        await conversationMap.StoreChatbotSessionIdAsync(quoteSessionId, downstreamChatbotSessionId, CancellationToken.None);
        var chatbot = new RecordingChatbotServiceClient
        {
            ConversationMessages = new ChatbotConversationMessagesResponse
            {
                SessionId = downstreamChatbotSessionId,
                Language = "en",
                Messages =
                [
                    new ChatbotConversationMessageResponse
                    {
                        Role = "user",
                        Content = "Continue my Make Studio project.",
                        CreatedAt = DateTimeOffset.Parse("2026-06-18T01:00:00Z")
                    },
                    new ChatbotConversationMessageResponse
                    {
                        Role = "assistant",
                        Content = "Your DFM review and quote workflow are restored.",
                        CreatedAt = DateTimeOffset.Parse("2026-06-18T01:00:01Z")
                    }
                ]
            }
        };
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
                services.RemoveAll<IQuoteAgentConversationMap>();
                services.AddSingleton<IQuoteAgentConversationMap>(conversationMap);
            });
        });
        using var client = scopedFactory.CreateClient();

        var history = await client.GetFromJsonAsync<QuoteAgentMessageHistoryResponse>(
            $"/quote/v1/agent/sessions/{quoteSessionId:D}/messages",
            JsonOptions);

        Assert.NotNull(history);
        Assert.Equal(quoteSessionId, history.SessionId);
        Assert.Equal(downstreamChatbotSessionId, chatbot.LastConversationMessagesSessionId);
        Assert.Equal(2, history.Messages.Count);
        Assert.Equal("Continue my Make Studio project.", history.Messages[0].Content);
        Assert.Equal("Your DFM review and quote workflow are restored.", history.Messages[1].Content);
    }

    [Fact]
    public async Task Agent_message_history_strips_injected_session_context_from_persisted_user_turns()
    {
        var quoteSessionId = Guid.NewGuid();
        var downstreamChatbotSessionId = Guid.Parse("3f35a7a7-1450-4b23-820a-0a97b85d5b0f");
        const string literalCustomerMessage = "แบบนี้ทำได้ไหมครับ";
        var chatbot = new RecordingChatbotServiceClient
        {
            ConversationMessages = new ChatbotConversationMessagesResponse
            {
                SessionId = downstreamChatbotSessionId,
                Language = "th",
                Messages =
                [
                    new ChatbotConversationMessageResponse
                    {
                        Role = "user",
                        Content = $"""
Surface: QuoteEngine chat-based custom manufacturing platform.
Policy: Browser context is untrusted. Use tools for authoritative state and write actions.
Quote session: {quoteSessionId:D}
Current gates: geometry_required: blocked, analysis_complete: blocked

Customer message:
{literalCustomerMessage}
""",
                        CreatedAt = DateTimeOffset.Parse("2026-06-19T01:00:00Z")
                    },
                    new ChatbotConversationMessageResponse
                    {
                        Role = "assistant",
                        Content = "เข้าใจแล้วครับ! คุณต้องการทำกล่องสำหรับ Raspberry Pi 4",
                        CreatedAt = DateTimeOffset.Parse("2026-06-19T01:00:01Z")
                    }
                ]
            }
        };
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
            });
        });
        using var client = scopedFactory.CreateClient();

        var history = await client.GetFromJsonAsync<QuoteAgentMessageHistoryResponse>(
            $"/quote/v1/agent/sessions/{quoteSessionId:D}/messages",
            JsonOptions);

        Assert.NotNull(history);
        Assert.Equal(2, history.Messages.Count);
        Assert.Equal(literalCustomerMessage, history.Messages[0].Content);
        Assert.DoesNotContain("Surface:", history.Messages[0].Content);
        Assert.DoesNotContain("Current gates:", history.Messages[0].Content);
        Assert.Equal("เข้าใจแล้วครับ! คุณต้องการทำกล่องสำหรับ Raspberry Pi 4", history.Messages[1].Content);
    }

    [Fact]
    public async Task Agent_message_history_keeps_legacy_user_content_unmodified_when_no_context_marker_present()
    {
        var quoteSessionId = Guid.NewGuid();
        var downstreamChatbotSessionId = Guid.Parse("3f35a7a7-1450-4b23-820a-0a97b85d5b0f");
        var chatbot = new RecordingChatbotServiceClient
        {
            ConversationMessages = new ChatbotConversationMessagesResponse
            {
                SessionId = downstreamChatbotSessionId,
                Language = "en",
                Messages =
                [
                    new ChatbotConversationMessageResponse
                    {
                        Role = "user",
                        Content = "Plain legacy message with no injected context.",
                        CreatedAt = DateTimeOffset.Parse("2026-06-19T01:00:00Z")
                    }
                ]
            }
        };
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
            });
        });
        using var client = scopedFactory.CreateClient();

        var history = await client.GetFromJsonAsync<QuoteAgentMessageHistoryResponse>(
            $"/quote/v1/agent/sessions/{quoteSessionId:D}/messages",
            JsonOptions);

        Assert.NotNull(history);
        var message = Assert.Single(history.Messages);
        Assert.Equal("Plain legacy message with no injected context.", message.Content);
    }

    [Fact]
    public async Task Agent_message_forwards_optional_model_override_to_chatbot_service()
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
            Message = "Clean up this dictated sentence.",
            Language = "en",
            ModelName = "gemini-2.5-flash-lite"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gemini-2.5-flash-lite", chatbot.LastSendRequest?.ModelName);
    }

    [Fact]
    public async Task Agent_message_WhenEditingLastTurn_TruncatesCurrentChatbotSessionBeforeResubmitting()
    {
        var quoteSessionId = Guid.NewGuid();
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

        var initialResponse = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = quoteSessionId,
            Message = "Quote this as an FDM plastic part.",
            Language = "en"
        });

        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        var chatbotSessionId = chatbot.LastSendRequest?.SessionId;
        Assert.NotNull(chatbotSessionId);

        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = quoteSessionId,
            Message = "Corrected quote request after editing the last message.",
            Language = "en",
            EditLastTurn = true
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(chatbotSessionId, chatbot.LastTruncatedSessionId);
        Assert.Equal(chatbotSessionId, chatbot.LastSendRequest?.SessionId);
        Assert.Contains("Corrected quote request after editing the last message.", chatbot.LastSendRequest?.Content, StringComparison.Ordinal);
        Assert.Equal(new[] { "initiate", "send", "truncate", "send" }, chatbot.Operations);
    }

    [Fact]
    public async Task Agent_message_instructs_chatbot_to_reply_only_in_requested_language()
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
            Message = "How much for a 30mm SLA resin part?",
            Language = "en"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(chatbot.LastSendRequest);
        Assert.Contains(
            "Response language: English (en). Reply only in English; do not include Thai translations or repeat the same answer in another language.",
            chatbot.LastSendRequest!.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_message_for_authenticated_customer_includes_customer_service_memory_context()
    {
        var customerEmail = $"memory.{Guid.NewGuid():N}@example.com";
        var customerId = DeterministicCustomerId(customerEmail);
        var chatbot = new RecordingChatbotServiceClient();
        var customerClient = new MemoryCustomerServiceClient(customerId);
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
                services.RemoveAll<ICustomerServiceClient>();
                services.AddSingleton<ICustomerServiceClient>(customerClient);
            });
        });
        using var client = scopedFactory.CreateClient();

        var signIn = await client.GetAsync($"/test/sign-in?email={Uri.EscapeDataString(customerEmail)}");
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            Message = "Quote another functional prototype.",
            Language = "en"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(customerId, customerClient.LastMemoryCustomerId);
        Assert.NotNull(chatbot.LastSendRequest);
        Assert.Contains("Customer memory:", chatbot.LastSendRequest!.Content, StringComparison.Ordinal);
        Assert.Contains("preferred_material", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("PA12 nylon", chatbot.LastSendRequest.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_message_stream_returns_incremental_events_before_final_state()
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
        using var request = new HttpRequestMessage(HttpMethod.Post, "/quote/v1/agent/messages/stream")
        {
            Content = JsonContent.Create(new QuoteAgentMessageRequest
            {
                Message = "I need a 3D printed bracket.",
                Language = "en"
            })
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();
        var events = body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<QuoteAgentStreamEvent>(line, JsonOptions))
            .Where(streamEvent => streamEvent is not null)
            .Select(streamEvent => streamEvent!)
            .ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        Assert.True(events.Count >= 3);
        Assert.Equal("started", events[0].Type);
        Assert.Contains(events, streamEvent => streamEvent.Type == "delta" && !string.IsNullOrWhiteSpace(streamEvent.Delta));
        var final = Assert.Single(events, streamEvent => streamEvent.Type == "final");
        Assert.NotNull(final.Response);
        Assert.Contains("bracket", final.Response.AssistantText, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(final.Response.UsageSnapshot);
        Assert.Equal(850_000, final.Response.UsageSnapshot!.UsedTokens);
        Assert.Contains(final.Response.Gates, gate => gate.Code == "geometry_required" && gate.Status == "blocked");
        Assert.False(string.IsNullOrWhiteSpace(chatbot.LastStreamRequest?.QuoteAgentContextToken));
    }

    [Fact]
    public async Task Agent_message_stream_with_edit_last_turn_truncates_chatbot_turn_before_resubmitting()
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
        using var request = new HttpRequestMessage(HttpMethod.Post, "/quote/v1/agent/messages/stream")
        {
            Content = JsonContent.Create(new QuoteAgentMessageRequest
            {
                SessionId = Guid.NewGuid(),
                Message = "Actually quote this as CNC aluminum, not FDM plastic.",
                Language = "en",
                EditLastTurn = true
            }, options: JsonOptions)
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        _ = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Guid.Parse("3f35a7a7-1450-4b23-820a-0a97b85d5b0f"), chatbot.LastTruncatedSessionId);
        Assert.NotNull(chatbot.LastStreamRequest);
        Assert.Contains("CNC aluminum", chatbot.LastStreamRequest!.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "initiate", "truncate", "stream" }, chatbot.Operations);
    }

    [Fact]
    public async Task Agent_message_with_edit_last_turn_does_not_resubmit_when_truncate_fails()
    {
        var chatbot = new RecordingChatbotServiceClient
        {
            TruncateLastTurnResult = false
        };
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
            SessionId = Guid.NewGuid(),
            Message = "Correct the previous manufacturing plan.",
            Language = "en",
            EditLastTurn = true
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var turn = await response.Content.ReadFromJsonAsync<QuoteAgentTurnResponse>(JsonOptions);
        Assert.NotNull(turn);
        Assert.Contains("couldn't safely roll back", turn!.AssistantText, StringComparison.OrdinalIgnoreCase);
        Assert.Null(chatbot.LastSendRequest);
        Assert.Equal(new[] { "initiate", "truncate" }, chatbot.Operations);
    }

    [Fact]
    public async Task Agent_message_stream_with_attachment_does_not_emit_callback_without_explicit_callback_base_url()
    {
        var chatbot = new RecordingChatbotServiceClient();
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["QuoteAgent:EnableThinkingCallbacks"] = "true"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
            });
        });
        using var client = scopedFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/quote/v1/agent/messages/stream")
        {
            Content = JsonContent.Create(new QuoteAgentMessageRequest
            {
                Message = "Please quote this hand sketch.",
                Language = "en",
                Attachments =
                [
                    new QuoteAgentAttachmentDto
                    {
                        FileName = "manufacturing-sketch.png",
                        ContentType = "image/png",
                        FileSizeBytes = 120_000,
                        Kind = "sketch",
                        Url = "https://files.example.test/manufacturing-sketch.png"
                    }
                ]
            })
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        _ = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(chatbot.LastStreamRequest);
        Assert.Null(chatbot.LastStreamRequest.CallbackUrl);
        var attachment = Assert.Single(chatbot.LastStreamRequest.Attachments!);
        Assert.Equal("image", attachment.Type);
        Assert.Equal("https://files.example.test/manufacturing-sketch.png", attachment.Url);
        Assert.Equal("image/png", attachment.MimeType);
    }

    [Fact]
    public async Task Agent_message_stream_with_uploaded_sketch_prefers_signed_storage_url_over_inline_preview()
    {
        var chatbot = new RecordingChatbotServiceClient();
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
                services.RemoveAll<QuoteUploadServiceClient>();
                services.AddSingleton<QuoteUploadServiceClient>(new RecordingUploadServiceClient());
            });
        });
        using var client = scopedFactory.CreateClient();
        var inlinePreview = $"data:image/png;base64,{new string('A', 1_200)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/quote/v1/agent/messages/stream")
        {
            Content = JsonContent.Create(new QuoteAgentMessageRequest
            {
                Message = "Please quote this hand sketch.",
                Language = "en",
                Attachments =
                [
                    new QuoteAgentAttachmentDto
                    {
                        FileName = "manufacturing-sketch.png",
                        ContentType = "image/png",
                        FileSizeBytes = 120_000,
                        Kind = "sketch",
                        Url = inlinePreview,
                        StoragePath = "quotes/temp/session/manufacturing-sketch.png"
                    }
                ]
            })
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        _ = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(chatbot.LastStreamRequest);
        // Content must stay within the downstream ChatbotService request limit (8000 chars)
        // so the BFF never triggers a 400; large inline image previews are replaced by signed URLs.
        Assert.True(chatbot.LastStreamRequest.Content.Length <= 8000);
        Assert.Contains("Please quote this hand sketch.", chatbot.LastStreamRequest.Content, StringComparison.Ordinal);
        var attachment = Assert.Single(chatbot.LastStreamRequest!.Attachments!);
        Assert.Equal("https://upload.example.test/download/quotes%2Ftemp%2Fsession%2Fmanufacturing-sketch.png", attachment.Url);
        Assert.False(attachment.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase));
        Assert.True(attachment.Url.Length < 10_000);
    }

    [Fact]
    public async Task Agent_message_stream_reattaches_recent_workbench_artifact_for_follow_up_turns()
    {
        var chatbot = new RecordingChatbotServiceClient();
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
                services.RemoveAll<QuoteUploadServiceClient>();
                services.AddSingleton<QuoteUploadServiceClient>(new RecordingUploadServiceClient());
            });
        });
        using var client = scopedFactory.CreateClient();
        var sessionId = Guid.NewGuid();
        var inlinePreview = $"data:image/png;base64,{new string('A', 1_200)}";

        using (var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/quote/v1/agent/messages/stream")
        {
            Content = JsonContent.Create(new QuoteAgentMessageRequest
            {
                SessionId = sessionId,
                Message = "Please quote this hand sketch.",
                Language = "en",
                Attachments =
                [
                    new QuoteAgentAttachmentDto
                    {
                        FileName = "manufacturing-sketch.png",
                        ContentType = "image/png",
                        FileSizeBytes = 120_000,
                        Kind = "sketch",
                        Url = inlinePreview,
                        StoragePath = "agent/sketches/abc/manufacturing-sketch.png"
                    }
                ]
            }, options: JsonOptions)
        })
        {
            using var firstResponse = await client.SendAsync(firstRequest, HttpCompletionOption.ResponseHeadersRead);
            _ = await firstResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        }

        using var followUpRequest = new HttpRequestMessage(HttpMethod.Post, "/quote/v1/agent/messages/stream")
        {
            Content = JsonContent.Create(new QuoteAgentMessageRequest
            {
                SessionId = sessionId,
                Message = "Please re-check the sketch in the workbench before answering.",
                Language = "en"
            }, options: JsonOptions)
        };

        using var followUpResponse = await client.SendAsync(followUpRequest, HttpCompletionOption.ResponseHeadersRead);
        _ = await followUpResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, followUpResponse.StatusCode);
        Assert.NotNull(chatbot.LastStreamRequest);
        var attachment = Assert.Single(chatbot.LastStreamRequest!.Attachments!);
        Assert.Equal("image", attachment.Type);
        Assert.Equal("image/png", attachment.MimeType);
        Assert.Equal("manufacturing-sketch.png", attachment.Filename);
        Assert.Equal(
            "https://upload.example.test/download/agent%2Fsketches%2Fabc%2Fmanufacturing-sketch.png",
            attachment.Url);
    }

    [Fact]
    public async Task Agent_message_stream_handles_ui_language_change_without_chatbot_or_preview_tools()
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
        using var request = new HttpRequestMessage(HttpMethod.Post, "/quote/v1/agent/messages/stream")
        {
            Content = JsonContent.Create(new QuoteAgentMessageRequest
            {
                Message = "change the UI language to English for me.",
                Language = "th"
            }, options: JsonOptions)
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();
        var events = body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<QuoteAgentStreamEvent>(line, JsonOptions))
            .Where(streamEvent => streamEvent is not null)
            .Select(streamEvent => streamEvent!)
            .ToList();
        var final = Assert.Single(events, streamEvent => streamEvent.Type == "final").Response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(final);
        Assert.Equal("en-US", final.UiCulture);
        Assert.Equal("en", final.Language);
        Assert.Contains("English", final.AssistantText, StringComparison.OrdinalIgnoreCase);
        Assert.Null(chatbot.LastStreamRequest);
        Assert.Null(chatbot.LastSendRequest);
        Assert.DoesNotContain(final.Artifacts, artifact => artifact.ArtifactType == "viewer");
    }

    [Fact]
    public async Task Agent_sketch_upload_returns_signed_url_and_download_redirect_is_session_scoped()
    {
        var uploadClient = new RecordingUploadServiceClient();
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<QuoteUploadServiceClient>();
                services.AddSingleton<QuoteUploadServiceClient>(uploadClient);
            });
        });
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var sessionId = Guid.NewGuid();
        using var form = new MultipartFormDataContent();
        using var pngContent = new ByteArrayContent([1, 2, 3, 4]);
        pngContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(pngContent, "file", "Manufacturing-sketch.png");

        var uploadResponse = await client.PostAsync($"/quote/v1/agent/sessions/{sessionId:D}/sketches", form);
        var upload = await uploadResponse.Content.ReadFromJsonAsync<UploadSketchResponse>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);
        Assert.NotNull(upload);
        Assert.Equal($"agent/sketches/{sessionId:N}/Manufacturing-sketch.png", upload.StoragePath);
        Assert.Equal(upload.StoragePath, uploadClient.LastInitiatedStoragePath);
        Assert.Equal(upload.StoragePath, uploadClient.LastStreamedStoragePath);
        Assert.Equal(
            $"https://upload.example.test/download/{Uri.EscapeDataString(upload.StoragePath)}",
            upload.Url);

        var redirectResponse = await client.GetAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/download?path={Uri.EscapeDataString(upload.StoragePath)}");
        Assert.Equal(HttpStatusCode.Redirect, redirectResponse.StatusCode);
        Assert.Equal(upload.Url, redirectResponse.Headers.Location?.ToString());

        var otherSessionPath = $"agent/sketches/{Guid.NewGuid():N}/Manufacturing-sketch.png";
        var blockedResponse = await client.GetAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/download?path={Uri.EscapeDataString(otherSessionPath)}");
        Assert.Equal(HttpStatusCode.BadRequest, blockedResponse.StatusCode);
    }

    [Fact]
    public async Task Agent_artifact_download_allows_registered_pdf_storage_path_without_session_prefix()
    {
        var uploadClient = new RecordingUploadServiceClient();
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<QuoteUploadServiceClient>();
                services.AddSingleton<QuoteUploadServiceClient>(uploadClient);
            });
        });
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var sessionId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var pdfStoragePath = $"customers/{customerId:D}/quotes/{sessionId:D}/drawings/cover motion.pdf";

        var registerResponse = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/attachments",
            new QuoteAgentAttachmentRegisterRequest
            {
                Message = "Analyze this PDF drawing.",
                Language = "en",
                Attachments =
                [
                    new QuoteAgentAttachmentDto
                    {
                        FileName = "cover motion.pdf",
                        ContentType = "application/pdf",
                        FileSizeBytes = 128_000,
                        Kind = "drawing",
                        UploadId = "upload-pdf-drawing",
                        StoragePath = pdfStoragePath
                    }
                ]
            },
            JsonOptions);
        var state = await registerResponse.Content.ReadFromJsonAsync<QuoteAgentStateResponse>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        Assert.NotNull(state);
        Assert.Contains(state.Artifacts, artifact =>
            artifact.Title == "cover motion.pdf" &&
            artifact.Metadata.TryGetValue("storagePath", out var storagePath) &&
            storagePath == pdfStoragePath);

        var redirectResponse = await client.GetAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/download?path={Uri.EscapeDataString(pdfStoragePath)}");

        Assert.Equal(HttpStatusCode.Redirect, redirectResponse.StatusCode);
        var redirectLocation = redirectResponse.Headers.Location?.ToString();
        Assert.NotNull(redirectLocation);
        Assert.StartsWith("https://upload.example.test/download/", redirectLocation, StringComparison.Ordinal);
        Assert.Equal(
            pdfStoragePath,
            Uri.UnescapeDataString(redirectLocation["https://upload.example.test/download/".Length..]));

        var unregisteredPath = $"customers/{customerId:D}/quotes/{sessionId:D}/drawings/other.pdf";
        var blockedResponse = await client.GetAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/download?path={Uri.EscapeDataString(unregisteredPath)}");

        Assert.Equal(HttpStatusCode.BadRequest, blockedResponse.StatusCode);
    }

    [Fact]
    public async Task Agent_message_stream_returns_fallback_final_state_when_chatbot_stream_fails()
    {
        var chatbot = new RecordingChatbotServiceClient
        {
            ThrowStreamException = true
        };
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
            });
        });
        using var client = scopedFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/quote/v1/agent/messages/stream")
        {
            Content = JsonContent.Create(new QuoteAgentMessageRequest
            {
                Message = "Can you quote this CNC housing?",
                Language = "en"
            })
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();
        var events = body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<QuoteAgentStreamEvent>(line, JsonOptions))
            .Where(streamEvent => streamEvent is not null)
            .Select(streamEvent => streamEvent!)
            .ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("started", events[0].Type);
        Assert.Contains(events, streamEvent => streamEvent.Type == "delta" && !string.IsNullOrWhiteSpace(streamEvent.Delta));
        var final = Assert.Single(events, streamEvent => streamEvent.Type == "final");
        Assert.NotNull(final.Response);
        Assert.Contains(final.Response.Gates, gate => gate.Code == "geometry_required" && gate.Status == "blocked");
        Assert.False(string.IsNullOrWhiteSpace(chatbot.LastStreamRequest?.QuoteAgentContextToken));
        Assert.DoesNotContain(events, streamEvent => streamEvent.Type == "error");
    }

    [Fact]
    public async Task Agent_message_with_cad_attachment_materializes_analysis_without_premature_price()
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
        Assert.Contains(body.Gates, gate => gate.Code == "configuration_complete" && gate.Status != "passed");
        Assert.Contains(body.Gates, gate => gate.Code == "priced" && gate.Status != "passed");
        Assert.Contains(body.Artifacts, artifact => artifact.ArtifactType == "viewer" && artifact.Status == "ready");
        Assert.Contains(body.Artifacts, artifact => artifact.ArtifactType == "dfm" && artifact.Status == "ready");
        Assert.DoesNotContain(body.Artifacts, artifact => artifact.ArtifactType == "pricing");
        Assert.Contains(body.UiDirectives, directive =>
            directive.Panel == "artifacts" &&
            directive.TargetType == "viewer" &&
            directive.HighlightKey.StartsWith("part:", StringComparison.OrdinalIgnoreCase) &&
            directive.CanvasX.HasValue &&
            directive.CanvasY.HasValue);

        var state = await client.GetFromJsonAsync<QuoteAgentStateResponse>(
            $"/quote/v1/agent/sessions/{body.SessionId:D}");
        Assert.NotNull(state);
        Assert.Single(state.Parts);
        Assert.Null(state.Estimate);
        Assert.Contains(state.UiDirectives, directive => directive.Panel == "artifacts" && directive.TargetType == "viewer");
        Assert.NotNull(chatbot.LastSendRequest);
        Assert.Contains("Current parts: fixture.stl", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("Current artifacts:", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Current estimate:", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_focus_ui_tool_returns_panel_directive_for_client_highlight()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var state = await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_focus_ui",
            new Dictionary<string, JsonElement>
            {
                ["panel"] = JsonSerializer.SerializeToElement("artifacts", JsonOptions),
                ["target_type"] = JsonSerializer.SerializeToElement("dfm_issue", JsonOptions),
                ["target_id"] = JsonSerializer.SerializeToElement("THIN_WALL", JsonOptions),
                ["highlight_key"] = JsonSerializer.SerializeToElement("dfm", JsonOptions),
                ["label"] = JsonSerializer.SerializeToElement("Show the DFM issue.", JsonOptions),
                ["canvas_x"] = JsonSerializer.SerializeToElement(0.42, JsonOptions),
                ["canvas_y"] = JsonSerializer.SerializeToElement(0.35, JsonOptions)
            });

        var directive = Assert.Single(state.UiDirectives, item => item.HighlightKey == "dfm");
        Assert.Equal("artifacts", directive.Panel);
        Assert.Equal("dfm_issue", directive.TargetType);
        Assert.Equal("THIN_WALL", directive.TargetId);
        Assert.Equal(0.42, directive.CanvasX);
        Assert.Equal(0.35, directive.CanvasY);
    }

    [Fact]
    public async Task Agent_message_rejects_book_length_customer_prompt()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            Message = new string('x', QuoteAgentTextLimits.MaxMessageCharacters + 1),
            Language = "en"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
    public async Task Agent_attachment_registration_materializes_uploaded_cad_state()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/attachments",
            new QuoteAgentAttachmentRegisterRequest
            {
                Message = "Customer uploaded a STEP file for 25 aluminum brackets.",
                Language = "en",
                Attachments =
                [
                    new QuoteAgentAttachmentDto
                    {
                        FileName = "uploaded-bracket.step",
                        ContentType = "model/step",
                        FileSizeBytes = 380_000,
                        Kind = "cad",
                        UploadId = "upload-agent-sync",
                        StoragePath = "quotes/temp/session/uploaded-bracket.step"
                    }
                ]
            });
        var body = await response.Content.ReadFromJsonAsync<QuoteAgentStateResponse>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(sessionId, body.SessionId);
        Assert.Contains(body.Attachments, attachment =>
            attachment.FileName == "uploaded-bracket.step" &&
            attachment.SatisfiesGeometryGate);
        Assert.Contains(body.Parts, part =>
            part.FileName == "uploaded-bracket.step" &&
            part.UploadId == "upload-agent-sync");
        Assert.Contains(body.Artifacts, artifact => artifact.ArtifactType == "viewer");
        Assert.Contains(body.Gates, gate => gate.Code == "geometry_required" && gate.Status == "passed");
    }

    [Fact]
    public async Task Agent_attachment_registration_keeps_supplemental_files_out_of_geometry_gate()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/attachments",
            new QuoteAgentAttachmentRegisterRequest
            {
                Message = "Customer uploaded a sketch for an aluminum bracket.",
                Language = "en",
                Attachments =
                [
                    new QuoteAgentAttachmentDto
                    {
                        FileName = "bracket-sketch.png",
                        ContentType = "image/png",
                        FileSizeBytes = 120_000,
                        Kind = "photo",
                        UploadId = "upload-agent-supplemental",
                        StoragePath = "quotes/temp/session/bracket-sketch.png"
                    }
                ]
            });
        var body = await response.Content.ReadFromJsonAsync<QuoteAgentStateResponse>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Contains(body.Attachments, attachment =>
            attachment.FileName == "bracket-sketch.png" &&
            !attachment.SatisfiesGeometryGate);
        Assert.Empty(body.Parts);
        Assert.Contains(body.Artifacts, artifact =>
            artifact.ArtifactType == "analysis" &&
            artifact.Status == "needs_geometry");
        var sketchArtifact = Assert.Single(body.Artifacts, artifact => artifact.Title == "bracket-sketch.png");
        Assert.Equal("sketch", sketchArtifact.ArtifactType);
        Assert.Equal("ready", sketchArtifact.Status);
        Assert.Equal("photo", sketchArtifact.Metadata["kind"]);
        Assert.Equal("false", sketchArtifact.Metadata["satisfiesGeometryGate"]);
        Assert.Equal("image/png", sketchArtifact.Metadata["contentType"]);
        Assert.Contains(body.Gates, gate => gate.Code == "geometry_required" && gate.Status == "blocked");
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
        var sketchArtifact = Assert.Single(body.Artifacts, artifact => artifact.Title == "bracket-sketch.jpg");
        Assert.Equal("sketch", sketchArtifact.ArtifactType);
        Assert.Equal("https://files.example.test/bracket-sketch.jpg", sketchArtifact.Url);
        Assert.Equal("false", sketchArtifact.Metadata["satisfiesGeometryGate"]);

        var state = await client.GetFromJsonAsync<QuoteAgentStateResponse>(
            $"/quote/v1/agent/sessions/{body.SessionId:D}");
        Assert.NotNull(state);
        Assert.Empty(state.Parts);
        Assert.Single(state.Attachments);
    }

    [Fact]
    public async Task Agent_supplemental_drawing_extracts_structured_requirements_without_satisfying_geometry()
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
            Message = "Need 50 of these brackets in 3mm aluminum. Overall 50 mm x 30 mm, 2x Ø6 thru holes, 8 x 16 mm slot, ±0.1 mm, clear anodize. Can you quote a 7 day lead time?",
            Language = "en",
            Attachments =
            [
                new QuoteAgentAttachmentDto
                {
                    FileName = "bracket-sketch.jpg",
                    ContentType = "image/jpeg",
                    FileSizeBytes = 1_800_000,
                    Kind = "sketch",
                    Url = "https://files.example.test/bracket-sketch.jpg"
                },
                new QuoteAgentAttachmentDto
                {
                    FileName = "bracket-drawing.pdf",
                    ContentType = "application/pdf",
                    FileSizeBytes = 520_000,
                    Kind = "drawing",
                    Url = "https://files.example.test/bracket-drawing.pdf"
                }
            ]
        });
        var body = await response.Content.ReadFromJsonAsync<QuoteAgentTurnResponse>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Contains(body.Gates, gate => gate.Code == "geometry_required" && gate.Status == "blocked");
        Assert.DoesNotContain(body.Gates, gate => gate.Code == "priced" && gate.Status == "passed");

        var analysis = Assert.Single(body.Artifacts, artifact => artifact.ArtifactType == "analysis");
        Assert.Contains(body.Artifacts, artifact =>
            artifact.ArtifactType == "sketch" &&
            artifact.Title == "bracket-sketch.jpg" &&
            artifact.Metadata["contentType"] == "image/jpeg");
        Assert.Contains(body.Artifacts, artifact =>
            artifact.ArtifactType == "drawing" &&
            artifact.Title == "bracket-drawing.pdf" &&
            artifact.Metadata["contentType"] == "application/pdf");
        Assert.Equal("needs_geometry", analysis.Status);
        Assert.Equal("not_satisfied_by_supplemental_files", analysis.Metadata["geometryGate"]);
        Assert.Equal("sketch, technical_drawing", analysis.Metadata["sourceTypes"]);
        Assert.Equal("50", analysis.Metadata["quantity"]);
        Assert.Equal("cnc", analysis.Metadata["process"]);
        Assert.Equal("al6061", analysis.Metadata["material"]);
        Assert.Equal("clear", analysis.Metadata["color"]);
        Assert.Equal("±0.1 mm", analysis.Metadata["tolerance"]);
        Assert.Equal("3 mm", analysis.Metadata["thicknessHint"]);
        Assert.Contains("50 mm", analysis.Metadata["dimensionHints"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("8 x 16 mm", analysis.Metadata["dimensionHints"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mounting holes", analysis.Metadata["featureHints"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("slot", analysis.Metadata["featureHints"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deadline-sensitive", analysis.Metadata["manufacturingNotes"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("STANDARD", analysis.Metadata["leadTime"]);
        Assert.Equal("7", analysis.Metadata["leadTimeDays"]);
        Assert.Equal("true", analysis.Metadata["geometryRequired"]);
        Assert.Equal("false", analysis.Metadata["usableForFinalPricing"]);

        var summaryJson = await ExecuteToolAsync(client, body.SessionId, "quote_get_project_summary");
        var summary = JsonSerializer.Deserialize<QuoteAgentProjectSummaryResponse>(summaryJson, JsonOptions);
        Assert.NotNull(summary);
        Assert.Equal(2, summary.AttachmentCount);
        Assert.Equal("sketch, technical_drawing", summary.RequirementFacts["sourceTypes"]);
        Assert.Equal("50", summary.RequirementFacts["quantity"]);
        Assert.Equal("3 mm", summary.RequirementFacts["thicknessHint"]);
        Assert.Equal("STANDARD", summary.RequirementFacts["leadTime"]);
        Assert.Equal("7", summary.RequirementFacts["leadTimeDays"]);
        Assert.Contains("slot", summary.RequirementFacts["featureHints"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("geometry_required", summary.BlockingGateCodes);
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
        Assert.Contains(body.Gates, gate => gate.Code == "priced" && gate.Status != "passed");
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
        Assert.Contains(state.Gates, gate => gate.Code == "priced" && gate.Status != "passed");
        Assert.Contains(state.Artifacts, artifact => artifact.ArtifactType == "viewer" && artifact.Status == "ready");
        Assert.Contains(state.Artifacts, artifact => artifact.ArtifactType == "dfm" && artifact.Status == "ready");
        Assert.Null(state.Estimate);
    }

    [Fact]
    public async Task Agent_register_uploads_tool_supersedes_previous_dfm_blocked_geometry()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var blockedState = await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_register_uploads",
            new Dictionary<string, JsonElement>
            {
                ["requirements"] = JsonSerializer.SerializeToElement("Quote this STEP as 10 aluminum pieces. Local DFM found a thin wall risk.", JsonOptions),
                ["files"] = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        file_name = "bracket-rev-a.step",
                        content_type = "model/step",
                        file_size_bytes = 240_000,
                        kind = "cad",
                        upload_id = "rev-a-upload",
                        storage_path = "quotes/temp/session/rev-a-upload/bracket-rev-a.step"
                    }
                }, JsonOptions)
            });

        var blockedPart = Assert.Single(blockedState.Parts);
        Assert.NotEmpty(blockedPart.Findings);
        Assert.Contains(blockedState.Gates, gate => gate.Code == "dfm_reviewed" && gate.Status == "blocked");

        var fixedState = await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_register_uploads",
            new Dictionary<string, JsonElement>
            {
                ["requirements"] = JsonSerializer.SerializeToElement("Reuploaded corrected revision B with thicker walls. Quote 10 aluminum pieces.", JsonOptions),
                ["files"] = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        file_name = "bracket-rev-b.step",
                        content_type = "model/step",
                        file_size_bytes = 260_000,
                        kind = "cad",
                        upload_id = "rev-b-upload",
                        storage_path = "quotes/temp/session/rev-b-upload/bracket-rev-b.step",
                        supersedes_upload_id = "rev-a-upload"
                    }
                }, JsonOptions)
            });

        var fixedPart = Assert.Single(fixedState.Parts);
        Assert.Equal("bracket-rev-b.step", fixedPart.FileName);
        Assert.Equal("rev-b-upload", fixedPart.UploadId);
        Assert.Empty(fixedPart.Findings);
        Assert.DoesNotContain(fixedState.Parts, part => part.UploadId == "rev-a-upload");
        Assert.Contains(fixedState.Gates, gate => gate.Code == "dfm_reviewed" && gate.Status == "passed");
        Assert.Null(fixedState.Estimate);
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
        Assert.Contains(state.Artifacts, artifact =>
            artifact.ArtifactType == "sketch" &&
            artifact.Title == "bracket-sketch.jpg" &&
            artifact.Url == "https://files.example.test/bracket-sketch.jpg");
        Assert.Contains(state.Artifacts, artifact =>
            artifact.ArtifactType == "drawing" &&
            artifact.Title == "bracket-notes.pdf" &&
            artifact.Url == "quotes/temp/session/agent-upload-drawing/bracket-notes.pdf");
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

        var summary = await GetProjectSummaryAsync(client, sessionId);
        Assert.Equal(2, summary.AttachmentCount);
        Assert.Equal("sketch, technical_drawing", summary.RequirementFacts["sourceTypes"]);
        Assert.Equal("50", summary.RequirementFacts["quantity"]);
        Assert.Equal("cnc", summary.RequirementFacts["process"]);
        Assert.Equal("al6061", summary.RequirementFacts["material"]);
        Assert.Equal("STANDARD", summary.RequirementFacts["leadTime"]);
        Assert.Equal("true", summary.RequirementFacts["needsCadGeometry"]);
        Assert.Equal("bracket-sketch.jpg, bracket-notes.pdf", summary.RequirementFacts["supplementalFiles"]);
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
        Assert.Null(summary.EstimateTotal);
        Assert.Null(summary.EstimateCurrency);
        Assert.Contains("geometry_required", summary.PassedGateCodes);
        Assert.DoesNotContain("priced", summary.PassedGateCodes);
        Assert.Contains(summary.NextActions, action => action.Contains("Confirm process", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Agent_project_summary_tool_guides_late_order_checkout_and_payment_steps()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-summary-late@example.com");
        var sessionId = await StartPricedCadSessionAsync(client);

        var formalQuoteState = await ExecuteToolForStateAsync(client, sessionId, "quote_prepare_formal_quote");
        await ConfirmActionAsync(client, Assert.Single(formalQuoteState.ProposedActions).ActionId);

        var approvalSummary = await GetProjectSummaryAsync(client, sessionId);
        Assert.Contains(approvalSummary.NextActions, action =>
            action.Contains("approve", StringComparison.OrdinalIgnoreCase) &&
            action.Contains("quote", StringComparison.OrdinalIgnoreCase));

        var approvalState = await ExecuteToolForStateAsync(client, sessionId, "quote_approve_quote");
        await ConfirmActionAsync(client, Assert.Single(approvalState.ProposedActions).ActionId);

        var orderSummary = await GetProjectSummaryAsync(client, sessionId);
        Assert.Contains(orderSummary.NextActions, action =>
            action.Contains("order", StringComparison.OrdinalIgnoreCase));

        var orderState = await ExecuteToolForStateAsync(client, sessionId, "quote_create_order");
        var statusCountBeforeOrderConfirmation = factory.OrderStatusUpdates.Count;
        await ConfirmActionAsync(client, Assert.Single(orderState.ProposedActions).ActionId);

        var checkoutSummary = await GetProjectSummaryAsync(client, sessionId);
        Assert.Equal("Accepted", checkoutSummary.CurrentOrderStatus);
        Assert.False(string.IsNullOrWhiteSpace(checkoutSummary.CurrentOrderNumber));
        Assert.Equal($"/orders/{Uri.EscapeDataString(checkoutSummary.CurrentOrderNumber!)}", checkoutSummary.CurrentOrderUrl);
        Assert.Equal("Quote and payment", checkoutSummary.CurrentOrderMilestoneLabel);
        Assert.Equal("current", checkoutSummary.CurrentOrderMilestoneState);
        Assert.Equal(35, checkoutSummary.CurrentOrderMilestonePercent);
        Assert.Contains("payment confirmation", checkoutSummary.CurrentOrderMilestoneDescription, StringComparison.OrdinalIgnoreCase);
        var newStatusUpdates = factory.OrderStatusUpdates.Skip(statusCountBeforeOrderConfirmation).ToArray();
        Assert.Equal(
            ["Reviewing", "Reviewed", "Quoted", "Accepted"],
            newStatusUpdates.Select(update => update.Status).ToArray());
        Assert.Contains(checkoutSummary.NextActions, action =>
            action.Contains("checkout", StringComparison.OrdinalIgnoreCase) ||
            action.Contains("billing", StringComparison.OrdinalIgnoreCase));

        await ExecuteToolForStateAsync(
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

        var paymentSummary = await GetProjectSummaryAsync(client, sessionId);
        Assert.Contains(paymentSummary.NextActions, action =>
            action.Contains("payment", StringComparison.OrdinalIgnoreCase));

        var paymentState = await ExecuteToolForStateAsync(client, sessionId, "quote_start_payment");
        await ConfirmActionAsync(client, Assert.Single(paymentState.ProposedActions).ActionId);

        var completeSummary = await GetProjectSummaryAsync(client, sessionId);
        Assert.Contains(completeSummary.NextActions, action =>
            action.Contains("payment", StringComparison.OrdinalIgnoreCase) &&
            action.Contains("handoff", StringComparison.OrdinalIgnoreCase));

        factory.MarkOrderPaid(checkoutSummary.CurrentOrderNumber!);

        var paidSummary = await GetProjectSummaryAsync(client, sessionId);
        Assert.Equal("Paid", paidSummary.CurrentOrderStatus);
        Assert.Equal("Paid", paidSummary.CurrentPaymentStatus);
        Assert.Equal($"/orders/{Uri.EscapeDataString(checkoutSummary.CurrentOrderNumber!)}", paidSummary.CurrentOrderUrl);
        Assert.Equal("Manufacturing", paidSummary.CurrentOrderMilestoneLabel);
        Assert.Equal("current", paidSummary.CurrentOrderMilestoneState);
        Assert.Equal(55, paidSummary.CurrentOrderMilestonePercent);
        Assert.Contains("queued", paidSummary.CurrentOrderMilestoneDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(paidSummary.NextActions, action =>
            action.Contains("order status", StringComparison.OrdinalIgnoreCase) &&
            action.Contains("production tracking", StringComparison.OrdinalIgnoreCase));
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

        await ConfigureFirstPartForEstimateAsync(client, sessionId);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_calculate_estimate");

        Assert.NotNull(state.Estimate);
        Assert.True(state.Estimate.Total > 0);
        Assert.Equal("THB", state.Estimate.Currency);
        Assert.Contains(state.Gates, gate => gate.Code == "priced" && gate.Status == "passed");
        Assert.Contains(state.Artifacts, artifact => artifact.ArtifactType == "pricing" && artifact.Status == "ready");
    }

    [Fact]
    public async Task Agent_estimate_does_not_fall_back_to_prototype_pricing_in_production()
    {
        await using var productionFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AnonymousVisitor:SigningKey"] = "quote-agent-production-pricing-test-signing-key",
                    ["QuoteAgent:ContextSigningKey"] = "maliev-local-development-quote-agent-context-key"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostEnvironment>();
                services.AddSingleton<IHostEnvironment>(new QuoteEngineWebApplicationFactory.TestHostEnvironment(Environments.Production));

                services.RemoveAll<IQePricingServiceClient>();
                services.AddSingleton<IQePricingServiceClient>(new QuoteEngineWebApplicationFactory.EmptyPricingServiceClient());
            });
        });
        using var client = productionFactory.CreateClient();
        var sessionId = Guid.NewGuid();

        await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_register_uploads",
            new Dictionary<string, JsonElement>
            {
                ["requirements"] = JsonSerializer.SerializeToElement(
                    "Quote this STEP as 10 aluminum pieces with standard lead time.",
                    JsonOptions),
                ["files"] = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        file_name = "production-priced-housing.step",
                        content_type = "model/step",
                        file_size_bytes = 240_000,
                        kind = "cad",
                        upload_id = "production-estimate-upload-cad",
                        storage_path = "quotes/temp/session/production-estimate-upload-cad/production-priced-housing.step"
                    }
                }, JsonOptions)
            });
        await ConfigureFirstPartForEstimateAsync(client, sessionId);

        var estimateJson = await ExecuteToolAsync(client, sessionId, "quote_calculate_estimate");

        using var estimate = JsonDocument.Parse(estimateJson);
        Assert.Equal("pricing_available", estimate.RootElement.GetProperty("requiredGateCode").GetString());
        Assert.Equal("calculate_estimate", estimate.RootElement.GetProperty("actionType").GetString());
        Assert.True(estimate.RootElement.GetProperty("state").TryGetProperty("estimate", out var estimateElement));
        Assert.Equal(JsonValueKind.Null, estimateElement.ValueKind);
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

        await ConfigureFirstPartForEstimateAsync(client, sessionId);

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
        var createRequest = factory.LastQuotationCreateRequest;
        Assert.NotNull(createRequest);
        Assert.NotEqual(Guid.Empty, createRequest.CustomerId);
        var quotedLine = Assert.Single(createRequest.LineItems);
        Assert.Equal(25, quotedLine.Quantity);
        Assert.True(quotedLine.UnitPrice > 0);
        Assert.NotEqual(Guid.Empty, quotedLine.MaterialServiceId);
        Assert.Contains("fixture.step", quotedLine.Notes, StringComparison.OrdinalIgnoreCase);

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
    public async Task Agent_formal_quote_artifact_download_is_scoped_to_registered_pdf_storage_path()
    {
        var uploadClient = new RecordingUploadServiceClient();
        await using var scopedFactory = CreateAgentFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<QuoteUploadServiceClient>();
                services.AddSingleton<QuoteUploadServiceClient>(uploadClient);
            });
        });
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var signIn = await client.GetAsync("/test/sign-in?email=agent-quote-artifact@example.com");
        signIn.EnsureSuccessStatusCode();
        var sessionId = await StartPricedCadSessionAsync(client);

        var formalQuoteState = await ExecuteToolForStateAsync(client, sessionId, "quote_prepare_formal_quote");
        var formalQuoteResult = await ConfirmActionAsync(client, Assert.Single(formalQuoteState.ProposedActions).ActionId);

        Assert.NotNull(formalQuoteResult.State);
        var formalQuoteArtifact = Assert.Single(
            formalQuoteResult.State.Artifacts,
            artifact => artifact.ArtifactType == "formal_quote");
        Assert.True(
            formalQuoteArtifact.Metadata.TryGetValue("storagePath", out var storagePath),
            "Formal quote artifact should carry the QuotationService PDF storage path.");
        Assert.StartsWith("quotations/", storagePath, StringComparison.OrdinalIgnoreCase);

        var redirectResponse = await client.GetAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/download?path={Uri.EscapeDataString(storagePath)}");

        Assert.Equal(HttpStatusCode.Redirect, redirectResponse.StatusCode);
        var redirectLocation = redirectResponse.Headers.Location?.ToString();
        Assert.NotNull(redirectLocation);
        Assert.StartsWith("https://upload.example.test/download/", redirectLocation, StringComparison.Ordinal);
        Assert.Equal(
            storagePath,
            Uri.UnescapeDataString(redirectLocation["https://upload.example.test/download/".Length..]));

        var blockedPath = storagePath.Replace(".pdf", "-other.pdf", StringComparison.OrdinalIgnoreCase);
        var blockedResponse = await client.GetAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/download?path={Uri.EscapeDataString(blockedPath)}");

        Assert.Equal(HttpStatusCode.BadRequest, blockedResponse.StatusCode);
    }

    [Fact]
    public async Task Agent_formal_quote_artifact_download_is_scoped_to_session_customer()
    {
        var uploadClient = new RecordingUploadServiceClient();
        await using var scopedFactory = CreateAgentFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<QuoteUploadServiceClient>();
                services.AddSingleton<QuoteUploadServiceClient>(uploadClient);
            });
        });
        using var ownerClient = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var ownerSignIn = await ownerClient.GetAsync("/test/sign-in?email=agent-quote-owner@example.com");
        ownerSignIn.EnsureSuccessStatusCode();
        var sessionId = await StartPricedCadSessionAsync(ownerClient);

        var formalQuoteState = await ExecuteToolForStateAsync(ownerClient, sessionId, "quote_prepare_formal_quote");
        var formalQuoteResult = await ConfirmActionAsync(ownerClient, Assert.Single(formalQuoteState.ProposedActions).ActionId);

        Assert.NotNull(formalQuoteResult.State);
        var formalQuoteArtifact = Assert.Single(
            formalQuoteResult.State.Artifacts,
            artifact => artifact.ArtifactType == "formal_quote");
        Assert.True(
            formalQuoteArtifact.Metadata.TryGetValue("storagePath", out var storagePath),
            "Formal quote artifact should carry the QuotationService PDF storage path.");

        var ownerRedirectResponse = await ownerClient.GetAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/download?path={Uri.EscapeDataString(storagePath)}");
        Assert.Equal(HttpStatusCode.Redirect, ownerRedirectResponse.StatusCode);

        using var otherClient = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var otherSignIn = await otherClient.GetAsync("/test/sign-in?email=agent-quote-other@example.com");
        otherSignIn.EnsureSuccessStatusCode();

        var otherResponse = await otherClient.GetAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/download?path={Uri.EscapeDataString(storagePath)}");

        Assert.Equal(HttpStatusCode.BadRequest, otherResponse.StatusCode);
    }

    [Fact]
    public async Task Agent_payment_confirmation_after_order_sets_payment_gate_and_artifact()
    {
        await using var scopedFactory = CreateAgentFactory();
        factory.ClearPaymentIdempotencyKeys();
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
        var orderArtifact = Assert.Single(orderResult.State.Artifacts, artifact => artifact.ArtifactType == "order");
        Assert.False(string.IsNullOrWhiteSpace(orderArtifact.Status));
        Assert.Equal("25", orderArtifact.Metadata["quantity"]);
        Assert.Equal("STANDARD", orderArtifact.Metadata["leadTimeCode"]);
        Assert.Equal("THB", orderArtifact.Metadata["currency"]);
        Assert.False(string.IsNullOrWhiteSpace(orderArtifact.Metadata["total"]));
        Assert.False(string.IsNullOrWhiteSpace(orderArtifact.Metadata["quoteNumber"]));
        Assert.False(string.IsNullOrWhiteSpace(orderArtifact.Metadata["quoteVersionId"]));
        Assert.Equal("1", orderArtifact.Metadata["quoteVersionNumber"]);
        Assert.False(string.IsNullOrWhiteSpace(orderArtifact.Metadata["orderId"]));
        Assert.Contains("fixture.step", orderArtifact.Metadata["parts"], StringComparison.Ordinal);
        if (factory.LastOrderCreateRequest is not { } orderCreateRequest)
        {
            throw new InvalidOperationException("Expected agent order confirmation to call OrderService.");
        }

        Assert.False(string.IsNullOrWhiteSpace(orderCreateRequest.CustomerId));
        Assert.Equal(1, orderCreateRequest.ServiceCategoryId);
        Assert.Equal(1, orderCreateRequest.ProcessTypeId);
        Assert.Equal(25, orderCreateRequest.OrderedQuantity);
        Assert.True(orderCreateRequest.QuotedAmount > 0);
        Assert.Equal("THB", orderCreateRequest.QuoteCurrency);
        Assert.NotNull(orderCreateRequest.QuoteId);
        Assert.False(string.IsNullOrWhiteSpace(orderCreateRequest.QuoteNumber));
        Assert.NotNull(orderCreateRequest.QuoteVersionId);
        Assert.Equal(1, orderCreateRequest.QuoteVersionNumber);
        var productionItem = Assert.Single(orderCreateRequest.ProductionItems);
        Assert.NotEqual(Guid.Empty, productionItem.MaterialId);
        Assert.Contains("FDM", productionItem.Technology, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(25, productionItem.Quantity);
        Assert.Contains("fixture.step", productionItem.ConfigurationSnapshotJson, StringComparison.OrdinalIgnoreCase);

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
        var paymentArtifact = Assert.Single(paymentResult.State.Artifacts, artifact => artifact.ArtifactType == "payment");
        Assert.False(string.IsNullOrWhiteSpace(paymentArtifact.Status));
        Assert.False(string.IsNullOrWhiteSpace(paymentArtifact.Url));
        var initiation = Assert.Single(factory.PaymentInitiations);
        Assert.Equal(orderArtifact.Metadata["orderId"], initiation.OrderId);
        Assert.Equal(orderArtifact.Metadata["orderNumber"], initiation.OrderNumber);
        Assert.Equal(orderCreateRequest.QuotedAmount, initiation.Amount);
        Assert.Equal(orderCreateRequest.QuoteCurrency, initiation.Currency);
        Assert.Equal(CheckoutBillingAddressId, initiation.BillingAddressId);
        Assert.Equal(CheckoutShippingAddressId, initiation.ShippingAddressId);
        Assert.Equal("MALIEV Buyer Co.", initiation.BillingCompanyName);
        Assert.Equal("TH1234567890", initiation.BillingVatNumber);
        Assert.Null(initiation.DeliveryContactName);
        Assert.Equal("+66 2 555 0100", initiation.DeliveryContactPhone);
        Assert.StartsWith("qe:", initiation.IdempotencyKey, StringComparison.Ordinal);
        Assert.True(initiation.IdempotencyKey.Length <= 100);
        Assert.StartsWith("https://", initiation.ReturnUrl, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("https://", initiation.CancelUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/payment/success", initiation.ReturnUrl, StringComparison.Ordinal);
        Assert.Contains("/payment/cancel", initiation.CancelUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_start_payment_uses_checkout_attempt_id_for_idempotency_key()
    {
        const string customerEmail = "agent-payment-attempt@example.com";
        await using var scopedFactory = CreateAgentFactory();
        factory.ClearPaymentIdempotencyKeys();
        using var client = await CreateSignedInClientAsync(scopedFactory, customerEmail);
        var sessionId = await StartPricedCadSessionAsync(client);
        var checkoutAttemptId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        var formalQuoteState = await ExecuteToolForStateAsync(client, sessionId, "quote_prepare_formal_quote");
        await ConfirmActionAsync(client, Assert.Single(formalQuoteState.ProposedActions).ActionId);
        var approvalState = await ExecuteToolForStateAsync(client, sessionId, "quote_approve_quote");
        await ConfirmActionAsync(client, Assert.Single(approvalState.ProposedActions).ActionId);
        var orderState = await ExecuteToolForStateAsync(client, sessionId, "quote_create_order");
        await ConfirmActionAsync(client, Assert.Single(orderState.ProposedActions).ActionId);

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

        var paymentState = await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_start_payment",
            new Dictionary<string, JsonElement>
            {
                ["checkout_attempt_id"] = JsonSerializer.SerializeToElement(checkoutAttemptId.ToString("D"), JsonOptions)
            });
        var paymentResult = await ConfirmActionAsync(client, Assert.Single(paymentState.ProposedActions).ActionId);

        Assert.NotNull(paymentResult.State);
        var initiation = Assert.Single(factory.PaymentInitiations);
        var customerId = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(customerEmail.Trim().ToLowerInvariant())));
        var orderId = Guid.Parse(initiation.OrderId);
        var expectedInput = $"{customerId:D}:{orderId:D}:{checkoutAttemptId:D}";
        var expectedKey = $"qe:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expectedInput))).ToLowerInvariant()}";
        Assert.Equal(expectedKey, initiation.IdempotencyKey);
    }

    [Fact]
    public async Task Agent_order_confirmation_double_submit_returns_completed_result_without_duplicate_order()
    {
        await using var scopedFactory = CreateAgentFactory();
        factory.DelayOrderCreateBy(TimeSpan.FromMilliseconds(100));
        try
        {
            using var client = await CreateSignedInClientAsync(scopedFactory, "agent-order-double-submit@example.com");
            var sessionId = await StartPricedCadSessionAsync(client);

            var formalQuoteState = await ExecuteToolForStateAsync(client, sessionId, "quote_prepare_formal_quote");
            await ConfirmActionAsync(client, Assert.Single(formalQuoteState.ProposedActions).ActionId);
            var approvalState = await ExecuteToolForStateAsync(client, sessionId, "quote_approve_quote");
            await ConfirmActionAsync(client, Assert.Single(approvalState.ProposedActions).ActionId);
            var orderState = await ExecuteToolForStateAsync(client, sessionId, "quote_create_order");
            var orderAction = Assert.Single(orderState.ProposedActions);

            var firstTask = ConfirmActionAsync(client, orderAction.ActionId);
            var secondTask = ConfirmActionAsync(client, orderAction.ActionId);
            var results = await Task.WhenAll(firstTask, secondTask);

            Assert.All(results, result =>
            {
                Assert.Equal(orderAction.ActionId, result.ActionId);
                Assert.Equal("completed", result.Status);
                Assert.NotNull(result.State);
            });
            var firstOrderArtifact = Assert.Single(results[0].State!.Artifacts, artifact => artifact.ArtifactType == "order");
            var secondOrderArtifact = Assert.Single(results[1].State!.Artifacts, artifact => artifact.ArtifactType == "order");
            Assert.Equal(
                firstOrderArtifact.Metadata["orderNumber"],
                secondOrderArtifact.Metadata["orderNumber"]);
            Assert.Single(factory.OrderCreateRequests);
        }
        finally
        {
            factory.DelayOrderCreateBy(TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task Agent_order_confirmation_rejects_superseded_formal_quote_before_order_service_call()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-superseded-quote@example.com");
        var sessionId = await StartPricedCadSessionAsync(client);

        var formalQuoteState = await ExecuteToolForStateAsync(client, sessionId, "quote_prepare_formal_quote");
        var formalQuoteResult = await ConfirmActionAsync(client, Assert.Single(formalQuoteState.ProposedActions).ActionId);
        var formalQuoteArtifact = Assert.Single(formalQuoteResult.State!.Artifacts, artifact => artifact.ArtifactType == "formal_quote");
        var quoteId = Guid.Parse(formalQuoteArtifact.Metadata["quoteId"]);
        factory.SupersedeQuoteVersion(quoteId);
        var orderCreateCount = factory.OrderCreateRequests.Count;

        var approvalState = await ExecuteToolForStateAsync(client, sessionId, "quote_approve_quote");
        await ConfirmActionAsync(client, Assert.Single(approvalState.ProposedActions).ActionId);

        var orderState = await ExecuteToolForStateAsync(client, sessionId, "quote_create_order");
        var orderAction = Assert.Single(orderState.ProposedActions);
        var orderResponse = await client.PostAsJsonAsync(
            $"/quote/v1/agent/actions/{orderAction.ActionId:D}/confirm",
            new QuoteAgentConfirmActionRequest
            {
                ConfirmationNote = "Customer confirmed a stale quote version."
            });

        Assert.Equal(HttpStatusCode.BadRequest, orderResponse.StatusCode);
        var body = await orderResponse.Content.ReadAsStringAsync();
        Assert.Contains("superseded", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(orderCreateCount, factory.OrderCreateRequests.Count);
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
        var orderResult = await ConfirmActionAsync(client, Assert.Single(orderState.ProposedActions).ActionId);
        Assert.NotNull(orderResult.State);
        Assert.Contains(orderResult.State.Artifacts, artifact => artifact.ArtifactType == "order");

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
    public async Task Agent_register_uploads_clears_commercial_state_after_new_geometry()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-reupload-after-payment@example.com");
        var sessionId = await StartPricedCadSessionAsync(client);
        var currentState = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        Assert.NotNull(currentState.Estimate);

        var formalQuoteState = await ExecuteToolForStateAsync(client, sessionId, "quote_prepare_formal_quote");
        await ConfirmActionAsync(client, Assert.Single(formalQuoteState.ProposedActions).ActionId);
        var approvalState = await ExecuteToolForStateAsync(client, sessionId, "quote_approve_quote");
        await ConfirmActionAsync(client, Assert.Single(approvalState.ProposedActions).ActionId);
        var orderState = await ExecuteToolForStateAsync(client, sessionId, "quote_create_order");
        await ConfirmActionAsync(client, Assert.Single(orderState.ProposedActions).ActionId);
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
        var paymentState = await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_start_payment",
            new Dictionary<string, JsonElement>
            {
                ["amount"] = JsonSerializer.SerializeToElement(currentState.Estimate.Total, JsonOptions),
                ["currency"] = JsonSerializer.SerializeToElement(currentState.Estimate.Currency, JsonOptions)
            });
        var paymentResult = await ConfirmActionAsync(client, Assert.Single(paymentState.ProposedActions).ActionId);
        Assert.NotNull(paymentResult.State);
        Assert.Contains(paymentResult.State.Artifacts, artifact => artifact.ArtifactType == "formal_quote");
        Assert.Contains(paymentResult.State.Artifacts, artifact => artifact.ArtifactType == "order");
        Assert.Contains(paymentResult.State.Artifacts, artifact => artifact.ArtifactType == "payment");

        var reuploadedState = await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_register_uploads",
            new Dictionary<string, JsonElement>
            {
                ["requirements"] = JsonSerializer.SerializeToElement("Added a revised cover plate after payment review. Recalculate before checkout.", JsonOptions),
                ["files"] = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        file_name = "fixture-cover-rev-b.step",
                        content_type = "model/step",
                        file_size_bytes = 260_000,
                        kind = "cad",
                        upload_id = "fixture-cover-rev-b-upload",
                        storage_path = "quotes/temp/session/fixture-cover-rev-b-upload/fixture-cover-rev-b.step"
                    }
                }, JsonOptions)
            });

        Assert.Null(reuploadedState.Estimate);
        Assert.Empty(reuploadedState.ProposedActions);
        Assert.Contains(reuploadedState.Parts, part => part.UploadId == "fixture-cover-rev-b-upload");
        Assert.DoesNotContain(reuploadedState.Artifacts, artifact => artifact.ArtifactType == "pricing");
        Assert.DoesNotContain(reuploadedState.Artifacts, artifact => artifact.ArtifactType == "formal_quote");
        Assert.DoesNotContain(reuploadedState.Artifacts, artifact => artifact.ArtifactType == "order");
        Assert.DoesNotContain(reuploadedState.Artifacts, artifact => artifact.ArtifactType == "payment");
        Assert.Contains(reuploadedState.Gates, gate => gate.Code == "priced" && gate.Status == "pending");
        Assert.Contains(reuploadedState.Gates, gate => gate.Code == "quote_artifact_ready" && gate.Status == "pending");
        Assert.Contains(reuploadedState.Gates, gate => gate.Code == "quote_approved" && gate.Status == "pending");
        Assert.Contains(reuploadedState.Gates, gate => gate.Code == "order_created" && gate.Status == "pending");
        Assert.Contains(reuploadedState.Gates, gate => gate.Code == "payment_started_or_completed" && gate.Status == "pending");
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
        Assert.True(Guid.TryParse(draftArtifact.Metadata["projectServiceProjectId"], out var projectServiceProjectId));
        Assert.Equal(projectServiceProjectId, sourceProjectId);
        Assert.True(Guid.TryParse(draftArtifact.Metadata["prototypeProjectId"], out var prototypeProjectId));
        Assert.NotEqual(projectServiceProjectId, prototypeProjectId);
        Assert.False(string.IsNullOrWhiteSpace(draftArtifact.Metadata["projectServiceProjectNumber"]));
        Assert.Equal(projectServiceProjectId, factory.LastProjectDraftCreate?.ProjectServiceProjectId);
        var projectPart = Assert.Single(factory.LastProjectPartCreates);
        Assert.Equal("fixture.step", projectPart.FileName);
        Assert.Equal(25, projectPart.Quantity);
        Assert.NotEqual(Guid.Empty, projectPart.MaterialId);

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
        Assert.Equal(projectServiceProjectId.ToString("D"), duplicateArtifact.Metadata["sourceProjectId"]);
        Assert.Equal(prototypeProjectId.ToString("D"), duplicateArtifact.Metadata["sourcePrototypeProjectId"]);
        Assert.True(Guid.TryParse(duplicateArtifact.Metadata["projectId"], out var duplicateProjectId));
        Assert.NotEqual(projectServiceProjectId, duplicateProjectId);
        Assert.Equal("Duplicate from agent", duplicateArtifact.Title);
        Assert.Equal(projectServiceProjectId, factory.LastProjectDraftCreate?.SourceProjectId);
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
    [InlineData("quote_request_employee_review", "request_employee_review", "project_review_request", "customer_review", "status", "CustomerReview")]
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
        Assert.True(Guid.TryParse(draftArtifact.Metadata["projectServiceProjectId"], out var projectServiceProjectId));
        Assert.Equal(projectServiceProjectId.ToString("D"), draftArtifact.Metadata["projectId"]);
        Assert.True(Guid.TryParse(draftArtifact.Metadata["prototypeProjectId"], out var prototypeProjectId));
        Assert.NotEqual(projectServiceProjectId, prototypeProjectId);
        var projectId = projectServiceProjectId;

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
    public async Task Agent_project_management_confirmation_does_not_fall_back_to_prototype_store_in_production()
    {
        const string customerEmail = "agent-project-production-fallback@example.com";
        var customerId = DeterministicCustomerId(customerEmail);

        await using var productionFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AnonymousVisitor:SigningKey"] = "quote-agent-production-project-test-signing-key",
                    ["QuoteAgent:ContextSigningKey"] = "maliev-local-development-quote-agent-context-key"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostEnvironment>();
                services.AddSingleton<IHostEnvironment>(new QuoteEngineWebApplicationFactory.TestHostEnvironment(Environments.Production));

                services.RemoveAll<IProjectServiceClient>();
                services.AddSingleton<IProjectServiceClient>(new QuoteEngineWebApplicationFactory.EmptyProjectServiceClient());
            });
        });

        var store = productionFactory.Services.GetRequiredService<QuoteEnginePrototypeStore>();
        var prototypeProject = store.CreateDraftProject(
            customerId,
            new CreateDraftProjectRequest(
                "prototype-agent-production-fallback",
                [],
                "This project exists only in the local prototype store.",
                "Prototype-only production agent project"));

        using var client = await CreateSignedInClientAsync(productionFactory, customerEmail);
        var sessionId = Guid.NewGuid();
        var pendingJson = await ExecuteToolAsync(
            client,
            sessionId,
            "quote_pin_project",
            new Dictionary<string, JsonElement>
            {
                ["project_id"] = JsonSerializer.SerializeToElement(prototypeProject.ProjectId.ToString("D"), JsonOptions)
            },
            customerId);
        var pendingState = JsonSerializer.Deserialize<QuoteAgentStateResponse>(pendingJson, JsonOptions);
        Assert.NotNull(pendingState);
        var pendingAction = Assert.Single(pendingState.ProposedActions);

        var response = await client.PostAsJsonAsync(
            $"/quote/v1/agent/actions/{pendingAction.ActionId:D}/confirm",
            new QuoteAgentConfirmActionRequest
            {
                ConfirmationNote = "Customer confirmed from a production fallback regression test."
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var failedProject = store.GetProject(customerId, prototypeProject.ProjectId);
        Assert.NotNull(failedProject);
        Assert.False(failedProject.IsPinned);
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
    public async Task Project_detail_endpoint_resumes_only_signed_in_customer_project()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var ownerClient = await CreateSignedInClientAsync(scopedFactory, "project-detail-owner@example.com");
        var ownerSessionId = await StartPricedCadSessionAsync(ownerClient);
        var draftState = await ExecuteToolForStateAsync(
            ownerClient,
            ownerSessionId,
            "quote_prepare_draft_project",
            new Dictionary<string, JsonElement>
            {
                ["title"] = JsonSerializer.SerializeToElement("Resume endpoint project", JsonOptions)
            });
        var draftResult = await ConfirmActionAsync(ownerClient, Assert.Single(draftState.ProposedActions).ActionId);
        Assert.NotNull(draftResult.State);
        var draftArtifact = Assert.Single(draftResult.State.Artifacts, artifact => artifact.ArtifactType == "draft_project");
        Assert.True(Guid.TryParse(draftArtifact.Metadata["projectId"], out var projectId));

        var response = await ownerClient.GetAsync($"/quote/v1/projects/{projectId:D}");
        var body = await response.Content.ReadFromJsonAsync<CustomerProjectDetailResponse>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(projectId, body.ProjectId);
        Assert.Equal("Resume endpoint project", body.Title);
        Assert.NotEmpty(body.Parts);

        using var anonymousClient = scopedFactory.CreateClient();
        var anonymousResponse = await anonymousClient.GetAsync($"/quote/v1/projects/{projectId:D}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var otherClient = await CreateSignedInClientAsync(scopedFactory, "project-detail-other@example.com");
        var otherResponse = await otherClient.GetAsync($"/quote/v1/projects/{projectId:D}");
        Assert.Equal(HttpStatusCode.NotFound, otherResponse.StatusCode);
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
    public async Task Agent_resume_project_hydrates_state_from_project_service_project()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-resume-project-service@example.com");
        var sourceSessionId = await StartPricedCadSessionAsync(client);

        var draftState = await ExecuteToolForStateAsync(client, sourceSessionId, "quote_prepare_draft_project");
        var draftAction = Assert.Single(draftState.ProposedActions);
        var draftResult = await ConfirmActionAsync(client, draftAction.ActionId);
        Assert.NotNull(draftResult.State);
        var draftArtifact = Assert.Single(draftResult.State.Artifacts, artifact => artifact.ArtifactType == "draft_project");
        Assert.True(Guid.TryParse(draftArtifact.Metadata["projectServiceProjectId"], out var projectServiceProjectId));

        var resumed = await ExecuteToolForStateAsync(
            client,
            Guid.NewGuid(),
            "quote_resume_project",
            new Dictionary<string, JsonElement>
            {
                ["project_id"] = JsonSerializer.SerializeToElement(projectServiceProjectId.ToString("D"), JsonOptions)
            });

        var resumedPart = Assert.Single(resumed.Parts);
        Assert.Equal("fixture.step", resumedPart.FileName);
        Assert.Equal(25, resumedPart.Quantity);
        Assert.Contains(resumed.Artifacts, artifact =>
            artifact.ArtifactType == "resumed_project" &&
            artifact.Metadata["projectId"] == projectServiceProjectId.ToString("D"));
        Assert.Contains(resumed.Gates, gate => gate.Code == "geometry_required" && gate.Status == "passed");
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
        var draftResult = await ConfirmActionAsync(ownerClient, Assert.Single(draftState.ProposedActions).ActionId);
        Assert.NotNull(draftResult.State);
        var draftArtifact = Assert.Single(draftResult.State.Artifacts, artifact => artifact.ArtifactType == "draft_project");
        Assert.True(Guid.TryParse(draftArtifact.Metadata["projectServiceProjectId"], out var projectServiceProjectId));

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
            result.ResourceId == projectServiceProjectId.ToString("D") &&
            result.Metadata["source"] == "project_service" &&
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
        var draftResult = await ConfirmActionAsync(ownerClient, Assert.Single(draftState.ProposedActions).ActionId);
        Assert.NotNull(draftResult.State);
        var draftArtifact = Assert.Single(draftResult.State.Artifacts, artifact => artifact.ArtifactType == "draft_project");
        Assert.True(Guid.TryParse(draftArtifact.Metadata["projectServiceProjectId"], out var projectServiceProjectId));

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

        var documentResponse = await ownerClient.PostAsJsonAsync("/quote/v1/account/documents", new CustomerDocumentUploadRequest
        {
            FileName = "search-requirements.pdf",
            Kind = "Requirement",
            StoragePath = "customer-documents/search-owner/search-requirements.pdf",
            ContentType = "application/pdf",
            FileSizeBytes = 18_240,
            OrderNumber = "ORD-SEARCH-1"
        });
        documentResponse.EnsureSuccessStatusCode();
        var uploadedDocument = await documentResponse.Content.ReadFromJsonAsync<CustomerDocumentDto>();
        Assert.NotNull(uploadedDocument);

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
            result.GetProperty("title").GetString() == "Fixture search project" &&
            result.GetProperty("resourceId").GetString() == projectServiceProjectId.ToString("D") &&
            result.GetProperty("metadata").GetProperty("source").GetString() == "project_service");
        var documentResult = Assert.Single(results, result =>
            result.GetProperty("resourceType").GetString() == "document" &&
            result.GetProperty("title").GetString() == "search-requirements.pdf");
        Assert.Equal(uploadedDocument.DocumentId.ToString("D"), documentResult.GetProperty("resourceId").GetString());
        Assert.Equal("customer_service", documentResult.GetProperty("metadata").GetProperty("source").GetString());
        Assert.Equal("ORD-SEARCH-1", documentResult.GetProperty("metadata").GetProperty("orderNumber").GetString());
        Assert.DoesNotContain(results, result =>
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
    public async Task Agent_connector_registry_requires_signed_in_customer()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var json = await ExecuteToolAsync(client, sessionId, "quote_get_connectors");
        using var document = JsonDocument.Parse(json);
        var connectors = document.RootElement.GetProperty("connectors").EnumerateArray().ToArray();

        Assert.Equal(sessionId, document.RootElement.GetProperty("sessionId").GetGuid());
        Assert.True(document.RootElement.GetProperty("requiresAuthenticationToList").GetBoolean());
        Assert.Empty(connectors);
    }

    [Fact]
    public async Task Agent_connector_registry_endpoint_lists_google_drive_only_for_signed_in_customer()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-connectors@example.com");
        var sessionId = Guid.NewGuid();

        var response = await client.GetAsync($"/quote/v1/agent/sessions/{sessionId:D}/connectors");

        response.EnsureSuccessStatusCode();
        var registry = await response.Content.ReadFromJsonAsync<QuoteAgentConnectorRegistryResponse>();
        Assert.NotNull(registry);
        Assert.Equal(sessionId, registry.SessionId);
        Assert.False(registry.RequiresAuthenticationToList);
        var connector = Assert.Single(registry.Connectors);
        Assert.Equal("google-drive", connector.ConnectorId);
        Assert.Equal("Google Drive", connector.DisplayName);
        Assert.Equal("available", connector.Status);
        Assert.Equal("file_import", connector.Category);
        Assert.True(connector.RequiresAuthenticationToConnect);
        Assert.Contains("STEP", connector.SupportedFileTypes);
        Assert.DoesNotContain(registry.Connectors, item => item.ConnectorId == "blender");
        Assert.DoesNotContain(registry.Connectors, item => item.ConnectorId == "freecad");
    }

    [Fact]
    public async Task Agent_connector_handoff_endpoint_allows_signed_in_browser_without_agent_context_token()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-connector-browser@example.com");
        var sessionId = Guid.NewGuid();

        var response = await client.GetAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/connectors/google-drive/handoff");

        response.EnsureSuccessStatusCode();
        var handoff = await response.Content.ReadFromJsonAsync<QuoteAgentConnectorHandoffResponse>();
        Assert.NotNull(handoff);
        Assert.Equal(sessionId, handoff.SessionId);
        Assert.Equal("google-drive", handoff.ConnectorId);
        Assert.True(handoff.IsAuthenticated);
        Assert.True(handoff.IsAvailableToConnect);
        Assert.Equal("ready_to_connect", handoff.Status);
        Assert.Equal("connect_google_drive", handoff.ActionHint);
    }

    [Fact]
    public async Task Agent_connector_handoff_for_google_drive_requires_trusted_auth_when_anonymous()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var json = await ExecuteToolAsync(
            client,
            sessionId,
            "quote_get_connector_handoff",
            new Dictionary<string, JsonElement>
            {
                ["connector_id"] = JsonSerializer.SerializeToElement("google-drive", JsonOptions),
                ["return_url"] = JsonSerializer.SerializeToElement("/quote/new?connector=google-drive", JsonOptions)
            });
        var handoff = JsonSerializer.Deserialize<QuoteAgentConnectorHandoffResponse>(json, JsonOptions);

        Assert.NotNull(handoff);
        Assert.Equal(sessionId, handoff.SessionId);
        Assert.Equal("google-drive", handoff.ConnectorId);
        Assert.Equal("Google Drive", handoff.DisplayName);
        Assert.False(handoff.IsAuthenticated);
        Assert.False(handoff.IsAvailableToConnect);
        Assert.Equal("authentication_required", handoff.Status);
        Assert.Equal("/auth/sign-in?returnUrl=%2Fquote%2Fnew%3Fconnector%3Dgoogle-drive", handoff.HandoffUrl);
        Assert.Equal("sign_in_to_connect", handoff.ActionHint);
        Assert.Contains("Google Drive", handoff.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_connector_handoff_for_google_drive_is_connectable_after_sign_in()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-connector@example.com");
        var sessionId = Guid.NewGuid();

        var json = await ExecuteToolAsync(
            client,
            sessionId,
            "quote_get_connector_handoff",
            new Dictionary<string, JsonElement>
            {
                ["connector_id"] = JsonSerializer.SerializeToElement("google-drive", JsonOptions)
            });
        var handoff = JsonSerializer.Deserialize<QuoteAgentConnectorHandoffResponse>(json, JsonOptions);

        Assert.NotNull(handoff);
        Assert.True(handoff.IsAuthenticated);
        Assert.True(handoff.IsAvailableToConnect);
        Assert.Equal("ready_to_connect", handoff.Status);
        Assert.Equal("/quote/new?connect=google-drive", handoff.HandoffUrl);
        Assert.Equal("connect_google_drive", handoff.ActionHint);
        Assert.Contains("trusted Make Studio connector panel", handoff.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_connector_handoff_rejects_unknown_connector()
    {
        using var client = factory.CreateClient();

        var json = await ExecuteToolAsync(
            client,
            Guid.NewGuid(),
            "quote_get_connector_handoff",
            new Dictionary<string, JsonElement>
            {
                ["connector_id"] = JsonSerializer.SerializeToElement("dropbox", JsonOptions)
            });
        var handoff = JsonSerializer.Deserialize<QuoteAgentConnectorHandoffResponse>(json, JsonOptions);

        Assert.NotNull(handoff);
        Assert.Equal("dropbox", handoff.ConnectorId);
        Assert.Equal("connector_not_found", handoff.Status);
        Assert.Equal("choose_available_connector", handoff.ActionHint);
    }

    [Fact]
    public async Task Google_drive_connector_start_requires_customer_session()
    {
        await using var scopedFactory = CreateAgentFactoryWithGoogleDriveConfig();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/quote/v1/connectors/google-drive/start?returnUrl=/quotes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Google_drive_connector_start_redirects_signed_in_customer_to_google_with_drive_scope()
    {
        await using var scopedFactory = CreateAgentFactoryWithGoogleDriveConfig();
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });
        var signIn = await client.GetAsync("/test/sign-in?email=drive-connect@example.com");
        signIn.EnsureSuccessStatusCode();

        var response = await client.GetAsync("/quote/v1/connectors/google-drive/start?returnUrl=/quotes");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var redirect = response.Headers.Location.OriginalString;
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth", redirect, StringComparison.Ordinal);
        Assert.Contains("client_id=quote-engine-google-client", redirect, StringComparison.Ordinal);
        Assert.Contains("scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fdrive.file", redirect, StringComparison.Ordinal);
        Assert.Contains("access_type=offline", redirect, StringComparison.Ordinal);
        Assert.Contains("include_granted_scopes=true", redirect, StringComparison.Ordinal);
        Assert.Contains("state=", redirect, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Google_drive_connector_registry_marks_drive_connected_for_signed_in_customer()
    {
        await using var scopedFactory = CreateAgentFactoryWithConnectedGoogleDrive();
        using var client = await CreateSignedInClientAsync(scopedFactory, "drive-connected@example.com");
        var sessionId = Guid.NewGuid();

        var registry = await client.GetFromJsonAsync<QuoteAgentConnectorRegistryResponse>(
            $"/quote/v1/agent/sessions/{sessionId:D}/connectors");

        Assert.NotNull(registry);
        var drive = Assert.Single(registry.Connectors, connector => connector.ConnectorId == "google-drive");
        Assert.True(drive.IsConnected);
        Assert.Equal("connected", drive.Status);
        Assert.Equal("browse_google_drive", drive.ActionHint);
    }

    [Fact]
    public async Task Google_drive_connector_files_lists_drive_files_with_customer_token()
    {
        var google = new RecordingGoogleDriveHttpClientFactory();
        await using var scopedFactory = CreateAgentFactoryWithConnectedGoogleDrive(google);
        using var client = await CreateSignedInClientAsync(scopedFactory, "drive-files@example.com");

        var response = await client.GetAsync("/quote/v1/connectors/google-drive/files?query=bracket&limit=7");

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var file = Assert.Single(document.RootElement.GetProperty("files").EnumerateArray());
        Assert.Equal("drive-file-1", file.GetProperty("id").GetString());
        Assert.Equal("bracket.step", file.GetProperty("name").GetString());
        Assert.NotNull(google.LastRequest);
        Assert.Equal("Bearer", google.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("access-token", google.LastRequest.Headers.Authorization?.Parameter);
        Assert.Contains("drive/v3/files", google.LastRequest.RequestUri!.OriginalString, StringComparison.Ordinal);
        Assert.Contains("name%20contains%20%27bracket%27", google.LastRequest.RequestUri.OriginalString, StringComparison.Ordinal);
        Assert.Contains("pageSize=7", google.LastRequest.RequestUri.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_settings_tool_returns_and_updates_customer_safe_session_settings()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var defaultsJson = await ExecuteToolAsync(client, sessionId, "quote_get_settings");
        using (var defaults = JsonDocument.Parse(defaultsJson))
        {
            Assert.Equal(sessionId, defaults.RootElement.GetProperty("sessionId").GetGuid());
            Assert.Equal("mm", defaults.RootElement.GetProperty("units").GetString());
            Assert.Equal("THB", defaults.RootElement.GetProperty("currency").GetString());
            Assert.Equal("chat", defaults.RootElement.GetProperty("interactionMode").GetString());
            Assert.True(defaults.RootElement.GetProperty("allowArtifactPanel").GetBoolean());
            Assert.True(defaults.RootElement.GetProperty("multilingual").GetBoolean());
        }

        var updateJson = await ExecuteToolAsync(
            client,
            sessionId,
            "quote_update_settings",
            new Dictionary<string, JsonElement>
            {
                ["units"] = JsonSerializer.SerializeToElement("inch", JsonOptions),
                ["currency"] = JsonSerializer.SerializeToElement("USD", JsonOptions),
                ["interaction_mode"] = JsonSerializer.SerializeToElement("chat-and-ui", JsonOptions),
                ["allow_artifact_panel"] = JsonSerializer.SerializeToElement(false, JsonOptions),
                ["language"] = JsonSerializer.SerializeToElement("th", JsonOptions)
            });
        using var updated = JsonDocument.Parse(updateJson);

        Assert.Equal(sessionId, updated.RootElement.GetProperty("sessionId").GetGuid());
        Assert.Equal("inch", updated.RootElement.GetProperty("units").GetString());
        Assert.Equal("USD", updated.RootElement.GetProperty("currency").GetString());
        Assert.Equal("chat-and-ui", updated.RootElement.GetProperty("interactionMode").GetString());
        Assert.False(updated.RootElement.GetProperty("allowArtifactPanel").GetBoolean());
        Assert.Equal("th", updated.RootElement.GetProperty("language").GetString());
        Assert.Contains("settings", updated.RootElement.GetProperty("nextActions").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Agent_account_context_returns_auth_handoff_without_customer_defaults_when_anonymous()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = scopedFactory.CreateClient();
        var sessionId = Guid.NewGuid();

        var json = await ExecuteToolAsync(client, sessionId, "quote_get_account_context");
        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("customerId").ValueKind);
        Assert.Equal("authentication_required", document.RootElement.GetProperty("authHandoff").GetProperty("status").GetString());
        Assert.False(document.RootElement.TryGetProperty("profile", out _));
        Assert.False(document.RootElement.TryGetProperty("defaultBillingAddress", out _));
        Assert.False(document.RootElement.TryGetProperty("defaultShippingAddress", out _));
    }

    [Fact]
    public async Task Agent_account_context_returns_signed_in_profile_and_default_checkout_addresses()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-account-context@example.com");
        var sessionId = Guid.NewGuid();

        var json = await ExecuteToolAsync(client, sessionId, "quote_get_account_context");
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("isAuthenticated").GetBoolean());
        Assert.NotEqual(Guid.Empty, document.RootElement.GetProperty("customerId").GetGuid());
        Assert.Equal("already_authenticated", document.RootElement.GetProperty("authHandoff").GetProperty("status").GetString());
        Assert.Equal("agent-account-context@example.com", document.RootElement.GetProperty("profile").GetProperty("email").GetString());
        Assert.Equal("MALIEV Test Buyer Co., Ltd.", document.RootElement.GetProperty("profile").GetProperty("companyName").GetString());
        Assert.Equal("TH-0123456789012", document.RootElement.GetProperty("profile").GetProperty("vatNumber").GetString());
        Assert.Equal("Billing", document.RootElement.GetProperty("defaultBillingAddress").GetProperty("type").GetString());
        Assert.True(document.RootElement.GetProperty("defaultBillingAddress").GetProperty("isDefault").GetBoolean());
        Assert.Equal("12 Billing Road", document.RootElement.GetProperty("defaultBillingAddress").GetProperty("addressLine1").GetString());
        Assert.Equal("Shipping", document.RootElement.GetProperty("defaultShippingAddress").GetProperty("type").GetString());
        Assert.True(document.RootElement.GetProperty("defaultShippingAddress").GetProperty("isDefault").GetBoolean());
        Assert.Equal("34 Shipping Road", document.RootElement.GetProperty("defaultShippingAddress").GetProperty("addressLine1").GetString());
        Assert.Equal("use_default_checkout_addresses", document.RootElement.GetProperty("nextActions")[0].GetString());
    }

    [Fact]
    public async Task Agent_update_account_profile_requires_confirmation_and_updates_customer_context()
    {
        await using var scopedFactory = CreateAgentFactory();
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-profile-update@example.com");
        var sessionId = Guid.NewGuid();

        var pendingState = await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_update_account_profile",
            new Dictionary<string, JsonElement>
            {
                ["display_name"] = JsonSerializer.SerializeToElement("Dana Manufacturing", JsonOptions),
                ["phone"] = JsonSerializer.SerializeToElement("+66 2 555 0200", JsonOptions),
                ["company_name"] = JsonSerializer.SerializeToElement("Northbridge Robotics", JsonOptions),
                ["vat_number"] = JsonSerializer.SerializeToElement("TH9876543210", JsonOptions),
                ["preferred_currency"] = JsonSerializer.SerializeToElement("USD", JsonOptions)
            });

        var action = Assert.Single(pendingState.ProposedActions);
        Assert.Equal("account_profile_update", action.ActionType);
        Assert.True(action.RequiresAuthentication);

        var result = await ConfirmActionAsync(client, action.ActionId);
        Assert.Contains("profile updated", result.Message, StringComparison.OrdinalIgnoreCase);

        var json = await ExecuteToolAsync(client, sessionId, "quote_get_account_context");
        using var document = JsonDocument.Parse(json);
        var profile = document.RootElement.GetProperty("profile");

        Assert.Equal("Dana Manufacturing", profile.GetProperty("displayName").GetString());
        Assert.Equal("agent-profile-update@example.com", profile.GetProperty("email").GetString());
        Assert.Equal("+66 2 555 0200", profile.GetProperty("phone").GetString());
        Assert.Equal("Northbridge Robotics", profile.GetProperty("companyName").GetString());
        Assert.Equal("THB", profile.GetProperty("preferredCurrency").GetString());
        Assert.Equal("TH9876543210", profile.GetProperty("vatNumber").GetString());
    }

    [Fact]
    public async Task Agent_message_forwards_current_session_settings_to_chatbot_service()
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
        var sessionId = Guid.NewGuid();

        await ExecuteToolAsync(
            client,
            sessionId,
            "quote_update_settings",
            new Dictionary<string, JsonElement>
            {
                ["units"] = JsonSerializer.SerializeToElement("inch", JsonOptions),
                ["currency"] = JsonSerializer.SerializeToElement("USD", JsonOptions),
                ["interaction_mode"] = JsonSerializer.SerializeToElement("chat-and-ui", JsonOptions),
                ["allow_artifact_panel"] = JsonSerializer.SerializeToElement(false, JsonOptions),
                ["language"] = JsonSerializer.SerializeToElement("th", JsonOptions)
            });

        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = sessionId,
            Message = "ช่วยเสนอราคาชิ้นงานนี้ตามค่าที่ตั้งไว้",
            Language = "th"
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(chatbot.LastSendRequest);
        Assert.Contains(
            "Current settings: language th, units inch, currency USD, interaction chat-and-ui, artifact panel disabled, multilingual enabled",
            chatbot.LastSendRequest.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_message_forwards_artifact_metadata_to_chatbot_service_after_order_creation()
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
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-order-context@example.com");
        var sessionId = await StartPricedCadSessionAsync(client);

        var formalQuoteState = await ExecuteToolForStateAsync(client, sessionId, "quote_prepare_formal_quote");
        await ConfirmActionAsync(client, Assert.Single(formalQuoteState.ProposedActions).ActionId);
        var approvalState = await ExecuteToolForStateAsync(client, sessionId, "quote_approve_quote");
        await ConfirmActionAsync(client, Assert.Single(approvalState.ProposedActions).ActionId);
        var orderState = await ExecuteToolForStateAsync(client, sessionId, "quote_create_order");
        await ConfirmActionAsync(client, Assert.Single(orderState.ProposedActions).ActionId);

        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = sessionId,
            Message = "Show me the order summary.",
            Language = "en"
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(chatbot.LastSendRequest);
        Assert.Contains("Current artifacts:", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("order", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("quantity=25", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("quoteNumber=", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("orderId=", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_message_forwards_payment_metadata_to_chatbot_service_after_payment_handoff()
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
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-payment-context@example.com");
        var sessionId = await StartPricedCadSessionAsync(client);

        var formalQuoteState = await ExecuteToolForStateAsync(client, sessionId, "quote_prepare_formal_quote");
        await ConfirmActionAsync(client, Assert.Single(formalQuoteState.ProposedActions).ActionId);
        var approvalState = await ExecuteToolForStateAsync(client, sessionId, "quote_approve_quote");
        await ConfirmActionAsync(client, Assert.Single(approvalState.ProposedActions).ActionId);
        var orderState = await ExecuteToolForStateAsync(client, sessionId, "quote_create_order");
        var orderResult = await ConfirmActionAsync(client, Assert.Single(orderState.ProposedActions).ActionId);
        Assert.NotNull(orderResult.State);
        var orderNumber = orderResult.State.Artifacts.Single(artifact => artifact.ArtifactType == "order").Metadata["orderNumber"];
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
        var paymentResult = await ConfirmActionAsync(client, Assert.Single(paymentState.ProposedActions).ActionId);
        Assert.NotNull(paymentResult.State);
        Assert.NotNull(factory.LastInvoiceCreateRequest);
        Assert.Equal(orderNumber, factory.LastInvoiceCreateRequest.OrderNumber);
        Assert.Equal("MALIEV Buyer Co.", factory.LastInvoiceCreateRequest.CustomerName);
        Assert.Equal("TH1234567890", factory.LastInvoiceCreateRequest.CustomerTaxId);
        Assert.Equal("THB", factory.LastInvoiceCreateRequest.Currency);
        Assert.NotEmpty(factory.LastInvoiceCreateRequest.Lines);
        Assert.NotNull(factory.LastInvoicePreparedResult);
        Assert.Contains(paymentResult.State.Artifacts, artifact =>
            artifact.ArtifactType == "order" &&
            artifact.Metadata["invoiceId"] == factory.LastInvoicePreparedResult.InvoiceId.ToString("D") &&
            artifact.Metadata["invoiceNumber"] == factory.LastInvoicePreparedResult.InvoiceNumber &&
            artifact.Metadata["invoiceStatus"] == factory.LastInvoicePreparedResult.Status);

        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = sessionId,
            Message = "Show me the payment handoff.",
            Language = "en"
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(chatbot.LastSendRequest);
        Assert.Contains("Current artifacts:", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("payment", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("transactionId=", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("paymentUrl=", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("paymentStatus=pending", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("orderNumber=", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("invoiceNumber=", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("invoiceStatus=Finalized", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("amount=", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("currency=THB", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_project_summary_and_prompt_refresh_paid_order_status_from_order_service()
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
        using var client = await CreateSignedInClientAsync(scopedFactory, "agent-paid-context@example.com");
        var sessionId = await StartPricedCadSessionAsync(client);

        var formalQuoteState = await ExecuteToolForStateAsync(client, sessionId, "quote_prepare_formal_quote");
        await ConfirmActionAsync(client, Assert.Single(formalQuoteState.ProposedActions).ActionId);
        var approvalState = await ExecuteToolForStateAsync(client, sessionId, "quote_approve_quote");
        await ConfirmActionAsync(client, Assert.Single(approvalState.ProposedActions).ActionId);
        var orderState = await ExecuteToolForStateAsync(client, sessionId, "quote_create_order");
        var orderResult = await ConfirmActionAsync(client, Assert.Single(orderState.ProposedActions).ActionId);
        Assert.NotNull(orderResult.State);
        var orderNumber = orderResult.State.Artifacts.Single(artifact => artifact.ArtifactType == "order").Metadata["orderNumber"];
        await ExecuteToolForStateAsync(
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
        var paymentState = await ExecuteToolForStateAsync(client, sessionId, "quote_start_payment");
        await ConfirmActionAsync(client, Assert.Single(paymentState.ProposedActions).ActionId);

        factory.MarkOrderPaid(orderNumber);

        var summary = await GetProjectSummaryAsync(client, sessionId);
        Assert.Equal(orderNumber, summary.CurrentOrderNumber);
        Assert.Equal("Paid", summary.CurrentOrderStatus);
        Assert.Equal("Paid", summary.CurrentPaymentStatus);
        Assert.Contains(summary.NextActions, action =>
            action.Contains("Payment is confirmed", StringComparison.OrdinalIgnoreCase));

        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = sessionId,
            Message = "Is my payment complete?",
            Language = "en"
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(chatbot.LastSendRequest);
        Assert.Contains("paymentStatus=Paid", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("currentStatus=Paid", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
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
    public async Task Agent_turn_includes_auth_handoff_when_authenticated_gate_blocks_next_steps()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            Message = "Quote this STEP as 25 black nylon SLS enclosures, then help me place the order.",
            Language = "en",
            Attachments =
            [
                new QuoteAgentAttachmentDto
                {
                    FileName = "enclosure.step",
                    ContentType = "model/step",
                    FileSizeBytes = 240_000,
                    Kind = "cad",
                    StoragePath = "quotes/temp/enclosure.step",
                    SatisfiesGeometryGate = true
                }
            ]
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<QuoteAgentTurnResponse>(JsonOptions);
        Assert.NotNull(body);
        var summary = await GetProjectSummaryAsync(client, body.SessionId);
        Assert.Equal("sls", summary.RequirementFacts["process"]);
        Assert.Equal("nylon-black", summary.RequirementFacts["material"]);
        Assert.Contains(body.Gates, gate => gate.Code == "customer_authenticated" && gate.Status == "blocked");
        Assert.NotNull(body.AuthHandoff);
        Assert.False(body.AuthHandoff.IsAuthenticated);
        Assert.Equal("customer_authenticated", body.AuthHandoff.RequiredGateCode);
        Assert.Contains(body.AuthHandoff.Methods, method => method.MethodId == "google" && method.Status == "preferred");
        Assert.Contains(body.AuthHandoff.Methods, method => method.MethodId == "passkey" && method.RequiresBrowserSupport);
        Assert.Contains(body.AuthHandoff.Methods, method => method.MethodId == "email-password" && method.Status == "fallback");
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

    [Fact]
    public async Task Agent_set_project_name_tool_accepts_descriptive_name()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var json = await ExecuteToolAsync(
            client,
            sessionId,
            "quote_set_project_name",
            new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement("Flower Oval – FDM PLA", JsonOptions)
            });

        using var document = JsonDocument.Parse(json);
        Assert.Equal("Flower Oval – FDM PLA", document.RootElement.GetProperty("project_name").GetString());
        Assert.Contains("Flower Oval – FDM PLA", document.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_set_project_name_tool_derives_name_from_filename_when_question_form_given()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        // Register a part first so state has a FileName to derive from.
        await ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_register_uploads",
            new Dictionary<string, JsonElement>
            {
                ["requirements"] = JsonSerializer.SerializeToElement("How much for 3D printing this in PLA?", JsonOptions),
                ["files"] = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        file_name = "bbdb-flower-3-oval.STEP",
                        content_type = "model/step",
                        file_size_bytes = 180_000,
                        kind = "cad",
                        upload_id = "pn-test-cad",
                        storage_path = "quotes/temp/session/pn-test-cad/bbdb-flower-3-oval.STEP"
                    }
                }, JsonOptions)
            });

        var json = await ExecuteToolAsync(
            client,
            sessionId,
            "quote_set_project_name",
            new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement("How much for 3D printing this in PLA?", JsonOptions)
            });

        using var document = JsonDocument.Parse(json);
        var projectName = document.RootElement.GetProperty("project_name").GetString();
        Assert.NotNull(projectName);
        // Should be derived from filename, not the raw question
        Assert.DoesNotContain("?", projectName, StringComparison.Ordinal);
        Assert.False(projectName.StartsWith("how ", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("bbdb", projectName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_set_project_name_tool_clears_name_when_question_form_given_and_no_parts()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var json = await ExecuteToolAsync(
            client,
            sessionId,
            "quote_set_project_name",
            new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement("How much for this?", JsonOptions)
            });

        using var document = JsonDocument.Parse(json);
        Assert.Equal(string.Empty, document.RootElement.GetProperty("project_name").GetString());
    }

    [Fact]
    public async Task Agent_message_context_includes_inference_and_naming_guidance()
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
            Message = "How much for 3D printing in PLA?",
            Language = "en"
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(chatbot.LastSendRequest);
        Assert.Contains("Guidance:", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("FDM", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("Project naming:", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("quote_set_project_name", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("quote_ask_customer", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("Unlabeled sketches need dimension confirmation", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("For PDF/technical drawings, inspect the attached document as drawing context", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("Do not claim you cannot read the PDF", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
        Assert.Contains("must not trigger a 3D preview", chatbot.LastSendRequest.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quote_ask_customer_sets_pending_question_on_session()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var json = await ExecuteToolAsync(
            client,
            sessionId,
            "quote_ask_customer",
            new Dictionary<string, JsonElement>
            {
                ["question"] = JsonSerializer.SerializeToElement("Which manufacturing process do you need?", JsonOptions),
                ["options"] = JsonSerializer.SerializeToElement(new[] { "FDM (filament)", "SLA (resin)", "SLS (nylon)" }, JsonOptions)
            });

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Quote_ask_customer_returns_error_for_too_few_options()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var json = await ExecuteToolAsync(
            client,
            sessionId,
            "quote_ask_customer",
            new Dictionary<string, JsonElement>
            {
                ["question"] = JsonSerializer.SerializeToElement("Which process?", JsonOptions),
                ["options"] = JsonSerializer.SerializeToElement(new[] { "FDM" }, JsonOptions)
            });

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Quote_ask_customer_returns_error_for_too_many_options()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var json = await ExecuteToolAsync(
            client,
            sessionId,
            "quote_ask_customer",
            new Dictionary<string, JsonElement>
            {
                ["question"] = JsonSerializer.SerializeToElement("Which process?", JsonOptions),
                ["options"] = JsonSerializer.SerializeToElement(new[] { "FDM", "SLA", "SLS", "CNC", "Sheet Metal" }, JsonOptions)
            });

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Quote_ask_customer_appears_in_stream_response_and_clears_on_next_message()
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

        // First turn: inject a question into state via tool endpoint
        var firstResponse = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            Message = "I need a part manufactured but not sure which process.",
            Language = "en"
        }, JsonOptions);
        firstResponse.EnsureSuccessStatusCode();
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<QuoteAgentTurnResponse>(JsonOptions);
        Assert.NotNull(firstBody);
        var sessionId = firstBody.SessionId;

        await ExecuteToolAsync(
            client,
            sessionId,
            "quote_ask_customer",
            new Dictionary<string, JsonElement>
            {
                ["question"] = JsonSerializer.SerializeToElement("Which process suits your part?", JsonOptions),
                ["options"] = JsonSerializer.SerializeToElement(new[] { "FDM (plastic)", "CNC (metal)" }, JsonOptions)
            });

        // Second turn: question should be cleared from state (PendingCustomerQuestion cleared at turn start)
        var secondResponse = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = sessionId,
            Message = "FDM please.",
            Language = "en"
        }, JsonOptions);
        secondResponse.EnsureSuccessStatusCode();
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<QuoteAgentTurnResponse>(JsonOptions);
        Assert.NotNull(secondBody);
        Assert.Null(secondBody.CustomerQuestion);
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
        Assert.Contains(body.Gates, gate => gate.Code == "priced" && gate.Status != "passed");

        var configuredState = await ConfigureFirstPartForEstimateAsync(client, body.SessionId);
        Assert.Contains(configuredState.Gates, gate => gate.Code == "configuration_complete" && gate.Status == "passed");

        var pricedState = await ExecuteToolForStateAsync(client, body.SessionId, "quote_calculate_estimate");
        Assert.Contains(pricedState.Gates, gate => gate.Code == "priced" && gate.Status == "passed");
        Assert.NotNull(pricedState.Estimate);
        return body.SessionId;
    }

    private static Task<QuoteAgentStateResponse> ConfigureFirstPartForEstimateAsync(HttpClient client, Guid sessionId)
    {
        return ExecuteToolForStateAsync(
            client,
            sessionId,
            "quote_update_part_configuration",
            new Dictionary<string, JsonElement>
            {
                ["process"] = JsonSerializer.SerializeToElement("fdm_3d_printing", JsonOptions),
                ["material"] = JsonSerializer.SerializeToElement("pla_black", JsonOptions),
                ["finish"] = JsonSerializer.SerializeToElement("as_printed", JsonOptions),
                ["tolerance"] = JsonSerializer.SerializeToElement("standard", JsonOptions),
                ["quantity"] = JsonSerializer.SerializeToElement("25", JsonOptions),
                ["lead_time"] = JsonSerializer.SerializeToElement("STANDARD", JsonOptions)
            });
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

    private static async Task<QuoteAgentProjectSummaryResponse> GetProjectSummaryAsync(
        HttpClient client,
        Guid sessionId)
    {
        var json = await ExecuteToolAsync(client, sessionId, "quote_get_project_summary");
        var summary = JsonSerializer.Deserialize<QuoteAgentProjectSummaryResponse>(json, JsonOptions);

        Assert.NotNull(summary);
        return summary;
    }

    private static async Task<string> ExecuteToolAsync(
        HttpClient client,
        Guid sessionId,
        string toolName,
        Dictionary<string, JsonElement>? arguments = null,
        Guid? customerId = null)
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
            CreateSignedAgentContextToken(sessionId, Guid.NewGuid(), customerId));

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

    private WebApplicationFactory<Program> CreateAgentFactoryWithGoogleDriveConfig()
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:Google:ClientId"] = "quote-engine-google-client",
                    ["Authentication:Google:ClientSecret"] = "quote-engine-google-secret",
                    ["GoogleDrive:RedirectUri"] = "https://make.maliev.com/auth/google/drive/callback"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient, RecordingChatbotServiceClient>();
            });
        });
    }

    private WebApplicationFactory<Program> CreateAgentFactoryWithConnectedGoogleDrive(IHttpClientFactory? googleHttpClientFactory = null)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:Google:ClientId"] = "quote-engine-google-client",
                    ["Authentication:Google:ClientSecret"] = "quote-engine-google-secret",
                    ["GoogleDrive:RedirectUri"] = "https://make.maliev.com/auth/google/drive/callback"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient, RecordingChatbotServiceClient>();
                services.RemoveAll<IGoogleDriveConnectorStore>();
                services.AddSingleton<IGoogleDriveConnectorStore, AlwaysConnectedGoogleDriveConnectorStore>();
                if (googleHttpClientFactory is not null)
                {
                    services.RemoveAll<IHttpClientFactory>();
                    services.AddSingleton(googleHttpClientFactory);
                }
            });
        });
    }

    private sealed class AlwaysConnectedGoogleDriveConnectorStore : IGoogleDriveConnectorStore
    {
        public bool IsConnected(Guid customerId) => true;

        public GoogleDriveConnection? Get(Guid customerId)
        {
            return new GoogleDriveConnection(
                customerId,
                "access-token",
                "refresh-token",
                DateTimeOffset.UtcNow.AddHours(1),
                "drive-connected@example.com",
                DateTimeOffset.UtcNow);
        }

        public void Save(GoogleDriveConnection connection)
        {
        }

        public void Remove(Guid customerId)
        {
        }
    }

    private sealed class RecordingGoogleDriveHttpClientFactory : IHttpClientFactory
    {
        private readonly RecordingGoogleDriveHandler _handler = new();

        public HttpRequestMessage? LastRequest => _handler.LastRequest;

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    private sealed class RecordingGoogleDriveHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    files = new[]
                    {
                        new
                        {
                            id = "drive-file-1",
                            name = "bracket.step",
                            mimeType = "model/step",
                            size = "12345",
                            webViewLink = "https://drive.google.com/file/d/drive-file-1/view"
                        }
                    }
                })
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task Generate_3d_preview_tool_creates_viewer_artifact_with_commands()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        object[] commands =
        [
            new
            {
                op = "box",
                id = "base",
                Params = new[] { 50.0, 30.0, 5.0 }
            },
            new
            {
                op = "cylinder",
                id = "hole",
                Params = new[] { 3.0, 5.0 }
            },
            new
            {
                op = "cut",
                targetId = "base",
                toolId = "hole",
                resultId = "bracket"
            },
            new
            {
                op = "fillet",
                targetId = "bracket",
                radius = 2.0,
                resultId = "finished"
            }
        ];

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Bracket 50x30x5mm with mounting hole", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions),
                ["process_hint"] = JsonSerializer.SerializeToElement("fdm", JsonOptions)
            });

        var toolDoc = JsonDocument.Parse(toolJson);
        var root = toolDoc.RootElement;

        Assert.True(root.TryGetProperty("success", out var success) && success.GetBoolean());
        Assert.True(root.TryGetProperty("artifact_id", out var artifactId) && artifactId.ValueKind == JsonValueKind.String);
        Assert.True(root.TryGetProperty("part_id", out var partId) && partId.ValueKind == JsonValueKind.String);
        Assert.True(root.TryGetProperty("command_count", out var count) && count.GetInt32() == 4);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");

        var viewerArtifact = state.Artifacts.FirstOrDefault(a =>
            a.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            a.Metadata.TryGetValue("generated", out var gen) && gen == "true");

        Assert.NotNull(viewerArtifact);
        Assert.True(viewerArtifact.Metadata.ContainsKey("cad_commands"));
        Assert.True(viewerArtifact.Metadata.ContainsKey("description"));

        Assert.Contains(state.Parts, part =>
            part.FileName.StartsWith("[Preview]", StringComparison.OrdinalIgnoreCase) &&
            part.Status == "ModelGenerated");
    }

    [Fact]
    public async Task Generate_3d_preview_tool_normalizes_operation_casing_for_browser_worker()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        object[] commands =
        [
            new
            {
                op = "Box",
                id = "base",
                Params = new[] { 50.0, 30.0, 5.0 }
            },
            new
            {
                op = "CYLINDER",
                id = "hole",
                Params = new[] { 3.0, 5.0 }
            },
            new
            {
                op = "Cut",
                targetId = "base",
                toolId = "hole",
                resultId = "bracket"
            }
        ];

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Mixed case generated preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean());

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        var commandJson = viewerArtifact.Metadata["cad_commands"];
        using var commandDoc = JsonDocument.Parse(commandJson);
        var ops = commandDoc.RootElement
            .EnumerateArray()
            .Select(command => command.GetProperty("op").GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal(new[] { "box", "cylinder", "cut" }, ops);
    }

    [Fact]
    public async Task Generate_3d_preview_tool_accepts_operation_and_type_command_aliases()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        object[] commands =
        [
            new
            {
                operation = "box",
                id = "base",
                width = 50.0,
                depth = 30.0,
                height = 5.0
            },
            new
            {
                type = "cylinder",
                id = "hole",
                diameter = 6.0,
                height = 8.0
            },
            new
            {
                operation = "cut",
                targetId = "base",
                toolId = "hole",
                resultId = "bracket"
            }
        ];

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Command alias generated preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean());

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var normalizedCommands = commandDoc.RootElement.EnumerateArray().ToArray();

        Assert.Equal("box", normalizedCommands[0].GetProperty("op").GetString());
        Assert.Equal("cylinder", normalizedCommands[1].GetProperty("op").GetString());
        Assert.Equal("cut", normalizedCommands[2].GetProperty("op").GetString());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_accepts_shape_command_alias()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var command = new
        {
            shape = "box",
            id = "base",
            width = 50.0,
            depth = 30.0,
            height = 5.0
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Shape alias generated preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(command, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean());

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var normalizedCommand = commandDoc.RootElement.EnumerateArray().Single();

        Assert.Equal("box", normalizedCommand.GetProperty("op").GetString());
        Assert.Equal(new[] { 50.0, 30.0, 5.0 }, normalizedCommand.GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_accepts_target_tool_and_result_reference_aliases()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        object[] commands =
        [
            new
            {
                op = "box",
                id = "Base",
                width = 50.0,
                depth = 30.0,
                height = 5.0
            },
            new
            {
                op = "cylinder",
                id = "Hole",
                diameter = 6.0,
                height = 8.0
            },
            new
            {
                op = "cut",
                target = "BASE",
                tool = "hole",
                result = "Bracket"
            },
            new
            {
                op = "fillet",
                target = "BRACKET",
                radius = 2.0,
                result = "Finished"
            }
        ];

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Reference alias generated preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean());

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var normalizedCommands = commandDoc.RootElement.EnumerateArray().ToArray();

        Assert.Equal("base", normalizedCommands[2].GetProperty("targetId").GetString());
        Assert.Equal("hole", normalizedCommands[2].GetProperty("toolId").GetString());
        Assert.Equal("bracket", normalizedCommands[2].GetProperty("resultId").GetString());
        Assert.Equal("bracket", normalizedCommands[3].GetProperty("targetId").GetString());
        Assert.Equal("finished", normalizedCommands[3].GetProperty("resultId").GetString());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_accepts_snake_case_shape_reference_fields()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        object[] commands =
        [
            new
            {
                op = "box",
                id = "Base",
                Params = new[] { 50.0, 30.0, 5.0 }
            },
            new
            {
                op = "cylinder",
                id = "Hole",
                Params = new[] { 3.0, 5.0 }
            },
            new
            {
                op = "cut",
                target_id = "BASE",
                tool_id = "hole",
                result_id = "Bracket"
            },
            new
            {
                op = "fillet",
                target_id = "BRACKET",
                radius = 2.0,
                result_id = "Finished"
            }
        ];

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Snake case reference preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean());

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var normalizedCommands = commandDoc.RootElement.EnumerateArray().ToArray();

        Assert.Equal("base", normalizedCommands[2].GetProperty("targetId").GetString());
        Assert.Equal("hole", normalizedCommands[2].GetProperty("toolId").GetString());
        Assert.Equal("bracket", normalizedCommands[2].GetProperty("resultId").GetString());
        Assert.Equal("bracket", normalizedCommands[3].GetProperty("targetId").GetString());
        Assert.Equal("finished", normalizedCommands[3].GetProperty("resultId").GetString());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_normalizes_shape_references_for_browser_worker()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        object[] commands =
        [
            new
            {
                op = "box",
                id = "Base",
                Params = new[] { 50.0, 30.0, 5.0 }
            },
            new
            {
                op = "cylinder",
                id = "Hole",
                Params = new[] { 3.0, 5.0 }
            },
            new
            {
                op = "cut",
                targetId = "BASE",
                toolId = "hole",
                resultId = "Bracket"
            },
            new
            {
                op = "fillet",
                targetId = "BRACKET",
                radius = 2.0,
                resultId = "Finished"
            }
        ];

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Mixed case reference preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean());

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        var commandJson = viewerArtifact.Metadata["cad_commands"];
        using var commandDoc = JsonDocument.Parse(commandJson);
        var normalizedCommands = commandDoc.RootElement.EnumerateArray().ToArray();

        Assert.Equal("base", normalizedCommands[0].GetProperty("id").GetString());
        Assert.Equal("hole", normalizedCommands[1].GetProperty("id").GetString());
        Assert.Equal("base", normalizedCommands[2].GetProperty("targetId").GetString());
        Assert.Equal("hole", normalizedCommands[2].GetProperty("toolId").GetString());
        Assert.Equal("bracket", normalizedCommands[2].GetProperty("resultId").GetString());
        Assert.Equal("bracket", normalizedCommands[3].GetProperty("targetId").GetString());
        Assert.Equal("finished", normalizedCommands[3].GetProperty("resultId").GetString());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_normalizes_profile_plane_for_browser_worker()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "extrude",
                id = "plate",
                Params = new[] { 4.0 },
                profile = new
                {
                    plane = "xy",
                    width = 30.0,
                    height = 12.0
                }
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Lowercase profile plane preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean());

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        var commandJson = viewerArtifact.Metadata["cad_commands"];
        using var commandDoc = JsonDocument.Parse(commandJson);
        var command = commandDoc.RootElement.EnumerateArray().Single();

        Assert.Equal("XY", command.GetProperty("profile").GetProperty("plane").GetString());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_normalizes_profile_segment_types_for_browser_worker()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "extrude",
                id = "sketched_plate",
                Params = new[] { 4.0 },
                profile = new
                {
                    plane = "XY",
                    segments = new object[]
                    {
                        new { type = "Move", Params = new[] { 0.0, 0.0 } },
                        new { type = "LINE", Params = new[] { 30.0, 0.0 } },
                        new { type = "vline", Params = new[] { 12.0 } },
                        new { type = "hline", Params = new[] { -30.0 } }
                    }
                }
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Mixed case sketch segment preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean());

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        var commandJson = viewerArtifact.Metadata["cad_commands"];
        using var commandDoc = JsonDocument.Parse(commandJson);
        var command = commandDoc.RootElement.EnumerateArray().Single();
        var segmentTypes = command.GetProperty("profile")
            .GetProperty("segments")
            .EnumerateArray()
            .Select(segment => segment.GetProperty("type").GetString())
            .ToArray();

        Assert.Equal(new[] { "move", "line", "vLine", "hLine" }, segmentTypes);
    }

    [Fact]
    public async Task Generate_3d_preview_tool_normalizes_named_profile_segment_coordinates_for_browser_worker()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "extrude",
                id = "sketched_plate",
                Params = new[] { 4.0 },
                profile = new
                {
                    plane = "XY",
                    segments = new object[]
                    {
                        new { type = "move", x = 0.0, y = 0.0 },
                        new { type = "line", x = 30.0, y = 0.0 },
                        new { type = "vLine", dy = 12.0 },
                        new { type = "hLine", dx = -30.0 }
                    }
                }
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Named sketch segment coordinate preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        var root = toolDoc.RootElement;

        Assert.True(root.TryGetProperty("success", out var success) && success.GetBoolean());
        Assert.True(root.TryGetProperty("command_count", out var count) && count.GetInt32() == 1);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var segments = commandDoc.RootElement
            .EnumerateArray()
            .Single()
            .GetProperty("profile")
            .GetProperty("segments")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(new[] { 0.0, 0.0 }, segments[0].GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
        Assert.Equal(new[] { 30.0, 0.0 }, segments[1].GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
        Assert.Equal(new[] { 12.0 }, segments[2].GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
        Assert.Equal(new[] { -30.0 }, segments[3].GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_normalizes_named_extrude_height_for_browser_worker()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "extrude",
                id = "plate",
                height = 4.0,
                profile = new
                {
                    plane = "XY",
                    width = 30.0,
                    height = 12.0
                }
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Named extrude height preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        var root = toolDoc.RootElement;

        Assert.True(root.TryGetProperty("success", out var success) && success.GetBoolean());
        Assert.True(root.TryGetProperty("command_count", out var count) && count.GetInt32() == 1);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var command = commandDoc.RootElement.EnumerateArray().Single();

        Assert.Equal(new[] { 4.0 }, command.GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_normalizes_profile_type_params_for_browser_worker()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "extrude",
                id = "rectangular_plate",
                height = 4.0,
                profile = new
                {
                    type = "rectangle",
                    Params = new[] { 30.0, 12.0 }
                }
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Rectangle profile params preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean());

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var profile = commandDoc.RootElement
            .EnumerateArray()
            .Single()
            .GetProperty("profile");

        Assert.Equal("rectangle", profile.GetProperty("type").GetString());
        Assert.Equal(30.0, profile.GetProperty("width").GetDouble());
        Assert.Equal(12.0, profile.GetProperty("height").GetDouble());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_accepts_nested_parameters_aliases()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "extrude",
                id = "sketched_plate",
                parameters = new[] { 4.0 },
                profile = new
                {
                    type = "rectangle",
                    parameters = new[] { 30.0, 12.0 },
                    segments = new object[]
                    {
                        new { type = "move", parameters = new[] { 0.0, 0.0 } },
                        new { type = "line", parameters = new[] { 30.0, 0.0 } }
                    }
                }
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Nested parameters alias preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean());

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var command = commandDoc.RootElement.EnumerateArray().Single();
        var profile = command.GetProperty("profile");
        var segments = profile.GetProperty("segments").EnumerateArray().ToArray();

        Assert.Equal(new[] { 4.0 }, command.GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
        Assert.Equal(new[] { 30.0, 12.0 }, profile.GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
        Assert.Equal(30.0, profile.GetProperty("width").GetDouble());
        Assert.Equal(12.0, profile.GetProperty("height").GetDouble());
        Assert.Equal(new[] { 0.0, 0.0 }, segments[0].GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
        Assert.Equal(new[] { 30.0, 0.0 }, segments[1].GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_requires_at_least_one_command()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var json = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Test", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(Array.Empty<object>(), JsonOptions),
            });

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Generate_3d_preview_tool_rejects_zero_height_cone_before_creating_ready_artifact()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new[]
        {
            new
            {
                op = "cone",
                id = "empty_cone",
                Params = new[] { 12.0, 0.0, 0.0 }
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Zero height cone", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("positive cone height", error.GetString(), StringComparison.OrdinalIgnoreCase);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        Assert.DoesNotContain(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(state.Parts, part => part.Status == "ModelGenerated");
    }

    [Fact]
    public async Task Generate_3d_preview_tool_accepts_stringified_cad_commands()
    {
        // Defense-in-depth: if an upstream path flattens cad_commands into a JSON string
        // (e.g. an LLM stringifies the array argument), the BFF must still recover it
        // rather than rejecting with "At least one CAD command is required."
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        const string stringifiedCommands = """[{"op":"box","id":"part","params":[30,50,100]}]""";

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Rectangular part 30x50x100mm", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(stringifiedCommands, JsonOptions),
                ["process_hint"] = JsonSerializer.SerializeToElement("fdm", JsonOptions)
            });

        var toolDoc = JsonDocument.Parse(toolJson);
        var root = toolDoc.RootElement;

        Assert.True(root.TryGetProperty("success", out var success) && success.GetBoolean());
        Assert.True(root.TryGetProperty("command_count", out var count) && count.GetInt32() == 1);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        Assert.Contains(state.Artifacts, a =>
            a.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            a.Metadata.TryGetValue("generated", out var gen) && gen == "true");
    }

    [Fact]
    public async Task Generate_3d_preview_tool_accepts_wrapped_cad_commands()
    {
        // Defense-in-depth: tolerate tool-forwarding paths that wrap the command array
        // instead of passing it as the direct cad_commands value.
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new[]
        {
            new
            {
                op = "box",
                id = "part",
                Params = new[] { 30.0, 50.0, 10.0 }
            }
        };

        var wrappedCommands = new
        {
            commands
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Wrapped command array", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(wrappedCommands, JsonOptions),
                ["process_hint"] = JsonSerializer.SerializeToElement("fdm", JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        var root = toolDoc.RootElement;

        Assert.True(root.TryGetProperty("success", out var success) && success.GetBoolean());
        Assert.True(root.TryGetProperty("command_count", out var count) && count.GetInt32() == 1);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var command = commandDoc.RootElement.EnumerateArray().Single();
        Assert.Equal("box", command.GetProperty("op").GetString());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_accepts_single_cad_command_object()
    {
        // Defense-in-depth: tolerate tool calls that send a single CAD command object
        // instead of wrapping it in an array.
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var command = new
        {
            op = "box",
            id = "part",
            width = 30.0,
            depth = 50.0,
            height = 10.0
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Single command preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(command, JsonOptions),
                ["process_hint"] = JsonSerializer.SerializeToElement("fdm", JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        var root = toolDoc.RootElement;

        Assert.True(root.TryGetProperty("success", out var success) && success.GetBoolean());
        Assert.True(root.TryGetProperty("command_count", out var count) && count.GetInt32() == 1);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var normalizedCommand = commandDoc.RootElement.EnumerateArray().Single();

        Assert.Equal("box", normalizedCommand.GetProperty("op").GetString());
        Assert.Equal(new[] { 30.0, 50.0, 10.0 }, normalizedCommand.GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_accepts_parameters_command_alias()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var command = new
        {
            op = "box",
            id = "part",
            parameters = new[] { 30.0, 50.0, 10.0 }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Parameters alias preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(command, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean());

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var normalizedCommand = commandDoc.RootElement.EnumerateArray().Single();

        Assert.Equal(new[] { 30.0, 50.0, 10.0 }, normalizedCommand.GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_accepts_commands_argument_alias()
    {
        // Defense-in-depth: tolerate agents or tool-forwarding layers that name the
        // top-level command argument "commands" instead of "cad_commands".
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new[]
        {
            new
            {
                op = "box",
                id = "part",
                Params = new[] { 25.0, 20.0, 8.0 }
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Commands alias preview", JsonOptions),
                ["commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        var root = toolDoc.RootElement;

        Assert.True(root.TryGetProperty("success", out var success) && success.GetBoolean());
        Assert.True(root.TryGetProperty("command_count", out var count) && count.GetInt32() == 1);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        Assert.Contains(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Generate_3d_preview_tool_normalizes_named_primitive_dimensions_for_browser_worker()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "box",
                id = "base",
                width = 50.0,
                depth = 30.0,
                height = 5.0
            },
            new
            {
                op = "cylinder",
                id = "hole",
                diameter = 6.0,
                height = 5.0
            },
            new
            {
                op = "sphere",
                id = "knob",
                diameter = 10.0
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Named primitive dimensions preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        var root = toolDoc.RootElement;

        Assert.True(root.TryGetProperty("success", out var success) && success.GetBoolean());
        Assert.True(root.TryGetProperty("command_count", out var count) && count.GetInt32() == 3);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var normalizedCommands = commandDoc.RootElement.EnumerateArray().ToArray();

        Assert.Equal(new[] { 50.0, 30.0, 5.0 }, normalizedCommands[0].GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
        Assert.Equal(new[] { 3.0, 5.0 }, normalizedCommands[1].GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
        Assert.Equal(new[] { 5.0 }, normalizedCommands[2].GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_accepts_dimensions_object_for_primitive()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "box",
                id = "base",
                dimensions = new
                {
                    width = 50.0,
                    depth = 30.0,
                    height = 5.0
                }
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Dimensions object preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean());

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var normalizedCommand = commandDoc.RootElement.EnumerateArray().Single();

        Assert.Equal(new[] { 50.0, 30.0, 5.0 }, normalizedCommand.GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_accepts_dimensions_axis_object_for_box()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "box",
                id = "base",
                dimensions = new
                {
                    x = 50.0,
                    y = 30.0,
                    z = 5.0
                }
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Dimensions axis object preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean());

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var normalizedCommand = commandDoc.RootElement.EnumerateArray().Single();

        Assert.Equal(new[] { 50.0, 30.0, 5.0 }, normalizedCommand.GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_accepts_dimensions_diameter_shorthand()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "cylinder",
                id = "post",
                dimensions = new
                {
                    d = 6.0,
                    height = 12.0
                }
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Dimensions diameter shorthand preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean());

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var normalizedCommand = commandDoc.RootElement.EnumerateArray().Single();

        Assert.Equal(new[] { 3.0, 12.0 }, normalizedCommand.GetProperty("params").EnumerateArray().Select(value => value.GetDouble()).ToArray());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_normalizes_named_translate_offset_for_browser_worker()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "box",
                id = "base",
                Params = new[] { 50.0, 30.0, 5.0 }
            },
            new
            {
                op = "translate",
                targetId = "base",
                resultId = "shifted_base",
                x = 4.0,
                y = -2.0,
                z = 8.0
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Named translate offset preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        var root = toolDoc.RootElement;

        Assert.True(root.TryGetProperty("success", out var success) && success.GetBoolean());
        Assert.True(root.TryGetProperty("command_count", out var count) && count.GetInt32() == 2);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var translateCommand = commandDoc.RootElement.EnumerateArray().ElementAt(1);

        Assert.Equal(new[] { 4.0, -2.0, 8.0 }, translateCommand.GetProperty("offset").EnumerateArray().Select(value => value.GetDouble()).ToArray());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_accepts_translate_offset_object()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "box",
                id = "base",
                Params = new[] { 50.0, 30.0, 5.0 }
            },
            new
            {
                op = "translate",
                targetId = "base",
                resultId = "shifted_base",
                offset = new
                {
                    x = 4.0,
                    y = -2.0,
                    z = 8.0
                }
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Object translate offset preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean());

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var translateCommand = commandDoc.RootElement.EnumerateArray().ElementAt(1);

        Assert.Equal(new[] { 4.0, -2.0, 8.0 }, translateCommand.GetProperty("offset").EnumerateArray().Select(value => value.GetDouble()).ToArray());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_accepts_translate_translation_object()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "box",
                id = "base",
                Params = new[] { 50.0, 30.0, 5.0 }
            },
            new
            {
                op = "translate",
                targetId = "base",
                resultId = "shifted_base",
                translation = new
                {
                    x = 4.0,
                    y = -2.0,
                    z = 8.0
                }
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Object translate translation preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean());

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var translateCommand = commandDoc.RootElement.EnumerateArray().ElementAt(1);

        Assert.Equal(new[] { 4.0, -2.0, 8.0 }, translateCommand.GetProperty("offset").EnumerateArray().Select(value => value.GetDouble()).ToArray());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_normalizes_named_rotate_axis_for_browser_worker()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "box",
                id = "base",
                Params = new[] { 50.0, 30.0, 5.0 }
            },
            new
            {
                op = "rotate",
                targetId = "base",
                resultId = "rotated_base",
                angle = 1.57079632679,
                axisX = 0.0,
                axisY = 1.0,
                axisZ = 0.0
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Named rotate axis preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        var root = toolDoc.RootElement;

        Assert.True(root.TryGetProperty("success", out var success) && success.GetBoolean());
        Assert.True(root.TryGetProperty("command_count", out var count) && count.GetInt32() == 2);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var rotateCommand = commandDoc.RootElement.EnumerateArray().ElementAt(1);

        Assert.Equal(new[] { 0.0, 1.0, 0.0 }, rotateCommand.GetProperty("axis").EnumerateArray().Select(value => value.GetDouble()).ToArray());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_accepts_rotation_axis_object()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "box",
                id = "base",
                Params = new[] { 50.0, 30.0, 5.0 }
            },
            new
            {
                op = "rotate",
                targetId = "base",
                resultId = "rotated_base",
                angle = 1.57079632679,
                rotationAxis = new
                {
                    x = 0.0,
                    y = 1.0,
                    z = 0.0
                }
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Rotation axis object preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean());

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var viewerArtifact = Assert.Single(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        using var commandDoc = JsonDocument.Parse(viewerArtifact.Metadata["cad_commands"]);
        var rotateCommand = commandDoc.RootElement.EnumerateArray().ElementAt(1);

        Assert.Equal(new[] { 0.0, 1.0, 0.0 }, rotateCommand.GetProperty("axis").EnumerateArray().Select(value => value.GetDouble()).ToArray());
    }

    [Fact]
    public async Task Generate_3d_preview_tool_rejects_unsupported_commands_before_creating_ready_artifact()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new[]
        {
            new
            {
                op = "torus",
                id = "unsupported",
                Params = new[] { 20.0, 5.0 }
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Unsupported generated preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("Unsupported CAD operation", error.GetString(), StringComparison.OrdinalIgnoreCase);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        Assert.DoesNotContain(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(state.Parts, part => part.Status == "ModelGenerated");
    }

    [Fact]
    public async Task Generate_3d_preview_tool_rejects_unsupported_profile_segments_before_creating_ready_artifact()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new[]
        {
            new
            {
                op = "extrude",
                id = "part",
                Params = new[] { 10.0 },
                profile = new
                {
                    segments = new[]
                    {
                        new { type = "spline", Params = new[] { 0.0, 0.0, 10.0, 0.0 } }
                    }
                }
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Unsupported spline profile", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("unsupported profile segment", error.GetString(), StringComparison.OrdinalIgnoreCase);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        Assert.DoesNotContain(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(state.Parts, part => part.Status == "ModelGenerated");
    }

    [Fact]
    public async Task Generate_3d_preview_tool_rejects_invalid_profile_plane_before_creating_ready_artifact()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new[]
        {
            new
            {
                op = "extrude",
                id = "part",
                Params = new[] { 10.0 },
                profile = new
                {
                    plane = "AB",
                    radius = 5.0
                }
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Invalid sketch plane", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("profile plane", error.GetString(), StringComparison.OrdinalIgnoreCase);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        Assert.DoesNotContain(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(state.Parts, part => part.Status == "ModelGenerated");
    }

    [Fact]
    public async Task Generate_3d_preview_tool_rejects_invalid_rotation_axis_before_creating_ready_artifact()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "box",
                id = "base",
                Params = new[] { 20.0, 20.0, 5.0 }
            },
            new
            {
                op = "rotate",
                targetId = "base",
                axis = new[] { 0.0, 1.0 },
                angle = Math.PI / 4.0
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Invalid rotate axis", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("rotation axis", error.GetString(), StringComparison.OrdinalIgnoreCase);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        Assert.DoesNotContain(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(state.Parts, part => part.Status == "ModelGenerated");
    }

    [Fact]
    public async Task Generate_3d_preview_tool_rejects_zero_rotation_axis_before_creating_ready_artifact()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "box",
                id = "base",
                Params = new[] { 20.0, 20.0, 5.0 }
            },
            new
            {
                op = "rotate",
                targetId = "base",
                axis = new[] { 0.0, 0.0, 0.0 },
                angle = Math.PI / 4.0
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Zero rotate axis", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("rotation axis", error.GetString(), StringComparison.OrdinalIgnoreCase);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        Assert.DoesNotContain(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(state.Parts, part => part.Status == "ModelGenerated");
    }

    [Fact]
    public async Task Generate_3d_preview_tool_rejects_loft_without_explicit_target_before_creating_ready_artifact()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new object[]
        {
            new
            {
                op = "box",
                id = "base",
                Params = new[] { 20.0, 20.0, 4.0 }
            },
            new
            {
                op = "box",
                id = "top",
                Params = new[] { 12.0, 12.0, 4.0 }
            },
            new
            {
                op = "loft",
                toolId = "top",
                resultId = "lofted"
            }
        };

        var toolJson = await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Loft missing target", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        using var toolDoc = JsonDocument.Parse(toolJson);
        Assert.True(toolDoc.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("targetId", error.GetString(), StringComparison.OrdinalIgnoreCase);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        Assert.DoesNotContain(state.Artifacts, artifact =>
            artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(state.Parts, part => part.Status == "ModelGenerated");
    }

    [Fact]
    public async Task Agent_preview_feedback_records_artifact_feedback_and_observes_customer_memory()
    {
        var customerId = Guid.NewGuid();
        var customerClient = new MemoryCustomerServiceClient(customerId);
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICustomerServiceClient>();
                services.AddSingleton<ICustomerServiceClient>(customerClient);
            });
        });
        using var client = scopedFactory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new[]
        {
            new
            {
                op = "box",
                id = "base",
                Params = new[] { 50.0, 30.0, 5.0 }
            }
        };
        await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Mounting plate preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            },
            customerId);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var artifact = Assert.Single(state.Artifacts, item =>
            item.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            item.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        var feedbackResponse = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/{artifact.ArtifactId:D}/feedback",
            new QuoteAgentPreviewFeedbackRequest
            {
                Rating = 2,
                Comment = "Holes should be closer to the corners and the plate needs rounded edges."
            },
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, feedbackResponse.StatusCode);
        var body = await feedbackResponse.Content.ReadFromJsonAsync<QuoteAgentPreviewFeedbackResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal("recorded", body.Status);

        var updatedState = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var updatedArtifact = Assert.Single(updatedState.Artifacts, item => item.ArtifactId == artifact.ArtifactId);
        Assert.Equal("2", updatedArtifact.Metadata["customerRating"]);
        Assert.Equal("Holes should be closer to the corners and the plate needs rounded edges.", updatedArtifact.Metadata["customerComment"]);

        Assert.Equal(customerId, customerClient.LastObservedMemoryCustomerId);
        Assert.NotNull(customerClient.LastObservedMemory);
        Assert.Equal("make_studio_feedback", customerClient.LastObservedMemory!.MemoryType);
        Assert.Equal("generated_3d_preview_feedback", customerClient.LastObservedMemory.Key);
        Assert.Contains("rounded edges", customerClient.LastObservedMemory.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_preview_feedback_records_artifact_feedback_when_memory_observation_fails()
    {
        var customerId = Guid.NewGuid();
        var customerClient = new MemoryCustomerServiceClient(customerId)
        {
            ThrowOnObserve = true
        };
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICustomerServiceClient>();
                services.AddSingleton<ICustomerServiceClient>(customerClient);
            });
        });
        using var client = scopedFactory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new[]
        {
            new
            {
                op = "box",
                id = "base",
                Params = new[] { 50.0, 30.0, 5.0 }
            }
        };
        await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Memory outage preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            },
            customerId);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var artifact = Assert.Single(state.Artifacts, item =>
            item.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            item.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        var feedbackResponse = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/{artifact.ArtifactId:D}/feedback",
            new QuoteAgentPreviewFeedbackRequest
            {
                Rating = 3,
                Comment = "The proportions are right, but move the hole pattern inward."
            },
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, feedbackResponse.StatusCode);
        var body = await feedbackResponse.Content.ReadFromJsonAsync<QuoteAgentPreviewFeedbackResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal("recorded", body.Status);
        Assert.False(body.MemoryObserved);

        var updatedArtifact = Assert.Single(body.State!.Artifacts, item => item.ArtifactId == artifact.ArtifactId);
        Assert.Equal("3", updatedArtifact.Metadata["customerRating"]);
        Assert.Equal("The proportions are right, but move the hole pattern inward.", updatedArtifact.Metadata["customerComment"]);
        Assert.Equal("feedback_recorded", updatedArtifact.Status);
    }

    [Fact]
    public async Task Agent_preview_feedback_rejects_whitespace_only_comment()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var commands = new[]
        {
            new
            {
                op = "box",
                id = "base",
                Params = new[] { 50.0, 30.0, 5.0 }
            }
        };
        await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Mounting plate preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var artifact = Assert.Single(state.Artifacts, item =>
            item.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            item.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        var feedbackResponse = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/{artifact.ArtifactId:D}/feedback",
            new QuoteAgentPreviewFeedbackRequest
            {
                Rating = 3,
                Comment = "   "
            },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, feedbackResponse.StatusCode);

        var updatedState = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var updatedArtifact = Assert.Single(updatedState.Artifacts, item => item.ArtifactId == artifact.ArtifactId);
        Assert.False(updatedArtifact.Metadata.ContainsKey("customerComment"));
        Assert.False(updatedArtifact.Metadata.ContainsKey("customerRating"));
    }

    [Fact]
    public async Task Agent_preview_feedback_can_record_feedback_for_earlier_generated_preview()
    {
        var customerId = Guid.NewGuid();
        var customerClient = new MemoryCustomerServiceClient(customerId);
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICustomerServiceClient>();
                services.AddSingleton<ICustomerServiceClient>(customerClient);
            });
        });
        using var client = scopedFactory.CreateClient();
        var sessionId = Guid.NewGuid();

        var firstCommands = new[]
        {
            new
            {
                op = "box",
                id = "base",
                Params = new[] { 50.0, 30.0, 5.0 }
            }
        };
        await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("First generated plate", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(firstCommands, JsonOptions)
            },
            customerId);

        var firstState = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var firstArtifact = Assert.Single(firstState.Artifacts, item =>
            item.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            item.Metadata.TryGetValue("description", out var description) &&
            description.Equals("First generated plate", StringComparison.OrdinalIgnoreCase));

        var secondCommands = new[]
        {
            new
            {
                op = "cylinder",
                id = "base",
                Params = new[] { 12.0, 20.0 }
            }
        };
        await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Second generated cylinder", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(secondCommands, JsonOptions)
            },
            customerId);

        var feedbackResponse = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/{firstArtifact.ArtifactId:D}/feedback",
            new QuoteAgentPreviewFeedbackRequest
            {
                Rating = 4,
                Comment = "The first plate was closer; keep that flat rectangular profile."
            },
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, feedbackResponse.StatusCode);

        var updatedState = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        Assert.Contains(updatedState.Artifacts, item =>
            item.ArtifactId == firstArtifact.ArtifactId &&
            item.Metadata.TryGetValue("customerComment", out var comment) &&
            comment.Contains("flat rectangular profile", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(updatedState.Artifacts, item =>
            item.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            item.Metadata.TryGetValue("description", out var description) &&
            description.Equals("Second generated cylinder", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(customerClient.LastObservedMemory);
        Assert.Contains("First generated plate", customerClient.LastObservedMemory!.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_message_after_preview_feedback_includes_observed_feedback_memory()
    {
        var customerEmail = $"preview.feedback.{Guid.NewGuid():N}@example.com";
        var customerId = DeterministicCustomerId(customerEmail);
        var chatbot = new RecordingChatbotServiceClient();
        var customerClient = new MemoryCustomerServiceClient(customerId);
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
                services.RemoveAll<ICustomerServiceClient>();
                services.AddSingleton<ICustomerServiceClient>(customerClient);
            });
        });
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var signIn = await client.GetAsync($"/test/sign-in?email={Uri.EscapeDataString(customerEmail)}");
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        var sessionId = Guid.NewGuid();
        var commands = new[]
        {
            new
            {
                op = "box",
                id = "base",
                Params = new[] { 50.0, 30.0, 5.0 }
            }
        };
        await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Mounting plate preview", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            },
            customerId);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var artifact = Assert.Single(state.Artifacts, item =>
            item.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            item.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        var feedbackResponse = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/{artifact.ArtifactId:D}/feedback",
            new QuoteAgentPreviewFeedbackRequest
            {
                Rating = 1,
                Comment = "Make the next draft thinner with rounded corners."
            },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, feedbackResponse.StatusCode);

        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = sessionId,
            Message = "Please revise the generated design draft.",
            Language = "en"
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(chatbot.LastSendRequest);
        Assert.Contains("Customer memory:", chatbot.LastSendRequest!.Content, StringComparison.Ordinal);
        Assert.Contains("Make the next draft thinner with rounded corners.", chatbot.LastSendRequest.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_message_after_preview_feedback_includes_session_feedback_when_memory_observation_fails()
    {
        var customerEmail = $"preview.feedback.session.{Guid.NewGuid():N}@example.com";
        var customerId = DeterministicCustomerId(customerEmail);
        var chatbot = new RecordingChatbotServiceClient();
        var customerClient = new MemoryCustomerServiceClient(customerId)
        {
            ThrowOnObserve = true
        };
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
                services.RemoveAll<ICustomerServiceClient>();
                services.AddSingleton<ICustomerServiceClient>(customerClient);
            });
        });
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var signIn = await client.GetAsync($"/test/sign-in?email={Uri.EscapeDataString(customerEmail)}");
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        var sessionId = Guid.NewGuid();
        var commands = new[]
        {
            new
            {
                op = "box",
                id = "base",
                Params = new[] { 50.0, 30.0, 5.0 }
            }
        };
        await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Session feedback plate", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            },
            customerId);

        var state = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var artifact = Assert.Single(state.Artifacts, item =>
            item.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            item.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));

        var feedbackResponse = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/{artifact.ArtifactId:D}/feedback",
            new QuoteAgentPreviewFeedbackRequest
            {
                Rating = 2,
                Comment = "Make the mounting ears wider and remove the center boss."
            },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, feedbackResponse.StatusCode);

        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = sessionId,
            Message = "Revise the preview using my feedback.",
            Language = "en"
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(chatbot.LastSendRequest);
        Assert.Contains("Generated preview feedback:", chatbot.LastSendRequest!.Content, StringComparison.Ordinal);
        Assert.Contains("Session feedback plate", chatbot.LastSendRequest.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rating 2/5", chatbot.LastSendRequest.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Make the mounting ears wider and remove the center boss.", chatbot.LastSendRequest.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_message_when_chatbot_send_fails_for_preview_request_returns_generated_preview_artifact()
    {
        var chatbot = new RecordingChatbotServiceClient
        {
            ThrowSendException = true
        };
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddSingleton<IChatbotServiceClient>(chatbot);
            });
        });
        using var client = scopedFactory.CreateClient();
        var sessionId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync("/quote/v1/agent/messages", new QuoteAgentMessageRequest
        {
            SessionId = sessionId,
            Message = "Create a simple 3D preview of a 40 by 30 by 12 mm electronics enclosure with four mounting holes.",
            Language = "en"
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<QuoteAgentTurnResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Contains("3D preview", body.AssistantText, StringComparison.OrdinalIgnoreCase);
        var artifact = Assert.Single(body.Artifacts, item =>
            item.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            item.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ready", artifact.Status);
        Assert.True(artifact.Metadata.TryGetValue("cad_commands", out var commandsJson));

        var commands = JsonSerializer.Deserialize<List<CadCommandDto>>(commandsJson, JsonOptions);
        Assert.NotNull(commands);
        Assert.Contains(commands, command => command.Op.Equals("box", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, commands.Count(command => command.Op.Equals("cut", StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class RecordingChatbotServiceClient : IChatbotServiceClient
    {
        public ChatbotInitiateSessionRequest? LastInitiateRequest { get; private set; }

        public ChatbotSendMessageRequest? LastSendRequest { get; private set; }

        public ChatbotSendMessageRequest? LastStreamRequest { get; private set; }

        public Guid? LastConversationMessagesSessionId { get; private set; }

        public Guid? LastTruncatedSessionId { get; private set; }

        public List<string> Operations { get; } = [];

        public ChatbotConversationMessagesResponse? ConversationMessages { get; init; }

        public bool ThrowStreamException { get; init; }

        public bool ThrowSendException { get; init; }

        public bool HealthAvailable { get; init; } = true;

        public bool TruncateLastTurnResult { get; init; } = true;

        public QuoteAgentUsageSnapshotDto UsageSnapshot { get; init; } = new()
        {
            IsEnabled = true,
            UsedTokens = 850_000,
            DailyTokenBudget = 2_000_000,
            RemainingTokens = 1_150_000,
            UsedRatio = 0.425,
            IsExceeded = false
        };

        public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(HealthAvailable);
        }

        public Task<ChatbotSessionResponse?> InitiateSessionAsync(
            ChatbotInitiateSessionRequest request,
            CancellationToken cancellationToken)
        {
            LastInitiateRequest = request;
            Operations.Add("initiate");
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
            if (ThrowSendException)
            {
                throw new HttpRequestException("ChatbotService is unavailable.");
            }

            LastSendRequest = request;
            Operations.Add("send");
            return Task.FromResult<ChatbotMessageResponse?>(new ChatbotMessageResponse
            {
                MessageId = Guid.Parse("d127db4e-1106-4106-8f6b-32c6b467e8ad"),
                Content = "Upload the bracket CAD file and I will check geometry, DFM, material, and price gates.",
                Role = "assistant",
                Language = "en",
                CreatedAt = DateTimeOffset.UtcNow,
                UsageSnapshot = UsageSnapshot
            });
        }

        public async IAsyncEnumerable<ChatbotMessageStreamEvent> SendMessageStreamAsync(
            ChatbotSendMessageRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastStreamRequest = request;
            Operations.Add("stream");
            await Task.CompletedTask;
            if (ThrowStreamException)
            {
                throw new HttpRequestException("Simulated ChatbotService stream failure.");
            }

            yield return new ChatbotMessageStreamEvent { Type = "started" };
            yield return new ChatbotMessageStreamEvent
            {
                Type = "delta",
                Delta = "Upload the bracket CAD file "
            };
            yield return new ChatbotMessageStreamEvent
            {
                Type = "delta",
                Delta = "and I will check geometry, DFM, material, and price gates."
            };
            yield return new ChatbotMessageStreamEvent
            {
                Type = "final",
                Message = new ChatbotMessageResponse
                {
                    MessageId = Guid.Parse("d127db4e-1106-4106-8f6b-32c6b467e8ad"),
                    Content = "Upload the bracket CAD file and I will check geometry, DFM, material, and price gates.",
                    Role = "assistant",
                    Language = "en",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UsageSnapshot = UsageSnapshot
                }
            };
        }

        public Task<ChatbotConversationMessagesResponse?> GetConversationMessagesAsync(
            Guid sessionId,
            CancellationToken cancellationToken)
        {
            LastConversationMessagesSessionId = sessionId;
            return Task.FromResult(ConversationMessages);
        }

        public Task<bool> TruncateLastTurnAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            LastTruncatedSessionId = sessionId;
            Operations.Add("truncate");
            return Task.FromResult(TruncateLastTurnResult);
        }

        public Task<string?> CleanSpeechAsync(string speech, string language, CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(speech);
        }
    }

    private sealed class RecordingQuoteAgentConversationMap : IQuoteAgentConversationMap
    {
        private readonly Dictionary<Guid, Guid> _mappings = [];

        public Task<Guid?> GetChatbotSessionIdAsync(Guid quoteSessionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_mappings.TryGetValue(quoteSessionId, out var chatbotSessionId)
                ? chatbotSessionId
                : (Guid?)null);
        }

        public Task StoreChatbotSessionIdAsync(Guid quoteSessionId, Guid chatbotSessionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _mappings[quoteSessionId] = chatbotSessionId;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPdfServiceClient : IPdfServiceClient
    {
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

        public string? LastDocumentType { get; private set; }

        public string? LastReferenceId { get; private set; }

        public string? LastDataJson { get; private set; }

        public Task<PdfGenerationResult?> GeneratePdfAsync(
            string documentType,
            string referenceId,
            object data,
            CancellationToken ct = default)
        {
            LastDocumentType = documentType;
            LastReferenceId = referenceId;
            LastDataJson = JsonSerializer.Serialize(data, SerializerOptions);
            return Task.FromResult<PdfGenerationResult?>(new PdfGenerationResult
            {
                RequestId = Guid.Parse("feedfeed-feed-feed-feed-feedfeedfeed"),
                StorageUrl = "/generated/chat-transcript.pdf",
                StoragePath = "generated/chat-transcript.pdf"
            });
        }
    }

    private sealed class RecordingUploadServiceClient()
        : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
    {
        public string? LastInitiatedStoragePath { get; private set; }

        public string? LastStreamedStoragePath { get; private set; }

        public override Task<string> InitiateResumableUploadAsync(
            string fileName,
            string contentType,
            long totalSize,
            string storagePath,
            IReadOnlyDictionary<string, string>? metadataTags,
            CancellationToken ct)
        {
            LastInitiatedStoragePath = storagePath;
            return Task.FromResult($"upload-{Guid.NewGuid():N}");
        }

        public override Task StreamUploadAsync(
            Stream body,
            string contentType,
            long contentLength,
            string contentRange,
            string downstreamUploadId,
            string storagePath,
            CancellationToken ct)
        {
            LastStreamedStoragePath = storagePath;
            return Task.CompletedTask;
        }

        public override Task<string> GetDownloadUrlByPathAsync(
            string storagePath,
            int expirationMinutes = 60,
            CancellationToken ct = default)
        {
            return Task.FromResult($"https://upload.example.test/download/{Uri.EscapeDataString(storagePath)}");
        }
    }

    private sealed class MemoryCustomerServiceClient(Guid expectedCustomerId) : ICustomerServiceClient
    {
        private readonly List<CustomerMemoryResponse> _observedMemories = [];

        public Guid? LastMemoryCustomerId { get; private set; }
        public Guid? LastObservedMemoryCustomerId { get; private set; }
        public CustomerMemoryObserveRequest? LastObservedMemory { get; private set; }
        public bool ThrowOnObserve { get; init; }

        public Task<CustomerProfileResponse?> GetByIdAsync(Guid customerId, CancellationToken ct = default)
        {
            return Task.FromResult<CustomerProfileResponse?>(BuildProfile(customerId));
        }

        public Task<CustomerProfileResponse?> GetByEmailAsync(string email, CancellationToken ct = default)
        {
            return Task.FromResult<CustomerProfileResponse?>(BuildProfile(expectedCustomerId, email));
        }

        public Task<CustomerProfileResponse?> EnsureCustomerAsync(
            string email,
            string displayName,
            string phone = "",
            CancellationToken ct = default)
        {
            return Task.FromResult<CustomerProfileResponse?>(BuildProfile(expectedCustomerId, email, displayName));
        }

        public Task<CustomerProfileResponse?> EnsureCompanyBillingIdentityAsync(
            Guid customerId,
            string companyName,
            string? vatNumber,
            string? phone,
            CancellationToken ct = default)
        {
            var profile = BuildProfile(customerId);
            return Task.FromResult<CustomerProfileResponse?>(profile with
            {
                CompanyName = string.IsNullOrWhiteSpace(companyName) ? "MALIEV Buyer Co." : companyName,
                VatNumber = string.IsNullOrWhiteSpace(vatNumber) ? "1234567890123" : vatNumber
            });
        }

        public Task<CustomerProfileResponse?> UpdateCustomerProfileAsync(
            Guid customerId,
            string displayName,
            string? phone,
            string? companyName,
            string? vatNumber,
            string preferredLanguage,
            string timezone,
            CancellationToken ct = default)
        {
            var profile = BuildProfile(customerId);
            return Task.FromResult<CustomerProfileResponse?>(profile with
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? profile.DisplayName : displayName,
                Phone = string.IsNullOrWhiteSpace(phone) ? profile.Phone : phone,
                CompanyName = string.IsNullOrWhiteSpace(companyName) ? profile.CompanyName : companyName,
                PreferredLanguage = string.IsNullOrWhiteSpace(preferredLanguage) ? profile.PreferredLanguage : preferredLanguage,
                Timezone = string.IsNullOrWhiteSpace(timezone) ? profile.Timezone : timezone,
                VatNumber = string.IsNullOrWhiteSpace(vatNumber) ? profile.VatNumber : vatNumber
            });
        }

        public Task<HttpResponseMessage> GetCustomerAddressesAsync(Guid customerId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Array.Empty<CustomerAddressDto>())
            });
        }

        public Task<HttpResponseMessage> CreateCustomerAddressAsync(object request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        public Task<HttpResponseMessage> UpdateCustomerAddressAsync(Guid addressId, object request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        public Task<HttpResponseMessage> DeleteCustomerAddressAsync(Guid addressId, object request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        public Task<IReadOnlyList<CustomerDocumentDto>?> GetCustomerDocumentsAsync(
            Guid customerId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<CustomerDocumentDto>?>([]);
        }

        public Task<CustomerDocumentDto?> CreateCustomerDocumentAsync(
            Guid customerId,
            CustomerDocumentUploadRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<CustomerDocumentDto?>(new CustomerDocumentDto(
                Guid.NewGuid(),
                request.FileName,
                request.Kind,
                DateTimeOffset.UtcNow,
                request.StoragePath,
                request.ContentType,
                request.FileSizeBytes,
                request.OrderNumber));
        }

        public Task<IReadOnlyList<CustomerNdaDto>?> GetCustomerNdasAsync(
            Guid customerId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<CustomerNdaDto>?>([]);
        }

        public Task<CustomerMemoryQueryResponse> GetCustomerMemoriesAsync(
            Guid customerId,
            string? query,
            int limit,
            CancellationToken cancellationToken)
        {
            LastMemoryCustomerId = customerId;
            return Task.FromResult(new CustomerMemoryQueryResponse
            {
                CustomerId = customerId,
                Query = query ?? string.Empty,
                Limit = limit,
                Items = customerId == expectedCustomerId
                    ? new[]
                        {
                            new CustomerMemoryResponse
                            {
                                Id = Guid.Parse("2e9be30f-6f39-44ed-9a5d-190272c94f45"),
                                CustomerId = customerId,
                                MemoryType = "make_studio_preference",
                                Key = "preferred_material",
                                Value = "Customer prefers PA12 nylon for functional prototypes.",
                                Confidence = 0.88m,
                                Source = "quote_agent",
                                HitCount = 3,
                                LastObservedAt = DateTime.UtcNow
                            }
                        }
                        .Concat(_observedMemories)
                        .Take(limit)
                        .ToList()
                    : []
            });
        }

        public Task<CustomerMemoryResponse?> ObserveCustomerMemoryAsync(
            Guid customerId,
            CustomerMemoryObserveRequest request,
            CancellationToken cancellationToken)
        {
            if (ThrowOnObserve)
            {
                throw new HttpRequestException("Simulated customer memory failure");
            }

            LastObservedMemoryCustomerId = customerId;
            LastObservedMemory = request;
            var response = new CustomerMemoryResponse
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                MemoryType = request.MemoryType,
                Key = request.Key,
                Value = request.Value,
                Confidence = request.Confidence,
                Source = request.Source,
                HitCount = 1,
                LastObservedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _observedMemories.RemoveAll(memory =>
                memory.MemoryType.Equals(request.MemoryType, StringComparison.OrdinalIgnoreCase) &&
                memory.Key.Equals(request.Key, StringComparison.OrdinalIgnoreCase));
            _observedMemories.Insert(0, response);
            return Task.FromResult<CustomerMemoryResponse?>(response);
        }

        private static CustomerProfileResponse BuildProfile(
            Guid customerId,
            string email = "memory@example.com",
            string displayName = "Memory Customer") =>
            new(
                customerId,
                displayName,
                email,
                string.Empty,
                string.Empty,
                "en");
    }

    private static Guid DeterministicCustomerId(string email)
    {
        var idBytes = MD5.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()));
        return new Guid(idBytes);
    }
}
