using Maliev.Aspire.ServiceDefaults;
using Maliev.Aspire.ServiceDefaults.IAM;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Bff.Security;
using Maliev.QuoteEngine.Bff.Services;
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
builder.Services.AddSingleton<QuoteEnginePrototypeStore>();
builder.Services.AddScoped<CustomerSessionResolver>();
builder.Services.AddScoped<CustomerAssistantHandoffCookie>();
builder.Services.AddScoped<ICustomerChatbotService, CustomerChatbotService>();
builder.AddAuthenticatedServiceClient<IChatbotServiceClient, ChatbotServiceClient>("ChatbotService")
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(45));

var app = builder.Build();

app.MapDefaultEndpoints("quote");
app.UseStaticFiles();
app.MapStaticAssets().ShortCircuit();

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
