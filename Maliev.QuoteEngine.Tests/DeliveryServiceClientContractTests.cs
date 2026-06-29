using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Tests;

public sealed class DeliveryServiceClientContractTests
{
    [Fact]
    public async Task GetShippingRatesAsync_PostsDeliveryServiceShippingContract()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new[]
            {
                new
                {
                    courierCode = "flash",
                    courierName = "Flash Express",
                    price = 82.25m,
                    currency = "THB",
                    serviceLevel = "standard",
                    estimatedDelivery = "2026-06-22",
                    courierLogoUrl = "https://cdn.example/flash.svg",
                    packageCount = 2,
                    totalWeight = 1820m,
                    packages = new[]
                    {
                        new
                        {
                            packageNumber = 1,
                            name = "MALIEV box 1",
                            weight = 920m,
                            width = 20m,
                            length = 30m,
                            height = 12m,
                            price = 40m,
                            currency = "THB",
                            estimatedDelivery = "2026-06-22",
                            items = new[]
                            {
                                new { name = "Bracket", quantity = 10, unitWidth = 8m, unitLength = 12m, unitHeight = 4m, unitWeight = 75m }
                            }
                        },
                        new
                        {
                            packageNumber = 2,
                            name = "MALIEV box 2",
                            weight = 900m,
                            width = 20m,
                            length = 30m,
                            height = 12m,
                            price = 42.25m,
                            currency = "THB",
                            estimatedDelivery = "2026-06-22",
                            items = new[]
                            {
                                new { name = "Bracket", quantity = 10, unitWidth = 8m, unitLength = 12m, unitHeight = 4m, unitWeight = 75m }
                            }
                        }
                    },
                    provider = "GoShip"
                }
            })
        };
        var handler = new RecordingHandler(response);
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://delivery.test")
        };
        var client = new DeliveryServiceClient(http);

        var result = await client.GetShippingRatesAsync(new ShippingRateRequestDto
        {
            From = new ShippingAddressDto
            {
                Name = "MALIEV",
                Address = "Factory",
                District = "Pathum Wan",
                State = "Pathum Wan",
                Province = "Bangkok",
                Postcode = "10400",
                Tel = "020000000"
            },
            To = new ShippingAddressDto
            {
                Name = "Customer",
                Address = "Dock 2",
                District = "Bang Rak",
                State = "Bang Rak",
                Province = "Bangkok",
                Postcode = "10500",
                CountryCode = "SG",
                Tel = "0800000000"
            },
            Parcel = new ShippingParcelDto
            {
                Name = "MALIEV order",
                Weight = 1250m,
                Length = 20m,
                Width = 15m,
                Height = 10m
            },
            CourierCodes = ["flash"],
            Parts =
            [
                new ShippingPackagePartDto
                {
                    Name = "Bracket",
                    Quantity = 20,
                    Weight = 75m,
                    Width = 8m,
                    Length = 12m,
                    Height = 4m
                }
            ]
        });

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/delivery/v1/shipping/rates", handler.Request.RequestUri!.PathAndQuery);
        Assert.NotNull(handler.Payload);
        Assert.Equal("10400", handler.Payload.RootElement.GetProperty("from").GetProperty("postcode").GetString());
        Assert.Equal("10500", handler.Payload.RootElement.GetProperty("to").GetProperty("postcode").GetString());
        Assert.Equal("SG", handler.Payload.RootElement.GetProperty("to").GetProperty("countryCode").GetString());
        Assert.Equal(1250m, handler.Payload.RootElement.GetProperty("parcel").GetProperty("weight").GetDecimal());
        Assert.Equal("flash", handler.Payload.RootElement.GetProperty("courierCodes")[0].GetString());
        Assert.Equal("Bracket", handler.Payload.RootElement.GetProperty("parts")[0].GetProperty("name").GetString());
        Assert.Equal(20, handler.Payload.RootElement.GetProperty("parts")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(12m, handler.Payload.RootElement.GetProperty("parts")[0].GetProperty("length").GetDecimal());
        var rate = Assert.Single(result.Rates);
        Assert.Equal("flash", rate.CourierCode);
        Assert.Equal("Flash Express", rate.ProductName);
        Assert.Equal(82.25m, rate.TotalPrice);
        Assert.Equal("https://cdn.example/flash.svg", rate.CourierLogoUrl);
        Assert.Equal(2, rate.PackageCount);
        Assert.Equal(1820m, rate.TotalWeight);
        Assert.Equal(2, rate.Packages.Count);
        Assert.Equal("Bracket", rate.Packages[0].Items[0].Name);
        Assert.Equal("GoShip", rate.Provider);
    }

    [Fact]
    public async Task GetShippingCouriersAsync_TargetsDeliveryServiceCouriersRoute()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new[]
            {
                new
                {
                    courierCode = "thaipost",
                    courierName = "Thailand Post",
                    logoUrl = "https://cdn.example/thailand-post.svg",
                    scope = "domestic",
                    provider = "Shippop"
                }
            })
        };
        var handler = new RecordingHandler(response);
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://delivery.test")
        };
        var client = new DeliveryServiceClient(http);

        var couriers = await client.GetShippingCouriersAsync();

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("/delivery/v1/shipping/couriers", handler.Request.RequestUri!.PathAndQuery);
        var courier = Assert.Single(couriers);
        Assert.Equal("thaipost", courier.CourierCode);
        Assert.Equal("Thailand Post", courier.CourierName);
        Assert.Equal("https://cdn.example/thailand-post.svg", courier.LogoUrl);
        Assert.Equal("Shippop", courier.Provider);
    }

    [Fact]
    public async Task GetShippingTrackingAsync_TargetsDeliveryServiceTrackingRoute()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                trackingCode = "TH-E2E-001",
                courierCode = "thaipost",
                courierName = "Thailand Post",
                status = "in_transit",
                description = "Parcel is in transit",
                provider = "Shippop"
            })
        };
        var handler = new RecordingHandler(response);
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://delivery.test")
        };
        var client = new DeliveryServiceClient(http);

        var tracking = await client.GetShippingTrackingAsync("TH-E2E-001");

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("/delivery/v1/shipping/tracking/TH-E2E-001", handler.Request.RequestUri!.PathAndQuery);
        Assert.NotNull(tracking);
        Assert.Equal("TH-E2E-001", tracking.TrackingCode);
        Assert.Equal("thaipost", tracking.CourierCode);
        Assert.Equal("in_transit", tracking.Status);
        Assert.Equal("Shippop", tracking.Provider);
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public JsonDocument? Payload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (request.Content is not null)
            {
                Payload = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return response;
        }
    }
}
