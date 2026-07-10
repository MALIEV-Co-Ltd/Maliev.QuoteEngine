using System.Net;
using System.Net.Http.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

public sealed class MaterialCatalogClientContractTests
{
    [Theory]
    [InlineData("fdm", "pla-black", "PLA", "11111111-1111-1111-1111-111111111111")]
    [InlineData("fdm", "petg-clear", "PETG", "22222222-2222-2222-2222-222222222222")]
    [InlineData("sla", "resin-gray", "STANDARD_RESIN", "33333333-3333-3333-3333-333333333333")]
    public async Task ResolveMaterialAsync_MapsLegacyAliasToAuthoritativeIdAndCanonicalCode(
        string processCode,
        string legacyCode,
        string canonicalCode,
        string expectedIdText)
    {
        var expectedId = Guid.Parse(expectedIdText);
        using var handler = new CatalogHandler(expectedId, canonicalCode);
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://material-service.test")
        };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var client = new MaterialCatalogClient(
            http,
            cache,
            NullLogger<MaterialCatalogClient>.Instance);

        var resolution = await client.ResolveMaterialAsync(processCode, legacyCode);
        var legacyGuidResult = await client.ResolveMaterialIdAsync(processCode, legacyCode);

        Assert.Equal(expectedId, resolution.Id);
        Assert.Equal(canonicalCode, resolution.Code);
        Assert.Equal(expectedId, legacyGuidResult);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(
            $"/material/v1/manufacturing/processes/{processCode.ToUpperInvariant()}/materials",
            handler.LastRequestPath);
    }

    private sealed class CatalogHandler(Guid materialId, string materialCode) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public string? LastRequestPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequestPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[]
                {
                    new
                    {
                        id = materialId,
                        code = materialCode,
                        name = materialCode
                    }
                })
            });
        }
    }
}
