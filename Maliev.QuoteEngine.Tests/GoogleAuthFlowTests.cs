using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maliev.QuoteEngine.Tests;

/// <summary>
/// Verifies the official Google Identity Services boundary owned by Make Studio.
/// </summary>
public sealed class GoogleAuthFlowTests
{
    private static (WebApplicationFactory<Program> Factory, RecordingAuthServiceClient AuthClient) CreateFactory(
        bool configureGoogle = false)
    {
        var authClient = new RecordingAuthServiceClient();
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                if (configureGoogle)
                {
                    builder.ConfigureAppConfiguration((_, config) =>
                    {
                        config.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Authentication:Google:ClientId"] = "quoteengine-test-client.apps.googleusercontent.com"
                        });
                    });
                }

                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IAuthServiceClient>();
                    services.AddSingleton<IAuthServiceClient>(authClient);
                });
                builder.ConfigureLogging(logging => logging.ClearProviders());
            });
        return (factory, authClient);
    }

    [Fact]
    public async Task Sign_in_route_opens_studio_dialog()
    {
        var (factory, _) = CreateFactory();
        await using (factory)
        using (var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
        {
            var response = await client.GetAsync("/auth/sign-in?returnUrl=%2Fprojects%2Fnew");

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/quotes?auth=sign-in", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Sign_in_route_does_not_forward_external_return_url()
    {
        var (factory, _) = CreateFactory();
        await using (factory)
        using (var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
        {
            var response = await client.GetAsync(
                "/auth/sign-in?returnUrl=https%3A%2F%2Fevil.example%2Fcheckout");

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var location = response.Headers.Location?.OriginalString ?? string.Empty;
            Assert.StartsWith("/quotes?auth=sign-in", location, StringComparison.Ordinal);
            Assert.DoesNotContain("evil.example", location, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Sign_up_route_opens_studio_dialog_in_sign_up_mode()
    {
        var (factory, _) = CreateFactory();
        await using (factory)
        using (var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
        {
            var response = await client.GetAsync("/auth/sign-up");

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/quotes?auth=sign-up", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Google_config_requires_only_the_public_GIS_client_id()
    {
        var (factory, authClient) = CreateFactory(configureGoogle: true);
        await using (factory)
        using (var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost:7297")
        }))
        {
            var response = await client.PostAsync("/quote/v1/auth/google/config", content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(
                "quoteengine-test-client.apps.googleusercontent.com",
                body.GetProperty("clientId").GetString());
            Assert.Equal(RecordingAuthServiceClient.IssuedNonce, body.GetProperty("nonce").GetString());
            Assert.Equal("quote-engine", authClient.LastNonceRequest.GetProperty("application").GetString());
            var flowCookie = Assert.Single(
                response.Headers.GetValues("Set-Cookie"),
                value => value.Contains("maliev_qe_google_flow", StringComparison.Ordinal));
            Assert.Contains("httponly", flowCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", flowCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", flowCookie, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Google_config_without_client_id_fails_closed()
    {
        var (factory, authClient) = CreateFactory();
        await using (factory)
        using (var client = factory.CreateClient())
        {
            var response = await client.PostAsync("/quote/v1/auth/google/config", content: null);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(0, authClient.NonceRequestCount);
        }
    }

    [Fact]
    public async Task Google_credential_exchange_forwards_only_raw_GIS_contract_and_issues_session()
    {
        var (factory, authClient) = CreateFactory(configureGoogle: true);
        await using (factory)
        using (var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost:7297"),
            HandleCookies = true
        }))
        {
            var config = await client.PostAsync("/quote/v1/auth/google/config", content: null);
            Assert.Equal(HttpStatusCode.OK, config.StatusCode);

            var response = await client.PostAsJsonAsync("/quote/v1/auth/google", new
            {
                credential = "signed-google-id-token",
                nonce = RecordingAuthServiceClient.IssuedNonce,
                preferredLanguage = "en"
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("signed-google-id-token", authClient.LastExchangeRequest.GetProperty("credential").GetString());
            Assert.Equal("quote-engine", authClient.LastExchangeRequest.GetProperty("application").GetString());
            Assert.Equal(RecordingAuthServiceClient.IssuedNonce, authClient.LastExchangeRequest.GetProperty("nonce").GetString());
            Assert.Equal("en", authClient.LastExchangeRequest.GetProperty("preferred_language").GetString());
            Assert.Equal("Asia/Bangkok", authClient.LastExchangeRequest.GetProperty("timezone").GetString());
            Assert.False(authClient.LastExchangeRequest.TryGetProperty("email", out _));
            Assert.False(authClient.LastExchangeRequest.TryGetProperty("google_user_id", out _));
            Assert.Contains(
                response.Headers.GetValues("Set-Cookie"),
                value => value.Contains("__Secure-Maliev.Identity", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Google_credential_exchange_rejects_nonce_not_bound_to_browser_cookie()
    {
        var (factory, authClient) = CreateFactory(configureGoogle: true);
        await using (factory)
        using (var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost:7297"),
            HandleCookies = false
        }))
        {
            var response = await client.PostAsJsonAsync("/quote/v1/auth/google", new
            {
                credential = "signed-google-id-token",
                nonce = RecordingAuthServiceClient.IssuedNonce
            });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(0, authClient.ExchangeRequestCount);
        }
    }

    [Fact]
    public async Task Google_credential_exchange_rejects_null_nonce_without_calling_AuthService()
    {
        var (factory, authClient) = CreateFactory(configureGoogle: true);
        await using (factory)
        using (var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost:7297"),
            HandleCookies = true
        }))
        {
            var config = await client.PostAsync("/quote/v1/auth/google/config", content: null);
            Assert.Equal(HttpStatusCode.OK, config.StatusCode);

            var response = await client.PostAsJsonAsync("/quote/v1/auth/google", new
            {
                credential = "signed-google-id-token",
                nonce = (string?)null
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, authClient.ExchangeRequestCount);
        }
    }

    [Fact]
    public async Task Session_endpoint_returns_not_signed_in_for_anonymous_request()
    {
        var (factory, _) = CreateFactory();
        await using (factory)
        using (var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false }))
        {
            var response = await client.GetAsync("/quote/v1/auth/session");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(
                "\"isSignedIn\":false",
                await response.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Quote_agent_uses_official_GIS_render_button_without_fake_Google_markup()
    {
        var shell = ReadRepoFile(
            "Maliev.QuoteEngine.Client",
            "Components",
            "QuoteAgent",
            "QuoteAgentLaunchShell.razor");
        var script = ReadRepoFile(
            "Maliev.QuoteEngine.Client",
            "wwwroot",
            "js",
            "quote-google-identity.js");

        Assert.Contains("qe-agent-google-identity-host", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("qe-agent-auth-google-mark", shell, StringComparison.Ordinal);
        Assert.Contains("https://accounts.google.com/gsi/client", script, StringComparison.Ordinal);
        Assert.Contains("globalThis.google.accounts.id", script, StringComparison.Ordinal);
        Assert.Contains("identity.initialize", script, StringComparison.Ordinal);
        Assert.Contains("identity.renderButton", script, StringComparison.Ordinal);
        Assert.Contains("nonce:", script, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Maliev.QuoteEngine.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory.FullName, .. parts]));
    }

    private sealed class RecordingAuthServiceClient : IAuthServiceClient
    {
        public const string IssuedNonce = "nonce-value-with-at-least-thirty-two-characters";

        public JsonElement LastNonceRequest { get; private set; }

        public JsonElement LastExchangeRequest { get; private set; }

        public int NonceRequestCount { get; private set; }

        public int ExchangeRequestCount { get; private set; }

        public Task<HttpResponseMessage> LoginAsync(object request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        public Task<HttpResponseMessage> IssueCustomerGoogleNonceAsync(
            object request,
            CancellationToken cancellationToken)
        {
            NonceRequestCount++;
            LastNonceRequest = JsonSerializer.SerializeToElement(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    nonce = IssuedNonce,
                    expires_at_utc = DateTime.UtcNow.AddMinutes(10)
                })
            });
        }

        public Task<HttpResponseMessage> ExchangeCustomerGoogleAsync(
            object request,
            CancellationToken cancellationToken)
        {
            ExchangeRequestCount++;
            LastExchangeRequest = JsonSerializer.SerializeToElement(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    user = new
                    {
                        user_id = "11111111-1111-1111-1111-111111111111",
                        principal_id = "11111111-1111-1111-1111-111111111111",
                        customer_id = "22222222-2222-2222-2222-222222222222",
                        email = "customer@gmail.com",
                        name = "Customer",
                        profile_image_url = "https://lh3.googleusercontent.com/a/customer",
                        email_verified = true
                    }
                })
            });
        }
    }
}
