// Maliev.QuoteEngine.Client/Models/QuotePartViewModel.cs
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Client.Models;

/// <summary>
/// Client-side view model for a single uploaded part in the QuoteWorkspace.
/// Extended in Phase 2 with 3D viewer URLs and typed DFM report fields.
/// </summary>
public sealed class QuotePartViewModel
{
    public Guid PartId { get; set; } = Guid.NewGuid();
    public Guid FileId { get; set; }
    public string UploadId { get; set; } = string.Empty;
    public string? ClientFileId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long FileSizeBytes { get; set; }
    public string ProcessId { get; set; } = "fdm";
    public string MaterialId { get; set; } = "pla-black";
    public string? FinishId { get; set; }
    public string? FinishCode { get; set; }
    public string? ToleranceId { get; set; }
    public string? ToleranceCode { get; set; }
    public string? InspectionLevel { get; set; } = "STANDARD";
    public string? RoughnessCode { get; set; }
    public string? Color { get; set; }
    public Dictionary<string, string> ProcessOptionValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasThreadedHoles { get; set; }
    public string? ThreadSpecification { get; set; }
    public int ThreadedHoleCount { get; set; }
    public string? InsertType { get; set; } = "None";
    public int InsertCount { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal VolumeCc { get; set; }
    public decimal SurfaceAreaCm2 { get; set; }
    public QuoteBoundingBoxDto? BoundingBox { get; set; }
    public bool DfmAcknowledged { get; set; }
    public bool LocalDfmRuntimeUnavailable { get; set; }
    public string? LocalDfmRuntimeUnavailableReason { get; set; }
    public string? LocalDfmRuntimeRunningProcessId { get; set; }
    public DateTimeOffset? LocalDfmRuntimeStartedAtUtc { get; set; }
    public string? PartNotes { get; set; }
    public string Status { get; set; } = "Waiting";
    public IReadOnlyList<DfmFindingDto> Findings { get; set; } = [];

    // Phase 2 fields
    public string? StoragePath { get; set; }
    public string? GlbUrl { get; set; }
    public string? ViewerStoragePath { get; set; }
    public string? ViewerFileExtension { get; set; }
    public string? ThumbnailUrl { get; set; }
    public int BodyCount { get; set; } = 1;
    public bool IsManifold { get; set; } = true;
    public string? NonManifoldReason { get; set; }
    public int? SelectedBodyIndex { get; set; }
    public QeFdmDfmReport? FdmDfmReport { get; set; }
    public QeSlaDfmReport? SlaDfmReport { get; set; }
    public QeCncDfmReport? CncDfmReport { get; set; }
    public IReadOnlyList<string> OverlayGlbUrls { get; set; } = [];
    public decimal? UnitPriceTHB { get; set; }
    public List<QuotePartAttachmentDto> DrawingFiles { get; set; } = [];
    public QuotePartViewerSettingsDto ViewerSettings { get; set; } = new("iso", null, true, false);

    public void ApplyProjectNewProcessSelection(string? processId, QuoteReferenceDataResponse? referenceData)
    {
        if (string.IsNullOrWhiteSpace(processId))
        {
            return;
        }

        ProcessId = processId.Trim();
        ClearProcessDependentConfiguration();
        ClearLocalDfmRuntimeState();

        var material = referenceData?.Materials.FirstOrDefault(item =>
            string.Equals(item.ProcessId, ProcessId, StringComparison.OrdinalIgnoreCase));
        MaterialId = material?.Id ?? string.Empty;

        ApplyProjectNewCatalogDefaults(referenceData);
    }

    public void ApplyProjectNewMaterialSelection(string? materialId, QuoteReferenceDataResponse? referenceData)
    {
        if (string.IsNullOrWhiteSpace(materialId))
        {
            return;
        }

        MaterialId = materialId.Trim();
        FinishId = null;
        FinishCode = null;
        Color = DefaultColorForMaterial(referenceData, MaterialId);
        ProcessOptionValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        UnitPriceTHB = null;

        ApplyProjectNewCatalogDefaults(referenceData);
    }

    private void ClearProcessDependentConfiguration()
    {
        MaterialId = string.Empty;
        FinishId = null;
        FinishCode = null;
        ToleranceId = null;
        ToleranceCode = null;
        InspectionLevel = "STANDARD";
        RoughnessCode = null;
        Color = null;
        ProcessOptionValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        UnitPriceTHB = null;
    }

    private void ClearLocalDfmRuntimeState()
    {
        LocalDfmRuntimeUnavailable = false;
        LocalDfmRuntimeUnavailableReason = null;
        LocalDfmRuntimeRunningProcessId = null;
        LocalDfmRuntimeStartedAtUtc = null;
        DfmAcknowledged = false;
    }

    private void ApplyProjectNewCatalogDefaults(QuoteReferenceDataResponse? referenceData)
    {
        if (referenceData is null)
        {
            return;
        }

        var finish = referenceData.Finishes.FirstOrDefault(item =>
            string.Equals(item.ProcessId, ProcessId, StringComparison.OrdinalIgnoreCase));
        FinishId = finish?.Id;
        FinishCode = finish?.Code;

        var tolerance = SelectDefaultTolerance(referenceData.Tolerances.Where(item =>
            string.Equals(item.ProcessId, ProcessId, StringComparison.OrdinalIgnoreCase)));
        ToleranceId = tolerance?.Id;
        ToleranceCode = tolerance?.Code;

        InspectionLevel = referenceData.InspectionLevels.FirstOrDefault()?.Code ?? "STANDARD";
        RoughnessCode = SelectDefaultRoughness(referenceData.RoughnessOptions.Where(item =>
            string.Equals(item.ProcessId, ProcessId, StringComparison.OrdinalIgnoreCase)))?.Code;
        Color = DefaultColorForMaterial(referenceData, MaterialId);
    }

    private ToleranceOptionDto? SelectDefaultTolerance(IEnumerable<ToleranceOptionDto> tolerances)
    {
        var ordered = tolerances.ToList();
        if (IsCncProcess(ProcessId))
        {
            var medium = ordered.FirstOrDefault(item =>
                $"{item.Id} {item.Code} {item.Name}".Contains("ISO2768_M", StringComparison.OrdinalIgnoreCase)
                || $"{item.Id} {item.Code} {item.Name}".Contains("ISO_2768_M", StringComparison.OrdinalIgnoreCase)
                || $"{item.Id} {item.Code} {item.Name}".Contains("ISO 2768-m", StringComparison.OrdinalIgnoreCase)
                || $"{item.Id} {item.Code} {item.Name}".Contains("ISO 2768 m", StringComparison.OrdinalIgnoreCase));
            if (medium is not null)
            {
                return medium;
            }
        }

        return ordered.FirstOrDefault();
    }

    private RoughnessOptionDto? SelectDefaultRoughness(IEnumerable<RoughnessOptionDto> roughnessOptions)
    {
        var ordered = roughnessOptions.ToList();
        if (IsCncProcess(ProcessId))
        {
            var standard = ordered.FirstOrDefault(item =>
                string.Equals(item.Code, "RA_3_2", StringComparison.OrdinalIgnoreCase));
            if (standard is not null)
            {
                return standard;
            }
        }

        return ordered.FirstOrDefault();
    }

    private static string? DefaultColorForMaterial(QuoteReferenceDataResponse? referenceData, string? materialId) =>
        referenceData?.Colors.FirstOrDefault(item =>
            item.MaterialIds.Count == 0
            || (!string.IsNullOrWhiteSpace(materialId) && item.MaterialIds.Contains(materialId, StringComparer.OrdinalIgnoreCase)))?.Code;

    private static bool IsCncProcess(string? processId) =>
        string.Equals(processId, "cnc", StringComparison.OrdinalIgnoreCase)
        || string.Equals(processId, "cnc_mill", StringComparison.OrdinalIgnoreCase)
        || string.Equals(processId, "cnc-turn", StringComparison.OrdinalIgnoreCase)
        || string.Equals(processId, "cnc_turn", StringComparison.OrdinalIgnoreCase)
        || string.Equals(processId, "CNC", StringComparison.OrdinalIgnoreCase)
        || string.Equals(processId, "CNC_MILL", StringComparison.OrdinalIgnoreCase)
        || string.Equals(processId, "CNC_TURN", StringComparison.OrdinalIgnoreCase);

    public QuotePartDraftDto ToDraft() => new()
    {
        PartId = PartId,
        FileId = FileId,
        UploadId = UploadId,
        FileName = FileName,
        ProcessId = ProcessId,
        MaterialId = MaterialId,
        FinishId = FinishId,
        FinishCode = FinishCode,
        ToleranceId = ToleranceId,
        ToleranceCode = ToleranceCode,
        InspectionLevel = InspectionLevel,
        RoughnessCode = RoughnessCode,
        Color = Color,
        ProcessOptionValues = new(ProcessOptionValues, StringComparer.OrdinalIgnoreCase),
        HasThreadedHoles = HasThreadedHoles,
        ThreadSpecification = ThreadSpecification,
        ThreadedHoleCount = ThreadedHoleCount,
        InsertType = InsertType,
        InsertCount = InsertCount,
        Quantity = Quantity,
        VolumeCc = Math.Max(VolumeCc, 1m),
        SurfaceAreaCm2 = SurfaceAreaCm2,
        BoundingBoxMm = BoundingBox is null
            ? null
            : new QuotePartBoundingBoxDto(BoundingBox.X, BoundingBox.Y, BoundingBox.Z),
        StoragePath = StoragePath,
        Status = Status,
        ViewerGlbUrl = GlbUrl,
        ViewerStoragePath = ViewerStoragePath,
        ViewerFileExtension = ViewerFileExtension,
        ThumbnailUrl = ThumbnailUrl,
        Findings = Findings,
        IsManifold = IsManifold,
        NonManifoldReason = NonManifoldReason,
        FdmReport = FdmDfmReport,
        SlaReport = SlaDfmReport,
        CncReport = CncDfmReport,
        OverlayGlbUrls = OverlayGlbUrls,
        DfmAcknowledged = DfmAcknowledged,
        PartNotes = PartNotes,
        BodyCount = BodyCount,
        SelectedBodyIndex = SelectedBodyIndex,
        DrawingFiles = [.. DrawingFiles],
        ViewerSettings = ViewerSettings
    };
}
