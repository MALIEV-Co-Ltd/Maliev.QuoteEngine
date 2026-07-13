using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Controllers;
using Maliev.QuoteEngine.Bff.Security;
using Maliev.QuoteEngine.Shared.Agent;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

[Collection(QuoteEngineEndpointTestCollection.Name)]
public sealed class QuoteIngressBudgetTests(QuoteEngineWebApplicationFactory factory)
    : IClassFixture<QuoteEngineWebApplicationFactory>
{
    public static TheoryData<Type, string, string, long> BoundedEndpoints => new()
    {
        { typeof(QuoteController), nameof(QuoteController.InitiateUpload), BffRateLimiterPolicies.UploadInitiate, 16_384 },
        { typeof(QuoteController), nameof(QuoteController.ResumeUpload), BffRateLimiterPolicies.UploadStream, QuoteUploadConstraints.MaxFileSizeBytes },
        { typeof(QuoteController), nameof(QuoteController.CompleteUpload), BffRateLimiterPolicies.UploadFinalize, 16_384 },
        { typeof(QuoteController), nameof(QuoteController.ImportHandoff), BffRateLimiterPolicies.UploadHandoff, 512_000 },
        { typeof(QuoteController), nameof(QuoteController.Estimate), BffRateLimiterPolicies.Estimate, 256_000 },
        { typeof(AgentController), nameof(AgentController.SubmitLocalDfm), BffRateLimiterPolicies.BrowserReport, 512_000 },
        { typeof(AgentController), nameof(AgentController.UploadSketch), BffRateLimiterPolicies.SketchUpload, 8 * 1024 * 1024 },
        { typeof(GeometryRuntimeController), nameof(GeometryRuntimeController.RecordTelemetry), BffRateLimiterPolicies.BrowserReport, 32_000 }
    };

    [Theory]
    [MemberData(nameof(BoundedEndpoints))]
    public void Anonymous_cost_bearing_endpoints_declare_rate_and_body_budgets(
        Type controllerType,
        string methodName,
        string expectedPolicy,
        long expectedBodyLimit)
    {
        var method = Assert.Single(
            controllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            candidate => candidate.Name == methodName);
        var limiter = Assert.Single(method.GetCustomAttributes<EnableRateLimitingAttribute>());
        var requestLimit = Assert.Single(method.GetCustomAttributes<RequestSizeLimitAttribute>());
        var metadata = Assert.IsAssignableFrom<IRequestSizeLimitMetadata>(requestLimit);

        Assert.Equal(expectedPolicy, limiter.PolicyName);
        Assert.Equal(expectedBodyLimit, metadata.MaxRequestBodySize);
    }

    [Fact]
    public async Task Browser_report_budget_uses_ip_floor_across_valid_visitor_cookies_and_returns_retry_after()
    {
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("QuoteIngress:BrowserReport:PermitLimit", "2"));
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var first = CreateTelemetryRequest(CreateVisitorCookie(Guid.NewGuid()));
        using var second = CreateTelemetryRequest(CreateVisitorCookie(Guid.NewGuid()));
        using var third = CreateTelemetryRequest(CreateVisitorCookie(Guid.NewGuid()));

        using var firstResponse = await client.SendAsync(first);
        using var secondResponse = await client.SendAsync(second);
        using var rejected = await client.SendAsync(third);

        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, secondResponse.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter?.Delta);
        Assert.True(rejected.Headers.RetryAfter!.Delta >= TimeSpan.FromSeconds(1));
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void Estimate_and_browser_reports_reject_unbounded_fanout_and_markers()
    {
        var estimate = new QuoteEstimateRequest
        {
            QuoteSessionId = "session",
            Parts = Enumerable.Range(0, 21).Select(_ => new QuotePartDraftDto()).ToList()
        };
        var dfm = new QuoteAgentLocalDfmRequest
        {
            StoragePath = "quote/file.stl",
            OverlayGlbUrls = Enumerable.Repeat("https://example.test/overlay.glb", 21).ToList()
        };
        var telemetry = new BrowserGeometryRuntimeTelemetryRequest
        {
            Status = new string('x', 81),
            InputByteCount = QuoteUploadConstraints.MaxFileSizeBytes + 1
        };

        Assert.Contains(Validate(estimate), result => result.MemberNames.Contains(nameof(QuoteEstimateRequest.Parts)));
        Assert.Contains(Validate(dfm), result => result.MemberNames.Contains(nameof(QuoteAgentLocalDfmRequest.OverlayGlbUrls)));
        Assert.Contains(Validate(telemetry), result => result.MemberNames.Contains(nameof(BrowserGeometryRuntimeTelemetryRequest.Status)));
        Assert.Contains(Validate(telemetry), result => result.MemberNames.Contains(nameof(BrowserGeometryRuntimeTelemetryRequest.InputByteCount)));
    }

    [Fact]
    public async Task Oversized_telemetry_body_is_rejected_before_controller_execution()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(new { status = "started", inputHash = new string('a', 40_000) }),
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync("/quote/v1/geometry/runtime/telemetry", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Estimate_fanout_limit_rejects_before_pricing_downstream()
    {
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IQePricingServiceClient>();
                services.AddSingleton<IQePricingServiceClient>(new QuoteEngineWebApplicationFactory.SentinelPricingServiceClient());
            }));
        using var client = scopedFactory.CreateClient();

        using var response = await client.PostAsJsonAsync("/quote/v1/estimate", new QuoteEstimateRequest
        {
            QuoteSessionId = Guid.NewGuid().ToString("D"),
            Parts = Enumerable.Range(0, 21).Select(_ => new QuotePartDraftDto()).ToList()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_stream_concurrency_rejects_excess_work_before_downstream()
    {
        var blockingUploadClient = new BlockingQuoteUploadServiceClient();
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("QuoteIngress:UploadStream:ConcurrencyLimit", "1");
            builder.UseSetting("QuoteIngress:UploadStream:RequestLimit", "20");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<QuoteUploadServiceClient>();
                services.AddSingleton<QuoteUploadServiceClient>(blockingUploadClient);
            });
        });
        using var client = scopedFactory.CreateClient();
        using var initiation = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            QuoteSessionId = Guid.NewGuid().ToString("D"),
            FileName = "concurrency.stl",
            ContentType = "model/stl",
            FileSizeBytes = 1
        });
        initiation.EnsureSuccessStatusCode();
        var upload = await initiation.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();
        Assert.NotNull(upload);

        using var firstRequest = CreateUploadRequest(upload.ProxyUploadUrl);
        var firstResponseTask = client.SendAsync(firstRequest);
        await blockingUploadClient.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        using var secondRequest = CreateUploadRequest(upload.ProxyUploadUrl);
        using var rejected = await client.SendAsync(secondRequest);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(1, blockingUploadClient.CallCount);
        Assert.NotNull(rejected.Headers.RetryAfter?.Delta);

        blockingUploadClient.Release();
        using var firstResponse = await firstResponseTask;
        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);
    }

    [Fact]
    public async Task Upload_stream_request_budget_rejects_sequential_excess_streams()
    {
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("QuoteIngress:UploadStream:ConcurrencyLimit", "2");
            builder.UseSetting("QuoteIngress:UploadStream:RequestLimit", "2");
        });
        using var client = scopedFactory.CreateClient();
        var statuses = new List<HttpStatusCode>();

        for (var index = 0; index < 3; index++)
        {
            using var initiation = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
            {
                QuoteSessionId = Guid.NewGuid().ToString("D"),
                FileName = $"stream-{index}.stl",
                ContentType = "model/stl",
                FileSizeBytes = 1
            });
            initiation.EnsureSuccessStatusCode();
            var upload = await initiation.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();
            Assert.NotNull(upload);

            using var request = CreateUploadRequest(upload.ProxyUploadUrl);
            using var response = await client.SendAsync(request);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(
            [HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.TooManyRequests],
            statuses);
    }

    private static HttpRequestMessage CreateTelemetryRequest(string visitorCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/quote/v1/geometry/runtime/telemetry")
        {
            Content = JsonContent.Create(new BrowserGeometryRuntimeTelemetryRequest
            {
                Status = "started",
                ProcessCode = "fdm",
                Authority = "browser-advisory",
                ExecutionMode = "browser"
            })
        };
        request.Headers.Add("Cookie", $"{AnonymousVisitorCookie.CookieName}={visitorCookie}");
        return request;
    }

    private static HttpRequestMessage CreateUploadRequest(string uploadUrl)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
        {
            Content = new ByteArrayContent([0x01])
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("model/stl");
        request.Content.Headers.ContentLength = 1;
        request.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 0-0/1");
        return request;
    }

    private static string CreateVisitorCookie(Guid visitorId)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new AnonymousVisitorPayload(visitorId, now, now.AddHours(1));
        var encodedPayload = Base64UrlTextEncoder.Encode(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("maliev-local-development-anonymous-visitor-key"));
        var signature = Base64UrlTextEncoder.Encode(hmac.ComputeHash(Encoding.UTF8.GetBytes(encodedPayload)));
        return $"{encodedPayload}.{signature}";
    }

    private static IReadOnlyList<ValidationResult> Validate(object value)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(value, new ValidationContext(value), results, validateAllProperties: true);
        return results;
    }

    private sealed class BlockingQuoteUploadServiceClient()
        : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public Task Entered => _entered.Task;

        public int CallCount => Volatile.Read(ref _callCount);

        public void Release() => _release.TrySetResult();

        public override Task<string> InitiateResumableUploadAsync(
            string fileName,
            string contentType,
            long totalSize,
            string storagePath,
            IReadOnlyDictionary<string, string>? metadataTags,
            CancellationToken ct) => Task.FromResult("blocking-upload");

        public override async Task<QuoteUploadStreamProgress> StreamUploadWithProgressAsync(
            Stream body,
            string contentType,
            long contentLength,
            string contentRange,
            string downstreamUploadId,
            string storagePath,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            _entered.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return new QuoteUploadStreamProgress(IsComplete: true, BytesReceived: 1);
        }
    }
}
