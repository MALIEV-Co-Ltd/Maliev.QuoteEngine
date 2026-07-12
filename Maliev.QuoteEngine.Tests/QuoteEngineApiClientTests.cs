using System.Net;
using Maliev.QuoteEngine.Shared.Quotes;
using Maliev.QuoteEngine.Client.Services;
using Xunit;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteEngineApiClientTests
{
    [Fact]
    public async Task BootstrapAgentSessionAsync_Uses_the_bounded_session_claim_endpoint()
    {
        var handler = new RecordingResponseHandler(HttpStatusCode.NoContent);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new QuoteEngineApiClient(httpClient);
        var sessionId = Guid.NewGuid();

        await client.BootstrapAgentSessionAsync(sessionId);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal($"/quote/v1/agent/sessions/{sessionId:D}/bootstrap", handler.Path);
    }

    [Fact]
    public async Task GetReferenceDataAsync_WhenApiReturnsHtml_FallsBackToEmptyReferenceData()
    {
        var client = CreateClient("<!doctype html><html><body>Client shell</body></html>");

        var referenceData = await client.GetReferenceDataAsync();

        Assert.Empty(referenceData.Processes);
        Assert.Empty(referenceData.Materials);
    }

    [Fact]
    public async Task GetCurrenciesAsync_WhenApiReturnsHtml_FallsBackToEmptyList()
    {
        var client = CreateClient("<!doctype html><html><body>Client shell</body></html>");

        var currencies = await client.GetCurrenciesAsync();

        Assert.Empty(currencies);
    }

    [Fact]
    public async Task GetAuthStatusAsync_WhenApiReturnsHtml_FallsBackToGuest()
    {
        var client = CreateClient("<!doctype html><html><body>Client shell</body></html>");

        var authStatus = await client.GetAuthStatusAsync();

        Assert.False(authStatus.IsSignedIn);
        Assert.Null(authStatus.DisplayName);
    }

    [Fact]
    public async Task GetDemoProjectAsync_WhenApiReturnsHtml_FallsBackToNull()
    {
        var client = CreateClient("<!doctype html><html><body>Client shell</body></html>");

        var demoProject = await client.GetDemoProjectAsync();

        Assert.Null(demoProject);
    }

    [Fact]
    public async Task InitiatePaymentAsync_WhenApiReturnsProblemDetails_IncludesServerDetailInExceptionMessage()
    {
        var client = CreateClient(
            """
            {
              "title": "Billing and shipping addresses are required before checkout.",
              "detail": "Select a billing address and a shipping address before starting payment.",
              "status": 400
            }
            """,
            HttpStatusCode.BadRequest,
            "application/problem+json");

        var exception = await Assert.ThrowsAsync<QuoteEngineApiException>(() => client.InitiatePaymentAsync(new InitiatePaymentRequest
        {
            OrderId = Guid.NewGuid(),
            OrderNumber = "ORD-2026-0001",
            Amount = 1500m,
            Currency = "THB",
            AcceptedTerms = true
        }));

        Assert.Contains("Select a billing address and a shipping address before starting payment.", exception.Message, StringComparison.Ordinal);
        Assert.Equal("Select a billing address and a shipping address before starting payment.", exception.UserMessage);
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
    }

    private static QuoteEngineApiClient CreateClient(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string contentType = "text/html")
    {
        var httpClient = new HttpClient(new StaticResponseHandler(responseBody, statusCode, contentType))
        {
            BaseAddress = new Uri("http://localhost/")
        };

        return new QuoteEngineApiClient(httpClient);
    }

    private sealed class StaticResponseHandler(
        string responseBody,
        HttpStatusCode statusCode,
        string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody)
            };

            response.Content.Headers.ContentType = new(contentType);
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingResponseHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }
}
