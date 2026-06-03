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
    public string ProcessId { get; set; } = "fdm";
    public string MaterialId { get; set; } = "pla-black";
    public string? FinishId { get; set; }
    public string? FinishCode { get; set; }
    public string? ToleranceId { get; set; }
    public string? ToleranceCode { get; set; }
    public string? InspectionLevel { get; set; } = "STANDARD";
    public string? RoughnessCode { get; set; }
    public string? Color { get; set; }
    public bool HasThreadedHoles { get; set; }
    public string? ThreadSpecification { get; set; }
    public int ThreadedHoleCount { get; set; }
    public string? InsertType { get; set; } = "None";
    public int InsertCount { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal VolumeCc { get; set; }
    public decimal SurfaceAreaCm2 { get; set; }
    public bool DfmAcknowledged { get; set; }
    public bool LocalDfmRuntimeUnavailable { get; set; }
    public string? LocalDfmRuntimeUnavailableReason { get; set; }
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
    public QuotePartViewerSettingsDto ViewerSettings { get; set; } = new("iso", true, true, false);

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
        HasThreadedHoles = HasThreadedHoles,
        ThreadSpecification = ThreadSpecification,
        ThreadedHoleCount = ThreadedHoleCount,
        InsertType = InsertType,
        InsertCount = InsertCount,
        Quantity = Quantity,
        VolumeCc = Math.Max(VolumeCc, 1m),
        SurfaceAreaCm2 = SurfaceAreaCm2,
        DfmAcknowledged = DfmAcknowledged,
        PartNotes = PartNotes,
        BodyCount = BodyCount,
        SelectedBodyIndex = SelectedBodyIndex,
        DrawingFiles = [.. DrawingFiles],
        ViewerSettings = ViewerSettings
    };
}
