using System.ComponentModel.DataAnnotations;

namespace Maliev.QuoteEngine.Shared.Quotes;

public sealed record ProcessOptionDto(string Id, string Name, string Description, IReadOnlyList<string> SupportedFileTypes);

public sealed record MaterialOptionDto(string Id, string ProcessId, string Name, string Grade, decimal DensityGPerCc, string Finish);

public sealed record LeadTimeOptionDto(string Code, string Name, int BusinessDays, decimal PriceMultiplier);

public sealed record QuoteReferenceDataResponse(
    IReadOnlyList<ProcessOptionDto> Processes,
    IReadOnlyList<MaterialOptionDto> Materials,
    IReadOnlyList<LeadTimeOptionDto> LeadTimes,
    IReadOnlyList<string> SupportedExtensions);

public sealed record QuoteEngineDemoProjectResponse(
    string DemoSessionId,
    string Title,
    IReadOnlyList<QuotePartDraftDto> Parts,
    string ViewerUrl,
    string ThumbnailUrl,
    string Notice);

public sealed record QuoteAuthStatusResponse(bool IsSignedIn, Guid? CustomerId, string? DisplayName);

public sealed class InitiateQuoteUploadRequest
{
    [Required]
    [MaxLength(260)]
    public string FileName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string ContentType { get; set; } = "application/octet-stream";

    [Range(1, 10_737_418_240L)]
    public long FileSizeBytes { get; set; }

    [Required]
    [MaxLength(80)]
    public string QuoteSessionId { get; set; } = string.Empty;
}

public sealed record InitiateQuoteUploadResponse(
    string UploadId,
    string ProxyUploadUrl,
    string StoragePath,
    long ExpectedSizeBytes);

public sealed record CompleteQuoteUploadResponse(
    string UploadId,
    Guid FileId,
    string FileName,
    string StoragePath,
    string Status);

public sealed record DfmFindingDto(string Severity, string Code, string Message);

public sealed class QuoteAnalysisStatusResponse
{
    public string UploadId { get; set; } = string.Empty;

    public string StoragePath { get; set; } = string.Empty;

    public string Status { get; set; } = "WaitingForUpload";

    public decimal VolumeCc { get; set; }

    public decimal SurfaceAreaCm2 { get; set; }

    public string? ViewerGlbUrl { get; set; }

    public string? ThumbnailUrl { get; set; }

    public IReadOnlyList<DfmFindingDto> Findings { get; set; } = [];
}

public sealed class QuotePartDraftDto
{
    [Required]
    public Guid PartId { get; set; } = Guid.NewGuid();

    [Required]
    public Guid FileId { get; set; }

    [Required]
    public string UploadId { get; set; } = string.Empty;

    [Required]
    public string FileName { get; set; } = string.Empty;

    [Required]
    public string ProcessId { get; set; } = "fdm";

    [Required]
    public string MaterialId { get; set; } = "pla-black";

    [Range(1, 100_000)]
    public int Quantity { get; set; } = 1;

    [Range(0.01, 1_000_000)]
    public decimal VolumeCc { get; set; }

    [Range(0, 1_000_000)]
    public decimal SurfaceAreaCm2 { get; set; }

    public bool DfmAcknowledged { get; set; }
}

public sealed class QuoteEstimateRequest
{
    [Required]
    public string QuoteSessionId { get; set; } = string.Empty;

    [Required]
    public string LeadTimeCode { get; set; } = "STANDARD";

    [MinLength(1)]
    public List<QuotePartDraftDto> Parts { get; set; } = [];
}

public sealed record QuoteLineEstimateDto(
    Guid PartId,
    string FileName,
    decimal UnitPrice,
    decimal LineTotal,
    string Currency,
    string Notes);

public sealed record QuoteEstimateResponse(
    string QuoteSessionId,
    decimal Subtotal,
    decimal Discount,
    decimal Total,
    string Currency,
    bool RequiresSignIn,
    IReadOnlyList<QuoteLineEstimateDto> Lines);

public sealed record CreateDraftProjectRequest(string QuoteSessionId, IReadOnlyList<QuotePartDraftDto> Parts, string Notes);

public sealed record CreateDraftProjectResponse(Guid ProjectId, string ProjectNumber, string Status);

public sealed record GenerateFormalQuoteRequest(Guid ProjectId, string QuoteSessionId, IReadOnlyList<QuotePartDraftDto> Parts, string Notes);

public sealed record GenerateFormalQuoteResponse(Guid QuoteId, string QuoteNumber, string PdfUrl, string Status);

public sealed record ApproveQuoteRequest(Guid QuoteId);

public sealed record CreateManufacturingOrderRequest(Guid QuoteId, string CustomerPoNumber, string Notes);

public sealed record CreateManufacturingOrderResponse(Guid OrderId, string OrderNumber, string Status);
