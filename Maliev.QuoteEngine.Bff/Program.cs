using Maliev.Aspire.ServiceDefaults;
using Maliev.Aspire.ServiceDefaults.IAM;
using Maliev.QuoteEngine.Bff;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Consumers;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Bff.Options;
using Maliev.QuoteEngine.Bff.Security;
using Maliev.QuoteEngine.Bff.Services;
using MassTransit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseStaticWebAssets();
builder.AddServiceDefaults();
builder.AddDefaultApiVersioning();
builder.AddServiceMeters("quote-engine");
builder.AddIAMServiceClient("QuoteEngineBff");

var redisConnectionString = builder.Configuration.GetConnectionString("redis");
if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.AddRedisConnectionMultiplexer();
}
else if (!builder.Environment.IsDevelopment() &&
         !builder.Environment.IsEnvironment("Testing"))
{
    throw new InvalidOperationException(
        "Redis connection string 'redis' is required outside Development and Testing. " +
        "QuoteEngine must not fall back to process-local session ownership in a deployable environment.");
}

builder.Services.AddControllers();
builder.Services.AddQuoteEngineSignalR(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);
var knownProxyAddresses = (builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    .Select(ParseKnownProxy)
    .ToArray();
var knownProxyNetworks = (builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
    .Select(ParseKnownNetwork)
    .ToArray();
if (!builder.Environment.IsDevelopment()
    && !builder.Environment.IsEnvironment("Testing")
    && knownProxyAddresses.Length == 0
    && knownProxyNetworks.Length == 0)
{
    throw new InvalidOperationException(
        "At least one trusted ingress proxy or network must be configured outside Development and Testing. "
        + "QuoteEngine rate limits must never partition all customers by an untrusted proxy address.");
}

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;

    foreach (var proxy in knownProxyAddresses)
    {
        options.KnownProxies.Add(proxy);
    }

    foreach (var network in knownProxyNetworks)
    {
        options.KnownIPNetworks.Add(network);
    }
});

static IPAddress ParseKnownProxy(string value) =>
    IPAddress.TryParse(value, out var proxy)
        ? proxy
        : throw new InvalidOperationException($"ForwardedHeaders trusted proxy '{value}' is not a valid IP address.");

static System.Net.IPNetwork ParseKnownNetwork(string value) =>
    System.Net.IPNetwork.TryParse(value, out var network)
        ? network
        : throw new InvalidOperationException($"ForwardedHeaders trusted network '{value}' is not valid CIDR notation.");
builder.Services.AddSingleton<BffMetrics>();
builder.AddMalievIdentityCookie(options =>
{
    // Unauthenticated requests redirect cross-domain to the Web sign-in page,
    // passing the original QuoteEngine URL as an absolute returnUrl.
    options.Events.OnRedirectToLogin = context =>
    {
        var webBaseUrl = context.HttpContext.RequestServices
            .GetRequiredService<IConfiguration>()["Web:BaseUrl"]?.TrimEnd('/') ?? "https://www.maliev.com";
        var returnUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.RedirectUri}";
        context.Response.Redirect($"{webBaseUrl}/auth/sign-in?returnUrl={Uri.EscapeDataString(returnUrl)}");
        return Task.CompletedTask;
    };
});
// Persist Data Protection keys to Redis so Web and QuoteEngine share the same key ring.
builder.Services.AddSingleton<IPostConfigureOptions<KeyManagementOptions>>(sp =>
    new PostConfigureOptions<KeyManagementOptions>(Microsoft.Extensions.Options.Options.DefaultName, opts =>
    {
        var mux = sp.GetService<IConnectionMultiplexer>();
        if (mux is not null)
        {
            opts.XmlRepository = new RedisXmlRepository(
                () => mux.GetDatabase(),
                IdentityCookieExtensions.DataProtectionRedisKey);
        }
    }));
builder.Services.AddAuthorization();
builder.AddStandardCors();
builder.Services.AddSingleton<QuoteEnginePrototypeStore>();
builder.Services.AddScoped<CustomerSessionResolver>();
builder.Services.AddScoped<AnonymousVisitorCookie>();

// Defense-in-depth budgets for anonymous, cost-bearing ingress. Every policy is partitioned by the
// trusted remote IP so clearing or rotating the signed visitor cookie cannot reset the abuse floor.
// Limits are disabled by default under integration tests unless a test enables one explicitly.
var isTesting = builder.Environment.IsEnvironment("Testing");
var agentRateLimitPerMinute = GetIngressLimit("QuoteAgent:RateLimit:PerMinute", 30);
var uploadInitiateLimit = GetIngressLimit("QuoteIngress:UploadInitiate:PermitLimit", 20);
var uploadFinalizeLimit = GetIngressLimit("QuoteIngress:UploadFinalize:PermitLimit", 60);
var uploadHandoffLimit = GetIngressLimit("QuoteIngress:UploadHandoff:PermitLimit", 6);
var uploadStreamRequestLimit = GetIngressLimit("QuoteIngress:UploadStream:RequestLimit", 20);
var uploadStreamConcurrency = GetIngressLimit("QuoteIngress:UploadStream:ConcurrencyLimit", 2);
var estimateLimit = GetIngressLimit("QuoteIngress:Estimate:PermitLimit", 12);
var browserReportLimit = GetIngressLimit("QuoteIngress:BrowserReport:PermitLimit", 120);
var sketchUploadLimit = GetIngressLimit("QuoteIngress:SketchUpload:PermitLimit", 6);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var leaseRetryAfter)
            ? leaseRetryAfter
            : TimeSpan.FromSeconds(1);
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        await response.WriteAsJsonAsync(
            new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Request rate limit exceeded.",
                Detail = "Please wait before retrying this operation."
            },
            options: null,
            contentType: "application/problem+json",
            cancellationToken: cancellationToken);
    };
    options.AddPolicy(BffRateLimiterPolicies.QuoteAgent, httpContext =>
        FixedWindowByClientIp(httpContext, agentRateLimitPerMinute));
    options.AddPolicy(BffRateLimiterPolicies.UploadInitiate, httpContext =>
        FixedWindowByClientIp(httpContext, uploadInitiateLimit));
    options.AddPolicy(BffRateLimiterPolicies.UploadFinalize, httpContext =>
        FixedWindowByClientIp(httpContext, uploadFinalizeLimit));
    options.AddPolicy(BffRateLimiterPolicies.UploadHandoff, httpContext =>
        FixedWindowByClientIp(httpContext, uploadHandoffLimit));
    options.AddPolicy(BffRateLimiterPolicies.Estimate, httpContext =>
        FixedWindowByClientIp(httpContext, estimateLimit));
    options.AddPolicy(BffRateLimiterPolicies.BrowserReport, httpContext =>
        FixedWindowByClientIp(httpContext, browserReportLimit));
    options.AddPolicy(BffRateLimiterPolicies.SketchUpload, httpContext =>
        FixedWindowByClientIp(httpContext, sketchUploadLimit));
    options.AddPolicy(BffRateLimiterPolicies.UploadStream, httpContext =>
    {
        if (uploadStreamConcurrency <= 0 || uploadStreamRequestLimit <= 0)
        {
            return RateLimitPartition.GetNoLimiter("disabled");
        }

        return RateLimitPartition.Get(ClientIpKey(httpContext), _ => RateLimiter.CreateChained(
            new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = uploadStreamRequestLimit,
                Window = TimeSpan.FromMinutes(1),
                AutoReplenishment = true,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }),
            new ConcurrencyLimiter(new ConcurrencyLimiterOptions
            {
                PermitLimit = uploadStreamConcurrency,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            })));
    });
});

int GetIngressLimit(string key, int productionDefault)
{
    var limit = builder.Configuration.GetValue<int?>(key) ?? (isTesting ? 0 : productionDefault);
    if (limit < 0 || (!isTesting && limit == 0))
    {
        throw new InvalidOperationException($"Ingress budget '{key}' must be positive outside the Testing environment.");
    }

    return limit;
}

static RateLimitPartition<string> FixedWindowByClientIp(HttpContext context, int permitLimit)
{
    if (permitLimit <= 0)
    {
        return RateLimitPartition.GetNoLimiter("disabled");
    }

    return RateLimitPartition.GetFixedWindowLimiter(ClientIpKey(context), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = permitLimit,
        Window = TimeSpan.FromMinutes(1),
        AutoReplenishment = true,
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
    });
}

static string ClientIpKey(HttpContext context) =>
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
builder.Services.AddScoped<CustomerAssistantHandoffCookie>();
builder.Services.AddScoped<QuoteUploadHandoffToken>();
builder.Services.AddScoped<QuoteAgentContextToken>();
builder.Services.AddOptions<QuoteAgentRetentionOptions>()
    .BindConfiguration(QuoteAgentRetentionOptions.Section)
    .Validate(
        options => options.HasValidBounds,
        "Quote agent retention must be positive, anonymous retention cannot exceed 30 days, and customer retention must be between anonymous retention and 365 days.")
    .ValidateOnStart();
builder.Services.AddSingleton<QuoteAgentSessionStore>();
builder.Services.AddSingleton<IQuoteAgentSessionOwnerStore>(sp =>
{
    var redis = sp.GetService<IConnectionMultiplexer>();
    if (redis is not null)
    {
        return new RedisQuoteAgentSessionOwnerStore(
            redis,
            sp.GetRequiredService<IOptions<QuoteAgentRetentionOptions>>());
    }

    var environment = sp.GetRequiredService<IHostEnvironment>();
    if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
    {
        throw new InvalidOperationException("Durable QuoteEngine session ownership requires Redis.");
    }

    return new InMemoryQuoteAgentSessionOwnerStore();
});
builder.Services.AddSingleton<IQuoteAgentConversationMap>(sp =>
{
    var redis = sp.GetService<IConnectionMultiplexer>();
    if (redis is not null)
    {
        return new RedisQuoteAgentConversationMap(
            redis,
            sp.GetRequiredService<ILogger<RedisQuoteAgentConversationMap>>(),
            sp.GetRequiredService<IOptions<QuoteAgentRetentionOptions>>());
    }

    var environment = sp.GetRequiredService<IHostEnvironment>();
    if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
    {
        throw new InvalidOperationException("Durable QuoteEngine conversation mapping requires Redis.");
    }

    return new InMemoryQuoteAgentConversationMap();
});
builder.Services.AddScoped<QuoteAgentSessionAccess>();
builder.Services.AddScoped<QuoteAgentAttachmentAccess>();
builder.Services.AddScoped<QuoteNotificationSubscriptionAuthorizer>();
builder.Services.AddScoped<IQuoteAgentSessionRequestAuthorizer>(sp =>
    sp.GetRequiredService<QuoteAgentSessionAccess>());
builder.Services.AddScoped<IQuoteAgentServerContextAuthorizer>(sp =>
    sp.GetRequiredService<QuoteAgentSessionAccess>());
builder.Services.AddSingleton<IGoogleDriveConnectorStore>(sp =>
{
    var redis = sp.GetService<IConnectionMultiplexer>();
    if (redis is not null)
    {
        return new RedisGoogleDriveConnectorStore(
            redis,
            sp.GetRequiredService<IDataProtectionProvider>(),
            sp.GetRequiredService<ILogger<RedisGoogleDriveConnectorStore>>());
    }

    var environment = sp.GetRequiredService<IHostEnvironment>();
    if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
    {
        throw new InvalidOperationException("Durable Google Drive connector state requires Redis.");
    }

    return new InMemoryGoogleDriveConnectorStore();
});
builder.Services.AddSingleton<IGoogleDriveOAuthStateStore>(sp =>
{
    var redis = sp.GetService<IConnectionMultiplexer>();
    if (redis is not null)
    {
        return new RedisGoogleDriveOAuthStateStore(
            redis,
            sp.GetRequiredService<IDataProtectionProvider>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RedisGoogleDriveOAuthStateStore>>());
    }

    var environment = sp.GetRequiredService<IHostEnvironment>();
    if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
    {
        throw new InvalidOperationException("One-time Google Drive OAuth state requires Redis.");
    }

    return new InMemoryGoogleDriveOAuthStateStore();
});
builder.Services.AddScoped<IQuoteAgentService, QuoteAgentService>();
builder.AddAuthenticatedServiceClient<IChatbotServiceClient, ChatbotServiceClient>("ChatbotService")
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(45));
builder.AddAuthenticatedServiceClient<IPdfServiceClient, PdfServiceClient>("PdfService")
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMinutes(2));

// ── Real downstream service clients ──────────────────────────────────────────
builder.AddAuthenticatedServiceClient<IAuthServiceClient, AuthServiceClient>("AuthService");
builder.AddAuthenticatedServiceClient<ICustomerRegistrationClient, CustomerRegistrationClient>("CustomerService");
builder.AddAuthenticatedServiceClient<IMaterialCatalogClient, MaterialCatalogClient>("MaterialService");
builder.AddAuthenticatedServiceClient<IQuotationServiceClient, QuotationServiceClient>("QuotationService");
builder.AddAuthenticatedServiceClient<IOrderServiceClient, OrderServiceClient>("OrderService");
builder.AddAuthenticatedServiceClient<IDeliveryServiceClient, DeliveryServiceClient>("DeliveryService");
builder.AddAuthenticatedServiceClient<ICustomerServiceClient, CustomerServiceClient>("CustomerService");
builder.AddAuthenticatedServiceClient<IProjectServiceClient, ProjectServiceClient>("ProjectService");
builder.AddAuthenticatedServiceClient<ISearchServiceClient, SearchServiceClient>("SearchService");
builder.AddAuthenticatedServiceClient<ICountryServiceClient, CountryServiceClient>("CountryService");
builder.AddAuthenticatedServiceClient<ICurrencyServiceClient, CurrencyServiceClient>("CurrencyService");
builder.AddAuthenticatedServiceClient<IRegistryServiceClient, RegistryServiceClient>("RegistryService");
builder.AddAuthenticatedServiceClient<IPaymentServiceClient, PaymentServiceClient>("PaymentService");
builder.AddAuthenticatedServiceClient<IInvoiceServiceClient, InvoiceServiceClient>("InvoiceService");
builder.AddAuthenticatedServiceClient<IReceiptServiceClient, ReceiptServiceClient>("ReceiptService");
builder.AddAuthenticatedServiceClient<IQePricingServiceClient, PricingServiceClient>("PricingService");
builder.Services.AddHttpClient<IQuoteGeometryRuntimeClient, QuoteGeometryRuntimeClient>(client =>
    {
        client.BaseAddress = new Uri("https+http://GeometryService");
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .AddServiceDiscovery()
    .AddHttpMessageHandler<ServiceAccountAuthenticationHandler>();
builder.Services.AddSingleton<GeometryRuntimeFallbackProvider>();

// DemoMode options (short-circuit for sample bracket in dev/demo)
builder.Services.Configure<DemoModeOptions>(
    builder.Configuration.GetSection(DemoModeOptions.Section));

builder.Services.AddQuoteFileAnalysisStatus();

// UploadService HTTP client (Aspire service discovery)
builder.Services.AddHttpClient<QuoteUploadServiceClient>(client =>
    {
        client.BaseAddress = new Uri("https+http://UploadService");
        client.Timeout = TimeSpan.FromSeconds(90);
    })
    .AddServiceDiscovery()
    .AddHttpMessageHandler<ServiceAccountAuthenticationHandler>();

// GeometryService publishes Python-generated MassTransit envelopes to a shared topic exchange.
// Keep those consumers on one explicit durable queue; the remaining consumers retain their
// conventional auto-configured endpoints.
builder.AddMassTransitWithRabbitMq(
    configure: cfg =>
    {
        cfg.AddConsumer<QuoteFileMetricsReadyConsumer>().ExcludeFromConfigureEndpoints();
        cfg.AddConsumer<QuoteFileAnalyzedConsumer>().ExcludeFromConfigureEndpoints();
        cfg.AddConsumer<QuoteFileAnalysisFailedConsumer>().ExcludeFromConfigureEndpoints();
        cfg.AddConsumer<QuoteDfmAnalysisReadyConsumer>().ExcludeFromConfigureEndpoints();
        cfg.AddConsumer<QuotePaymentCompletedConsumer>();
        cfg.AddConsumer<QuotePaymentPendingConsumer>();
        cfg.AddConsumer<QuotePaymentFailedConsumer>();
        cfg.AddConsumer<QuotePaymentCancelledConsumer>();
        cfg.AddConsumer<QuotePaymentExpiredConsumer>();
        cfg.AddConsumer<QuoteOrderStatusChangedConsumer>();
    },
    configureRabbitMq: (context, cfg) =>
    {
        cfg.ReceiveEndpoint("quote-engine-geometry-analysis-v1", e =>
        {
            // Status snapshots are now distributed, but consumer pre-checks and downstream side effects are not
            // one atomic Redis claim. Keep this queue sequential and HPA disabled until that follow-up is complete.
            e.ConcurrentMessageLimit = 1;
            e.ConfigureConsumeTopology = false;
            e.ConfigureConsumer<QuoteFileMetricsReadyConsumer>(context);
            e.ConfigureConsumer<QuoteFileAnalyzedConsumer>(context);
            e.ConfigureConsumer<QuoteFileAnalysisFailedConsumer>(context);
            e.ConfigureConsumer<QuoteDfmAnalysisReadyConsumer>(context);
            foreach (var routingKey in new[]
            {
                "maliev.geometryservice.v1.metrics.ready",
                "maliev.geometryservice.v1.analysis.completed",
                "maliev.geometryservice.v1.analysis.failed",
                "maliev.geometryservice.v1.dfm.ready"
            })
            {
                e.Bind("maliev.events", binding =>
                {
                    binding.ExchangeType = "topic";
                    binding.RoutingKey = routingKey;
                });
            }
        });

        cfg.ConfigureEndpoints(context);
    });

var app = builder.Build();

app.MapDefaultEndpoints("quote");
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    if (context.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect("/quotes/new");
        return;
    }

    await next();
});
app.UseStaticFiles();
app.MapStaticAssets().ShortCircuit();

app.UseRouting();
app.Use(async (context, next) =>
{
    var maxRequestBodySize = context.GetEndpoint()?
        .Metadata
        .GetMetadata<IRequestSizeLimitMetadata>()?
        .MaxRequestBodySize;
    if (maxRequestBodySize.HasValue
        && context.Request.ContentLength is long contentLength
        && contentLength > maxRequestBodySize.Value)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await context.Response.WriteAsJsonAsync(
            new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge,
                Title = "Request body too large.",
                Detail = $"This endpoint accepts request bodies up to {maxRequestBodySize.Value} bytes."
            },
            options: null,
            contentType: "application/problem+json",
            cancellationToken: context.RequestAborted);
        return;
    }

    await next();
});
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Issue the signed anonymous-visitor capability used for session and upload ownership. Abuse budgets
// deliberately remain IP-partitioned, so rotating this cookie cannot reset a limiter.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/quote/v1/agent"))
    {
        _ = context.RequestServices.GetRequiredService<AnonymousVisitorCookie>()
            .ResolveForRequest(context);
    }

    await next();
});
app.UseRateLimiter();

app.MapGet("/", RenderClientAppAsync).ExcludeFromDescription();
// Auth pages open the studio sign-in dialog; Google and email flows complete
// against AuthService inside this BFF, never via the Maliev.Web frontend.
app.MapGet("/auth/sign-in", (HttpContext context) => RedirectToStudioAuth(context, "sign-in")).ExcludeFromDescription();
app.MapGet("/auth/sign-up", (HttpContext context) => RedirectToStudioAuth(context, "sign-up")).ExcludeFromDescription();
// Account hub redirect: Blazor client links here instead of embedding the Web URL.
app.MapGet("/account-hub", (IConfiguration config) =>
{
    var webBaseUrl = config["Web:BaseUrl"]?.TrimEnd('/') ?? "https://www.maliev.com";
    return Results.Redirect($"{webBaseUrl}/account/profile");
}).ExcludeFromDescription();
app.MapControllers();
app.MapHub<QuoteNotificationsHub>("/hubs/quote-notifications");
app.MapFallback(async context =>
{
    var user = context.User;
    var customerId = ResolveCustomerIdClaim(user);
    var isAuthenticated = customerId is not null;

    // Quote-start routes are public so customers can try the chat-based intake before signing in.
    // Redirecting unauthenticated users here (server-side, before WASM loads) avoids the
    // 15-30 second WASM cold-start just to end up showing a sign-in redirect anyway.
    var normalizedPath = context.Request.Path.Value?.TrimEnd('/') ?? string.Empty;
    var isPublicQuoteStartRoute =
        normalizedPath.Equals("/quotes", StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.StartsWithSegments("/quote/new") ||
        context.Request.Path.StartsWithSegments("/quotes/new") ||
        context.Request.Path.StartsWithSegments("/projects/new");
    if (!isAuthenticated && !isPublicQuoteStartRoute)
    {
        var returnUrl = $"{context.Request.Path}{context.Request.QueryString}";
        context.Response.Redirect($"/quotes?auth=sign-in&returnUrl={Uri.EscapeDataString(returnUrl)}");
        return;
    }

    await RenderClientAppAsync(context);
});

app.Run();

static IResult RedirectToStudioAuth(HttpContext context, string page)
{
    var mode = page == "sign-up" ? "sign-up" : "sign-in";
    var returnUrl = ReadReturnUrlQuery(context);
    string relativeReturnUrl;
    if (string.IsNullOrWhiteSpace(returnUrl))
    {
        relativeReturnUrl = string.Empty;
    }
    else if (Uri.TryCreate(returnUrl, UriKind.Absolute, out var absoluteReturnUrl))
    {
        relativeReturnUrl = IsSameOriginReturnUrl(context, absoluteReturnUrl)
            ? absoluteReturnUrl.PathAndQuery
            : string.Empty;
    }
    else if (returnUrl.StartsWith("/", StringComparison.Ordinal)
             && !returnUrl.StartsWith("//", StringComparison.Ordinal)
             && !returnUrl.StartsWith("/\\", StringComparison.Ordinal))
    {
        relativeReturnUrl = returnUrl;
    }
    else
    {
        relativeReturnUrl = string.Empty;
    }

    var destination = string.IsNullOrWhiteSpace(relativeReturnUrl)
        ? $"/quotes?auth={mode}"
        : $"/quotes?auth={mode}&returnUrl={Uri.EscapeDataString(relativeReturnUrl)}";
    return Results.Redirect(destination);
}

static string ReadReturnUrlQuery(HttpContext context)
{
    var returnUrl = context.Request.Query["returnUrl"].ToString();
    if (!string.IsNullOrWhiteSpace(returnUrl))
    {
        return returnUrl;
    }

    var rawQuery = context.Request.QueryString.Value;
    if (string.IsNullOrWhiteSpace(rawQuery))
    {
        return string.Empty;
    }

    foreach (var segment in rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var pair = segment.Split('=', 2);
        if (pair.Length == 2 && pair[0].Equals("returnUrl", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.UnescapeDataString(pair[1].Replace("+", " ", StringComparison.Ordinal));
        }
    }

    return string.Empty;
}

static bool IsSameOriginReturnUrl(HttpContext context, Uri returnUrl)
{
    var requestPort = context.Request.Host.Port;
    return (returnUrl.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || returnUrl.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
           && returnUrl.Host.Equals(context.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
           && (requestPort.HasValue
               ? returnUrl.Port == requestPort.Value
               : returnUrl.IsDefaultPort);
}

static async Task RenderClientAppAsync(HttpContext context)
{
    var indexPath = Program.ResolveStaticWebAssetPath("index.html");
    if (indexPath is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var user = context.User;
    var customerId = ResolveCustomerIdClaim(user);
    var isAuthenticated = customerId is not null;

    // Inject auth state into the page so Blazor pre-hydrates on first paint.
    var displayName = user.FindFirst(ClaimTypes.Name)?.Value
        ?? user.FindFirst(ClaimTypes.Email)?.Value
        ?? string.Empty;
    var authJson = isAuthenticated
        ? $"{{\"isSignedIn\":true,\"customerId\":{JsonSerializer.Serialize(customerId)},\"displayName\":{JsonSerializer.Serialize(displayName)}}}"
        : "{\"isSignedIn\":false,\"customerId\":null,\"displayName\":null}";
    var staticAssetMapJson = Program.ResolveStaticWebAssetMapJson();

    var html = await File.ReadAllTextAsync(indexPath, context.RequestAborted);
    html = html.Replace("</head>",
        $"<script>window.malievStaticAssetMap={staticAssetMapJson};window.getMalievAuth=function(){{return {authJson};}};</script></head>",
        StringComparison.OrdinalIgnoreCase);

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(html, context.RequestAborted);
}

static string? ResolveCustomerIdClaim(ClaimsPrincipal user)
{
    if (user.Identity?.IsAuthenticated != true)
    {
        return null;
    }

    var rawCustomerId = user.FindFirst("customer_id")?.Value
        ?? user.FindFirst("customerId")?.Value
        ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    return Guid.TryParse(rawCustomerId, out var customerId) && customerId != Guid.Empty
        ? customerId.ToString("D")
        : null;
}

public partial class Program
{
    internal static string? ResolveStaticWebAssetPath(string relativePath)
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "Maliev.QuoteEngine.Bff.staticwebassets.runtime.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!manifest.RootElement.TryGetProperty("ContentRoots", out var contentRoots))
        {
            return null;
        }

        foreach (var contentRoot in contentRoots.EnumerateArray())
        {
            var rootPath = contentRoot.GetString();
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                continue;
            }

            var candidate = Path.Combine(rootPath, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static string ResolveStaticWebAssetMapJson()
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var manifestPath in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.staticwebassets.endpoints.json"))
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!manifest.RootElement.TryGetProperty("Endpoints", out var endpoints))
            {
                continue;
            }

            foreach (var endpoint in endpoints.EnumerateArray())
            {
                if (!endpoint.TryGetProperty("Route", out var routeProperty))
                {
                    continue;
                }

                var route = routeProperty.GetString();
                var label = ResolveStaticWebAssetLabel(endpoint);
                if (string.IsNullOrWhiteSpace(route) ||
                    string.IsNullOrWhiteSpace(label) ||
                    string.Equals(route, label, StringComparison.Ordinal))
                {
                    continue;
                }

                map[label] = route;
            }
        }

        return JsonSerializer.Serialize(map);
    }

    private static string? ResolveStaticWebAssetLabel(JsonElement endpoint)
    {
        if (!endpoint.TryGetProperty("EndpointProperties", out var properties))
        {
            return null;
        }

        foreach (var property in properties.EnumerateArray())
        {
            if (!property.TryGetProperty("Name", out var nameProperty) ||
                !string.Equals(nameProperty.GetString(), "label", StringComparison.Ordinal))
            {
                continue;
            }

            return property.TryGetProperty("Value", out var valueProperty)
                ? valueProperty.GetString()
                : null;
        }

        return null;
    }
}
