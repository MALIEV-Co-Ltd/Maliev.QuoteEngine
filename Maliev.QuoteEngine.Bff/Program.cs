using Maliev.Aspire.ServiceDefaults;
using Maliev.Aspire.ServiceDefaults.IAM;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Consumers;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Bff.Options;
using Maliev.QuoteEngine.Bff.Security;
using Maliev.QuoteEngine.Bff.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseStaticWebAssets();
builder.AddServiceDefaults();
builder.AddDefaultApiVersioning();
builder.AddIAMServiceClient("QuoteEngineBff");

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
var authentication = builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultSignInScheme = "MalievQuoteExternal";
    })
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = "__Host-Maliev.QuoteEngine";
        options.LoginPath = "/auth/sign-in";
        options.LogoutPath = "/quote/v1/auth/sign-out";
        options.AccessDeniedPath = "/auth/sign-in";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
    })
    .AddCookie("MalievQuoteExternal", options =>
    {
        options.Cookie.Name = "__Host-Maliev.QuoteEngine.External";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
    });

var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
{
    authentication.AddGoogle(GoogleDefaults.AuthenticationScheme, options =>
    {
        options.SignInScheme = "MalievQuoteExternal";
        options.ClientId = googleClientId;
        options.ClientSecret = googleClientSecret;
        options.CallbackPath = "/auth/google/signin";
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.SaveTokens = true;
        options.Events.OnRedirectToAuthorizationEndpoint = context =>
        {
            context.Response.Redirect(context.RedirectUri + "&prompt=select_account");
            return Task.CompletedTask;
        };
    });
}
builder.Services.AddAuthorization();
builder.Services.AddSingleton<QuoteEnginePrototypeStore>();
builder.Services.AddScoped<CustomerSessionResolver>();
builder.Services.AddScoped<CustomerAssistantHandoffCookie>();
builder.Services.AddScoped<CustomerSessionHandoffToken>();
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
    cfg.AddConsumer<QuoteOrderStatusChangedConsumer>();
});

var app = builder.Build();

app.MapDefaultEndpoints("quote");
app.UseStaticFiles();
app.MapStaticAssets().ShortCircuit();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<QuoteNotificationsHub>("/hubs/quote-notifications");
app.MapFallback(async context =>
{
    var indexPath = Program.ResolveStaticWebAssetPath("index.html");
    if (indexPath is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(indexPath, context.RequestAborted);
});

app.Run();

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
