// Maliev.QuoteEngine.Bff/Clients/OrderServiceClient.cs
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Maliev.QuoteEngine.Shared.Account;
using System.Net;

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
        public string? CurrentStatus { get; set; }
        public DateTime UpdatedAt { get; set; }
        public decimal? QuotedAmount { get; set; }
        public string? QuoteCurrency { get; set; }
    }

    private sealed class OsPaginatedResponse
    {
        public IEnumerable<OsOrderResponse>? Items { get; set; }
    }

    private sealed class OsOrderDetailResponse
    {
        public string OrderId { get; set; } = string.Empty;
        public string? CurrentStatus { get; set; }
        public string PaymentStatus { get; set; } = "Unpaid";
        public decimal? QuotedAmount { get; set; }
        public string? QuoteCurrency { get; set; }
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

    private static CustomerOrderSummaryDto MapSummary(OsOrderResponse r) => new(
        DeterministicGuid(r.OrderId),
        r.OrderId,
        r.CurrentStatus ?? "Pending",
        new DateTimeOffset(r.UpdatedAt, TimeSpan.Zero),
        r.OrderId);

    private static Guid DeterministicGuid(string value)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"order:{value}"));
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
                requirements = request.Requirements
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

    public async Task<IReadOnlyList<CustomerOrderSummaryDto>> GetByCustomerAsync(string customerId, CancellationToken ct = default)
    {
        try
        {
            var paged = await http.GetFromJsonAsync<OsPaginatedResponse>(
                $"/order/v1/orders?customerId={Uri.EscapeDataString(customerId)}&pageSize=100", ct);

            return paged?.Items?.Select(MapSummary).ToArray() ?? [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OrderService GetByCustomer failed for {CustomerId}.", customerId);
            return [];
        }
    }

    public async Task<CustomerOrderDetailDto?> GetDetailAsync(string orderNumber, CancellationToken ct = default)
    {
        try
        {
            // Fetch order detail and status history in parallel
            var detailTask = http.GetAsync($"/order/v1/orders/{Uri.EscapeDataString(orderNumber)}", ct);
            var statusTask = http.GetAsync($"/order/v1/orders/{Uri.EscapeDataString(orderNumber)}/statuses", ct);

            await Task.WhenAll(detailTask, statusTask);

            using var detailResponse = detailTask.Result;
            using var statusResponse = statusTask.Result;

            if (detailResponse.StatusCode == HttpStatusCode.NotFound) return null;
            if (!detailResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("OrderService GetDetail returned {Status} for {OrderNumber}.", detailResponse.StatusCode, orderNumber);
                return null;
            }

            var detail = await detailResponse.Content.ReadFromJsonAsync<OsOrderDetailResponse>(cancellationToken: ct);
            if (detail is null) return null;

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

            return new CustomerOrderDetailDto(
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
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OrderService GetDetail failed for {OrderNumber}.", orderNumber);
            return null;
        }
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
            normalizedStatus.Contains("quality", StringComparison.Ordinal) ||
            normalizedStatus.Contains("inspection", StringComparison.Ordinal);
        var manufacturing = inspected ||
            normalizedStatus.Contains("manufacturing", StringComparison.Ordinal) ||
            normalizedStatus.Contains("production", StringComparison.Ordinal);
        var accepted = manufacturing ||
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
                !accepted && !manufacturing && !inspected && !shipped && !delivered,
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
                manufacturing,
                accepted && !manufacturing,
                FindStatusTimestamp(statusHistory, "manufacturing", "production")),
            CreateMilestone(
                "quality-inspection",
                "Quality inspection",
                "Finished parts move through quality review before delivery handoff.",
                75,
                inspected,
                manufacturing && !inspected,
                FindStatusTimestamp(statusHistory, "quality", "inspection")),
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
