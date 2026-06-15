using Maliev.Aspire.ServiceDefaults;
using Maliev.Aspire.ServiceDefaults.IAM;
using Maliev.QuoteEngine.Bff;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Consumers;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Bff.Options;
using Maliev.QuoteEngine.Bff.Security;
using Maliev.QuoteEngine.Bff.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Security.Claims;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseStaticWebAssets();
builder.AddServiceDefaults();
builder.AddDefaultApiVersioning();
builder.AddServiceMeters("quote-engine");
builder.AddIAMServiceClient("QuoteEngineBff");

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
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
builder.Services.AddSingleton<QuoteEnginePrototypeStore>();
builder.Services.AddScoped<CustomerSessionResolver>();
builder.Services.AddScoped<CustomerAssistantHandoffCookie>();
builder.Services.AddScoped<QuoteUploadHandoffToken>();
builder.Services.AddScoped<QuoteAgentContextToken>();
builder.Services.AddScoped<ICustomerChatbotService, CustomerChatbotService>();
builder.Services.AddSingleton<QuoteAgentSessionStore>();
builder.Services.AddSingleton<IGoogleDriveConnectorStore>(sp =>
{
    var redis = sp.GetService<IConnectionMultiplexer>();
    return redis is null
        ? new InMemoryGoogleDriveConnectorStore()
        : new RedisGoogleDriveConnectorStore(
            redis,
            sp.GetRequiredService<IDataProtectionProvider>(),
            sp.GetRequiredService<ILogger<RedisGoogleDriveConnectorStore>>());
});
builder.Services.AddScoped<IQuoteAgentService, QuoteAgentService>();
builder.AddAuthenticatedServiceClient<IChatbotServiceClient, ChatbotServiceClient>("ChatbotService")
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(45));

// ── Real downstream service clients ──────────────────────────────────────────
builder.AddAuthenticatedServiceClient<IMaterialCatalogClient, MaterialCatalogClient>("MaterialService");
builder.AddAuthenticatedServiceClient<IQuotationServiceClient, QuotationServiceClient>("QuotationService");
builder.AddAuthenticatedServiceClient<IOrderServiceClient, OrderServiceClient>("OrderService");
builder.AddAuthenticatedServiceClient<ICustomerServiceClient, CustomerServiceClient>("CustomerService");
builder.AddAuthenticatedServiceClient<ICountryServiceClient, CountryServiceClient>("CountryService");
builder.AddAuthenticatedServiceClient<ICurrencyServiceClient, CurrencyServiceClient>("CurrencyService");
builder.AddAuthenticatedServiceClient<IRegistryServiceClient, RegistryServiceClient>("RegistryService");
builder.AddAuthenticatedServiceClient<IPaymentServiceClient, PaymentServiceClient>("PaymentService");
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

// In-memory file analysis status cache (keyed by storagePath; Redis-upgradable)
builder.Services.AddSingleton<IQuoteFileAnalysisStatusService, QuoteFileAnalysisStatusService>();

// UploadService HTTP client (Aspire service discovery)
builder.Services.AddHttpClient<QuoteUploadServiceClient>(client =>
    {
        client.BaseAddress = new Uri("https+http://UploadService");
        client.Timeout = TimeSpan.FromSeconds(90);
    })
    .AddServiceDiscovery()
    .AddHttpMessageHandler<ServiceAccountAuthenticationHandler>();

// MassTransit consumers — FileAnalyzedEvent → GlbReady, DfmAnalysisReadyEvent → DfmAnalysisReady
builder.AddMassTransitWithRabbitMq(cfg =>
{
    cfg.AddConsumer<QuoteFileAnalyzedConsumer>();
    cfg.AddConsumer<QuoteDfmAnalysisReadyConsumer>();
    cfg.AddConsumer<QuotePaymentCompletedConsumer>();
    cfg.AddConsumer<QuotePaymentPendingConsumer>();
    cfg.AddConsumer<QuotePaymentFailedConsumer>();
    cfg.AddConsumer<QuotePaymentCancelledConsumer>();
    cfg.AddConsumer<QuotePaymentExpiredConsumer>();
    cfg.AddConsumer<QuoteOrderStatusChangedConsumer>();
});

var app = builder.Build();

app.MapDefaultEndpoints("quote");
app.UseForwardedHeaders();
app.UseStaticFiles();
app.MapStaticAssets().ShortCircuit();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", RenderClientAppAsync).ExcludeFromDescription();
// Auth pages redirect to Maliev.Web — QuoteEngine has no own sign-in surface.
app.MapGet("/auth/sign-in", (HttpContext context) => RedirectToWebAuth(context, "sign-in")).ExcludeFromDescription();
app.MapGet("/auth/sign-up", (HttpContext context) => RedirectToWebAuth(context, "sign-up")).ExcludeFromDescription();
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
    var customerId = user.FindFirst("customer_id")?.Value;
    var isAuthenticated = user.Identity?.IsAuthenticated == true
        && Guid.TryParse(customerId, out _);

    // Quote-start routes are public so customers can try the chat-based intake before signing in.
    // Redirecting unauthenticated users here (server-side, before WASM loads) avoids the
    // 15-30 second WASM cold-start just to end up showing a sign-in redirect anyway.
    var normalizedPath = context.Request.Path.Value?.TrimEnd('/') ?? string.Empty;
    var isPublicQuoteStartRoute =
        normalizedPath.Equals("/quotes", StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.StartsWithSegments("/demo") ||
        context.Request.Path.StartsWithSegments("/quote/new") ||
        context.Request.Path.StartsWithSegments("/quotes/new") ||
        context.Request.Path.StartsWithSegments("/projects/new");
    if (!isAuthenticated && !isPublicQuoteStartRoute)
    {
        var config = context.RequestServices.GetRequiredService<IConfiguration>();
        var webBaseUrl = config["Web:BaseUrl"]?.TrimEnd('/') ?? "https://www.maliev.com";
        var returnUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}{context.Request.QueryString}";
        context.Response.Redirect($"{webBaseUrl}/auth/sign-in?returnUrl={Uri.EscapeDataString(returnUrl)}");
        return;
    }

    await RenderClientAppAsync(context);
});

app.Run();

static IResult RedirectToWebAuth(HttpContext context, string page)
{
    var webBaseUrl = context.RequestServices
        .GetRequiredService<IConfiguration>()["Web:BaseUrl"]?.TrimEnd('/') ?? "https://www.maliev.com";
    var returnUrl = context.Request.Query["returnUrl"].ToString();
    string absReturnUrl;
    if (string.IsNullOrWhiteSpace(returnUrl))
    {
        absReturnUrl = string.Empty;
    }
    else if (Uri.TryCreate(returnUrl, UriKind.Absolute, out var absoluteReturnUrl))
    {
        absReturnUrl = IsSameOriginReturnUrl(context, absoluteReturnUrl)
            ? absoluteReturnUrl.ToString()
            : string.Empty;
    }
    else if (returnUrl.StartsWith("/", StringComparison.Ordinal)
             && !returnUrl.StartsWith("//", StringComparison.Ordinal)
             && !returnUrl.StartsWith("/\\", StringComparison.Ordinal))
    {
        absReturnUrl = $"{context.Request.Scheme}://{context.Request.Host}{returnUrl}";
    }
    else
    {
        absReturnUrl = string.Empty;
    }
    var destination = string.IsNullOrWhiteSpace(absReturnUrl)
        ? $"{webBaseUrl}/auth/{page}"
        : $"{webBaseUrl}/auth/{page}?returnUrl={Uri.EscapeDataString(absReturnUrl)}";
    return Results.Redirect(destination);
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
    var customerId = user.FindFirst("customer_id")?.Value;
    var isAuthenticated = user.Identity?.IsAuthenticated == true
        && Guid.TryParse(customerId, out _);

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
