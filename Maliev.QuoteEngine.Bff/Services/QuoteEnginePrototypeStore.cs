using System.Collections.Concurrent;
using Maliev.QuoteEngine.Shared.Account;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Bff.Services;

public sealed class QuoteEnginePrototypeStore
{
    private readonly ConcurrentDictionary<string, UploadState> _uploads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, CustomerQuoteSummaryDto> _quotes = new();
    private readonly ConcurrentDictionary<Guid, CustomerOrderSummaryDto> _orders = new();

    public CustomerProfileResponse PrototypeCustomer { get; } = new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        "Natt MALIEV",
        "customer@example.com",
        "+66 2 000 0000",
        "MALIEV Prototype Customer",
        "en-US");

    public QuoteReferenceDataResponse ReferenceData { get; } = new(
        [
            new("fdm", "FDM 3D Printing", "Fast polymer prototypes and fixtures.", ["stl", "3mf", "obj"]),
            new("sla", "SLA 3D Printing", "High-detail resin parts for presentation models.", ["stl", "obj"]),
            new("cnc", "CNC Machining", "Aluminum and engineering plastic machining.", ["step", "stp", "iges", "igs"])
        ],
        [
            new("pla-black", "fdm", "PLA Black", "General prototype", 1.24m, "Matte black"),
            new("petg-clear", "fdm", "PETG Clear", "Functional prototype", 1.27m, "Translucent"),
            new("resin-gray", "sla", "Standard Resin", "Presentation model", 1.10m, "Smooth gray"),
            new("al6061", "cnc", "Aluminum 6061", "Machining stock", 2.70m, "As machined")
        ],
        [
            new("ECONOMY", "Economy", 10, 0.90m),
            new("STANDARD", "Standard", 6, 1.00m),
            new("EXPRESS", "Express", 3, 1.35m)
        ],
        ["stl", "step", "stp", "obj", "3mf", "iges", "igs"]);

    public IReadOnlyList<CustomerNdaDto> Ndas { get; } =
    [
        new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "Mutual NDA", "Active", DateTimeOffset.UtcNow.AddDays(-12))
    ];

    public IReadOnlyList<CustomerDocumentDto> Documents { get; } =
    [
        new(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), "manufacturing-requirements.pdf", "Requirement", DateTimeOffset.UtcNow.AddDays(-4))
    ];

    public IReadOnlyList<CustomerQuoteSummaryDto> Quotes => _quotes.Values.OrderByDescending(x => x.UpdatedAt).ToArray();

    public IReadOnlyList<CustomerOrderSummaryDto> Orders => _orders.Values.OrderByDescending(x => x.UpdatedAt).ToArray();

    public UploadState InitiateUpload(InitiateQuoteUploadRequest request)
    {
        var uploadId = Guid.NewGuid().ToString("N");
        var safeName = string.Join("_", request.FileName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var state = new UploadState(
            uploadId,
            Guid.NewGuid(),
            safeName,
            request.ContentType,
            request.FileSizeBytes,
            $"quotes/{request.QuoteSessionId}/{uploadId}/{safeName}");

        _uploads[uploadId] = state;
        return state;
    }

    public UploadState? GetUpload(string uploadId)
    {
        return _uploads.TryGetValue(uploadId, out var upload) ? upload : null;
    }

    public UploadState MarkUploaded(string uploadId, long receivedBytes)
    {
        var current = GetRequiredUpload(uploadId);
        var updated = current with
        {
            ReceivedBytes = receivedBytes,
            Status = "Uploaded"
        };
        _uploads[uploadId] = updated;
        return updated;
    }

    public UploadState MarkAnalyzed(string uploadId)
    {
        var current = GetRequiredUpload(uploadId);
        var volume = Math.Max(1.5m, Math.Round(current.ExpectedSizeBytes / 175_000m, 2));
        var updated = current with
        {
            Status = "Analyzed",
            VolumeCc = volume,
            SurfaceAreaCm2 = Math.Round(volume * 6.4m, 2),
            ViewerGlbUrl = "/images/generated/sample-part.svg",
            ThumbnailUrl = "/images/generated/sample-part.svg",
            Findings =
            [
                new("Info", "WALL_MIN", "Minimum wall thickness appears acceptable for prototype quoting."),
                new("Warning", "THREAD_REVIEW", "Threaded features should be confirmed before production.")
            ]
        };
        _uploads[uploadId] = updated;
        return updated;
    }

    public QuoteEstimateResponse Estimate(QuoteEstimateRequest request)
    {
        var leadTime = ReferenceData.LeadTimes.FirstOrDefault(x => x.Code.Equals(request.LeadTimeCode, StringComparison.OrdinalIgnoreCase))
            ?? ReferenceData.LeadTimes.First(x => x.Code == "STANDARD");

        var lines = request.Parts.Select(part =>
        {
            var material = ReferenceData.Materials.FirstOrDefault(x => x.Id.Equals(part.MaterialId, StringComparison.OrdinalIgnoreCase));
            var baseRate = material?.ProcessId switch
            {
                "cnc" => 520m,
                "sla" => 180m,
                _ => 95m
            };

            var setup = material?.ProcessId == "cnc" ? 850m : 120m;
            var unitPrice = Math.Round((setup + (Math.Max(part.VolumeCc, 1m) * baseRate)) * leadTime.PriceMultiplier, 2);
            var lineTotal = Math.Round(unitPrice * part.Quantity, 2);
            return new QuoteLineEstimateDto(part.PartId, part.FileName, unitPrice, lineTotal, "THB", "Prototype estimate from geometry, material, and lead-time inputs.");
        }).ToArray();

        var subtotal = lines.Sum(x => x.LineTotal);
        var discount = subtotal >= 25_000m ? Math.Round(subtotal * 0.05m, 2) : 0m;
        return new QuoteEstimateResponse(request.QuoteSessionId, subtotal, discount, subtotal - discount, "THB", true, lines);
    }

    public CreateDraftProjectResponse CreateDraftProject()
    {
        return new CreateDraftProjectResponse(Guid.NewGuid(), $"QE-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}", "Draft");
    }

    public GenerateFormalQuoteResponse GenerateQuote()
    {
        var quote = new CustomerQuoteSummaryDto(
            Guid.NewGuid(),
            $"MQ-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}",
            "Ready for approval",
            12_500m,
            "THB",
            DateTimeOffset.UtcNow,
            "/quote/v1/account/quotes/sample.pdf");
        _quotes[quote.QuoteId] = quote;
        return new GenerateFormalQuoteResponse(quote.QuoteId, quote.QuoteNumber, quote.PdfUrl, quote.Status);
    }

    public CreateManufacturingOrderResponse CreateOrder(Guid quoteId)
    {
        _ = quoteId;
        var order = new CustomerOrderSummaryDto(
            Guid.NewGuid(),
            $"MO-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}",
            "Order received",
            DateTimeOffset.UtcNow,
            "Waiting for production review");
        _orders[order.OrderId] = order;
        return new CreateManufacturingOrderResponse(order.OrderId, order.OrderNumber, order.Status);
    }

    private UploadState GetRequiredUpload(string uploadId)
    {
        return GetUpload(uploadId) ?? throw new KeyNotFoundException($"Upload '{uploadId}' was not found.");
    }
}

public sealed record UploadState(
    string UploadId,
    Guid FileId,
    string FileName,
    string ContentType,
    long ExpectedSizeBytes,
    string StoragePath)
{
    public string Status { get; init; } = "WaitingForUpload";

    public long ReceivedBytes { get; init; }

    public decimal VolumeCc { get; init; }

    public decimal SurfaceAreaCm2 { get; init; }

    public string? ViewerGlbUrl { get; init; }

    public string? ThumbnailUrl { get; init; }

    public IReadOnlyList<DfmFindingDto> Findings { get; init; } = [];

    public QuoteAnalysisStatusResponse ToAnalysisStatus() => new()
    {
        UploadId = UploadId,
        StoragePath = StoragePath,
        Status = Status,
        VolumeCc = VolumeCc,
        SurfaceAreaCm2 = SurfaceAreaCm2,
        ViewerGlbUrl = ViewerGlbUrl,
        ThumbnailUrl = ThumbnailUrl,
        Findings = Findings
    };
}
