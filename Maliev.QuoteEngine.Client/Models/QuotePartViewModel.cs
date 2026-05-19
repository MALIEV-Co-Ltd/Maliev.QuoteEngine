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
    public string FileName { get; set; } = string.Empty;
    public string ProcessId { get; set; } = "fdm";
    public string MaterialId { get; set; } = "pla-black";
    public int Quantity { get; set; } = 1;
    public decimal VolumeCc { get; set; }
    public decimal SurfaceAreaCm2 { get; set; }
    public bool DfmAcknowledged { get; set; }
    public string Status { get; set; } = "Waiting";
    public IReadOnlyList<DfmFindingDto> Findings { get; set; } = [];

    // Phase 2 fields
    public string? StoragePath { get; set; }
    public string? GlbUrl { get; set; }
    public string? ThumbnailUrl { get; set; }
    public int BodyCount { get; set; } = 1;
    public bool IsManifold { get; set; } = true;
    public string? NonManifoldReason { get; set; }
    public QeFdmDfmReport? FdmDfmReport { get; set; }
    public QeSlaDfmReport? SlaDfmReport { get; set; }
    public QeCncDfmReport? CncDfmReport { get; set; }
    public IReadOnlyList<string> OverlayGlbUrls { get; set; } = [];
    public decimal? UnitPriceTHB { get; set; }

    public QuotePartDraftDto ToDraft() => new()
    {
        PartId = PartId,
        FileId = FileId,
        UploadId = UploadId,
        FileName = FileName,
        ProcessId = ProcessId,
        MaterialId = MaterialId,
        Quantity = Quantity,
        VolumeCc = Math.Max(VolumeCc, 1m),
        SurfaceAreaCm2 = SurfaceAreaCm2,
        DfmAcknowledged = DfmAcknowledged
    };
}
