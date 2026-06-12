using Maliev.Aspire.ServiceDefaults;
using Maliev.Aspire.ServiceDefaults.IAM;
using Maliev.QuoteEngine.Bff;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Consumers;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Bff.Options;
using Maliev.QuoteEngine.Bff.Pages;
using Maliev.QuoteEngine.Bff.Security;
using Maliev.QuoteEngine.Bff.Services;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
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
builder.Services.AddScoped<ICustomerChatbotService, CustomerChatbotService>();
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
    cfg.AddConsumer<QuotePaymentFailedConsumer>();
    cfg.AddConsumer<QuotePaymentCancelledConsumer>();
    cfg.AddConsumer<QuoteOrderStatusChangedConsumer>();
});

var app = builder.Build();

app.MapDefaultEndpoints("quote");
app.UseStaticFiles();
app.MapStaticAssets().ShortCircuit();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", LandingPageRenderer.RenderAsync).ExcludeFromDescription();
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

    // /demo is the only public WASM route — everything else requires a valid session.
    // Redirecting unauthenticated users here (server-side, before WASM loads) avoids the
    // 15-30 second WASM cold-start just to end up showing a sign-in redirect anyway.
    var isDemoRoute = context.Request.Path.StartsWithSegments("/demo");
    if (!isAuthenticated && !isDemoRoute)
    {
        var config = context.RequestServices.GetRequiredService<IConfiguration>();
        var webBaseUrl = config["Web:BaseUrl"]?.TrimEnd('/') ?? "https://www.maliev.com";
        var returnUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}{context.Request.QueryString}";
        context.Response.Redirect($"{webBaseUrl}/auth/sign-in?returnUrl={Uri.EscapeDataString(returnUrl)}");
        return;
    }

    var indexPath = Program.ResolveStaticWebAssetPath("index.html");
    if (indexPath is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    // Inject auth state into the page so Blazor pre-hydrates on first paint — no API round-trip needed.
    // Authenticated users see their account info in the header immediately; unauthenticated (/demo)
    // see the sign-in button immediately. Both cases eliminate the sign-in button flash.
    var displayName = user.FindFirst(ClaimTypes.Name)?.Value
        ?? user.FindFirst(ClaimTypes.Email)?.Value
        ?? string.Empty;
    var authJson = isAuthenticated
        ? $"{{\"isSignedIn\":true,\"customerId\":{JsonSerializer.Serialize(customerId)},\"displayName\":{JsonSerializer.Serialize(displayName)}}}"
        : "{\"isSignedIn\":false,\"customerId\":null,\"displayName\":null}";

    var html = await File.ReadAllTextAsync(indexPath, context.RequestAborted);
    html = html.Replace("</head>",
        $"<script>window.getMalievAuth=function(){{return {authJson};}};</script></head>",
        StringComparison.OrdinalIgnoreCase);

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(html, context.RequestAborted);
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
    else if (returnUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
          || returnUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        absReturnUrl = returnUrl;
    }
    else
    {
        absReturnUrl = $"{context.Request.Scheme}://{context.Request.Host}{returnUrl}";
    }
    var destination = string.IsNullOrWhiteSpace(absReturnUrl)
        ? $"{webBaseUrl}/auth/{page}"
        : $"{webBaseUrl}/auth/{page}?returnUrl={Uri.EscapeDataString(absReturnUrl)}";
    return Results.Redirect(destination);
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
}
