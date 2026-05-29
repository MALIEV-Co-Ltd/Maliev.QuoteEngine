using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Maliev.QuoteEngine.Tests;

/// <summary>
/// Verifies that QuoteEngine delegates all authentication to Maliev.Web.
/// With the shared-cookie SSO approach, QuoteEngine has no own sign-in surface.
/// </summary>
public sealed class GoogleAuthFlowTests
{
    [Fact]
    public async Task Sign_in_route_redirects_to_web_sign_in_page()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/auth/sign-in?returnUrl=%2Fprojects%2Fnew");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var location = response.Headers.Location.OriginalString;
        Assert.Contains("/auth/sign-in", location, StringComparison.Ordinal);
        Assert.Contains("returnUrl=", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_up_route_redirects_to_web_sign_up_page()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/auth/sign-up");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("/auth/sign-up", response.Headers.Location.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Google_auth_route_is_not_handled_by_quote_engine()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/auth/google");

        // The Google auth route is gone. The SPA fallback catches it and returns the WASM shell (200),
        // NOT a redirect to Google OAuth — confirming QuoteEngine no longer has its own Google sign-in.
        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("accounts.google.com", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Web_handoff_route_is_not_handled_by_quote_engine()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/auth/web-handoff?token=anything");

        // The handoff route was retired. The SPA fallback catches it (200 WASM shell),
        // and no customer cookie should be set (confirming the route is inert).
        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            value => value.Contains("maliev_quote_customer=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Session_endpoint_returns_not_signed_in_for_anonymous_request()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.GetAsync("/quote/v1/auth/session");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"isSignedIn\":false", body, StringComparison.OrdinalIgnoreCase);
    }
}
