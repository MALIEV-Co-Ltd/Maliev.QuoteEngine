using System.Net.Http.Json;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>
/// Calculates QuoteEngine part prices through PricingService.
/// </summary>
public interface IQePricingServiceClient
{
    /// <summary>
    /// Calculates a unit and total price for one configured quote part.
    /// </summary>
    Task<PricingCalculationResult?> CalculateAsync(
        QuotePartDraftDto part,
        Guid customerId,
        Guid materialId,
        Guid manufacturingProcessId,
        string leadTimeCode,
        decimal? toleranceAdditionalCostPercent,
        CancellationToken ct = default);

    /// <summary>
    /// Calculates pricing with the canonical MaterialService code while preserving existing client implementations.
    /// </summary>
    Task<PricingCalculationResult?> CalculateAsync(
        QuotePartDraftDto part,
        Guid customerId,
        Guid materialId,
        string materialCode,
        Guid manufacturingProcessId,
        string leadTimeCode,
        decimal? toleranceAdditionalCostPercent,
        CancellationToken ct = default)
    {
        return CalculateAsync(
            part,
            customerId,
            materialId,
            manufacturingProcessId,
            leadTimeCode,
            toleranceAdditionalCostPercent,
            ct);
    }
}

internal sealed class PricingServiceClient(HttpClient http, ILogger<PricingServiceClient> logger) : IQePricingServiceClient
{
    public async Task<PricingCalculationResult?> CalculateAsync(
        QuotePartDraftDto part,
        Guid customerId,
        Guid materialId,
        Guid manufacturingProcessId,
        string leadTimeCode,
        decimal? toleranceAdditionalCostPercent,
        CancellationToken ct = default)
    {
        return await CalculateAsync(
            part,
            customerId,
            materialId,
            part.MaterialId,
            manufacturingProcessId,
            leadTimeCode,
            toleranceAdditionalCostPercent,
            ct);
    }

    public async Task<PricingCalculationResult?> CalculateAsync(
        QuotePartDraftDto part,
        Guid customerId,
        Guid materialId,
        string materialCode,
        Guid manufacturingProcessId,
        string leadTimeCode,
        decimal? toleranceAdditionalCostPercent,
        CancellationToken ct = default)
    {
        try
        {
            var request = new PricingCalculationRequest
            {
                FileId = part.FileId == Guid.Empty ? part.PartId : part.FileId,
                CustomerId = customerId,
                MaterialId = materialId,
                MaterialCode = materialCode,
                ManufacturingProcessId = manufacturingProcessId,
                ManufacturingProcessName = part.ProcessId.ToUpperInvariant(),
                Quantity = part.Quantity,
                Currency = "THB",
                Geometry = new PricingGeometryMetrics
                {
                    VolumeCm3 = part.VolumeCc,
                    SurfaceAreaCm2 = part.SurfaceAreaCm2,
                    IsManifold = part.IsManifold,
                    TriangleCount = 0
                },
                Dfm = BuildDfmMetrics(part),
                StoragePath = part.StoragePath,
                LeadTimeCode = leadTimeCode,
                ToleranceCode = part.ToleranceCode,
                ToleranceAdditionalCostPercent = toleranceAdditionalCostPercent
            };

            using var response = await http.PostAsJsonAsync("/pricing/v1/calculate", request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning(
                    "PricingService returned {Status} for quote part {PartId}: {Body}",
                    response.StatusCode,
                    part.PartId,
                    body);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<PricingCalculationResult>(cancellationToken: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PricingService calculation failed for quote part {PartId}.", part.PartId);
            return null;
        }
    }

    private static PricingDfmMetrics? BuildDfmMetrics(QuotePartDraftDto part)
    {
        if (part.FdmReport is not null)
        {
            return new PricingDfmMetrics
            {
                ReportType = "FDM",
                ThinWallCount = part.FdmReport.ThinWallCount,
                SupportRequired = part.FdmReport.SupportRequired
            };
        }

        if (part.SlaReport is not null)
        {
            return new PricingDfmMetrics
            {
                ReportType = "SLA",
                ThinWallCount = part.SlaReport.ThinWallCount,
                ResinTrappingRisk = part.SlaReport.ResinTrappingRisk,
                SuctionRisk = part.SlaReport.SuctionRisk
            };
        }

        if (part.CncReport is not null)
        {
            return new PricingDfmMetrics
            {
                ReportType = "CNC",
                SharpCornerCount = part.CncReport.SharpCornerCount,
                HasUndercuts = part.CncReport.HasUndercuts,
                RequiresEdm = part.CncReport.RequiresEdm,
                RequiresGrinding = part.CncReport.RequiresGrinding
            };
        }

        return null;
    }

    private sealed record PricingCalculationRequest
    {
        public Guid FileId { get; init; }
        public Guid CustomerId { get; init; }
        public Guid MaterialId { get; init; }
        public string MaterialCode { get; init; } = string.Empty;
        public Guid ManufacturingProcessId { get; init; }
        public string ManufacturingProcessName { get; init; } = string.Empty;
        public decimal Quantity { get; init; }
        public string Currency { get; init; } = "THB";
        public PricingGeometryMetrics Geometry { get; init; } = new();
        public PricingDfmMetrics? Dfm { get; init; }
        public string? StoragePath { get; init; }
        public string? LeadTimeCode { get; init; }
        public string? ToleranceCode { get; init; }
        public decimal? ToleranceAdditionalCostPercent { get; init; }
    }

    private sealed record PricingGeometryMetrics
    {
        public decimal VolumeCm3 { get; init; }
        public decimal SupportVolumeCm3 { get; init; }
        public decimal SurfaceAreaCm2 { get; init; }
        public decimal BoundingBoxX { get; init; }
        public decimal BoundingBoxY { get; init; }
        public decimal BoundingBoxZ { get; init; }
        public bool IsManifold { get; init; }
        public int TriangleCount { get; init; }
    }

    private sealed record PricingDfmMetrics
    {
        public string ReportType { get; init; } = "FDM";
        public int ThinWallCount { get; init; }
        public bool SupportRequired { get; init; }
        public decimal? EstimatedSupportVolumeCm3 { get; init; }
        public bool ResinTrappingRisk { get; init; }
        public bool SuctionRisk { get; init; }
        public int SharpCornerCount { get; init; }
        public bool HasUndercuts { get; init; }
        public bool RequiresEdm { get; init; }
        public bool RequiresGrinding { get; init; }
    }
}

/// <summary>
/// PricingService calculation result used by the QuoteEngine BFF.
/// </summary>
public sealed record PricingCalculationResult
{
    /// <summary>Gets the calculated unit price.</summary>
    public decimal UnitPrice { get; init; }

    /// <summary>Gets the calculated total amount for the requested quantity.</summary>
    public decimal TotalAmount { get; init; }

    /// <summary>Gets the unit price before volume discount.</summary>
    public decimal UnitPriceBeforeVolumeDiscount { get; init; }

    /// <summary>Gets the per-unit volume discount amount.</summary>
    public decimal VolumeDiscountUnitAmount { get; init; }

    /// <summary>Gets the volume discount percentage.</summary>
    public decimal VolumeDiscountPercent { get; init; }

    /// <summary>Gets the pricing confidence score.</summary>
    public decimal ConfidenceScore { get; init; }

    /// <summary>Gets the pricing engine name.</summary>
    public string EngineName { get; init; } = string.Empty;

    /// <summary>Gets the PricingService audit identifier.</summary>
    public Guid AuditId { get; init; }

    /// <summary>Gets the estimated lead time in days.</summary>
    public int EstimatedLeadTimeDays { get; init; }
}
