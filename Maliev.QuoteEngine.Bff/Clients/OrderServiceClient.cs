// Maliev.QuoteEngine.Bff/Clients/OrderServiceClient.cs
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using Maliev.QuoteEngine.Shared.Account;

namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>
/// Calls OrderService to create manufacturing orders and retrieve order history.
/// </summary>
public interface IOrderServiceClient
{
    /// <summary>Creates a new manufacturing order.</summary>
    Task<OrderCreatedResult?> CreateAsync(OrderCreateRequest request, CancellationToken ct = default);

    /// <summary>Returns order summaries for the given customer.</summary>
    Task<IReadOnlyList<CustomerOrderSummaryDto>> GetByCustomerAsync(string customerId, CancellationToken ct = default);

    /// <summary>Returns full order detail including status timeline, or null if not found.</summary>
    Task<CustomerOrderDetailDto?> GetDetailAsync(string orderNumber, CancellationToken ct = default);

    /// <summary>Returns order detail only when OrderService confirms the expected customer owns it.</summary>
    Task<CustomerOrderDetailDto?> GetDetailForCustomerAsync(
        string orderNumber,
        string customerId,
        CancellationToken ct = default) =>
        Task.FromResult<CustomerOrderDetailDto?>(null);

    /// <summary>Returns all customer orders while preserving downstream availability.</summary>
    async Task<DownstreamLookupResult<IReadOnlyList<CustomerOrderSummaryDto>>> LookupByCustomerAsync(
        string customerId,
        CancellationToken ct = default) =>
        new(true, await GetByCustomerAsync(customerId, ct));

    /// <summary>Returns customer-owned order detail while preserving downstream availability.</summary>
    async Task<DownstreamLookupResult<CustomerOrderDetailDto?>> LookupDetailForCustomerAsync(
        string orderNumber,
        string customerId,
        CancellationToken ct = default) =>
        new(true, await GetDetailForCustomerAsync(orderNumber, customerId, ct));

    /// <summary>Appends a status entry to an order. Returns false on non-success (caller logs and continues).</summary>
    Task<bool> AddStatusAsync(string orderId, string status, CancellationToken ct = default);

    /// <summary>Persists the checkout delivery snapshot on an existing order.</summary>
    Task<bool> UpdateDeliverySnapshotAsync(OrderDeliverySnapshotRequest request, CancellationToken ct = default);
}

internal sealed class OrderServiceClient(HttpClient http, ILogger<OrderServiceClient> logger) : IOrderServiceClient
{
    // ── Internal response shapes (match OrderService JSON contract) ──────────

    private sealed class OsOrderResponse
    {
        public string OrderId { get; set; } = string.Empty;
        public string CustomerId { get; set; } = string.Empty;
        public string? CurrentStatus { get; set; }
        public DateTime UpdatedAt { get; set; }
        public decimal? QuotedAmount { get; set; }
        public string? QuoteCurrency { get; set; }
        public Guid? QuoteId { get; set; }
        public string? QuoteNumber { get; set; }
    }

    private sealed class OsPaginatedResponse
    {
        public IEnumerable<OsOrderResponse>? Items { get; set; }
        public int TotalPages { get; set; }
    }

    private sealed class OsOrderDetailResponse
    {
        public string OrderId { get; set; } = string.Empty;
        public string CustomerId { get; set; } = string.Empty;
        public string? CurrentStatus { get; set; }
        public string PaymentStatus { get; set; } = "Unpaid";
        public decimal? QuotedAmount { get; set; }
        public string? QuoteCurrency { get; set; }
        public Guid? QuoteId { get; set; }
        public string? QuoteNumber { get; set; }
        public Guid? QuoteVersionId { get; set; }
        public int? QuoteVersionNumber { get; set; }
        public DateTime? PromisedDeliveryDate { get; set; }
        public DateTime? ActualDeliveryDate { get; set; }
        public string? CustomerPoNumber { get; set; }
        public string? Requirements { get; set; }
        public string Version { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private sealed class OsOrderStatusEntry
    {
        public string Status { get; set; } = string.Empty;
        public string? CustomerNotes { get; set; }
        public DateTime Timestamp { get; set; }
    }

    private sealed class OsOrderFileResponse
    {
        public long FileId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string FileRole { get; set; } = string.Empty;
        public string FileCategory { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string FileType { get; set; } = "application/octet-stream";
        public string ObjectPath { get; set; } = string.Empty;
        public DateTime UploadedAt { get; set; }
    }

    private sealed class OsOrderLineItemResponse
    {
        public Guid OrderItemId { get; set; }
        public Guid? SourceProjectPartId { get; set; }
        public Guid MaterialId { get; set; }
        public string MaterialSnapshotJson { get; set; } = string.Empty;
        public string ConfigurationSnapshotJson { get; set; } = string.Empty;
        public string Technology { get; set; } = string.Empty;
        public decimal VolumeCm3 { get; set; }
        public int Quantity { get; set; } = 1;
    }

    private static CustomerOrderSummaryDto MapSummary(OsOrderResponse r) => new(
        DeterministicGuid(r.OrderId),
        r.OrderId,
        r.CurrentStatus ?? "Pending",
        new DateTimeOffset(r.UpdatedAt, TimeSpan.Zero),
        r.OrderId)
    {
        QuoteId = r.QuoteId,
        QuoteNumber = r.QuoteNumber
    };

    private static Guid DeterministicGuid(string value)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash);
    }

    // ── Interface implementation ──────────────────────────────────────────────

    public async Task<OrderCreatedResult?> CreateAsync(OrderCreateRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("/order/v1/orders", new
            {
                customerId = request.CustomerId,
                customerType = "Customer",
                serviceCategoryId = request.ServiceCategoryId,
                processTypeId = (int?)request.ProcessTypeId,
                orderedQuantity = request.OrderedQuantity,
                customerPoNumber = request.CustomerPoNumber,
                requirements = request.Requirements,
                quotedAmount = request.QuotedAmount,
                quoteCurrency = request.QuoteCurrency,
                quoteId = request.QuoteId,
                quoteNumber = request.QuoteNumber,
                quoteVersionId = request.QuoteVersionId,
                quoteVersionNumber = request.QuoteVersionNumber,
                productionItems = request.ProductionItems
            }, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("OrderService returned {Status} on create: {Body}", response.StatusCode, body);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<OsOrderResponse>(cancellationToken: ct);
            if (result is null) return null;

            return new OrderCreatedResult
            {
                OrderId = DeterministicGuid(result.OrderId),
                OrderNumber = result.OrderId,
                Status = result.CurrentStatus ?? "Pending"
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OrderService create failed.");
            return null;
        }
    }

    public async Task<IReadOnlyList<CustomerOrderSummaryDto>> GetByCustomerAsync(
        string customerId,
        CancellationToken ct = default) =>
        (await LookupByCustomerAsync(customerId, ct)).Value;

    public async Task<DownstreamLookupResult<IReadOnlyList<CustomerOrderSummaryDto>>> LookupByCustomerAsync(
        string customerId,
        CancellationToken ct = default)
    {
        try
        {
            var mapped = new List<CustomerOrderSummaryDto>();
            var totalPages = 1;
            for (var pageNumber = 1; pageNumber <= totalPages; pageNumber++)
            {
                using var response = await http.GetAsync(
                    $"/order/v1/orders?customerId={Uri.EscapeDataString(customerId)}&page={pageNumber}&pageSize=100",
                    ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning(
                        "OrderService returned {Status} while listing customer {CustomerId} page {Page}.",
                        response.StatusCode,
                        customerId,
                        pageNumber);
                    return new DownstreamLookupResult<IReadOnlyList<CustomerOrderSummaryDto>>(false, []);
                }

                var paged = await response.Content.ReadFromJsonAsync<OsPaginatedResponse>(cancellationToken: ct);
                if (paged is null)
                {
                    return new DownstreamLookupResult<IReadOnlyList<CustomerOrderSummaryDto>>(false, []);
                }

                if (pageNumber == 1)
                {
                    totalPages = Math.Clamp(Math.Max(1, paged.TotalPages), 1, 1000);
                }

                mapped.AddRange((paged.Items ?? [])
                    .Where(order => customerId.Equals(order.CustomerId, StringComparison.OrdinalIgnoreCase))
                    .Select(MapSummary));
            }

            var orders = mapped
                .GroupBy(order => order.OrderNumber, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(order => order.UpdatedAt).First())
                .ToArray();
            return new DownstreamLookupResult<IReadOnlyList<CustomerOrderSummaryDto>>(true, orders);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OrderService GetByCustomer failed for {CustomerId}.", customerId);
            return new DownstreamLookupResult<IReadOnlyList<CustomerOrderSummaryDto>>(false, []);
        }
    }

    public Task<CustomerOrderDetailDto?> GetDetailAsync(string orderNumber, CancellationToken ct = default) =>
        GetDetailValueAsync(orderNumber, expectedCustomerId: null, ct);

    public async Task<CustomerOrderDetailDto?> GetDetailForCustomerAsync(
        string orderNumber,
        string customerId,
        CancellationToken ct = default) =>
        (await LookupDetailForCustomerAsync(orderNumber, customerId, ct)).Value;

    public Task<DownstreamLookupResult<CustomerOrderDetailDto?>> LookupDetailForCustomerAsync(
        string orderNumber,
        string customerId,
        CancellationToken ct = default) =>
        LookupDetailCoreAsync(orderNumber, customerId, ct);

    private async Task<CustomerOrderDetailDto?> GetDetailValueAsync(
        string orderNumber,
        string? expectedCustomerId,
        CancellationToken ct) =>
        (await LookupDetailCoreAsync(orderNumber, expectedCustomerId, ct)).Value;

    private async Task<DownstreamLookupResult<CustomerOrderDetailDto?>> LookupDetailCoreAsync(
        string orderNumber,
        string? expectedCustomerId,
        CancellationToken ct)
    {
        try
        {
            // Fetch order detail, status history, files, and production items in parallel.
            var detailTask = http.GetAsync($"/order/v1/orders/{Uri.EscapeDataString(orderNumber)}", ct);
            var statusTask = http.GetAsync($"/order/v1/orders/{Uri.EscapeDataString(orderNumber)}/statuses", ct);
            var filesTask = http.GetAsync($"/order/v1/orders/{Uri.EscapeDataString(orderNumber)}/files", ct);
            var itemsTask = http.GetAsync($"/order/v1/orders/{Uri.EscapeDataString(orderNumber)}/items", ct);

            await Task.WhenAll(detailTask, statusTask, filesTask, itemsTask);

            using var detailResponse = detailTask.Result;
            using var statusResponse = statusTask.Result;
            using var filesResponse = filesTask.Result;
            using var itemsResponse = itemsTask.Result;

            if (detailResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return new DownstreamLookupResult<CustomerOrderDetailDto?>(true, null);
            }
            if (!detailResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("OrderService GetDetail returned {Status} for {OrderNumber}.", detailResponse.StatusCode, orderNumber);
                return new DownstreamLookupResult<CustomerOrderDetailDto?>(false, null);
            }

            var detail = await detailResponse.Content.ReadFromJsonAsync<OsOrderDetailResponse>(cancellationToken: ct);
            if (detail is null)
            {
                return new DownstreamLookupResult<CustomerOrderDetailDto?>(false, null);
            }
            if (!string.IsNullOrWhiteSpace(expectedCustomerId) &&
                !expectedCustomerId.Equals(detail.CustomerId, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "OrderService GetDetail ownership mismatch for {OrderNumber}: expected customer {ExpectedCustomerId}.",
                    orderNumber,
                    expectedCustomerId);
                return new DownstreamLookupResult<CustomerOrderDetailDto?>(true, null);
            }

            IReadOnlyList<OsOrderStatusEntry> statusEntries = [];
            if (statusResponse.IsSuccessStatusCode)
            {
                var entries = await statusResponse.Content.ReadFromJsonAsync<List<OsOrderStatusEntry>>(cancellationToken: ct);
                statusEntries = entries?.AsReadOnly() ?? (IReadOnlyList<OsOrderStatusEntry>)[];
            }

            var customerStatusEntries = statusEntries
                .Select(s => new OrderStatusEntryDto(
                    s.Status,
                    s.CustomerNotes,
                    new DateTimeOffset(s.Timestamp, TimeSpan.Zero)))
                .ToArray();
            var orderFiles = Array.Empty<CustomerOrderFileDto>();
            if (filesResponse.IsSuccessStatusCode)
            {
                var files = await filesResponse.Content.ReadFromJsonAsync<List<OsOrderFileResponse>>(cancellationToken: ct);
                orderFiles = files?
                    .Where(file => string.IsNullOrWhiteSpace(file.ObjectPath) || file.ObjectPath.Contains("://", StringComparison.Ordinal) is false)
                    .Select(MapOrderFile)
                    .ToArray() ?? [];
            }
            else if (filesResponse.StatusCode != HttpStatusCode.NotFound)
            {
                logger.LogWarning("OrderService GetFiles returned {Status} for {OrderNumber}.", filesResponse.StatusCode, orderNumber);
            }

            var shippingParts = Array.Empty<CustomerOrderShippingPartDto>();
            if (itemsResponse.IsSuccessStatusCode)
            {
                var items = await itemsResponse.Content.ReadFromJsonAsync<List<OsOrderLineItemResponse>>(cancellationToken: ct);
                shippingParts = BuildShippingParts(items ?? []);
            }
            else if (itemsResponse.StatusCode != HttpStatusCode.NotFound)
            {
                logger.LogWarning("OrderService GetItems returned {Status} for {OrderNumber}.", itemsResponse.StatusCode, orderNumber);
            }

            var mappedDetail = new CustomerOrderDetailDto(
                OrderId: DeterministicGuid(detail.OrderId),
                OrderNumber: detail.OrderId,
                CurrentStatus: detail.CurrentStatus ?? "Pending",
                PaymentStatus: detail.PaymentStatus,
                QuotedAmount: detail.QuotedAmount,
                QuoteCurrency: detail.QuoteCurrency,
                PromisedDeliveryDate: detail.PromisedDeliveryDate.HasValue
                    ? new DateTimeOffset(detail.PromisedDeliveryDate.Value, TimeSpan.Zero)
                    : null,
                ActualDeliveryDate: detail.ActualDeliveryDate.HasValue
                    ? new DateTimeOffset(detail.ActualDeliveryDate.Value, TimeSpan.Zero)
                    : null,
                CustomerPoNumber: detail.CustomerPoNumber,
                Requirements: detail.Requirements,
                CreatedAt: new DateTimeOffset(detail.CreatedAt, TimeSpan.Zero),
                UpdatedAt: new DateTimeOffset(detail.UpdatedAt, TimeSpan.Zero),
                StatusHistory: customerStatusEntries)
            {
                QuoteId = detail.QuoteId,
                QuoteNumber = detail.QuoteNumber,
                QuoteVersionId = detail.QuoteVersionId,
                QuoteVersionNumber = detail.QuoteVersionNumber,
                OrderFiles = orderFiles,
                ShippingParts = shippingParts,
                ManufacturingMilestones = BuildCustomerManufacturingMilestones(
                    detail.CurrentStatus ?? "Pending",
                    detail.PaymentStatus,
                    detail.PromisedDeliveryDate.HasValue
                        ? new DateTimeOffset(detail.PromisedDeliveryDate.Value, TimeSpan.Zero)
                        : null,
                    detail.ActualDeliveryDate.HasValue
                        ? new DateTimeOffset(detail.ActualDeliveryDate.Value, TimeSpan.Zero)
                        : null,
                    customerStatusEntries)
            };
            var auxiliaryReadsAvailable =
                statusResponse.IsSuccessStatusCode || statusResponse.StatusCode == HttpStatusCode.NotFound;
            auxiliaryReadsAvailable &=
                filesResponse.IsSuccessStatusCode || filesResponse.StatusCode == HttpStatusCode.NotFound;
            auxiliaryReadsAvailable &=
                itemsResponse.IsSuccessStatusCode || itemsResponse.StatusCode == HttpStatusCode.NotFound;
            return new DownstreamLookupResult<CustomerOrderDetailDto?>(auxiliaryReadsAvailable, mappedDetail);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OrderService GetDetail failed for {OrderNumber}.", orderNumber);
            return new DownstreamLookupResult<CustomerOrderDetailDto?>(false, null);
        }
    }

    private static CustomerOrderFileDto MapOrderFile(OsOrderFileResponse file) => new(
        file.FileId,
        file.FileName,
        file.FileRole,
        file.FileCategory,
        file.ObjectPath,
        file.FileType,
        file.FileSize,
        new DateTimeOffset(file.UploadedAt, TimeSpan.Zero));

    private static CustomerOrderShippingPartDto[] BuildShippingParts(IReadOnlyList<OsOrderLineItemResponse> items)
    {
        return items
            .Select(MapShippingPart)
            .Where(part => part is not null)
            .Select(part => part!)
            .ToArray();
    }

    private static CustomerOrderShippingPartDto? MapShippingPart(OsOrderLineItemResponse item)
    {
        using var configuration = TryParseJson(item.ConfigurationSnapshotJson);
        if (configuration is null || !TryReadBoundingBoxCm(configuration.RootElement, out var widthCm, out var lengthCm, out var heightCm))
        {
            return null;
        }

        using var material = TryParseJson(item.MaterialSnapshotJson);
        var materialId = FirstNonEmpty(
            TryReadString(configuration.RootElement, "materialId"),
            material is null ? null : TryReadString(material.RootElement, "sourceMaterialId"),
            item.MaterialId == Guid.Empty ? null : item.MaterialId.ToString("D"));
        var processId = FirstNonEmpty(
            TryReadString(configuration.RootElement, "processId"),
            item.Technology);
        var volumeCc = TryReadDecimal(configuration.RootElement, "volumeCc") ?? item.VolumeCm3;
        var quantity = TryReadInt(configuration.RootElement, "quantity") ?? item.Quantity;

        return new CustomerOrderShippingPartDto
        {
            PartId = TryReadGuid(configuration.RootElement, "partId") ?? item.SourceProjectPartId ?? item.OrderItemId,
            FileName = FirstNonEmpty(TryReadString(configuration.RootElement, "fileName"), "Manufactured part"),
            ProcessId = processId,
            MaterialId = materialId,
            Quantity = Math.Max(1, quantity),
            VolumeCc = Math.Max(0m, volumeCc),
            WeightGrams = EstimatePartWeightGrams(volumeCc, materialId, processId),
            WidthCm = widthCm,
            LengthCm = lengthCm,
            HeightCm = heightCm
        };
    }

    private static JsonDocument? TryParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadBoundingBoxCm(JsonElement root, out decimal widthCm, out decimal lengthCm, out decimal heightCm)
    {
        widthCm = 0m;
        lengthCm = 0m;
        heightCm = 0m;

        if (!TryGetProperty(root, "boundingBoxMm", out var box) || box.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var xMm = TryReadDecimal(box, "x") ?? TryReadDecimal(box, "X");
        var yMm = TryReadDecimal(box, "y") ?? TryReadDecimal(box, "Y");
        var zMm = TryReadDecimal(box, "z") ?? TryReadDecimal(box, "Z");
        if (xMm is not > 0 || yMm is not > 0 || zMm is not > 0)
        {
            return false;
        }

        widthCm = Math.Round(xMm.Value / 10m, 2);
        lengthCm = Math.Round(yMm.Value / 10m, 2);
        heightCm = Math.Round(zMm.Value / 10m, 2);
        return true;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string? TryReadString(JsonElement root, string name)
    {
        return TryGetProperty(root, name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? TryReadInt(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static Guid? TryReadGuid(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String when Guid.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static decimal? TryReadDecimal(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDecimal(out var value) => value,
            JsonValueKind.String when decimal.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static decimal EstimatePartWeightGrams(decimal volumeCc, string? materialId, string? processId)
    {
        var density = ResolveDensityGramsPerCc(materialId, processId);
        return Math.Max(1m, Math.Round(Math.Max(volumeCc, 0.1m) * density, 1));
    }

    private static decimal ResolveDensityGramsPerCc(string? materialId, string? processId)
    {
        var material = materialId ?? string.Empty;
        if (material.Contains("al", StringComparison.OrdinalIgnoreCase) ||
            material.Contains("aluminum", StringComparison.OrdinalIgnoreCase) ||
            material.Contains("aluminium", StringComparison.OrdinalIgnoreCase))
        {
            return 2.70m;
        }

        if (material.Contains("steel", StringComparison.OrdinalIgnoreCase) ||
            material.Contains("stainless", StringComparison.OrdinalIgnoreCase))
        {
            return 7.85m;
        }

        if (material.Contains("brass", StringComparison.OrdinalIgnoreCase))
        {
            return 8.50m;
        }

        if (material.Contains("copper", StringComparison.OrdinalIgnoreCase))
        {
            return 8.96m;
        }

        if (material.Contains("petg", StringComparison.OrdinalIgnoreCase))
        {
            return 1.27m;
        }

        if (material.Contains("abs", StringComparison.OrdinalIgnoreCase))
        {
            return 1.04m;
        }

        if (material.Contains("tpu", StringComparison.OrdinalIgnoreCase))
        {
            return 1.20m;
        }

        if (material.Contains("nylon", StringComparison.OrdinalIgnoreCase) ||
            material.Contains("pa12", StringComparison.OrdinalIgnoreCase))
        {
            return 1.01m;
        }

        if (material.Contains("resin", StringComparison.OrdinalIgnoreCase) ||
            (processId ?? string.Empty).Contains("sla", StringComparison.OrdinalIgnoreCase))
        {
            return 1.12m;
        }

        return (processId ?? string.Empty).Contains("cnc", StringComparison.OrdinalIgnoreCase) ? 2.70m : 1.24m;
    }

    public async Task<bool> AddStatusAsync(string orderId, string status, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                $"/order/v1/orders/{Uri.EscapeDataString(orderId)}/statuses",
                new { status },
                ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning(
                    "OrderService AddStatus {Status} returned {StatusCode} for {OrderId}: {Body}",
                    status, response.StatusCode, orderId, body);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OrderService AddStatus {Status} failed for {OrderId}.", status, orderId);
            return false;
        }
    }

    public async Task<bool> UpdateDeliverySnapshotAsync(OrderDeliverySnapshotRequest request, CancellationToken ct = default)
    {
        try
        {
            using var detailResponse = await http.GetAsync($"/order/v1/orders/{Uri.EscapeDataString(request.OrderNumber)}", ct);
            if (!detailResponse.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "OrderService GetDetail before delivery snapshot update returned {Status} for {OrderNumber}.",
                    detailResponse.StatusCode,
                    request.OrderNumber);
                return false;
            }

            var detail = await detailResponse.Content.ReadFromJsonAsync<OsOrderDetailResponse>(cancellationToken: ct);
            if (detail is null || string.IsNullOrWhiteSpace(detail.Version))
            {
                logger.LogWarning(
                    "OrderService GetDetail returned no concurrency version for delivery snapshot update on {OrderNumber}.",
                    request.OrderNumber);
                return false;
            }

            using var updateResponse = await http.PutAsJsonAsync(
                $"/order/v1/orders/{Uri.EscapeDataString(request.OrderNumber)}",
                new
                {
                    version = detail.Version,
                    billingAddressId = request.BillingAddressId,
                    shippingAddressId = request.ShippingAddressId,
                    shippingAddressLine1 = request.ShippingAddressLine1,
                    shippingAddressLine2 = request.ShippingAddressLine2,
                    shippingCity = request.ShippingCity,
                    shippingProvince = request.ShippingProvince,
                    shippingPostalCode = request.ShippingPostalCode,
                    shippingCountry = request.ShippingCountry,
                    billingCompanyName = request.BillingCompanyName,
                    billingVatNumber = request.BillingVatNumber,
                    deliveryContactName = request.DeliveryContactName,
                    deliveryContactPhone = request.DeliveryContactPhone,
                    deliveryContactEmail = request.DeliveryContactEmail
                },
                ct);

            if (updateResponse.IsSuccessStatusCode)
            {
                return true;
            }

            var body = await updateResponse.Content.ReadAsStringAsync(ct);
            logger.LogWarning(
                "OrderService delivery snapshot update returned {Status} for {OrderNumber}: {Body}",
                updateResponse.StatusCode,
                request.OrderNumber,
                body);
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OrderService delivery snapshot update failed for {OrderNumber}.", request.OrderNumber);
            return false;
        }
    }

    private static IReadOnlyList<CustomerManufacturingMilestoneDto> BuildCustomerManufacturingMilestones(
        string currentStatus,
        string paymentStatus,
        DateTimeOffset? promisedDeliveryDate,
        DateTimeOffset? actualDeliveryDate,
        IReadOnlyList<OrderStatusEntryDto> statusHistory)
    {
        var normalizedStatus = currentStatus.ToLowerInvariant();
        var paid = paymentStatus.Contains("paid", StringComparison.OrdinalIgnoreCase);
        var delivered = actualDeliveryDate.HasValue || normalizedStatus.Contains("delivered", StringComparison.Ordinal);
        var shipped = delivered || normalizedStatus.Contains("shipped", StringComparison.Ordinal);
        var inspected = shipped ||
            normalizedStatus.Contains("qualityreleased", StringComparison.Ordinal) ||
            normalizedStatus.Contains("quality", StringComparison.Ordinal) ||
            normalizedStatus.Contains("inspection", StringComparison.Ordinal);
        var manufacturingComplete = inspected ||
            normalizedStatus.Contains("finished", StringComparison.Ordinal) ||
            normalizedStatus.Contains("completed", StringComparison.Ordinal);
        var manufacturingStarted = manufacturingComplete ||
            normalizedStatus.Contains("inprogress", StringComparison.Ordinal) ||
            normalizedStatus.Contains("manufacturing", StringComparison.Ordinal) ||
            normalizedStatus.Contains("production", StringComparison.Ordinal);
        var accepted = manufacturingStarted ||
            paid ||
            normalizedStatus.Contains("accepted", StringComparison.Ordinal) ||
            normalizedStatus.Contains("quoted", StringComparison.Ordinal) ||
            normalizedStatus.Contains("reviewed", StringComparison.Ordinal);

        var milestones = new[]
        {
            CreateMilestone(
                "order-received",
                "Order received",
                "We have received the order and attached customer requirements.",
                15,
                true,
                !accepted && !manufacturingStarted && !inspected && !shipped && !delivered,
                FindStatusTimestamp(statusHistory, "pending", "new", "received")),
            CreateMilestone(
                "quote-payment",
                "Quote and payment",
                "Formal quote and payment confirmation are tracked before production starts.",
                35,
                accepted,
                !accepted,
                FindStatusTimestamp(statusHistory, "accepted", "paid", "quoted", "reviewed")),
            CreateMilestone(
                "manufacturing",
                "Manufacturing",
                "The parts are queued or active on the selected manufacturing process.",
                55,
                manufacturingComplete,
                accepted && !manufacturingComplete,
                FindStatusTimestamp(statusHistory, "inprogress", "finished", "manufacturing", "production")),
            CreateMilestone(
                "quality-inspection",
                "Quality inspection",
                "Finished parts move through quality review before delivery handoff.",
                75,
                inspected,
                manufacturingComplete && !inspected,
                FindStatusTimestamp(statusHistory, "qualityreleased", "quality", "inspection")),
            CreateMilestone(
                "delivery",
                "Delivery",
                promisedDeliveryDate.HasValue
                    ? $"Delivery target: {promisedDeliveryDate.Value:yyyy-MM-dd}."
                    : "Shipment tracking appears after delivery handoff.",
                100,
                delivered,
                inspected && !delivered,
                actualDeliveryDate ?? FindStatusTimestamp(statusHistory, "delivered", "shipped"))
        };

        return milestones;
    }

    private static CustomerManufacturingMilestoneDto CreateMilestone(
        string key,
        string label,
        string description,
        int percent,
        bool isComplete,
        bool isCurrent,
        DateTimeOffset? timestamp)
    {
        var state = isComplete ? "complete" : isCurrent ? "current" : "pending";
        return new CustomerManufacturingMilestoneDto(key, label, description, state, percent, timestamp);
    }

    private static DateTimeOffset? FindStatusTimestamp(
        IReadOnlyList<OrderStatusEntryDto> statusHistory,
        params string[] terms)
    {
        return statusHistory
            .Where(entry => terms.Any(term => entry.Status.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(entry => entry.Timestamp)
            .Select(entry => (DateTimeOffset?)entry.Timestamp)
            .FirstOrDefault();
    }
}

// ── Public DTOs ───────────────────────────────────────────────────────────────

/// <summary>Parameters for creating a manufacturing order.</summary>
public sealed class OrderCreateRequest
{
    public string CustomerId { get; set; } = string.Empty;
    public int ServiceCategoryId { get; set; } = 1;
    public int? ProcessTypeId { get; set; }
    public int OrderedQuantity { get; set; } = 1;
    public string? CustomerPoNumber { get; set; }
    public string? Requirements { get; set; }
    /// <summary>Gets or sets the quoted order total that checkout must match.</summary>
    public decimal? QuotedAmount { get; set; }
    /// <summary>Gets or sets the quote currency code.</summary>
    public string? QuoteCurrency { get; set; }
    /// <summary>Gets or sets the formal quote identifier accepted for this order.</summary>
    public Guid? QuoteId { get; set; }
    /// <summary>Gets or sets the formal quote number accepted for this order.</summary>
    public string? QuoteNumber { get; set; }
    /// <summary>Gets or sets the immutable quote version identifier accepted for this order.</summary>
    public Guid? QuoteVersionId { get; set; }
    /// <summary>Gets or sets the immutable quote version number accepted for this order.</summary>
    public int? QuoteVersionNumber { get; set; }
    /// <summary>Gets or sets structured production items used by JobService after payment.</summary>
    public IReadOnlyList<OrderProductionItemRequest> ProductionItems { get; set; } = [];

    // ── Process → OrderService ID mapping ────────────────────────────────────
    // ServiceCategoryId=1 "3D Printing" (FDM ProcessTypeId=1, SLA ProcessTypeId=2)
    // ServiceCategoryId=2 "CNC Machining"

    private static readonly Dictionary<string, (int ServiceCategoryId, int? ProcessTypeId)> ProcessMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["fdm"] = (1, 1),
            ["sla"] = (1, 2),
            ["cnc"] = (2, null),
        };

    /// <summary>Populates ServiceCategoryId/ProcessTypeId from a QuoteEngine process string such as "fdm".</summary>
    public void SetProcessFromCode(string processCode)
    {
        if (ProcessMap.TryGetValue(processCode, out var ids))
        {
            ServiceCategoryId = ids.ServiceCategoryId;
            ProcessTypeId = ids.ProcessTypeId;
        }
    }
}

/// <summary>Structured production item snapshot sent to OrderService for downstream job creation.</summary>
public sealed class OrderProductionItemRequest
{
    /// <summary>Gets or sets the source ProjectService project identifier, falling back to quote id for legacy standalone orders.</summary>
    public Guid? SourceProjectId { get; set; }

    /// <summary>Gets or sets the source part identifier.</summary>
    public Guid? SourceProjectPartId { get; set; }

    /// <summary>Gets or sets the resolved production material identifier.</summary>
    public Guid MaterialId { get; set; }

    /// <summary>Gets or sets the locked material snapshot JSON.</summary>
    public string MaterialSnapshotJson { get; set; } = string.Empty;

    /// <summary>Gets or sets the locked configuration snapshot JSON.</summary>
    public string ConfigurationSnapshotJson { get; set; } = string.Empty;

    /// <summary>Gets or sets the manufacturing technology.</summary>
    public string Technology { get; set; } = string.Empty;

    /// <summary>Gets or sets the per-unit volume in cubic centimeters.</summary>
    public decimal VolumeCm3 { get; set; }

    /// <summary>Gets or sets the ordered quantity.</summary>
    public int Quantity { get; set; } = 1;

    /// <summary>Gets or sets the per-unit estimated print time in minutes.</summary>
    public int EstimatedPrintTimeMinutes { get; set; }

    /// <summary>Gets or sets the promised delivery date for this item.</summary>
    public DateTime? DeliveryDate { get; set; }
}

/// <summary>Checkout shipping and contact snapshot persisted on the order before payment.</summary>
public sealed record OrderDeliverySnapshotRequest(
    string OrderNumber,
    Guid BillingAddressId,
    Guid ShippingAddressId,
    string? ShippingAddressLine1,
    string? ShippingAddressLine2,
    string? ShippingCity,
    string? ShippingProvince,
    string? ShippingPostalCode,
    string? ShippingCountry,
    string? BillingCompanyName,
    string? BillingVatNumber,
    string? DeliveryContactName,
    string? DeliveryContactPhone,
    string? DeliveryContactEmail);

/// <summary>Result returned after a manufacturing order is created.</summary>
public sealed class OrderCreatedResult
{
    public Guid OrderId { get; init; }
    public string OrderNumber { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}
