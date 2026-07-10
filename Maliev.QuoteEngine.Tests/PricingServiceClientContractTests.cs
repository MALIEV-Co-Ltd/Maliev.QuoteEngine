using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

public sealed class PricingServiceClientContractTests
{
    [Fact]
    public async Task CalculateAsync_SendsPricingServiceWireContractAndMapsResponse()
    {
        var auditId = Guid.Parse("2bb3e5db-3612-4d2f-80fb-e4ceca16be8b");
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                unitPrice = 1234.56m,
                totalAmount = 2469.12m,
                unitPriceBeforeVolumeDiscount = 1300m,
                volumeDiscountUnitAmount = 65.44m,
                volumeDiscountPercent = 5m,
                confidenceScore = 0.91m,
                engineName = "RuleBased-CNC",
                auditId,
                estimatedLeadTimeDays = 6
            })
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://pricing-service.test")
        };
        var client = new PricingServiceClient(http, NullLogger<PricingServiceClient>.Instance);
        var fileId = Guid.Parse("a99a5177-1c1f-4d9d-ad31-7e786d66d338");
        var customerId = Guid.Parse("9491011a-6731-46bd-8e90-0f452ad3b838");
        var materialId = Guid.Parse("98174ff2-0fa0-45fb-a74e-3d871c955556");
        var processId = Guid.Parse("8ca73525-f9cb-49d7-aa0b-ae3576116b56");

        var result = await client.CalculateAsync(
            new QuotePartDraftDto
            {
                FileId = fileId,
                PartId = Guid.NewGuid(),
                ProcessId = "cnc",
                MaterialId = "legacy-aluminum",
                Quantity = 2,
                VolumeCc = 12.5m,
                SurfaceAreaCm2 = 88.2m,
                IsManifold = true,
                StoragePath = "customers/c/quotes/q/part.step",
                ToleranceCode = "ISO2768_M"
            },
            customerId,
            materialId,
            "AL6061",
            processId,
            "STANDARD",
            8m);

        Assert.NotNull(result);
        Assert.Equal(1234.56m, result.UnitPrice);
        Assert.Equal(2469.12m, result.TotalAmount);
        Assert.Equal(auditId, result.AuditId);
        Assert.Equal(6, result.EstimatedLeadTimeDays);

        Assert.Equal(HttpMethod.Post, handler.Request?.Method);
        Assert.Equal("/pricing/v1/calculate", handler.Request?.RequestUri?.AbsolutePath);
        var body = await handler.ReadJsonBodyAsync();
        Assert.Equal(fileId, body.GetProperty("fileId").GetGuid());
        Assert.Equal(customerId, body.GetProperty("customerId").GetGuid());
        Assert.Equal(materialId, body.GetProperty("materialId").GetGuid());
        Assert.Equal("AL6061", body.GetProperty("materialCode").GetString());
        Assert.Equal(processId, body.GetProperty("manufacturingProcessId").GetGuid());
        Assert.Equal("CNC", body.GetProperty("manufacturingProcessName").GetString());
        Assert.Equal(2m, body.GetProperty("quantity").GetDecimal());
        Assert.Equal("THB", body.GetProperty("currency").GetString());
        Assert.Equal("STANDARD", body.GetProperty("leadTimeCode").GetString());
        Assert.Equal("ISO2768_M", body.GetProperty("toleranceCode").GetString());
        Assert.Equal(8m, body.GetProperty("toleranceAdditionalCostPercent").GetDecimal());
        Assert.Equal(12.5m, body.GetProperty("geometry").GetProperty("volumeCm3").GetDecimal());
        Assert.Equal(88.2m, body.GetProperty("geometry").GetProperty("surfaceAreaCm2").GetDecimal());
        Assert.True(body.GetProperty("geometry").GetProperty("isManifold").GetBoolean());
        Assert.Equal("customers/c/quotes/q/part.step", body.GetProperty("storagePath").GetString());
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        private string? _body;

        public HttpRequestMessage? Request { get; private set; }

        public async Task<JsonElement> ReadJsonBodyAsync()
        {
            Assert.False(string.IsNullOrWhiteSpace(_body));
            return JsonDocument.Parse(_body).RootElement.Clone();
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            _body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
