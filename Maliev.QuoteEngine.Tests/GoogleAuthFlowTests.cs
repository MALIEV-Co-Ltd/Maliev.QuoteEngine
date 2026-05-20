using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Maliev.QuoteEngine.Tests;

public sealed class GoogleAuthFlowTests
{
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
}
