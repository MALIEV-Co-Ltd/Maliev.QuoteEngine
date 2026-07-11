using System.Net.Http.Json;

namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>Reads customer-safe receipt summaries for a verified InvoiceService invoice.</summary>
public interface IReceiptServiceClient
{
    /// <summary>Returns receipts linked to the supplied invoice identifier.</summary>
    Task<IReadOnlyList<CustomerProjectReceiptResult>> GetByInvoiceAsync(
        Guid invoiceId,
        CancellationToken ct = default);

    /// <summary>Looks up receipts while preserving whether ReceiptService answered successfully.</summary>
    async Task<CustomerProjectReceiptLookupResult> LookupByInvoiceAsync(
        Guid invoiceId,
        CancellationToken ct = default) =>
        new(true, await GetByInvoiceAsync(invoiceId, ct));
}

internal sealed class ReceiptServiceClient(HttpClient http, ILogger<ReceiptServiceClient> logger)
    : IReceiptServiceClient
{
    private sealed class ReceiptPageResponse
    {
        public List<ReceiptResponse> Data { get; set; } = [];
    }

    private sealed class ReceiptResponse
    {
        public Guid Id { get; set; }
        public string ReceiptNumber { get; set; } = string.Empty;
        public Guid InvoiceId { get; set; }
        public DateTime IssueDate { get; set; }
        public decimal TotalAmount { get; set; }
        public string Currency { get; set; } = "THB";
        public string Status { get; set; } = string.Empty;
        public Guid? PdfReferenceId { get; set; }
    }

    public async Task<IReadOnlyList<CustomerProjectReceiptResult>> GetByInvoiceAsync(
        Guid invoiceId,
        CancellationToken ct = default) =>
        (await LookupByInvoiceAsync(invoiceId, ct)).Receipts;

    public async Task<CustomerProjectReceiptLookupResult> LookupByInvoiceAsync(
        Guid invoiceId,
        CancellationToken ct = default)
    {
        if (invoiceId == Guid.Empty)
        {
            return new CustomerProjectReceiptLookupResult(true, []);
        }

        try
        {
            var page = await http.GetFromJsonAsync<ReceiptPageResponse>(
                $"/receipt/v1/receipts?invoiceId={invoiceId:D}&page=1&pageSize=100",
                ct);
            var receipts = page?.Data
                .Where(receipt => receipt.InvoiceId == invoiceId)
                .OrderByDescending(receipt => receipt.IssueDate)
                .Select(receipt => new CustomerProjectReceiptResult
                {
                    ReceiptId = receipt.Id,
                    ReceiptNumber = receipt.ReceiptNumber,
                    InvoiceId = receipt.InvoiceId,
                    Status = receipt.Status,
                    IssueDate = new DateTimeOffset(receipt.IssueDate, TimeSpan.Zero),
                    TotalAmount = receipt.TotalAmount,
                    Currency = string.IsNullOrWhiteSpace(receipt.Currency) ? "THB" : receipt.Currency,
                    PdfReferenceId = receipt.PdfReferenceId
                })
                .ToArray() ?? [];
            return new CustomerProjectReceiptLookupResult(true, receipts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ReceiptService lookup failed for invoice {InvoiceId}.", invoiceId);
            return new CustomerProjectReceiptLookupResult(false, []);
        }
    }
}

/// <summary>Customer-safe receipt fields attached to a project workspace.</summary>
public sealed class CustomerProjectReceiptResult
{
    public Guid ReceiptId { get; init; }
    public string ReceiptNumber { get; init; } = string.Empty;
    public Guid InvoiceId { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset IssueDate { get; init; }
    public decimal TotalAmount { get; init; }
    public string Currency { get; init; } = "THB";
    public Guid? PdfReferenceId { get; init; }
}

/// <summary>Receipt lookup result that distinguishes absence from downstream unavailability.</summary>
public sealed record CustomerProjectReceiptLookupResult(
    bool IsAvailable,
    IReadOnlyList<CustomerProjectReceiptResult> Receipts);
