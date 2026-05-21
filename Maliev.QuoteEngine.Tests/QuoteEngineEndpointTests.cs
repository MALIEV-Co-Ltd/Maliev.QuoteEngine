using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Shared.Account;
using Maliev.QuoteEngine.Shared.Chatbot;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

/// <summary>
/// Custom factory that sets environment to "Testing" so that:
/// - MassTransit:UseInMemory=true takes effect (no RabbitMQ required)
/// - appsettings.Testing.json is loaded (DemoMode.GlbUrl configured)
/// - All real service HTTP clients are replaced with in-memory fakes
/// </summary>
public sealed class QuoteEngineWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            // Replace the real UploadService HTTP client with a no-op test double.
            services.RemoveAll<QuoteUploadServiceClient>();
            services.AddSingleton<QuoteUploadServiceClient>(new NoOpQuoteUploadServiceClient());

            // Replace real downstream service clients with in-memory fakes.
            services.RemoveAll<IMaterialCatalogClient>();
            services.AddSingleton<IMaterialCatalogClient>(new FakeMaterialCatalogClient());

            services.RemoveAll<IQuotationServiceClient>();
            services.AddSingleton<IQuotationServiceClient>(new FakeQuotationServiceClient());

            services.RemoveAll<IOrderServiceClient>();
            services.AddSingleton<IOrderServiceClient>(new FakeOrderServiceClient());

            // Returns null → AccountController falls back to PrototypeStore for profile
            services.RemoveAll<ICustomerServiceClient>();
            services.AddSingleton<ICustomerServiceClient>(new FakeCustomerServiceClient());

            // Returns a fixed hosted payment URL
            services.RemoveAll<IPaymentServiceClient>();
            services.AddSingleton<IPaymentServiceClient>(new FakePaymentServiceClient());
        });
    }

    // ── Upload no-op ──────────────────────────────────────────────────────────

    private sealed class NoOpQuoteUploadServiceClient()
        : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
    {
        public override Task<string> InitiateResumableUploadAsync(
            string fileName,
            string contentType,
            long totalSize,
            string storagePath,
            CancellationToken ct) => Task.FromResult($"downstream-{Guid.NewGuid():N}");

        public override Task StreamUploadAsync(Stream body, string contentType, long contentLength,
            string contentRange, string downstreamUploadId, string storagePath, CancellationToken ct) => Task.CompletedTask;

        public override Task<string> GetDownloadUrlByPathAsync(string storagePath,
            int expirationMinutes = 60, CancellationToken ct = default)
            => Task.FromResult($"https://test-cdn.example.com/{Uri.EscapeDataString(storagePath)}");
    }

    // ── Fake service clients ──────────────────────────────────────────────────

    private sealed class FakeMaterialCatalogClient : IMaterialCatalogClient
    {
        private static readonly Guid FdmProcessId  = Guid.Parse("11111111-0000-0000-0000-000000000001");
        private static readonly Guid SlaProcessId  = Guid.Parse("11111111-0000-0000-0000-000000000002");
        private static readonly Guid CncProcessId  = Guid.Parse("11111111-0000-0000-0000-000000000003");
        private static readonly Guid DefaultMatId  = Guid.Parse("22222222-0000-0000-0000-000000000001");

        public Task<Guid> ResolveProcessIdAsync(string processCode, CancellationToken ct = default) =>
            Task.FromResult(processCode.ToLowerInvariant() switch
            {
                "sla" => SlaProcessId,
                "cnc" => CncProcessId,
                _     => FdmProcessId
            });

        public Task<Guid> ResolveMaterialIdAsync(string processCode, string materialCode, CancellationToken ct = default) =>
            Task.FromResult(DefaultMatId);
    }

    private sealed class FakeQuotationServiceClient : IQuotationServiceClient
    {
        private readonly ConcurrentDictionary<Guid, QuotationCreatedResult> _quotes = new();

        public Task<QuotationCreatedResult?> CreateAsync(QuotationCreateRequest request, CancellationToken ct = default)
        {
            var result = new QuotationCreatedResult
            {
                Id = Guid.NewGuid(),
                CustomerId = request.CustomerId,
                QuotationNumber = $"MQ-TEST-{Guid.NewGuid():N}"[..16],
                Status = "Draft",
                Total = request.LineItems.Sum(x => x.UnitPrice * x.Quantity),
                CurrencyCode = "THB",
                UpdatedAt = DateTime.UtcNow
            };
            _quotes[result.Id] = result;
            return Task.FromResult<QuotationCreatedResult?>(result);
        }

        public Task<QuotationCreatedResult?> GetByIdAsync(Guid quotationId, CancellationToken ct = default) =>
            Task.FromResult(_quotes.TryGetValue(quotationId, out var r) ? r : null);

        public Task<IReadOnlyList<CustomerQuoteSummaryDto>> GetByCustomerAsync(Guid customerId, CancellationToken ct = default)
        {
            IReadOnlyList<CustomerQuoteSummaryDto> result = _quotes.Values
                .Where(q => q.CustomerId == customerId)
                .Select(q => new CustomerQuoteSummaryDto(
                    q.Id, q.QuotationNumber, q.Status, q.Total, q.CurrencyCode,
                    new DateTimeOffset(q.UpdatedAt, TimeSpan.Zero), string.Empty))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    private sealed class FakeOrderServiceClient : IOrderServiceClient
    {
        private readonly ConcurrentDictionary<string, List<CustomerOrderSummaryDto>> _ordersByCustomer = new();
        private readonly ConcurrentDictionary<string, CustomerOrderDetailDto> _ordersByNumber = new();

        public Task<OrderCreatedResult?> CreateAsync(OrderCreateRequest request, CancellationToken ct = default)
        {
            var orderId = Guid.NewGuid();
            var orderNumber = $"ORD-TEST-{orderId:N}"[..16];
            var summary = new CustomerOrderSummaryDto(
                orderId, orderNumber, "Pending", DateTimeOffset.UtcNow, orderNumber);

            _ordersByCustomer.AddOrUpdate(
                request.CustomerId,
                _ => [summary],
                (_, list) => { lock (list) { list.Add(summary); return list; } });

            var detail = new CustomerOrderDetailDto(
                OrderId: orderId,
                OrderNumber: orderNumber,
                CurrentStatus: "Pending",
                PaymentStatus: "Unpaid",
                QuotedAmount: null,
                QuoteCurrency: "THB",
                PromisedDeliveryDate: null,
                ActualDeliveryDate: null,
                CustomerPoNumber: request.CustomerPoNumber,
                Requirements: request.Requirements,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                StatusHistory: [new OrderStatusEntryDto("Pending", "Your order has been received.", DateTimeOffset.UtcNow)]);
            _ordersByNumber[orderNumber] = detail;

            return Task.FromResult<OrderCreatedResult?>(new OrderCreatedResult
            {
                OrderId = orderId,
                OrderNumber = orderNumber,
                Status = "Pending"
            });
        }

        public Task<IReadOnlyList<CustomerOrderSummaryDto>> GetByCustomerAsync(string customerId, CancellationToken ct = default)
        {
            IReadOnlyList<CustomerOrderSummaryDto> result =
                _ordersByCustomer.TryGetValue(customerId, out var list) ? [.. list] : [];
            return Task.FromResult(result);
        }

        public Task<CustomerOrderDetailDto?> GetDetailAsync(string orderNumber, CancellationToken ct = default) =>
            Task.FromResult(_ordersByNumber.TryGetValue(orderNumber, out var detail) ? detail : null);

        public Task<bool> AddStatusAsync(string orderId, string status, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeCustomerServiceClient : ICustomerServiceClient
    {
        public Task<CustomerProfileResponse?> GetByIdAsync(Guid customerId, CancellationToken ct = default) =>
            Task.FromResult<CustomerProfileResponse?>(null);

        public Task<CustomerProfileResponse?> GetByEmailAsync(string email, CancellationToken ct = default) =>
            Task.FromResult<CustomerProfileResponse?>(CreateProfile(email));

        public Task<CustomerProfileResponse?> EnsureCustomerAsync(
            string email,
            string displayName,
            string phone = "",
            CancellationToken ct = default) =>
            Task.FromResult<CustomerProfileResponse?>(CreateProfile(email, displayName, phone));

        private static CustomerProfileResponse CreateProfile(string email, string displayName = "Quote customer", string phone = "")
        {
            var normalizedEmail = string.IsNullOrWhiteSpace(email) ? "customer@example.com" : email.Trim().ToLowerInvariant();
            var idBytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedEmail));
            return new CustomerProfileResponse(
                new Guid(idBytes),
                string.IsNullOrWhiteSpace(displayName) ? normalizedEmail : displayName,
                normalizedEmail,
                phone,
                string.Empty,
                "en");
        }
    }

    private sealed class FakePaymentServiceClient : IPaymentServiceClient
    {
        public Task<PaymentInitiatedResult?> InitiateAsync(
            string customerId, string orderId, string orderNumber,
            decimal amount, string currency,
            string returnUrl, string cancelUrl, string idempotencyKey,
            CancellationToken ct = default)
        {
            return Task.FromResult<PaymentInitiatedResult?>(new PaymentInitiatedResult
            {
                TransactionId = Guid.NewGuid(),
                PaymentUrl = $"https://pay.test.example.com/hosted/{Guid.NewGuid():N}",
                Status = "1"
            });
        }
    }
}

public sealed class QuoteEngineEndpointTests(QuoteEngineWebApplicationFactory factory)
    : IClassFixture<QuoteEngineWebApplicationFactory>
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
    public async Task ResumeUpload_validates_content_range_and_streams_chunk()
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

        // Missing Content-Range → 400
        using var badContent = new ByteArrayContent([1, 2, 3]);
        var badPut = await client.PutAsync(upload.ProxyUploadUrl, badContent);
        Assert.Equal(HttpStatusCode.BadRequest, badPut.StatusCode);

        // Valid Content-Range → 204 (no-op stream accepted)
        using var goodContent = new ByteArrayContent([1, 2, 3, 4]);
        goodContent.Headers.ContentType = MediaTypeHeaderValue.Parse("model/stl");
        goodContent.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 4);
        var goodPut = await client.PutAsync(upload.ProxyUploadUrl, goodContent);
        Assert.Equal(HttpStatusCode.NoContent, goodPut.StatusCode);
    }

    [Fact]
    public async Task CompleteUpload_demo_filename_returns_analyzed_status()
    {
        using var client = factory.CreateClient();

        var initResp = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            FileName = "maliev-sample-bracket.step",
            ContentType = "application/octet-stream",
            FileSizeBytes = 1024,
            QuoteSessionId = "test-session-demo"
        });
        initResp.EnsureSuccessStatusCode();
        var initiated = await initResp.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();
        Assert.NotNull(initiated);

        var chunkContent = new ByteArrayContent(new byte[1024]);
        chunkContent.Headers.TryAddWithoutValidation("Content-Range", "bytes 0-1023/1024");
        chunkContent.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
        await client.PutAsync(initiated.ProxyUploadUrl, chunkContent);

        var completeResp = await client.PostAsync(
            $"/quote/v1/uploads/resumable/{initiated.UploadId}/complete", null);
        completeResp.EnsureSuccessStatusCode();
        var completed = await completeResp.Content.ReadFromJsonAsync<CompleteQuoteUploadResponse>();

        Assert.NotNull(completed);
        Assert.Equal("Analyzed", completed.Status);
        Assert.Equal("maliev-sample-bracket.step", completed.FileName);
    }

    [Fact]
    public async Task CompleteUpload_non_demo_file_returns_processing_status()
    {
        using var client = factory.CreateClient();

        var initResp = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            FileName = "my-custom-bracket.step",
            ContentType = "application/octet-stream",
            FileSizeBytes = 512,
            QuoteSessionId = "test-session-live"
        });
        initResp.EnsureSuccessStatusCode();
        var initiated = await initResp.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();
        Assert.NotNull(initiated);

        var completeResp = await client.PostAsync(
            $"/quote/v1/uploads/resumable/{initiated.UploadId}/complete", null);
        completeResp.EnsureSuccessStatusCode();
        var completed = await completeResp.Content.ReadFromJsonAsync<CompleteQuoteUploadResponse>();

        Assert.NotNull(completed);
        Assert.Equal("Processing", completed.Status);
        Assert.Equal("my-custom-bracket.step", completed.FileName);
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

        // Create formal quote via real QuotationService integration (FakeQuotationServiceClient)
        var quoteResponse = await customerA.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-a", [], "Customer A quote."));
        quoteResponse.EnsureSuccessStatusCode();
        var quote = await quoteResponse.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);
        Assert.NotEqual(Guid.Empty, quote.QuoteId);

        // Create order referencing that quote
        var orderResponse = await customerA.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, string.Empty, "Customer A accepted."));
        orderResponse.EnsureSuccessStatusCode();

        // Customer A sees their quote and order
        var customerAQuotes = await customerA.GetFromJsonAsync<CustomerQuoteSummaryDto[]>("/quote/v1/account/quotes");
        var customerAOrders = await customerA.GetFromJsonAsync<CustomerOrderSummaryDto[]>("/quote/v1/account/orders");
        Assert.NotNull(customerAQuotes);
        Assert.NotNull(customerAOrders);
        Assert.Contains(customerAQuotes, item => item.QuoteId == quote.QuoteId);
        Assert.Single(customerAOrders);

        // Customer B sees no quotes or orders
        using var customerB = await CreateSignedInClientAsync("quote-owner-b@example.com");
        var customerBQuotes = await customerB.GetFromJsonAsync<CustomerQuoteSummaryDto[]>("/quote/v1/account/quotes");
        var customerBOrders = await customerB.GetFromJsonAsync<CustomerOrderSummaryDto[]>("/quote/v1/account/orders");
        Assert.NotNull(customerBQuotes);
        Assert.NotNull(customerBOrders);
        Assert.Empty(customerBQuotes);
        Assert.Empty(customerBOrders);

        // Customer B cannot create an order against Customer A's quote
        var crossCustomerOrder = await customerB.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, string.Empty, "Cross-customer attempt."));
        Assert.Equal(HttpStatusCode.NotFound, crossCustomerOrder.StatusCode);
    }

    [Fact]
    public async Task Order_detail_returns_status_history_for_signed_in_customer()
    {
        using var client = await CreateSignedInClientAsync("order-detail@example.com");

        // Create an order first
        var quoteResp = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-detail", [], "Detail test."));
        quoteResp.EnsureSuccessStatusCode();
        var quote = await quoteResp.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var orderResp = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, "PO-001", "Test order detail."));
        orderResp.EnsureSuccessStatusCode();
        var order = await orderResp.Content.ReadFromJsonAsync<CreateManufacturingOrderResponse>();
        Assert.NotNull(order);

        // Fetch order detail
        var detailResp = await client.GetFromJsonAsync<CustomerOrderDetailDto>(
            $"/quote/v1/account/orders/{Uri.EscapeDataString(order.OrderNumber)}");

        Assert.NotNull(detailResp);
        Assert.Equal(order.OrderNumber, detailResp.OrderNumber);
        Assert.Equal("Pending", detailResp.CurrentStatus);
        Assert.NotEmpty(detailResp.StatusHistory);
        Assert.Contains(detailResp.StatusHistory, s => s.Status == "Pending");
    }

    [Fact]
    public async Task Order_detail_returns_404_for_unknown_order_number()
    {
        using var client = await CreateSignedInClientAsync("order-detail-404@example.com");

        var response = await client.GetAsync("/quote/v1/account/orders/ORD-DOESNT-EXIST");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Order_detail_returns_401_for_anonymous_user()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/quote/v1/account/orders/ORD-2026-00001");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Payment_initiation_returns_hosted_payment_url_for_signed_in_customer()
    {
        using var client = await CreateSignedInClientAsync("payer@example.com");

        var response = await client.PostAsJsonAsync("/quote/v1/payments", new InitiatePaymentRequest
        {
            OrderId = Guid.NewGuid(),
            OrderNumber = "ORD-2026-99999",
            Amount = 1250.00m,
            Currency = "THB"
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<InitiatePaymentResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.TransactionId);
        Assert.StartsWith("https://pay.test.example.com/hosted/", body.PaymentUrl);
    }

    [Fact]
    public async Task Payment_initiation_returns_401_for_anonymous_user()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/payments", new InitiatePaymentRequest
        {
            OrderId = Guid.NewGuid(),
            OrderNumber = "ORD-2026-00001",
            Amount = 500m,
            Currency = "THB"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Full_self_service_flow_quote_to_payment_succeeds_for_signed_in_customer()
    {
        using var client = await CreateSignedInClientAsync("self-service@example.com");

        // Step 1: Generate a formal quote
        var quoteResp = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-e2e", [], "Self-service E2E test."));
        quoteResp.EnsureSuccessStatusCode();
        var quote = await quoteResp.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);
        Assert.NotEqual(Guid.Empty, quote.QuoteId);

        // Step 2: Create manufacturing order (triggers self-service fast-track: New → Reviewing → Reviewed → Quoted)
        var orderResp = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, "PO-E2E-001", "Full self-service test."));
        orderResp.EnsureSuccessStatusCode();
        var order = await orderResp.Content.ReadFromJsonAsync<CreateManufacturingOrderResponse>();
        Assert.NotNull(order);
        Assert.NotEqual(Guid.Empty, order.OrderId);
        Assert.NotEmpty(order.OrderNumber);

        // Step 3: Initiate payment (advances order to Accepted, then calls PaymentService)
        var paymentResp = await client.PostAsJsonAsync("/quote/v1/payments", new InitiatePaymentRequest
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            Amount = 1500m,
            Currency = "THB"
        });
        paymentResp.EnsureSuccessStatusCode();
        var payment = await paymentResp.Content.ReadFromJsonAsync<InitiatePaymentResponse>();
        Assert.NotNull(payment);
        Assert.NotEqual(Guid.Empty, payment.TransactionId);
        Assert.StartsWith("https://pay.test.example.com/hosted/", payment.PaymentUrl);
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
