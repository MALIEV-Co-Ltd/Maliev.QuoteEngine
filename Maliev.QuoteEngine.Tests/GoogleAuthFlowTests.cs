using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maliev.QuoteEngine.Tests;

/// <summary>
/// Verifies the studio-owned authentication contract: sign-in happens against
/// AuthService via this BFF (Google browser flow + email credentials), the
/// dialog lives in the workspace, and no route depends on the Maliev.Web
/// frontend. The shared identity cookie still provides cross-app SSO.
/// </summary>
public sealed class GoogleAuthFlowTests
{
    private static WebApplicationFactory<Program> CreateFactory(bool configureGoogle = false)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                if (configureGoogle)
                {
                    builder.ConfigureAppConfiguration((_, config) =>
                    {
                        config.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Authentication:Google:ClientId"] = "quoteengine-test-client.apps.googleusercontent.com",
                            ["Authentication:Google:ClientSecret"] = "quoteengine-test-client-secret"
                        });
                    });
                    builder.ConfigureServices(services =>
                    {
                        services.AddAuthentication().AddGoogle(GoogleDefaults.AuthenticationScheme, options =>
                        {
                            options.SignInScheme = IdentityCookieExtensions.ExternalSchemeName;
                            options.ClientId = "quoteengine-test-client.apps.googleusercontent.com";
                            options.ClientSecret = "quoteengine-test-client-secret";
                            options.CallbackPath = "/auth/google/signin";
                            options.Scope.Add("profile");
                            options.Scope.Add("email");
                            options.ClaimActions.MapJsonKey("picture", "picture");
                            options.SaveTokens = true;
                            options.Events.OnRedirectToAuthorizationEndpoint = context =>
                            {
                                context.Response.Redirect(context.RedirectUri + "&prompt=select_account");
                                return Task.CompletedTask;
                            };
                        });
                    });
                }

                builder.ConfigureLogging(logging => logging.ClearProviders());
            });
    }

    [Fact]
    public async Task Sign_in_route_opens_studio_dialog()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/auth/sign-in?returnUrl=%2Fprojects%2Fnew");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var location = response.Headers.Location.OriginalString;
        Assert.StartsWith("/quotes?auth=sign-in", location, StringComparison.Ordinal);
        Assert.Contains("returnUrl=", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_in_route_does_not_forward_external_return_url()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/auth/sign-in?returnUrl=https%3A%2F%2Fevil.example%2Fcheckout");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var location = response.Headers.Location.OriginalString;
        Assert.StartsWith("/quotes?auth=sign-in", location, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sign_up_route_opens_studio_dialog_in_sign_up_mode()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/auth/sign-up");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith("/quotes?auth=sign-up", response.Headers.Location.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Google_auth_route_degrades_gracefully_when_google_is_not_configured()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/auth/google");

        // Testing environment has no Google client credentials: the route sends
        // the customer back to the studio with an error instead of challenging.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("accounts.google.com", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("/quotes", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authError=", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Google_auth_route_emits_quoteengine_signin_redirect_uri_for_current_host()
    {
        await using var factory = CreateFactory(configureGoogle: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost:7297")
        });

        var response = await client.GetAsync("/auth/google?returnUrl=%2Fquote%2Fnew");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("accounts.google.com", response.Headers.Location.Host);
        Assert.Equal(
            "https://localhost:7297/auth/google/signin",
            QueryStringValue(response.Headers.Location, "redirect_uri"));
        Assert.Equal(
            "quoteengine-test-client.apps.googleusercontent.com",
            QueryStringValue(response.Headers.Location, "client_id"));
    }

    [Fact]
    public async Task Web_handoff_route_opens_studio_dialog_and_sets_no_customer_cookie()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/auth/web-handoff?token=anything");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/quotes?auth=sign-in", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            value => value.Contains("maliev_quote_customer=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Session_endpoint_returns_not_signed_in_for_anonymous_request()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.GetAsync("/quote/v1/auth/session");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"isSignedIn\":false", body, StringComparison.OrdinalIgnoreCase);
    }

    private static string? QueryStringValue(Uri uri, string key)
    {
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in query)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(Uri.UnescapeDataString(parts[0]), key, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(parts[1].Replace("+", "%20", StringComparison.Ordinal));
            }
        }

        return null;
    }
}
