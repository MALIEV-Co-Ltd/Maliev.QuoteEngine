using System.Text.Json;

namespace Maliev.QuoteEngine.Tests;

public sealed class BffLoggingConfigurationTests
{
    [Fact]
    public void Appsettings_suppresses_chatbotservice_httpclient_lifecycle_info_logs()
    {
        var appsettingsPath = Path.Combine(
            FindRepositoryRoot().FullName,
            "Maliev.QuoteEngine.Bff",
            "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(appsettingsPath));

        var logLevels = document.RootElement
            .GetProperty("Logging")
            .GetProperty("LogLevel");

        Assert.True(
            logLevels.TryGetProperty("System.Net.Http.HttpClient.ChatbotService", out var chatbotHttpClientLevel),
            "The ChatbotService named HttpClient category should be explicitly filtered so readiness polling does not spam Information logs.");
        Assert.Equal("Warning", chatbotHttpClientLevel.GetString());
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Maliev.QuoteEngine.slnx")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return current!;
    }
}
