using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Maliev.QuoteEngine.Tests;

[Collection(QuoteEngineEndpointTestCollection.Name)]
public sealed class AccountAuthBoundaryTests(QuoteEngineWebApplicationFactory factory)
    : IClassFixture<QuoteEngineWebApplicationFactory>
{
    [Theory]
    [InlineData("/quote/v1/account/profile")]
    [InlineData("/quote/v1/account/addresses")]
    [InlineData("/quote/v1/address/google-config")]
    [InlineData("/quote/v1/account/ndas")]
    [InlineData("/quote/v1/account/documents")]
    public async Task Account_routes_return_unauthorized_for_anonymous_users(string path)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
