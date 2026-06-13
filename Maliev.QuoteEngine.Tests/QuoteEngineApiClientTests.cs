using System.Net;
using Maliev.QuoteEngine.Client.Services;
using Xunit;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteEngineApiClientTests
{
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

    private static QuoteEngineApiClient CreateClient(string responseBody)
    {
        var httpClient = new HttpClient(new StaticResponseHandler(responseBody))
        {
            BaseAddress = new Uri("http://localhost/")
        };

        return new QuoteEngineApiClient(httpClient);
    }

    private sealed class StaticResponseHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };

            response.Content.Headers.ContentType = new("text/html");
            return Task.FromResult(response);
        }
    }
}
