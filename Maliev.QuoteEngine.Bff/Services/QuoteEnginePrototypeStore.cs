using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Maliev.QuoteEngine.Bff.Options;
using Maliev.QuoteEngine.Shared.Account;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Bff.Services;

public sealed class QuoteEnginePrototypeStore
{
    private readonly ConcurrentDictionary<string, UploadState> _uploads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Guid> _customerIdsByEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, CustomerProfileResponse> _customers = new();
    private readonly ConcurrentDictionary<Guid, CustomerQuoteRecord> _quotes = new();
    private readonly ConcurrentDictionary<Guid, CustomerOrderRecord> _orders = new();

    public CustomerProfileResponse PrototypeCustomer { get; } = new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        "Natt MALIEV",
        "customer@example.com",
        "+66 2 000 0000",
        "MALIEV Prototype Customer",
        "en-US");

    public QuoteReferenceDataResponse ReferenceData { get; } = new(
        [
            new("fdm", "FDM 3D Printing", "Fast polymer prototypes and fixtures.", QuoteUploadConstraints.MeshFileExtensions),
            new("sla", "SLA 3D Printing", "High-detail resin parts for presentation models.", QuoteUploadConstraints.MeshFileExtensions),
            new("cnc", "CNC Machining", "Aluminum and engineering plastic machining.", QuoteUploadConstraints.MachiningFileExtensions)
        ],
        [
            new("pla-black", "fdm", "PLA Black", "General prototype", 1.24m, "Matte black"),
            new("petg-clear", "fdm", "PETG Clear", "Functional prototype", 1.27m, "Translucent"),
            new("resin-gray", "sla", "Standard Resin", "Presentation model", 1.10m, "Smooth gray"),
            new("al6061", "cnc", "Aluminum 6061", "Machining stock", 2.70m, "As machined")
        ],
        [
            new("fdm-matte", "fdm", "MATTE", "Matte", "Standard printed polymer finish.", 1.00m),
            new("fdm-vapor-smooth", "fdm", "VAPOR_SMOOTH", "Vapor smooth", "Smoothed polymer surface for customer-facing prototypes.", 1.18m),
            new("sla-standard-cure", "sla", "STANDARD_CURE", "Standard cure", "Washed, cured, and support marks cleaned.", 1.00m),
            new("sla-sanded-primer", "sla", "SANDED_PRIMER", "Sanded and primed", "Presentation-ready resin finish.", 1.22m),
            new("cnc-as-machined", "cnc", "AS_MACHINED", "As machined", "Standard tool marks accepted.", 1.00m),
            new("cnc-bead-blast-clear", "cnc", "BEAD_BLAST_CLEAR", "Bead blast + clear anodize", "Uniform satin aluminum surface with clear anodize.", 1.16m)
        ],
        [
            new("fdm-standard", "fdm", "FDM_STANDARD", "FDM standard", "General prototype tolerances.", 1.00m),
            new("sla-standard", "sla", "SLA_STANDARD", "SLA standard", "Fine resin prototype tolerances.", 1.00m),
            new("iso-2768-m", "cnc", "ISO2768_M", "ISO 2768-m", "General CNC tolerance without individual drawing callouts.", 1.08m),
            new("iso-2768-f", "cnc", "ISO2768_F", "ISO 2768-f", "Finer CNC tolerance requiring additional setup and inspection.", 1.18m)
        ],
        [
            new("STANDARD", "Standard inspection", "Visual inspection plus critical dimension spot-check.", 1.00m),
            new("DIMENSIONAL_REPORT", "Dimensional report", "Customer-visible dimensional report for selected features.", 1.10m),
            new("CMM_REPORT", "CMM report", "Full CMM measurement report for qualified CNC parts.", 1.28m)
        ],
        [
            new("RA_3_2", "cnc", "Ra 3.2", "Standard machined surface roughness.", 1.00m),
            new("RA_1_6", "cnc", "Ra 1.6", "Finer machined surface for customer-facing parts.", 1.08m),
            new("RA_0_8", "cnc", "Ra 0.8", "Precision machined surface requiring additional finishing.", 1.18m)
        ],
        [
            new("BLACK", "Black", "#242424", ["pla-black"]),
            new("CLEAR", "Clear", "#d9eef2", ["petg-clear"]),
            new("GRAY", "Gray", "#a8adb4", ["resin-gray"]),
            new("Natural", "Natural", "#b9c2c5", ["al6061"])
        ],
        [
            new("ECONOMY", "Economy", 10, 0.90m),
            new("STANDARD", "Standard", 6, 1.00m),
            new("EXPRESS", "Express", 3, 1.35m)
        ],
        QuoteUploadConstraints.SupportedCadExtensions);

    public IReadOnlyList<CustomerNdaDto> Ndas { get; } =
    [
        new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "Mutual NDA", "Active", DateTimeOffset.UtcNow.AddDays(-12))
    ];

    public IReadOnlyList<CustomerDocumentDto> Documents { get; } =
    [
        new(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), "manufacturing-requirements.pdf", "Requirement", DateTimeOffset.UtcNow.AddDays(-4))
    ];

    public IReadOnlyList<CustomerQuoteSummaryDto> GetQuotes(Guid customerId)
    {
        return _quotes.Values
            .Where(record => record.CustomerId == customerId)
            .Select(record => record.Quote)
            .OrderByDescending(x => x.UpdatedAt)
            .ToArray();
    }

    public IReadOnlyList<CustomerOrderSummaryDto> GetOrders(Guid customerId)
    {
        return _orders.Values
            .Where(record => record.CustomerId == customerId)
            .Select(record => record.Order)
            .OrderByDescending(x => x.UpdatedAt)
            .ToArray();
    }

    public CustomerProfileResponse GetProfile(Guid customerId)
    {
        return _customers.TryGetValue(customerId, out var profile) ? profile : PrototypeCustomer;
    }

    public CustomerProfileResponse GetOrCreateCustomer(string email, string displayName, string phone = "", string companyName = "")
    {
        var normalizedEmail = NormalizeEmail(email);
        var customerId = _customerIdsByEmail.GetOrAdd(normalizedEmail, CreateDeterministicCustomerId);
        return _customers.GetOrAdd(customerId, _ => new CustomerProfileResponse(
            customerId,
            string.IsNullOrWhiteSpace(displayName) ? normalizedEmail : displayName,
            normalizedEmail,
            phone,
            companyName,
            "en-US"));
    }

    public CustomerProfileResponse UpsertCustomer(
        Guid customerId,
        string email,
        string displayName,
        string phone = "",
        string companyName = "",
        string preferredLanguage = "en-US")
    {
        var normalizedEmail = NormalizeEmail(email);
        var profile = new CustomerProfileResponse(
            customerId,
            string.IsNullOrWhiteSpace(displayName) ? normalizedEmail : displayName,
            normalizedEmail,
            phone,
            companyName,
            string.IsNullOrWhiteSpace(preferredLanguage) ? "en-US" : preferredLanguage);

        _customerIdsByEmail[normalizedEmail] = customerId;
        _customers[customerId] = profile;
        return profile;
    }

    public QuoteEngineDemoProjectResponse DemoProject { get; } = new(
        "demo-sample-bracket",
        "MALIEV sample bracket demo",
        [
            new QuotePartDraftDto
            {
                PartId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                FileId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                UploadId = "demo-upload-sample-bracket",
                FileName = "maliev-sample-bracket.step",
                ProcessId = "cnc",
                MaterialId = "al6061",
                Quantity = 2,
                VolumeCc = 12.4m,
                SurfaceAreaCm2 = 78.2m,
                DfmAcknowledged = true
            }
        ],
        "/images/generated/sample-part.svg",
        "/images/generated/sample-part.svg",
        "Demo mode uses MALIEV-owned sample files and does not create customer projects, uploads, quotations, orders, or history.");

    public UploadState InitiateUpload(InitiateQuoteUploadRequest request, Guid? customerId)
    {
        var uploadId = Guid.NewGuid().ToString("N");
        var safeName = string.Join("_", request.FileName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var storagePath = customerId.HasValue
            ? $"customers/{customerId.Value:N}/quotes/{request.QuoteSessionId}/{uploadId}/{safeName}"
            : $"quotes/temp/{request.QuoteSessionId}/{uploadId}/{safeName}";
        var state = new UploadState(
            uploadId,
            Guid.NewGuid(),
            safeName,
            request.ContentType,
            request.FileSizeBytes,
            storagePath,
            customerId,
            IsTemporary: !customerId.HasValue);

        _uploads[uploadId] = state;
        return state;
    }

    public QuoteUploadHandoffResponse ImportHandoff(QuoteUploadHandoffRequest request, Guid? customerId)
    {
        var parts = new List<QuoteUploadHandoffPartDto>();
        foreach (var file in request.Files.Where(file => !string.IsNullOrWhiteSpace(file.UploadId)))
        {
            var safeName = string.Join("_", file.FileName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            var uploadId = file.UploadId.Trim();
            var state = new UploadState(
                uploadId,
                file.FileId ?? Guid.NewGuid(),
                string.IsNullOrWhiteSpace(safeName) ? "uploaded-part" : safeName,
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                file.FileSizeBytes,
                file.StoragePath,
                customerId,
                IsTemporary: !customerId.HasValue)
            {
                ReceivedBytes = file.FileSizeBytes,
                Status = "Uploaded"
            };

            _uploads[uploadId] = state;
            var analyzed = MarkAnalyzed(uploadId);
            parts.Add(new QuoteUploadHandoffPartDto(
                Guid.NewGuid(),
                analyzed.FileId,
                analyzed.UploadId,
                analyzed.FileName,
                analyzed.StoragePath,
                analyzed.Status,
                analyzed.VolumeCc,
                analyzed.SurfaceAreaCm2,
                analyzed.Findings));
        }

        return new QuoteUploadHandoffResponse(request.QuoteSessionId, parts);
    }

    public UploadState? GetUpload(string uploadId)
    {
        return _uploads.TryGetValue(uploadId, out var upload) ? upload : null;
    }

    public UploadState AttachDownstreamUpload(string uploadId, string downstreamUploadId)
    {
        var current = GetRequiredUpload(uploadId);
        var updated = current with { DownstreamUploadId = downstreamUploadId };
        _uploads[uploadId] = updated;
        return updated;
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

    /// <summary>Transitions an upload to "Processing" status (real pipeline path).</summary>
    public UploadState MarkProcessing(string uploadId)
    {
        var current = GetRequiredUpload(uploadId);
        var updated = current with { Status = "Processing" };
        _uploads[uploadId] = updated;
        return updated;
    }

    /// <summary>Returns a pre-computed demo result without running real geometry analysis.</summary>
    public UploadState MarkDemoAnalyzed(string uploadId, DemoModeOptions options)
    {
        var current = GetRequiredUpload(uploadId);
        var updated = current with
        {
            Status = "Analyzed",
            ViewerGlbUrl = options.GlbUrl ?? "/images/generated/sample-part.svg",
            ThumbnailUrl = options.ThumbnailUrl ?? "/images/generated/sample-part.svg",
            VolumeCc = 12.4m,
            SurfaceAreaCm2 = 78.2m,
            Findings = []
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
            var config = EstimateConfiguration(part);
            var unitPrice = Math.Round(((setup + (Math.Max(part.VolumeCc, 1m) * baseRate)) * config.Multiplier + config.Additive) * leadTime.PriceMultiplier, 2);
            var lineTotal = Math.Round(unitPrice * part.Quantity, 2);
            return new QuoteLineEstimateDto(part.PartId, part.FileName, unitPrice, lineTotal, "THB", config.Notes);
        }).ToArray();

        var subtotal = lines.Sum(x => x.LineTotal);
        var discount = subtotal >= 25_000m ? Math.Round(subtotal * 0.05m, 2) : 0m;
        return new QuoteEstimateResponse(request.QuoteSessionId, subtotal, discount, subtotal - discount, "THB", true, lines);
    }

    public CreateDraftProjectResponse CreateDraftProject()
    {
        return new CreateDraftProjectResponse(Guid.NewGuid(), $"QE-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}", "Draft");
    }

    public GenerateFormalQuoteResponse GenerateQuote(Guid customerId)
    {
        var quote = new CustomerQuoteSummaryDto(
            Guid.NewGuid(),
            $"MQ-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}",
            "Ready for approval",
            12_500m,
            "THB",
            DateTimeOffset.UtcNow,
            "/quote/v1/account/quotes/sample.pdf");
        _quotes[quote.QuoteId] = new CustomerQuoteRecord(customerId, quote);
        return new GenerateFormalQuoteResponse(quote.QuoteId, quote.QuoteNumber, quote.PdfUrl, quote.Status);
    }

    public CreateManufacturingOrderResponse CreateOrder(Guid customerId, Guid quoteId)
    {
        if (!_quotes.TryGetValue(quoteId, out var quote) || quote.CustomerId != customerId)
        {
            throw new KeyNotFoundException($"Quote '{quoteId}' was not found for the signed-in customer.");
        }

        var order = new CustomerOrderSummaryDto(
            Guid.NewGuid(),
            $"MO-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}",
            "Order received",
            DateTimeOffset.UtcNow,
            "Waiting for production review");
        _orders[order.OrderId] = new CustomerOrderRecord(customerId, order);
        return new CreateManufacturingOrderResponse(order.OrderId, order.OrderNumber, order.Status);
    }

    private static string NormalizeEmail(string email)
    {
        return string.IsNullOrWhiteSpace(email) ? "customer@example.com" : email.Trim().ToLowerInvariant();
    }

    private static Guid CreateDeterministicCustomerId(string normalizedEmail)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"quote-engine-prototype:{normalizedEmail}"));
        return new Guid(hash[..16]);
    }

    private (decimal Multiplier, decimal Additive, string Notes) EstimateConfiguration(QuotePartDraftDto part)
    {
        var multiplier = 1m;
        var additive = 0m;
        var notes = new List<string> { "Prototype estimate from geometry, material, and lead-time inputs" };

        var finish = ReferenceData.Finishes.FirstOrDefault(option =>
            Matches(option.Id, part.FinishId) || Matches(option.Code, part.FinishCode));
        if (finish is not null && finish.PriceMultiplier != 1m)
        {
            multiplier *= finish.PriceMultiplier;
            notes.Add($"finish {finish.Name}");
        }

        var tolerance = ReferenceData.Tolerances.FirstOrDefault(option =>
            Matches(option.Id, part.ToleranceId) || Matches(option.Code, part.ToleranceCode));
        if (tolerance is not null && tolerance.PriceMultiplier != 1m)
        {
            multiplier *= tolerance.PriceMultiplier;
            notes.Add($"tolerance {tolerance.Code}");
        }

        var inspection = ReferenceData.InspectionLevels.FirstOrDefault(option =>
            Matches(option.Code, part.InspectionLevel));
        if (inspection is not null)
        {
            notes.Add($"inspection {inspection.Name}");
            if (inspection.PriceMultiplier != 1m)
            {
                multiplier *= inspection.PriceMultiplier;
            }
        }

        var roughness = ReferenceData.RoughnessOptions.FirstOrDefault(option =>
            Matches(option.Code, part.RoughnessCode));
        if (roughness is not null && roughness.PriceMultiplier != 1m)
        {
            multiplier *= roughness.PriceMultiplier;
            notes.Add($"roughness {roughness.Name}");
        }

        if (part.HasThreadedHoles || part.ThreadedHoleCount > 0)
        {
            var count = Math.Max(part.ThreadedHoleCount, 1);
            additive += count * 85m;
            notes.Add($"threaded holes {count}");
        }

        if (!string.IsNullOrWhiteSpace(part.InsertType) && !part.InsertType.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            var count = Math.Max(part.InsertCount, 1);
            additive += count * 120m;
            notes.Add($"thread inserts {count}");
        }

        if (part.DrawingFiles.Count > 0)
        {
            notes.Add("drawing reviewed");
        }

        return (multiplier, additive, string.Join("; ", notes) + ".");
    }

    private static bool Matches(string candidate, string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && candidate.Equals(value, StringComparison.OrdinalIgnoreCase);
    }

    private UploadState GetRequiredUpload(string uploadId)
    {
        return GetUpload(uploadId) ?? throw new KeyNotFoundException($"Upload '{uploadId}' was not found.");
    }
}

internal sealed record CustomerQuoteRecord(Guid CustomerId, CustomerQuoteSummaryDto Quote);

internal sealed record CustomerOrderRecord(Guid CustomerId, CustomerOrderSummaryDto Order);

public sealed record UploadState(
    string UploadId,
    Guid FileId,
    string FileName,
    string ContentType,
    long ExpectedSizeBytes,
    string StoragePath,
    Guid? CustomerId,
    bool IsTemporary)
{
    public string Status { get; init; } = "WaitingForUpload";

    public string? DownstreamUploadId { get; init; }

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
