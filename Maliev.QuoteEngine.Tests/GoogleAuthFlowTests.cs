using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;

namespace Maliev.QuoteEngine.Tests;

public sealed class GoogleAuthFlowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Google_auth_browser_route_redirects_to_sign_in_when_not_configured()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/auth/google?returnUrl=%2Fprojects%2Fnew");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith("/auth/sign-in", response.Headers.Location.OriginalString, StringComparison.Ordinal);
        Assert.Contains("Google%20sign-in%20is%20not%20configured%20yet", response.Headers.Location.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Web_handoff_route_accepts_signed_customer_session_and_sets_quote_cookie()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var customerId = Guid.Parse("39543cbf-f925-4b1c-a723-2402f4f60a5f");
        var token = CreateSignedHandoffToken(customerId);

        var response = await client.GetAsync($"/auth/web-handoff?token={Uri.EscapeDataString(token)}&returnUrl=%2Fndas");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/ndas", response.Headers.Location?.OriginalString);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            value => value.Contains($"maliev_quote_customer={customerId:D}", StringComparison.Ordinal)
                && value.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Web_handoff_route_rejects_invalid_customer_session_token()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/auth/web-handoff?token=invalid-token&returnUrl=%2Fndas");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/auth/sign-in", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    private static string CreateSignedHandoffToken(Guid customerId)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new
        {
            customerId,
            principalId = customerId,
            email = "handoff@example.com",
            displayName = "Handoff Customer",
            issuedAt = now,
            expiresAt = now.AddMinutes(2)
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var encodedPayload = Base64UrlTextEncoder.Encode(Encoding.UTF8.GetBytes(json));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("maliev-local-development-customer-session-handoff-key"));
        var signature = Base64UrlTextEncoder.Encode(hmac.ComputeHash(Encoding.UTF8.GetBytes(encodedPayload)));
        return $"{encodedPayload}.{signature}";
    }
}
