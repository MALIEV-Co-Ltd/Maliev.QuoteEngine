// Maliev.QuoteEngine.Bff/Clients/QuotationServiceClient.cs
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Maliev.QuoteEngine.Shared.Account;

namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>
/// Calls QuotationService to create and retrieve formal quotations.
/// </summary>
public interface IQuotationServiceClient
{
    /// <summary>Creates a new quotation and returns a summary on success.</summary>
    Task<QuotationCreatedResult?> CreateAsync(QuotationCreateRequest request, CancellationToken ct = default);

    /// <summary>Returns the quotation with the given ID, or null if not found.</summary>
    Task<QuotationCreatedResult?> GetByIdAsync(Guid quotationId, CancellationToken ct = default);

    /// <summary>Returns all quotations for the given customer as <see cref="CustomerQuoteSummaryDto"/>.</summary>
    Task<IReadOnlyList<CustomerQuoteSummaryDto>> GetByCustomerAsync(Guid customerId, CancellationToken ct = default);
}

internal sealed class QuotationServiceClient(HttpClient http, ILogger<QuotationServiceClient> logger) : IQuotationServiceClient
{
    // ── Internal response shapes (match QuotationService JSON contract) ──────

    private sealed class QsQuotationResponse
    {
        public Guid Id { get; set; }
        public Guid CustomerId { get; set; }
        public string QuotationNumber { get; set; } = string.Empty;
        public JsonElement Status { get; set; }
        public decimal Total { get; set; }
        public string CurrencyCode { get; set; } = "THB";
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<QsVersionSummary> Versions { get; set; } = [];
    }

    private sealed class QsVersionSummary
    {
        public string? PdfArtifactUrl { get; set; }
        public string? PdfArtifactStoragePath { get; set; }
    }

    private sealed class QsPagedResponse
    {
        public IEnumerable<QsQuotationResponse>? Data { get; set; }
    }

    private static QuotationCreatedResult MapResult(QsQuotationResponse r) => new()
    {
        Id = r.Id,
        CustomerId = r.CustomerId,
        QuotationNumber = r.QuotationNumber,
        Status = ReadStatus(r.Status),
        Total = r.Total,
        CurrencyCode = r.CurrencyCode,
        UpdatedAt = r.UpdatedAt,
        PdfArtifactUrl = r.Versions.FirstOrDefault()?.PdfArtifactUrl,
        PdfArtifactStoragePath = r.Versions.FirstOrDefault()?.PdfArtifactStoragePath
    };

    // ── Interface implementation ──────────────────────────────────────────────

    public async Task<QuotationCreatedResult?> CreateAsync(QuotationCreateRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("/quotation/v1/quotations", request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("QuotationService returned {Status} on create: {Body}", response.StatusCode, body);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<QsQuotationResponse>(cancellationToken: ct);
            return result is null ? null : MapResult(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "QuotationService create failed.");
            return null;
        }
    }

    public async Task<QuotationCreatedResult?> GetByIdAsync(Guid quotationId, CancellationToken ct = default)
    {
        try
        {
            var result = await http.GetFromJsonAsync<QsQuotationResponse>(
                $"/quotation/v1/quotations/{quotationId:D}", ct);
            return result is null ? null : MapResult(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "QuotationService GetById failed for {Id}.", quotationId);
            return null;
        }
    }

    public async Task<IReadOnlyList<CustomerQuoteSummaryDto>> GetByCustomerAsync(Guid customerId, CancellationToken ct = default)
    {
        try
        {
            var paged = await http.GetFromJsonAsync<QsPagedResponse>(
                $"/quotation/v1/quotations?customerId={customerId:D}&pageSize=100", ct);
            if (paged?.Data is null) return [];

            return paged.Data.Select(q => new CustomerQuoteSummaryDto(
                q.Id,
                q.QuotationNumber,
                ReadStatus(q.Status),
                q.Total,
                q.CurrencyCode,
                new DateTimeOffset(q.UpdatedAt, TimeSpan.Zero),
                q.Versions.FirstOrDefault()?.PdfArtifactUrl ?? string.Empty))
            .ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "QuotationService GetByCustomer failed for {CustomerId}.", customerId);
            return [];
        }
    }

    private static string ReadStatus(JsonElement status)
    {
        return status.ValueKind switch
        {
            JsonValueKind.String => status.GetString() ?? string.Empty,
            JsonValueKind.Number when status.TryGetInt32(out var value) => value switch
            {
                1 => "Draft",
                2 => "PendingApproval",
                3 => "Approved",
                4 => "CustomerReview",
                5 => "Accepted",
                6 => "Expired",
                7 => "Cancelled",
                _ => value.ToString(CultureInfo.InvariantCulture)
            },
            _ => string.Empty
        };
    }
}

// ── Public DTOs ───────────────────────────────────────────────────────────────

/// <summary>Result returned after a quotation is created or retrieved.</summary>
public sealed class QuotationCreatedResult
{
    public Guid Id { get; init; }
    public Guid CustomerId { get; init; }
    public string QuotationNumber { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public decimal Total { get; init; }
    public string CurrencyCode { get; init; } = "THB";
    public DateTime UpdatedAt { get; init; }
    public string? PdfArtifactUrl { get; init; }
    public string? PdfArtifactStoragePath { get; init; }
}

/// <summary>Request body for creating a new quotation.</summary>
public sealed class QuotationCreateRequest
{
    public Guid CustomerId { get; set; }
    public int BillingIdentityType { get; set; } = 1; // Corporate
    public DateTime ValidityPeriodStart { get; set; }
    public DateTime ValidityPeriodEnd { get; set; }
    public List<QuotationLineItemCreate> LineItems { get; set; } = [];
    public string? GeneratedByDisplayName { get; set; }
}

/// <summary>A single line item in a quotation creation request.</summary>
public sealed class QuotationLineItemCreate
{
    public Guid MaterialServiceId { get; set; }
    public int Quantity { get; set; } = 1;
    public string UnitOfMeasure { get; set; } = "pcs";
    public decimal UnitPrice { get; set; }
    public string? ManufacturingProcess { get; set; }
    public string? Notes { get; set; }
}
