// Maliev.QuoteEngine.Bff/Clients/InvoiceServiceClient.cs
using System.Net.Http.Json;
using System.Text.Json;

namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>
/// Calls InvoiceService to create and finalize Make Studio order invoices.
/// </summary>
public interface IInvoiceServiceClient
{
    /// <summary>Creates a draft invoice and finalizes it for payment allocation.</summary>
    Task<InvoicePreparedResult?> CreateAndFinalizeForOrderAsync(
        InvoiceCreateForOrderRequest request,
        CancellationToken ct = default);
}

internal sealed class InvoiceServiceClient(HttpClient http, ILogger<InvoiceServiceClient> logger) : IInvoiceServiceClient
{
    private sealed class InvoiceResponse
    {
        public Guid Id { get; set; }
        public string InvoiceNumber { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal GrandTotal { get; set; }
        public string Currency { get; set; } = "THB";
    }

    public async Task<InvoicePreparedResult?> CreateAndFinalizeForOrderAsync(
        InvoiceCreateForOrderRequest request,
        CancellationToken ct = default)
    {
        try
        {
            using var createResponse = await http.PostAsJsonAsync("/invoice/v1/invoices", new
            {
                customerId = request.CustomerId,
                billingIdentityType = 1,
                customerName = request.CustomerName,
                customerTaxId = request.CustomerTaxId,
                billingAddress = request.BillingAddress,
                shippingAddress = request.ShippingAddress,
                poNumber = request.OrderNumber,
                quotationReference = request.QuoteNumber,
                currency = request.Currency,
                issueDate = request.IssueDate,
                dueDate = request.DueDate,
                paymentTermsDays = request.PaymentTermsDays,
                lines = request.Lines.Select(line => new
                {
                    lineNumber = line.LineNumber,
                    description = line.Description,
                    quantity = line.Quantity,
                    unitPrice = line.UnitPrice,
                    taxCategory = line.TaxCategory,
                    taxRate = line.TaxRate
                }).ToArray()
            }, ct);

            if (!createResponse.IsSuccessStatusCode)
            {
                var body = await createResponse.Content.ReadAsStringAsync(ct);
                logger.LogWarning("InvoiceService returned {Status} on create: {Body}", createResponse.StatusCode, body);
                throw new InvalidOperationException($"InvoiceService returned {(int)createResponse.StatusCode} on create: {body}");
            }

            var created = await createResponse.Content.ReadFromJsonAsync<InvoiceResponse>(cancellationToken: ct);
            if (created is null || created.Id == Guid.Empty)
            {
                return null;
            }

            using var finalizeResponse = await http.PostAsync($"/invoice/v1/invoices/{created.Id:D}/finalize", null, ct);
            if (!finalizeResponse.IsSuccessStatusCode)
            {
                var body = await finalizeResponse.Content.ReadAsStringAsync(ct);
                logger.LogWarning("InvoiceService returned {Status} on finalize for {InvoiceId}: {Body}",
                    finalizeResponse.StatusCode,
                    created.Id,
                    body);
                throw new InvalidOperationException($"InvoiceService returned {(int)finalizeResponse.StatusCode} on finalize for {created.Id:D}: {body}");
            }

            var finalized = await finalizeResponse.Content.ReadFromJsonAsync<InvoiceResponse>(cancellationToken: ct);
            var invoice = finalized ?? created;
            return new InvoicePreparedResult
            {
                InvoiceId = invoice.Id,
                InvoiceNumber = invoice.InvoiceNumber,
                Status = invoice.Status,
                GrandTotal = invoice.GrandTotal,
                Currency = string.IsNullOrWhiteSpace(invoice.Currency) ? request.Currency : invoice.Currency
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "InvoiceService invoice preparation failed for order {OrderNumber}.", request.OrderNumber);
            return null;
        }
    }
}

/// <summary>Request for a Make Studio order invoice.</summary>
public sealed class InvoiceCreateForOrderRequest
{
    public Guid CustomerId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string CustomerTaxId { get; init; } = string.Empty;
    public string BillingAddress { get; init; } = string.Empty;
    public string? ShippingAddress { get; init; }
    public string OrderNumber { get; init; } = string.Empty;
    public string? QuoteNumber { get; init; }
    public string Currency { get; init; } = "THB";
    public DateTime IssueDate { get; init; }
    public DateTime DueDate { get; init; }
    public int PaymentTermsDays { get; init; } = 7;
    public IReadOnlyList<InvoiceCreateLineRequest> Lines { get; init; } = [];
}

/// <summary>Line item for a Make Studio invoice.</summary>
public sealed class InvoiceCreateLineRequest
{
    public int LineNumber { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public string TaxCategory { get; init; } = "VAT";
    public decimal TaxRate { get; init; } = 7m;
}

/// <summary>Prepared invoice summary returned to the Make Studio order flow.</summary>
public sealed class InvoicePreparedResult
{
    public Guid InvoiceId { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public decimal GrandTotal { get; init; }
    public string Currency { get; init; } = "THB";
}
