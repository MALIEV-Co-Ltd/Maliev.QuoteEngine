using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Clients;

namespace Maliev.QuoteEngine.Tests;

public sealed class RegistryServiceClientContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SearchThaiAddressHierarchyAsync_UsesMultiFieldQueryAndMapsBilingualResponse()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                success = true,
                data = new[]
                {
                    new
                    {
                        id = Guid.Parse("cbaf2da7-5ff8-40f4-9089-147e07f6f920"),
                        postalCode = "21150",
                        subDistrictTh = "มาบตาพุด",
                        districtTh = "เมืองระยอง",
                        provinceTh = "ระยอง",
                        subDistrictEn = "Map Ta Phut",
                        districtEn = "Mueang Rayong",
                        provinceEn = "Rayong"
                    }
                }
            }, options: JsonOptions)
        };
        var handler = new RecordingHandler(response);
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://registry.test")
        };
        var client = new RegistryServiceClient(http);

        var result = await client.SearchThaiAddressHierarchyAsync(
            "21150",
            "Map Ta Phut",
            "Mueang Rayong",
            "Rayong",
            6,
            CancellationToken.None);

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal(
            "/registry/v1/thai/addresses/autocomplete-multi?postalCode=21150&district=Map%20Ta%20Phut&city=Mueang%20Rayong&province=Rayong&limit=6",
            handler.Request.RequestUri!.PathAndQuery);

        Assert.True(result.IsAvailable);
        var location = Assert.Single(result.Locations);
        Assert.Equal("21150", location.PostalCode);
        Assert.Equal("มาบตาพุด", location.SubDistrictTh);
        Assert.Equal("เมืองระยอง", location.DistrictTh);
        Assert.Equal("ระยอง", location.ProvinceTh);
        Assert.Equal("Map Ta Phut", location.SubDistrictEn);
        Assert.Equal("Mueang Rayong", location.DistrictEn);
        Assert.Equal("Rayong", location.ProvinceEn);
    }

    [Fact]
    public async Task SearchThaiAddressHierarchyAsync_DependencyFailureReturnsUnavailableNotNoMatch()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var handler = new RecordingHandler(response);
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://registry.test")
        };
        var client = new RegistryServiceClient(http);

        var result = await client.SearchThaiAddressHierarchyAsync(
            "21150",
            "Map Ta Phut",
            "Mueang Rayong",
            "Rayong",
            6,
            CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Empty(result.Locations);
    }

    [Fact]
    public async Task SearchThaiAddressHierarchyAsync_DependencyTimeoutReturnsUnavailable()
    {
        var handler = new ThrowingHandler(new TaskCanceledException("Registry request timed out."));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://registry.test")
        };
        var client = new RegistryServiceClient(http);

        var result = await client.SearchThaiAddressHierarchyAsync(
            "21150",
            "Map Ta Phut",
            "Mueang Rayong",
            "Rayong",
            6,
            CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Empty(result.Locations);
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
