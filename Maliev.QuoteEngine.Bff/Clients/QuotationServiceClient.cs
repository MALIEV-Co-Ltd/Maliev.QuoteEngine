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

    /// <summary>Creates the first project quotation or revises the existing project quotation.</summary>
    Task<QuotationCreatedResult?> CreateOrReviseProjectQuoteAsync(QuotationCreateRequest request, CancellationToken ct = default);

    /// <summary>Returns the quotation with the given ID, or null if not found.</summary>
    Task<QuotationCreatedResult?> GetByIdAsync(Guid quotationId, CancellationToken ct = default);

    /// <summary>Returns the quotation for a source project, or null if none exists.</summary>
    Task<QuotationCreatedResult?> GetBySourceProjectAsync(Guid customerId, Guid sourceProjectId, CancellationToken ct = default);

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
        public Guid? SourceProjectId { get; set; }
        public string? SourceProjectNumber { get; set; }
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
        public Guid Id { get; set; }
        public int VersionNumber { get; set; }
        public decimal TotalPrice { get; set; }
        public string CurrencyCode { get; set; } = "THB";
        public string? ChangeSummary { get; set; }
        public string? PdfArtifactUrl { get; set; }
        public string? PdfArtifactStoragePath { get; set; }
        public string? GeneratedByDisplayName { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class QsPagedResponse
    {
        public IEnumerable<QsQuotationResponse>? Data { get; set; }
    }

    private static QuotationCreatedResult MapResult(QsQuotationResponse r)
    {
        var currentVersion = r.Versions
            .OrderByDescending(version => version.VersionNumber)
            .FirstOrDefault();

        return new QuotationCreatedResult
        {
            Id = r.Id,
            CustomerId = r.CustomerId,
            SourceProjectId = r.SourceProjectId,
            SourceProjectNumber = r.SourceProjectNumber,
            QuotationNumber = r.QuotationNumber,
            Status = ReadStatus(r.Status),
            Total = r.Total,
            CurrencyCode = r.CurrencyCode,
            UpdatedAt = r.UpdatedAt,
            QuoteVersionId = currentVersion?.Id,
            QuoteVersionNumber = currentVersion?.VersionNumber,
            PdfArtifactUrl = currentVersion?.PdfArtifactUrl,
            PdfArtifactStoragePath = currentVersion?.PdfArtifactStoragePath
        };
    }

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

    public async Task<QuotationCreatedResult?> CreateOrReviseProjectQuoteAsync(QuotationCreateRequest request, CancellationToken ct = default)
    {
        if (request.SourceProjectId is not { } sourceProjectId)
        {
            return await CreateAsync(request, ct);
        }

        var existing = await GetBySourceProjectAsync(request.CustomerId, sourceProjectId, ct);
        if (existing is null)
        {
            return await CreateAsync(request, ct);
        }

        return await UpdateAsync(existing.Id, QuotationUpdateRequest.FromCreate(request), ct);
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

    public async Task<QuotationCreatedResult?> GetBySourceProjectAsync(Guid customerId, Guid sourceProjectId, CancellationToken ct = default)
    {
        try
        {
            var paged = await http.GetFromJsonAsync<QsPagedResponse>(
                $"/quotation/v1/quotations?customerId={customerId:D}&pageSize=100", ct);
            return paged?.Data?
                .Where(quotation => quotation.SourceProjectId == sourceProjectId)
                .OrderByDescending(quotation => quotation.UpdatedAt)
                .Select(MapResult)
                .FirstOrDefault();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "QuotationService GetBySourceProject failed for customer {CustomerId} project {SourceProjectId}.",
                customerId,
                sourceProjectId);
            return null;
        }
    }

    private async Task<QuotationCreatedResult?> UpdateAsync(Guid quotationId, QuotationUpdateRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PutAsJsonAsync($"/quotation/v1/quotations/{quotationId:D}", request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("QuotationService returned {Status} on update: {Body}", response.StatusCode, body);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<QsQuotationResponse>(cancellationToken: ct);
            return result is null ? null : MapResult(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "QuotationService update failed for {Id}.", quotationId);
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
                q.Versions.FirstOrDefault()?.PdfArtifactUrl ?? string.Empty,
                q.Versions.Select(version => new CustomerQuoteVersionSummaryDto(
                    version.Id,
                    version.VersionNumber,
                    version.TotalPrice,
                    version.CurrencyCode,
                    version.ChangeSummary,
                    version.PdfArtifactUrl,
                    version.PdfArtifactStoragePath,
                    version.GeneratedByDisplayName,
                    new DateTimeOffset(version.CreatedAt, TimeSpan.Zero)))
                .ToArray()))
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
    public Guid? SourceProjectId { get; init; }
    public string? SourceProjectNumber { get; init; }
    public string QuotationNumber { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public decimal Total { get; init; }
    public string CurrencyCode { get; init; } = "THB";
    public DateTime UpdatedAt { get; init; }
    public Guid? QuoteVersionId { get; init; }
    public int? QuoteVersionNumber { get; init; }
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
    public Guid? SourceProjectId { get; set; }
    public string? SourceProjectNumber { get; set; }
    public string? ProjectSnapshotJson { get; set; }
    public string? ProjectSnapshotHash { get; set; }
    public string? ChangeSummary { get; set; }
    public List<QuotationLineItemCreate> LineItems { get; set; } = [];
    public string? GeneratedByDisplayName { get; set; }
}

/// <summary>Request body for creating a revised quotation version.</summary>
public sealed class QuotationUpdateRequest
{
    public List<QuotationLineItemCreate>? LineItems { get; set; }
    public string ChangeSummary { get; set; } = string.Empty;
    public string? ProjectSnapshotJson { get; set; }
    public string? ProjectSnapshotHash { get; set; }
    public string? GeneratedByDisplayName { get; set; }

    public static QuotationUpdateRequest FromCreate(QuotationCreateRequest request) => new()
    {
        LineItems = request.LineItems,
        ChangeSummary = string.IsNullOrWhiteSpace(request.ChangeSummary)
            ? "Make Studio project quote revision"
            : request.ChangeSummary,
        ProjectSnapshotJson = request.ProjectSnapshotJson,
        ProjectSnapshotHash = request.ProjectSnapshotHash,
        GeneratedByDisplayName = request.GeneratedByDisplayName
    };
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
