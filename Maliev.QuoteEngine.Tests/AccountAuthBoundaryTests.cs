using System.Net;
using System.Security.Claims;
using Maliev.QuoteEngine.Bff.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using NSubstitute;

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

    [Fact]
    public void Customer_session_resolver_rejects_unauthenticated_customer_claims()
    {
        var customerId = Guid.Parse("81b29779-d78c-4d5b-bef2-21f315dafa70");
        var identity = new ClaimsIdentity([new Claim("customer_id", customerId.ToString("D"))]);
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            }
        };
        var environment = Substitute.For<IHostEnvironment>();
        var resolver = new CustomerSessionResolver(accessor, new QuoteEnginePrototypeStore(), environment);

        var resolved = resolver.TryResolveCustomerId(out var resolvedCustomerId);

        Assert.False(resolved);
        Assert.Equal(Guid.Empty, resolvedCustomerId);
    }
}
