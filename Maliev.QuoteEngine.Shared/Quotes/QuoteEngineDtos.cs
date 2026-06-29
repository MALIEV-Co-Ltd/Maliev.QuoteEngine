using System.ComponentModel.DataAnnotations;

namespace Maliev.QuoteEngine.Shared.Quotes;

public sealed record ProcessOptionDto(string Id, string Name, string Description, IReadOnlyList<string> SupportedFileTypes);

public sealed record MaterialOptionDto(string Id, string ProcessId, string Name, string Grade, decimal DensityGPerCc, string Finish);

public sealed record FinishOptionDto(
    string Id,
    string ProcessId,
    string Code,
    string Name,
    string Description,
    decimal PriceMultiplier);

public sealed record ToleranceOptionDto(
    string Id,
    string ProcessId,
    string Code,
    string Name,
    string Description,
    decimal PriceMultiplier);

public sealed record InspectionOptionDto(string Code, string Name, string Description, decimal PriceMultiplier);

public sealed record RoughnessOptionDto(string Code, string ProcessId, string Name, string Description, decimal PriceMultiplier);

public sealed record ColorOptionDto(string Code, string Name, string CssValue, IReadOnlyList<string> MaterialIds);

public sealed record LeadTimeOptionDto(string Code, string Name, int BusinessDays, decimal PriceMultiplier);

public sealed record ProcessConfigOptionDto(
    string ConfigKey,
    string ProcessId,
    string Label,
    string ConfigType,
    string DefaultValue,
    string HelpText,
    IReadOnlyList<string> Choices);

public sealed record QuoteReferenceDataResponse(
    IReadOnlyList<ProcessOptionDto> Processes,
    IReadOnlyList<MaterialOptionDto> Materials,
    IReadOnlyList<FinishOptionDto> Finishes,
    IReadOnlyList<ToleranceOptionDto> Tolerances,
    IReadOnlyList<InspectionOptionDto> InspectionLevels,
    IReadOnlyList<RoughnessOptionDto> RoughnessOptions,
    IReadOnlyList<ColorOptionDto> Colors,
    IReadOnlyList<LeadTimeOptionDto> LeadTimes,
    IReadOnlyList<ProcessConfigOptionDto> ProcessOptions,
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

    [Range(1, QuoteUploadConstraints.MaxFileSizeBytes)]
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

public sealed class QuoteUploadHandoffRequest
{
    /// <summary>Gets or sets the signed Web-to-QuoteEngine handoff token.</summary>
    [MaxLength(20000)]
    public string? HandoffToken { get; set; }

    /// <summary>Gets or sets the Web quote session id from a verified handoff token.</summary>
    [MaxLength(80)]
    public string QuoteSessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the uploaded files from a verified handoff token.</summary>
    public List<QuoteUploadHandoffFileDto> Files { get; set; } = [];
}

public sealed class QuoteUploadHandoffFileDto
{
    [Required]
    [MaxLength(120)]
    public string UploadId { get; set; } = string.Empty;

    public Guid? FileId { get; set; }

    [Required]
    [MaxLength(260)]
    public string FileName { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string StoragePath { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string ContentType { get; set; } = "application/octet-stream";

    [Range(1, 10_737_418_240L)]
    public long FileSizeBytes { get; set; }

    [MaxLength(40)]
    public string Status { get; set; } = "Completed";
}

public sealed record QuoteUploadHandoffPartDto(
    Guid PartId,
    Guid FileId,
    string UploadId,
    string FileName,
    string StoragePath,
    string Status,
    decimal VolumeCc,
    decimal SurfaceAreaCm2,
    IReadOnlyList<DfmFindingDto> Findings,
    string? ViewerGlbUrl = null,
    string? ViewerStoragePath = null,
    string? ViewerFileExtension = null,
    string? ThumbnailUrl = null,
    string ContentType = "application/octet-stream",
    long FileSizeBytes = 0);

public sealed record QuoteUploadHandoffResponse(string QuoteSessionId, IReadOnlyList<QuoteUploadHandoffPartDto> Parts);

public sealed record DfmFindingDto(string Severity, string Code, string Message);

public sealed record QuotePartBoundingBoxDto(decimal X, decimal Y, decimal Z);

public sealed record QuotePartAttachmentDto(
    string FileName,
    string StoragePath,
    string ContentType,
    long FileSizeBytes,
    string Kind);

public sealed record QuotePartViewerSettingsDto(
    string CameraPreset,
    bool EdgesEnabled,
    bool GridEnabled,
    bool DfmOverlayEnabled)
{
    public string RenderMode { get; init; } = "realistic";

    public string InitialRenderMode { get; init; } = "realistic";

    public string TargetRenderMode { get; init; } = "realistic";

    public RenderModeTransitionSettings RenderModeTransition { get; init; } = new();

    public string CameraProjection { get; init; } = "orthographic";

    public bool CuttingMatEnabled { get; init; } = false;
}

public sealed class RenderModeTransitionSettings
{
    public bool Enabled { get; init; } = true;

    public string Trigger { get; init; } = "runtime_complete";

    public int FallbackDelayMs { get; init; } = 1200;

    public int TransitionMs { get; init; } = 250;
}

public sealed class QuoteAnalysisStatusResponse
{
    public string UploadId { get; set; } = string.Empty;

    public string StoragePath { get; set; } = string.Empty;

    public string Status { get; set; } = "WaitingForUpload";

    public decimal VolumeCc { get; set; }

    public decimal SurfaceAreaCm2 { get; set; }

    public string? ViewerGlbUrl { get; set; }

    public string? ViewerStoragePath { get; set; }

    public string? ViewerFileExtension { get; set; }

    public string? ThumbnailUrl { get; set; }

    public IReadOnlyList<DfmFindingDto> Findings { get; set; } = [];

    public bool IsManifold { get; set; } = true;

    public int BodyCount { get; set; } = 1;

    public string? NonManifoldReason { get; set; }

    public string? AnalysisErrorCode { get; set; }

    public QeFdmDfmReport? FdmReport { get; set; }

    public QeSlaDfmReport? SlaReport { get; set; }

    public QeCncDfmReport? CncReport { get; set; }

    public IReadOnlyList<string> OverlayGlbUrls { get; set; } = [];
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

    [MaxLength(120)]
    public string? FinishId { get; set; }

    [MaxLength(80)]
    public string? FinishCode { get; set; }

    [MaxLength(120)]
    public string? ToleranceId { get; set; }

    [MaxLength(80)]
    public string? ToleranceCode { get; set; }

    [MaxLength(80)]
    public string? InspectionLevel { get; set; } = "STANDARD";

    [MaxLength(80)]
    public string? RoughnessCode { get; set; }

    [MaxLength(80)]
    public string? Color { get; set; }

    public Dictionary<string, string> ProcessOptionValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasThreadedHoles { get; set; }

    [MaxLength(80)]
    public string? ThreadSpecification { get; set; }

    [Range(0, 10_000)]
    public int ThreadedHoleCount { get; set; }

    [MaxLength(80)]
    public string? InsertType { get; set; }

    [Range(0, 10_000)]
    public int InsertCount { get; set; }

    [Range(1, 100_000)]
    public int Quantity { get; set; } = 1;

    [Range(0.01, 1_000_000)]
    public decimal VolumeCc { get; set; }

    [Range(0, 1_000_000)]
    public decimal SurfaceAreaCm2 { get; set; }

    public QuotePartBoundingBoxDto? BoundingBoxMm { get; set; }

    public string? StoragePath { get; set; }

    [MaxLength(80)]
    public string Status { get; set; } = "Waiting";

    public string? ViewerGlbUrl { get; set; }

    public string? ViewerStoragePath { get; set; }

    [MaxLength(16)]
    public string? ViewerFileExtension { get; set; }

    public string? ThumbnailUrl { get; set; }

    public IReadOnlyList<DfmFindingDto> Findings { get; set; } = [];

    public bool IsManifold { get; set; } = true;

    [MaxLength(2_000)]
    public string? NonManifoldReason { get; set; }

    public QeFdmDfmReport? FdmReport { get; set; }

    public QeSlaDfmReport? SlaReport { get; set; }

    public QeCncDfmReport? CncReport { get; set; }

    public IReadOnlyList<string> OverlayGlbUrls { get; set; } = [];

    public bool DfmAcknowledged { get; set; }

    [MaxLength(2_000)]
    public string? PartNotes { get; set; }

    [Range(0, 10_000)]
    public int? BodyCount { get; set; }

    [Range(0, 10_000)]
    public int? SelectedBodyIndex { get; set; }

    public List<QuotePartAttachmentDto> DrawingFiles { get; set; } = [];

    public QuotePartViewerSettingsDto ViewerSettings { get; set; } = new("iso", true, true, false);
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

public sealed record CreateDraftProjectRequest(
    string QuoteSessionId,
    IReadOnlyList<QuotePartDraftDto> Parts,
    string Notes,
    string Title = "Untitled quote");

public sealed record CreateDraftProjectResponse(
    Guid ProjectId,
    string ProjectNumber,
    string Status,
    string Title = "Untitled quote",
    IReadOnlyList<QuotePartDraftDto>? Parts = null,
    Guid? ProjectServiceProjectId = null,
    string? ProjectServiceProjectNumber = null);

public sealed record CustomerProjectDetailResponse(
    Guid ProjectId,
    string ProjectNumber,
    string Status,
    string Title,
    bool IsPinned,
    bool IsArchived,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<QuotePartDraftDto> Parts);

public sealed record DuplicateDraftProjectRequest(string? Title);

public sealed record DuplicateDraftProjectResponse(
    Guid ProjectId,
    string ProjectNumber,
    string Status,
    string Title,
    IReadOnlyList<QuotePartDraftDto> Parts);

public sealed record ProjectManagementResponse(
    Guid ProjectId,
    string ProjectNumber,
    string Status,
    string Title,
    bool IsPinned,
    bool IsArchived);

public sealed record CustomerProjectNavItemDto(
    Guid ProjectId,
    string ProjectNumber,
    string Title,
    string Status,
    bool IsPinned,
    bool IsArchived,
    DateTimeOffset UpdatedAt);

public sealed record GenerateFormalQuoteRequest(Guid ProjectId, string QuoteSessionId, IReadOnlyList<QuotePartDraftDto> Parts, string Notes);

public sealed record GenerateFormalQuoteResponse(Guid QuoteId, string QuoteNumber, string PdfUrl, string Status)
{
    /// <summary>Gets the immutable quotation version identifier represented by the generated PDF.</summary>
    public Guid? QuoteVersionId { get; init; }

    /// <summary>Gets the immutable quotation version number represented by the generated PDF.</summary>
    public int? QuoteVersionNumber { get; init; }

    /// <summary>Gets the durable PDF artifact storage path for the generated quotation version.</summary>
    public string? PdfArtifactStoragePath { get; init; }
}

public sealed record ApproveQuoteRequest(Guid QuoteId);

public sealed record CreateManufacturingOrderRequest(Guid QuoteId, string CustomerPoNumber, string Notes, Guid? ProjectServiceProjectId = null)
{
    /// <summary>Gets the immutable quotation version identifier the customer is accepting.</summary>
    public Guid? QuoteVersionId { get; init; }

    /// <summary>Gets the immutable quotation version number the customer is accepting.</summary>
    public int? QuoteVersionNumber { get; init; }

    public IReadOnlyList<QuotePartDraftDto> Parts { get; init; } = [];
}

public sealed record CreateManufacturingOrderResponse(Guid OrderId, string OrderNumber, string Status);

// ── Payment ───────────────────────────────────────────────────────────────────

public sealed class InitiatePaymentRequest
{
    [Required]
    public Guid OrderId { get; set; }

    [Required]
    [MaxLength(80)]
    public string OrderNumber { get; set; } = string.Empty;

    [Range(0.01, 999_999_999.99)]
    public decimal Amount { get; set; }

    [Required]
    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; set; } = "THB";

    public Guid? BillingAddressId { get; set; }

    public Guid? ShippingAddressId { get; set; }

    [MaxLength(200)]
    public string? BillingCompanyName { get; set; }

    [MaxLength(50)]
    public string? BillingVatNumber { get; set; }

    public bool AcceptedTerms { get; set; }

    public Guid CheckoutAttemptId { get; set; } = Guid.NewGuid();
}

public sealed record InitiatePaymentResponse(
    Guid TransactionId,
    string PaymentUrl,
    string Status);
