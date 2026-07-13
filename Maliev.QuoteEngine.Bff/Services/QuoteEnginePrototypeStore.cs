using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Maliev.QuoteEngine.Bff.Options;
using Maliev.QuoteEngine.Shared.Account;
using Maliev.QuoteEngine.Shared.Agent;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Bff.Services;

public sealed class QuoteEnginePrototypeStore
{
    private readonly ConcurrentDictionary<string, UploadState> _uploads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Guid> _customerIdsByEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, CustomerProfileResponse> _customers = new();
    private readonly ConcurrentDictionary<Guid, CustomerQuoteRecord> _quotes = new();
    private readonly ConcurrentDictionary<Guid, CustomerOrderRecord> _orders = new();
    private readonly ConcurrentDictionary<Guid, CustomerProjectRecord> _projects = new();
    private readonly ConcurrentDictionary<Guid, List<CustomerAddressDto>> _addressesByCustomer = new();
    private readonly ConcurrentDictionary<Guid, List<CustomerDocumentDto>> _documentsByCustomer = new();
    private readonly ConcurrentDictionary<string, CustomerOrderRecordDetail> _orderDetailsByNumber = new(StringComparer.OrdinalIgnoreCase);

    public CustomerProfileResponse PrototypeCustomer { get; } = new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        "Demo Customer",
        "demo.customer@example.com",
        "",
        "MALIEV Demo Account",
        "en-US",
        ProfileImageUrl: null,
        PreferredCurrency: "THB",
        Timezone: "Asia/Bangkok",
        Segment: "Prototype buyer",
        Tier: "Customer",
        NdaStatus: "Active",
        NdaExpiresAt: DateTimeOffset.UtcNow.AddDays(90));

    public QuoteReferenceDataResponse ReferenceData { get; } = new(
        [
            new("fdm", "FDM 3D Printing", "Fast polymer prototypes and fixtures.", QuoteUploadConstraints.MeshFileExtensions),
            new("sla", "SLA 3D Printing", "High-detail resin parts for presentation models.", QuoteUploadConstraints.MeshFileExtensions),
            new("cnc", "CNC Machining", "Aluminum and engineering plastic machining.", QuoteUploadConstraints.MachiningFileExtensions)
        ],
        [
            new("pla", "fdm", "PLA", "General purpose FDM material, multiple colors available", 1.24m, "Matte"),
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
            new("RED", "Red", "#CC2200", ["pla"]),
            new("WHITE", "White", "#F2F0EC", ["pla"]),
            new("BLACK", "Black", "#242424", ["pla", "pla-black"]),
            new("CLEAR", "Clear", "#d9eef2", ["petg-clear"]),
            new("GRAY", "Gray", "#a8adb4", ["resin-gray"]),
            new("Natural", "Natural", "#b9c2c5", ["al6061"])
        ],
        [
            new("ECONOMY", "Economy", 10, 0.90m),
            new("STANDARD", "Standard", 6, 1.00m),
            new("EXPRESS", "Express", 3, 1.35m)
        ],
        [
            new(
                "deburr_edges",
                "cnc",
                "Deburr edges",
                "boolean",
                "true",
                "Remove sharp edges after machining.",
                []),
            new(
                "print_orientation",
                "fdm",
                "Print orientation",
                "select",
                "Best strength",
                "Choose how the part should be oriented for FDM printing.",
                ["Best strength", "Best surface", "Fastest print"]),
            new(
                "support_removal",
                "sla",
                "Support removal",
                "select",
                "Standard",
                "Select the resin support cleanup level.",
                ["Standard", "Cosmetic faces protected"])
        ],
        QuoteUploadConstraints.SupportedCadExtensions);

    public IReadOnlyList<CustomerNdaDto> Ndas { get; } =
    [
        new(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "Mutual NDA",
            "Active",
            DateTimeOffset.UtcNow.AddDays(-12),
            DateTimeOffset.UtcNow.AddDays(90))
    ];

    public QuoteEnginePrototypeStore()
    {
        SeedPrototypeSalesHistory();
    }

    // Gives the prototype "Demo Customer" a small, realistic sales history so dev/test agents can
    // exercise the populated Quotes and Orders surfaces (not just empty states). Only surfaces via
    // the dev/test prototype fallbacks; inert in production.
    private void SeedPrototypeSalesHistory()
    {
        var customerId = PrototypeCustomer.CustomerId;
        var now = DateTimeOffset.UtcNow;
        const string pdfUrl = "/quote/v1/account/quotes/sample.pdf";

        var quoteId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var quote = new CustomerQuoteSummaryDto(
            quoteId,
            "MQ-DEMO-0001",
            "Ready for approval",
            12_500m,
            "THB",
            now.AddDays(-3),
            pdfUrl,
            [
                new CustomerQuoteVersionSummaryDto(
                    Guid.Parse("d1d1d1d1-dddd-dddd-dddd-dddddddddddd"),
                    1,
                    12_500m,
                    "THB",
                    "Initial customer quote",
                    pdfUrl,
                    null,
                    "Make Studio",
                    now.AddDays(-3))
            ]);
        _quotes[quoteId] = new CustomerQuoteRecord(customerId, quote);

        SeedPrototypeOrder(
            customerId, quoteId,
            Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            "MO-DEMO-0001",
            currentStatus: "In production",
            paymentStatus: "Paid",
            trackingLabel: "CNC machining in progress",
            createdAt: now.AddDays(-3),
            updatedAt: now.AddDays(-2),
            promisedDelivery: now.AddDays(6),
            requirements: "CNC-machined 6061-T6 bracket, clear anodize, ±0.1 mm, qty 50.",
            statusHistory:
            [
                new OrderStatusEntryDto("Order received", null, now.AddDays(-3)),
                new OrderStatusEntryDto("Accepted", "Quote accepted by customer.", now.AddDays(-2).AddHours(-2)),
                new OrderStatusEntryDto("In production", "CNC machining in progress.", now.AddDays(-2))
            ],
            milestones:
            [
                new CustomerManufacturingMilestoneDto("received", "Order received", "Order confirmed and queued.", "complete", 100, now.AddDays(-3)),
                new CustomerManufacturingMilestoneDto("production", "In production", "CNC machining in progress.", "active", 60, now.AddDays(-2)),
                new CustomerManufacturingMilestoneDto("qc", "Quality check", "Dimensional inspection.", "pending", 0, null),
                new CustomerManufacturingMilestoneDto("shipped", "Shipped", "Handover to carrier.", "pending", 0, null)
            ]);

        SeedPrototypeOrder(
            customerId, quoteId,
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            "MO-DEMO-0002",
            currentStatus: "Paid",
            paymentStatus: "Paid",
            trackingLabel: "Awaiting shipment",
            createdAt: now.AddDays(-1).AddHours(-3),
            updatedAt: now.AddDays(-1),
            promisedDelivery: now.AddDays(3),
            requirements: "SLA resin prototype, standard finish, qty 5.",
            statusHistory:
            [
                new OrderStatusEntryDto("Order received", null, now.AddDays(-1).AddHours(-3)),
                new OrderStatusEntryDto("Accepted", "Quote accepted by customer.", now.AddDays(-1).AddHours(-2)),
                new OrderStatusEntryDto("Paid", "Payment completed.", now.AddDays(-1))
            ],
            milestones:
            [
                new CustomerManufacturingMilestoneDto("received", "Order received", "Order confirmed and queued.", "complete", 100, now.AddDays(-1).AddHours(-3)),
                new CustomerManufacturingMilestoneDto("paid", "Paid", "Payment completed.", "complete", 100, now.AddDays(-1)),
                new CustomerManufacturingMilestoneDto("production", "Production", "Awaiting production slot.", "pending", 0, null),
                new CustomerManufacturingMilestoneDto("shipped", "Shipped", "Handover to carrier.", "pending", 0, null)
            ]);
    }

    private void SeedPrototypeOrder(
        Guid customerId,
        Guid quoteId,
        Guid orderId,
        string orderNumber,
        string currentStatus,
        string paymentStatus,
        string trackingLabel,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset promisedDelivery,
        string requirements,
        IReadOnlyList<OrderStatusEntryDto> statusHistory,
        IReadOnlyList<CustomerManufacturingMilestoneDto> milestones)
    {
        var summary = new CustomerOrderSummaryDto(orderId, orderNumber, currentStatus, updatedAt, trackingLabel);
        _orders[orderId] = new CustomerOrderRecord(customerId, summary);

        var detail = new CustomerOrderDetailDto(
            orderId,
            orderNumber,
            currentStatus,
            paymentStatus,
            12_500m,
            "THB",
            promisedDelivery,
            ActualDeliveryDate: null,
            CustomerPoNumber: "PO-DEMO-0001",
            Requirements: requirements,
            createdAt,
            updatedAt,
            statusHistory)
        {
            QuoteId = quoteId,
            QuoteNumber = "MQ-DEMO-0001",
            ManufacturingMilestones = milestones
        };
        _orderDetailsByNumber[orderNumber] = new CustomerOrderRecordDetail(customerId, detail);
    }

    public CustomerOrderDetailDto? GetOrderDetail(Guid customerId, string orderNumber)
    {
        return _orderDetailsByNumber.TryGetValue(orderNumber, out var record) && record.CustomerId == customerId
            ? record.Detail
            : null;
    }

    public bool TryResolveOrderNumber(Guid customerId, Guid orderId, out string orderNumber)
    {
        if (_orders.TryGetValue(orderId, out var record) && record.CustomerId == customerId)
        {
            orderNumber = record.Order.OrderNumber;
            return true;
        }

        orderNumber = string.Empty;
        return false;
    }

    // Marks a prototype order Paid (dev/test checkout completion) and returns whether it was found.
    public bool MarkOrderPaid(Guid customerId, string orderNumber)
    {
        if (!_orderDetailsByNumber.TryGetValue(orderNumber, out var record) || record.CustomerId != customerId)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var history = record.Detail.StatusHistory.ToList();
        if (!history.Any(entry => entry.Status == "Paid"))
        {
            history.Add(new OrderStatusEntryDto("Paid", "Payment completed.", now));
        }

        var paidDetail = record.Detail with
        {
            CurrentStatus = "Paid",
            PaymentStatus = "Paid",
            UpdatedAt = now,
            StatusHistory = history
        };
        _orderDetailsByNumber[orderNumber] = new CustomerOrderRecordDetail(customerId, paidDetail);

        if (_orders.TryGetValue(record.Detail.OrderId, out var orderRecord))
        {
            var paidSummary = orderRecord.Order with { Status = "Paid", TrackingLabel = "Awaiting shipment", UpdatedAt = now };
            _orders[record.Detail.OrderId] = orderRecord with { Order = paidSummary };
        }

        return true;
    }

    public IReadOnlyList<CustomerDocumentDto> GetDocuments(Guid customerId)
    {
        var documents = new List<CustomerDocumentDto>
        {
            new(
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                "manufacturing-requirements.pdf",
                "Requirement",
                DateTimeOffset.UtcNow.AddDays(-4),
                "customer-documents/sample/manufacturing-requirements.pdf",
                "application/pdf",
                285_000)
        };

        if (_documentsByCustomer.TryGetValue(customerId, out var customerDocuments))
        {
            lock (customerDocuments)
            {
                documents.AddRange(customerDocuments);
            }
        }

        return documents.OrderByDescending(document => document.UploadedAt).ToArray();
    }

    public CustomerDocumentDto UploadDocument(Guid customerId, CustomerDocumentUploadRequest request)
    {
        var document = new CustomerDocumentDto(
            Guid.NewGuid(),
            string.IsNullOrWhiteSpace(request.FileName) ? "customer-document" : request.FileName.Trim(),
            string.IsNullOrWhiteSpace(request.Kind) ? "Document" : request.Kind.Trim(),
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(request.StoragePath) ? null : request.StoragePath.Trim(),
            string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType.Trim(),
            request.FileSizeBytes,
            string.IsNullOrWhiteSpace(request.OrderNumber) ? null : request.OrderNumber.Trim());

        var documents = _documentsByCustomer.GetOrAdd(customerId, _ => []);
        lock (documents)
        {
            documents.Add(document);
        }

        return document;
    }

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

    public IReadOnlyList<CustomerProjectNavItemDto> GetProjectNavigation(Guid customerId)
    {
        return _projects.Values
            .Where(record => record.CustomerId == customerId && !record.IsArchived)
            .OrderByDescending(record => record.IsPinned)
            .ThenByDescending(record => record.UpdatedAt)
            .Select(record => new CustomerProjectNavItemDto(
                record.ProjectId,
                record.ProjectNumber,
                record.Title,
                record.Status,
                record.IsPinned,
                record.IsArchived,
                record.UpdatedAt)
            {
                CreatedAt = record.CreatedAt
            })
            .ToArray();
    }

    public CustomerProjectDetailResponse? GetProjectDetail(Guid customerId, Guid projectId)
    {
        var project = GetProject(customerId, projectId);
        if (project is null)
        {
            return null;
        }

        return new CustomerProjectDetailResponse(
            project.ProjectId,
            project.ProjectNumber,
            project.Status,
            project.Title,
            project.IsPinned,
            project.IsArchived,
            project.UpdatedAt,
            ClonePartsForResponse(project.Parts))
        {
            CreatedAt = project.CreatedAt
        };
    }

    public IReadOnlyList<QuoteAgentSearchResultDto> SearchCustomerData(
        Guid customerId,
        string? query,
        int limit)
    {
        var normalizedQuery = NormalizeSearchQuery(query);
        var normalizedLimit = Math.Clamp(limit, 1, 50);
        var results = new List<QuoteAgentSearchResultDto>();

        foreach (var project in _projects.Values.Where(record => record.CustomerId == customerId))
        {
            AddIfMatches(results, normalizedQuery, new QuoteAgentSearchResultDto
            {
                ResourceType = "project",
                ResourceId = project.ProjectId.ToString("D"),
                Title = project.Title,
                Detail = $"{project.ProjectNumber} · {project.Status} · {project.Parts.Count} part(s)",
                ActionHint = "resume_project",
                Url = $"/quotes?projectId={project.ProjectId:D}",
                Metadata = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["projectNumber"] = project.ProjectNumber,
                    ["status"] = project.Status,
                    ["isPinned"] = project.IsPinned.ToString().ToLowerInvariant(),
                    ["isArchived"] = project.IsArchived.ToString().ToLowerInvariant()
                }
            });
        }

        foreach (var quote in GetQuotes(customerId))
        {
            AddIfMatches(results, normalizedQuery, new QuoteAgentSearchResultDto
            {
                ResourceType = "quote",
                ResourceId = quote.QuoteId.ToString("D"),
                Title = quote.QuoteNumber,
                Detail = $"{quote.Status} · {quote.Total:N2} {quote.Currency}",
                ActionHint = "open_quote",
                Url = quote.PdfUrl,
                Metadata = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["status"] = quote.Status,
                    ["currency"] = quote.Currency
                }
            });
        }

        foreach (var order in GetOrders(customerId))
        {
            AddIfMatches(results, normalizedQuery, new QuoteAgentSearchResultDto
            {
                ResourceType = "order",
                ResourceId = order.OrderId.ToString("D"),
                Title = order.OrderNumber,
                Detail = $"{order.Status} · {order.TrackingLabel}",
                ActionHint = "open_order",
                Url = $"/orders/{order.OrderNumber}",
                Metadata = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["status"] = order.Status
                }
            });
        }

        foreach (var document in GetDocuments(customerId))
        {
            AddIfMatches(results, normalizedQuery, new QuoteAgentSearchResultDto
            {
                ResourceType = "document",
                ResourceId = document.DocumentId.ToString("D"),
                Title = document.FileName,
                Detail = $"{document.Kind} · {document.ContentType ?? "file"}",
                ActionHint = "open_document",
                Url = document.StoragePath,
                Metadata = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["kind"] = document.Kind,
                    ["fileSizeBytes"] = document.FileSizeBytes.ToString(CultureInfo.InvariantCulture)
                }
            });
        }

        return results
            .OrderBy(result => SearchRank(result.ResourceType))
            .ThenBy(result => result.Title, StringComparer.OrdinalIgnoreCase)
            .Take(normalizedLimit)
            .ToArray();
    }

    public CustomerProfileResponse GetProfile(Guid customerId)
    {
        return _customers.TryGetValue(customerId, out var profile) ? profile : PrototypeCustomer;
    }

    public bool TryGetProfile(Guid customerId, out CustomerProfileResponse? profile)
    {
        if (_customers.TryGetValue(customerId, out profile))
        {
            return true;
        }

        // The prototype "Demo Customer" is a first-class fallback identity (matches GetProfile),
        // so account surfaces resolve a profile for it when CustomerService has no record.
        if (customerId == PrototypeCustomer.CustomerId)
        {
            profile = PrototypeCustomer;
            return true;
        }

        profile = null;
        return false;
    }

    public IReadOnlyList<CustomerAddressDto> GetAddresses(Guid customerId)
    {
        var addresses = _addressesByCustomer.GetOrAdd(customerId, _ => CreateDefaultAddresses());

        lock (addresses)
        {
            return addresses.Select(CloneAddress).ToArray();
        }
    }

    public CustomerAddressDto CreateAddress(Guid customerId, CustomerAddressUpsertRequest request, Guid resolvedCountryId)
    {
        var addresses = _addressesByCustomer.GetOrAdd(customerId, _ => []);
        lock (addresses)
        {
            var address = MapAddressRequest(request, resolvedCountryId);
            address.Id = Guid.NewGuid();
            address.IsDefault = request.IsDefault || !addresses.Any(item => SameAddressType(item, request.Type));
            address.Version = 1;

            if (address.IsDefault)
            {
                ResetSameRoleDefault(addresses, address);
            }

            addresses.Add(address);
            return CloneAddress(address);
        }
    }

    public CustomerAddressDto? UpdateAddress(Guid customerId, Guid addressId, CustomerAddressUpsertRequest request)
    {
        if (!_addressesByCustomer.TryGetValue(customerId, out var addresses))
        {
            return null;
        }

        lock (addresses)
        {
            var index = addresses.FindIndex(item => item.Id == addressId);
            if (index < 0)
            {
                return null;
            }

            var updated = MapAddressRequest(request, request.CountryId);
            updated.Id = addressId;
            updated.Version = addresses[index].Version + 1;

            addresses[index] = updated;
            if (updated.IsDefault)
            {
                ResetSameRoleDefault(addresses, updated);
            }

            return CloneAddress(updated);
        }
    }

    public bool DeleteAddress(Guid customerId, Guid addressId)
    {
        if (!_addressesByCustomer.TryGetValue(customerId, out var addresses))
        {
            return false;
        }

        lock (addresses)
        {
            var address = addresses.FirstOrDefault(item => item.Id == addressId);
            if (address is null)
            {
                return false;
            }

            addresses.Remove(address);
            if (address.IsDefault)
            {
                var replacement = addresses.FirstOrDefault(item => SameAddressType(item, address.Type));
                if (replacement is not null)
                {
                    replacement.IsDefault = true;
                    replacement.Version++;
                }
            }

            return true;
        }
    }

    public bool CustomerOwnsAddress(Guid customerId, Guid addressId)
    {
        if (!_addressesByCustomer.TryGetValue(customerId, out var addresses))
        {
            return false;
        }

        lock (addresses)
        {
            return addresses.Any(item => item.Id == addressId);
        }
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
            "en-US",
            ProfileImageUrl: null,
            PreferredCurrency: "THB",
            Timezone: "Asia/Bangkok",
            Segment: string.IsNullOrWhiteSpace(companyName) ? "Self-service manufacturing" : "Company manufacturing",
            Tier: "Customer",
            NdaStatus: "Active",
            NdaExpiresAt: DateTimeOffset.UtcNow.AddDays(90)));
    }

    public CustomerProfileResponse UpsertCustomer(
        Guid customerId,
        string email,
        string displayName,
        string phone = "",
        string companyName = "",
        string preferredLanguage = "en-US",
        string? profileImageUrl = null,
        string preferredCurrency = "THB",
        string timezone = "Asia/Bangkok",
        string segment = "Self-service manufacturing",
        string tier = "Customer",
        string ndaStatus = "Active",
        DateTimeOffset? ndaExpiresAt = null,
        string vatNumber = "")
    {
        var normalizedEmail = NormalizeEmail(email);
        var profile = new CustomerProfileResponse(
            customerId,
            string.IsNullOrWhiteSpace(displayName) ? normalizedEmail : displayName,
            normalizedEmail,
            phone,
            companyName,
            string.IsNullOrWhiteSpace(preferredLanguage) ? "en-US" : preferredLanguage,
            ProfileImageUrl: profileImageUrl,
            PreferredCurrency: string.IsNullOrWhiteSpace(preferredCurrency) ? "THB" : preferredCurrency,
            Timezone: string.IsNullOrWhiteSpace(timezone) ? "Asia/Bangkok" : timezone,
            Segment: string.IsNullOrWhiteSpace(segment)
                ? string.IsNullOrWhiteSpace(companyName) ? "Self-service manufacturing" : "Company manufacturing"
                : segment,
            Tier: string.IsNullOrWhiteSpace(tier) ? "Customer" : tier,
            NdaStatus: string.IsNullOrWhiteSpace(ndaStatus) ? "Active" : ndaStatus,
            NdaExpiresAt: ndaExpiresAt ?? DateTimeOffset.UtcNow.AddDays(90),
            VatNumber: string.IsNullOrWhiteSpace(vatNumber) ? string.Empty : vatNumber.Trim());

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
                FileName = "sample.step",
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

    public UploadState InitiateUpload(
        InitiateQuoteUploadRequest request,
        Guid? customerId,
        Guid? visitorId = null)
    {
        if (!Guid.TryParse(request.QuoteSessionId, out var quoteSessionId) || quoteSessionId == Guid.Empty)
        {
            throw new ArgumentException("A valid quote session identifier is required.", nameof(request));
        }

        var uploadId = Guid.NewGuid().ToString("N");
        var safeName = SanitizeUploadFileName(request.FileName);
        var storagePath = customerId.HasValue
            ? $"customers/{customerId.Value:N}/quotes/{quoteSessionId:N}/{uploadId}/{safeName}"
            : $"quotes/temp/{quoteSessionId:N}/{uploadId}/{safeName}";
        var state = new UploadState(
            uploadId,
            Guid.NewGuid(),
            safeName,
            request.ContentType,
            request.FileSizeBytes,
            storagePath,
            customerId,
            IsTemporary: !customerId.HasValue,
            VisitorId: visitorId,
            QuoteSessionId: quoteSessionId.ToString("D"));

        _uploads[uploadId] = state;
        return state;
    }

    public QuoteUploadHandoffResponse ImportHandoff(
        QuoteUploadHandoffRequest request,
        Guid? customerId,
        Guid? visitorId = null)
    {
        if (!Guid.TryParse(request.QuoteSessionId, out var quoteSessionId) || quoteSessionId == Guid.Empty)
        {
            throw new ArgumentException("A valid quote session identifier is required.", nameof(request));
        }

        var parts = new List<QuoteUploadHandoffPartDto>();
        foreach (var file in request.Files.Where(file => !string.IsNullOrWhiteSpace(file.UploadId)))
        {
            var safeName = SanitizeUploadFileName(file.FileName);
            var uploadId = file.UploadId.Trim();
            var state = new UploadState(
                uploadId,
                file.FileId ?? Guid.NewGuid(),
                string.IsNullOrWhiteSpace(safeName) ? "uploaded-part" : safeName,
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                file.FileSizeBytes,
                file.StoragePath,
                customerId,
                IsTemporary: !customerId.HasValue,
                VisitorId: visitorId,
                QuoteSessionId: quoteSessionId.ToString("D"))
            {
                ReceivedBytes = file.FileSizeBytes,
                Status = "Processing"
            };

            _uploads[uploadId] = state;
            parts.Add(new QuoteUploadHandoffPartDto(
                Guid.NewGuid(),
                state.FileId,
                state.UploadId,
                state.FileName,
                state.StoragePath,
                state.Status,
                0,
                0,
                [],
                null,
                state.StoragePath,
                NormalizeViewerFileExtension(state.StoragePath),
                null,
                state.ContentType,
                state.ExpectedSizeBytes));
        }

        return new QuoteUploadHandoffResponse(request.QuoteSessionId, parts);
    }

    private static string SanitizeUploadFileName(string fileName)
    {
        var leafName = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/')).Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(leafName
            .Select(character =>
                character == '/' ||
                character == '\\' ||
                char.IsControl(character) ||
                invalid.Contains(character)
                    ? '_'
                    : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or ".."
            ? "upload.bin"
            : sanitized;
    }

    public UploadState? GetUpload(string uploadId)
    {
        return _uploads.TryGetValue(uploadId, out var upload) ? upload : null;
    }

    /// <summary>
    /// Resolves an upload from its canonical storage path. Callers must still verify the returned
    /// upload's customer or visitor owner before exposing it; a storage path is only a locator.
    /// </summary>
    public UploadState? FindUploadByStoragePath(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            return null;
        }

        var normalized = storagePath.Trim();
        return _uploads.Values.FirstOrDefault(upload =>
            string.Equals(upload.StoragePath, normalized, StringComparison.Ordinal));
    }

    public IReadOnlyList<UploadState> GetSessionUploads(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            return [];
        }

        return _uploads.Values
            .Where(upload =>
                Guid.TryParse(upload.QuoteSessionId, out var uploadSessionId) &&
                uploadSessionId == sessionId)
            .ToArray();
    }

    public void PromoteSessionUploads(Guid sessionId, Guid expectedVisitorId, Guid customerId)
    {
        if (sessionId == Guid.Empty || expectedVisitorId == Guid.Empty || customerId == Guid.Empty)
        {
            return;
        }

        foreach (var entry in _uploads)
        {
            var current = entry.Value;
            while (!current.CustomerId.HasValue &&
                   current.VisitorId == expectedVisitorId &&
                   Guid.TryParse(current.QuoteSessionId, out var uploadSessionId) &&
                   uploadSessionId == sessionId)
            {
                var promoted = current with { CustomerId = customerId };
                if (_uploads.TryUpdate(entry.Key, promoted, current))
                {
                    break;
                }

                if (!_uploads.TryGetValue(entry.Key, out current))
                {
                    break;
                }
            }
        }
    }

    public UploadState TrackAgentUpload(
        string uploadId,
        Guid fileId,
        string fileName,
        string contentType,
        long fileSizeBytes,
        string storagePath,
        Guid sessionId,
        Guid? customerId,
        Guid? visitorId)
    {
        var state = new UploadState(
            uploadId,
            fileId,
            fileName,
            contentType,
            fileSizeBytes,
            storagePath,
            customerId,
            IsTemporary: !customerId.HasValue,
            VisitorId: visitorId,
            QuoteSessionId: sessionId.ToString("D"))
        {
            DownstreamUploadId = uploadId,
            ReceivedBytes = fileSizeBytes,
            Status = "Uploaded"
        };
        _uploads[uploadId] = state;
        return state;
    }

    public UploadState AttachDownstreamUpload(string uploadId, string downstreamUploadId)
    {
        var current = GetRequiredUpload(uploadId);
        var updated = current with { DownstreamUploadId = downstreamUploadId };
        _uploads[uploadId] = updated;
        return updated;
    }

    public UploadState? TryAdvanceUpload(string uploadId, long expectedReceivedBytes, long receivedBytes)
    {
        while (_uploads.TryGetValue(uploadId, out var current))
        {
            if (current.ReceivedBytes != expectedReceivedBytes ||
                receivedBytes <= expectedReceivedBytes ||
                receivedBytes > current.ExpectedSizeBytes)
            {
                return null;
            }

            var updated = current with
            {
                ReceivedBytes = receivedBytes,
                Status = receivedBytes == current.ExpectedSizeBytes ? "Uploaded" : "Uploading"
            };
            if (_uploads.TryUpdate(uploadId, updated, current))
            {
                return updated;
            }
        }

        return null;
    }

    /// <summary>Transitions an upload to "Processing" status (real pipeline path).</summary>
    public UploadState MarkProcessing(string uploadId, Guid? canonicalFileId = null)
    {
        var current = GetRequiredUpload(uploadId);
        if (canonicalFileId == Guid.Empty)
        {
            throw new ArgumentException("A canonical UploadService file identifier must not be empty.", nameof(canonicalFileId));
        }

        var updated = current with
        {
            FileId = canonicalFileId ?? current.FileId,
            Status = "Processing"
        };
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
            ViewerGlbUrl = "/models/sample.glb",
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
            var quantity = Math.Max(1, part.Quantity);
            var setupCost = setup * config.Multiplier * leadTime.PriceMultiplier;
            var variableUnitCost =
                ((Math.Max(part.VolumeCc, 1m) * baseRate * config.Multiplier) + config.Additive) *
                leadTime.PriceMultiplier;
            var lineTotal = Math.Round(setupCost + (variableUnitCost * quantity), 2);
            var unitPrice = QuoteEstimateMoney.DeriveDisplayUnitPrice(lineTotal, quantity);
            var notes = QuoteEstimateMoney.AppendRoundedDisplayUnitNote(
                config.Notes,
                lineTotal,
                quantity,
                "THB",
                lineTotalIsAuthoritative: false);
            return new QuoteLineEstimateDto(part.PartId, part.FileName, unitPrice, lineTotal, "THB", notes);
        }).ToArray();

        var subtotal = lines.Sum(x => x.LineTotal);
        var discount = subtotal >= 25_000m ? Math.Round(subtotal * 0.05m, 2) : 0m;
        return new QuoteEstimateResponse(request.QuoteSessionId, subtotal, discount, subtotal - discount, "THB", true, lines)
        {
            PricingSource = "prototype",
            IsAuthoritative = false
        };
    }

    public CreateDraftProjectResponse CreateDraftProject(Guid customerId, CreateDraftProjectRequest request)
    {
        var project = CreateProjectRecord(
            customerId,
            string.IsNullOrWhiteSpace(request.Title) ? "Untitled quote" : request.Title.Trim(),
            request.Parts,
            request.Notes);

        _projects[project.ProjectId] = project;
        return new CreateDraftProjectResponse(project.ProjectId, project.ProjectNumber, project.Status, project.Title, ClonePartsForResponse(project.Parts));
    }

    public DuplicateDraftProjectResponse? DuplicateDraftProject(Guid customerId, Guid projectId, DuplicateDraftProjectRequest request)
    {
        if (!_projects.TryGetValue(projectId, out var source) || source.CustomerId != customerId)
        {
            return null;
        }

        var title = string.IsNullOrWhiteSpace(request.Title)
            ? $"{source.Title} copy"
            : request.Title.Trim();
        var duplicated = CreateProjectRecord(customerId, title, ClonePartsForResponse(source.Parts), source.Notes);
        _projects[duplicated.ProjectId] = duplicated;
        return new DuplicateDraftProjectResponse(
            duplicated.ProjectId,
            duplicated.ProjectNumber,
            duplicated.Status,
            duplicated.Title,
            ClonePartsForResponse(duplicated.Parts));
    }

    internal CustomerProjectRecord? GetProject(Guid customerId, Guid projectId)
    {
        if (!_projects.TryGetValue(projectId, out var project) || project.CustomerId != customerId)
        {
            return null;
        }

        return project with
        {
            Parts = ClonePartsForResponse(project.Parts)
        };
    }

    internal ProjectManagementResponse? SetProjectPinned(Guid customerId, Guid projectId, bool isPinned)
    {
        return UpdateProject(customerId, projectId, project => project with
        {
            IsPinned = isPinned,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    internal ProjectManagementResponse? SetProjectArchived(Guid customerId, Guid projectId, bool isArchived)
    {
        return UpdateProject(customerId, projectId, project => project with
        {
            Status = isArchived ? "Archived" : "Draft",
            IsArchived = isArchived,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    internal ProjectManagementResponse? SetProjectAchieved(Guid customerId, Guid projectId)
    {
        return UpdateProject(customerId, projectId, project => project with
        {
            Status = "Achieved",
            IsArchived = true,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private ProjectManagementResponse? UpdateProject(
        Guid customerId,
        Guid projectId,
        Func<CustomerProjectRecord, CustomerProjectRecord> update)
    {
        if (!_projects.TryGetValue(projectId, out var project) || project.CustomerId != customerId)
        {
            return null;
        }

        var updated = update(project);
        _projects[projectId] = updated;
        return new ProjectManagementResponse(
            updated.ProjectId,
            updated.ProjectNumber,
            updated.Status,
            updated.Title,
            updated.IsPinned,
            updated.IsArchived);
    }

    private static CustomerProjectRecord CreateProjectRecord(
        Guid customerId,
        string title,
        IReadOnlyList<QuotePartDraftDto> parts,
        string notes)
    {
        var projectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new CustomerProjectRecord(
            customerId,
            projectId,
            $"QE-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}",
            "Draft",
            title,
            notes,
            ClonePartsForStorage(parts),
            IsPinned: false,
            IsArchived: false,
            now,
            now);
    }

    public GenerateFormalQuoteResponse GenerateQuote(Guid customerId)
    {
        var versionId = Guid.NewGuid();
        var pdfUrl = "/quote/v1/account/quotes/sample.pdf";
        var now = DateTimeOffset.UtcNow;
        var quote = new CustomerQuoteSummaryDto(
            Guid.NewGuid(),
            $"MQ-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}",
            "Ready for approval",
            12_500m,
            "THB",
            now,
            pdfUrl,
            [
                new CustomerQuoteVersionSummaryDto(
                    versionId,
                    1,
                    12_500m,
                    "THB",
                    "Initial customer quote",
                    pdfUrl,
                    null,
                    "Make Studio",
                    now)
            ]);
        _quotes[quote.QuoteId] = new CustomerQuoteRecord(customerId, quote);
        return new GenerateFormalQuoteResponse(quote.QuoteId, quote.QuoteNumber, quote.PdfUrl, quote.Status);
    }

    public CreateManufacturingOrderResponse CreateOrder(Guid customerId, Guid quoteId)
    {
        if (!_quotes.TryGetValue(quoteId, out var quote) || quote.CustomerId != customerId)
        {
            throw new KeyNotFoundException($"Quote '{quoteId}' was not found for the signed-in customer.");
        }

        var now = DateTimeOffset.UtcNow;
        var order = new CustomerOrderSummaryDto(
            Guid.NewGuid(),
            $"MO-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}",
            "Order received",
            now,
            "Waiting for production review");
        _orders[order.OrderId] = new CustomerOrderRecord(customerId, order);

        // Register an order-detail record so agent-created orders are drill-down-able and can be
        // marked paid by the dev prototype-checkout (consistent with the seeded demo orders).
        var detail = new CustomerOrderDetailDto(
            order.OrderId,
            order.OrderNumber,
            order.Status,
            "Pending",
            quote.Quote.Total,
            quote.Quote.Currency,
            now.AddDays(7),
            ActualDeliveryDate: null,
            CustomerPoNumber: null,
            Requirements: "Manufacturing order created from a Make Studio quote.",
            now,
            now,
            [new OrderStatusEntryDto("Order received", null, now)])
        {
            QuoteId = quoteId,
            QuoteNumber = quote.Quote.QuoteNumber
        };
        _orderDetailsByNumber[order.OrderNumber] = new CustomerOrderRecordDetail(customerId, detail);

        return new CreateManufacturingOrderResponse(order.OrderId, order.OrderNumber, order.Status);
    }

    public InitiatePaymentResponse StartPayment(Guid customerId, Guid orderId)
    {
        if (!_orders.TryGetValue(orderId, out var order) || order.CustomerId != customerId)
        {
            throw new KeyNotFoundException($"Order '{orderId}' was not found for the signed-in customer.");
        }

        return new InitiatePaymentResponse(
            Guid.NewGuid(),
            $"/quote/v1/payments/prototype-checkout?orderId={orderId:D}",
            "pending");
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

        if (!string.IsNullOrWhiteSpace(part.PartNotes))
        {
            notes.Add("customer notes supplied");
        }

        return (multiplier, additive, string.Join("; ", notes) + ".");
    }

    private static IReadOnlyList<QuotePartDraftDto> ClonePartsForStorage(IReadOnlyList<QuotePartDraftDto> parts)
    {
        return parts.Select(part => ClonePart(part, Guid.NewGuid())).ToArray();
    }

    private static string NormalizeSearchQuery(string? query)
    {
        return string.IsNullOrWhiteSpace(query) ? string.Empty : query.Trim();
    }

    private static void AddIfMatches(
        List<QuoteAgentSearchResultDto> results,
        string query,
        QuoteAgentSearchResultDto result)
    {
        if (string.IsNullOrWhiteSpace(query) || MatchesSearch(result, query))
        {
            results.Add(result);
        }
    }

    private static bool MatchesSearch(QuoteAgentSearchResultDto result, string query)
    {
        return Contains(result.ResourceType, query) ||
            Contains(result.ResourceId, query) ||
            Contains(result.Title, query) ||
            Contains(result.Detail, query) ||
            result.Metadata.Values.Any(value => Contains(value, query));
    }

    private static bool Contains(string? value, string query)
    {
        return value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static int SearchRank(string resourceType)
    {
        return resourceType.ToLowerInvariant() switch
        {
            "project" => 0,
            "quote" => 1,
            "order" => 2,
            "document" => 3,
            "artifact" => 4,
            _ => 9
        };
    }

    private static IReadOnlyList<QuotePartDraftDto> ClonePartsForResponse(IReadOnlyList<QuotePartDraftDto> parts)
    {
        return parts.Select(part => ClonePart(part, part.PartId)).ToArray();
    }

    private static QuotePartDraftDto ClonePart(QuotePartDraftDto source, Guid partId)
    {
        return new QuotePartDraftDto
        {
            PartId = partId,
            FileId = source.FileId,
            UploadId = source.UploadId,
            FileName = source.FileName,
            ProcessId = source.ProcessId,
            MaterialId = source.MaterialId,
            FinishId = source.FinishId,
            FinishCode = source.FinishCode,
            ToleranceId = source.ToleranceId,
            ToleranceCode = source.ToleranceCode,
            InspectionLevel = source.InspectionLevel,
            RoughnessCode = source.RoughnessCode,
            Color = source.Color,
            ProcessOptionValues = new(source.ProcessOptionValues, StringComparer.OrdinalIgnoreCase),
            HasThreadedHoles = source.HasThreadedHoles,
            ThreadSpecification = source.ThreadSpecification,
            ThreadedHoleCount = source.ThreadedHoleCount,
            InsertType = source.InsertType,
            InsertCount = source.InsertCount,
            Quantity = source.Quantity,
            VolumeCc = source.VolumeCc,
            SurfaceAreaCm2 = source.SurfaceAreaCm2,
            BoundingBoxMm = source.BoundingBoxMm,
            StoragePath = source.StoragePath,
            Status = source.Status,
            ViewerGlbUrl = source.ViewerGlbUrl,
            ViewerStoragePath = source.ViewerStoragePath,
            ViewerFileExtension = source.ViewerFileExtension,
            ThumbnailUrl = source.ThumbnailUrl,
            Findings = source.Findings.Select(finding => finding with { }).ToArray(),
            IsManifold = source.IsManifold,
            NonManifoldReason = source.NonManifoldReason,
            FdmReport = CloneReport(source.FdmReport),
            SlaReport = CloneReport(source.SlaReport),
            CncReport = CloneReport(source.CncReport),
            OverlayGlbUrls = source.OverlayGlbUrls.ToArray(),
            DfmAcknowledged = source.DfmAcknowledged,
            PartNotes = source.PartNotes,
            BodyCount = source.BodyCount,
            SelectedBodyIndex = source.SelectedBodyIndex,
            DrawingFiles = source.DrawingFiles.Select(file => file with { }).ToList(),
            ViewerSettings = source.ViewerSettings with { }
        };
    }

    private static QeFdmDfmReport? CloneReport(QeFdmDfmReport? source)
        => source is null ? null : source with { Issues = CloneIssues(source.Issues) };

    private static QeSlaDfmReport? CloneReport(QeSlaDfmReport? source)
        => source is null ? null : source with { Issues = CloneIssues(source.Issues) };

    private static QeCncDfmReport? CloneReport(QeCncDfmReport? source)
        => source is null ? null : source with { Issues = CloneIssues(source.Issues) };

    private static IReadOnlyList<QeDfmIssueItem> CloneIssues(IReadOnlyList<QeDfmIssueItem> issues)
        => issues.Select(issue => issue with { }).ToArray();

    private static CustomerAddressDto MapAddressRequest(CustomerAddressUpsertRequest request, Guid resolvedCountryId)
    {
        return new CustomerAddressDto
        {
            Type = string.IsNullOrWhiteSpace(request.Type) ? "Shipping" : request.Type,
            IsDefault = request.IsDefault,
            PlaceLabel = request.PlaceLabel,
            PlaceLabelOther = request.PlaceLabelOther,
            AddressLine1 = request.AddressLine1,
            AddressLine2 = request.AddressLine2,
            AddressLine3 = request.AddressLine3,
            District = request.District,
            City = request.City,
            StateProvince = request.StateProvince,
            PostalCode = request.PostalCode,
            CountryId = resolvedCountryId == Guid.Empty ? request.CountryId : resolvedCountryId,
            RecipientName = request.RecipientName,
            RecipientPhone = request.RecipientPhone,
            DriverNote = request.DriverNote,
            AddressSource = string.IsNullOrWhiteSpace(request.AddressSource) ? "Manual" : request.AddressSource,
            GooglePlaceId = request.GooglePlaceId,
            FormattedAddress = request.FormattedAddress,
            Latitude = request.Latitude,
            Longitude = request.Longitude
        };
    }

    private static CustomerAddressDto CloneAddress(CustomerAddressDto source)
    {
        return new CustomerAddressDto
        {
            Id = source.Id,
            Type = source.Type,
            IsDefault = source.IsDefault,
            PlaceLabel = source.PlaceLabel,
            PlaceLabelOther = source.PlaceLabelOther,
            AddressLine1 = source.AddressLine1,
            AddressLine2 = source.AddressLine2,
            AddressLine3 = source.AddressLine3,
            District = source.District,
            City = source.City,
            StateProvince = source.StateProvince,
            PostalCode = source.PostalCode,
            CountryId = source.CountryId,
            RecipientName = source.RecipientName,
            RecipientPhone = source.RecipientPhone,
            DriverNote = source.DriverNote,
            AddressSource = source.AddressSource,
            GooglePlaceId = source.GooglePlaceId,
            FormattedAddress = source.FormattedAddress,
            Latitude = source.Latitude,
            Longitude = source.Longitude,
            Version = source.Version
        };
    }

    private static List<CustomerAddressDto> CreateDefaultAddresses()
    {
        var countryId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        return
        [
            new CustomerAddressDto
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Type = "Billing",
                IsDefault = true,
                AddressLine1 = "12 Billing Road",
                City = "Bangkok",
                StateProvince = "Bangkok",
                PostalCode = "10110",
                CountryId = countryId,
                RecipientName = "MALIEV Buyer",
                RecipientPhone = "+66 2 555 0100",
                AddressSource = "Prototype",
                FormattedAddress = "12 Billing Road, Bangkok 10110"
            },
            new CustomerAddressDto
            {
                Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                Type = "Shipping",
                IsDefault = true,
                AddressLine1 = "34 Shipping Road",
                City = "Bangkok",
                StateProvince = "Bangkok",
                PostalCode = "10110",
                CountryId = countryId,
                RecipientName = "MALIEV Receiving",
                RecipientPhone = "+66 2 555 0100",
                AddressSource = "Prototype",
                FormattedAddress = "34 Shipping Road, Bangkok 10110"
            }
        ];
    }

    private static void ResetSameRoleDefault(List<CustomerAddressDto> addresses, CustomerAddressDto current)
    {
        foreach (var address in addresses.Where(item => item.Id != current.Id && SameAddressType(item, current.Type)))
        {
            address.IsDefault = false;
        }
    }

    private static bool SameAddressType(CustomerAddressDto address, string? type)
    {
        return address.Type.Equals(string.IsNullOrWhiteSpace(type) ? "Shipping" : type, StringComparison.OrdinalIgnoreCase);
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

    private static string? NormalizeViewerFileExtension(string? storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            return null;
        }

        var extension = Path.GetExtension(storagePath.Trim());
        return string.IsNullOrWhiteSpace(extension) ? null : extension.ToLowerInvariant();
    }
}

internal sealed record CustomerQuoteRecord(Guid CustomerId, CustomerQuoteSummaryDto Quote);

internal sealed record CustomerOrderRecord(Guid CustomerId, CustomerOrderSummaryDto Order);

internal sealed record CustomerOrderRecordDetail(Guid CustomerId, CustomerOrderDetailDto Detail);

internal sealed record CustomerProjectRecord(
    Guid CustomerId,
    Guid ProjectId,
    string ProjectNumber,
    string Status,
    string Title,
    string Notes,
    IReadOnlyList<QuotePartDraftDto> Parts,
    bool IsPinned,
    bool IsArchived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record UploadState(
    string UploadId,
    Guid FileId,
    string FileName,
    string ContentType,
    long ExpectedSizeBytes,
    string StoragePath,
    Guid? CustomerId,
    bool IsTemporary,
    Guid? VisitorId = null,
    string? QuoteSessionId = null)
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
        IsAuthoritative = false,
        AnalysisSource = Status is "Analyzed" or "DfmAnalysisReady" ? "prototype_demo" : "pending",
        VolumeCc = VolumeCc,
        SurfaceAreaCm2 = SurfaceAreaCm2,
        ViewerGlbUrl = ViewerGlbUrl,
        ViewerFileExtension = string.IsNullOrWhiteSpace(ViewerGlbUrl) ? null : ".glb",
        ThumbnailUrl = ThumbnailUrl,
        Findings = Findings
    };
}
