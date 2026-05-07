using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Bff.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultApiVersioning();

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<QuoteEnginePrototypeStore>();
builder.Services.AddScoped<CustomerSessionResolver>();

var app = builder.Build();

app.MapDefaultEndpoints("quote");
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapControllers();
app.MapHub<QuoteNotificationsHub>("/hubs/quote-notifications");
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program
{
}
