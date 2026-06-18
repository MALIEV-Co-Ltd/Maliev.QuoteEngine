// Maliev.QuoteEngine.Bff/Clients/PaymentServiceClient.cs
using System.Net.Http.Json;

namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>
/// Calls PaymentService to initiate a hosted payment checkout.
/// </summary>
public interface IPaymentServiceClient
{
    /// <summary>
    /// Submits a payment request and returns the result (including the redirect URL) on success,
    /// or null when the payment service is unavailable.
    /// </summary>
    Task<PaymentInitiatedResult?> InitiateAsync(
        string customerId,
        string orderId,
        string orderNumber,
        decimal amount,
        string currency,
        string returnUrl,
        string cancelUrl,
        string idempotencyKey,
        Guid? billingAddressId,
        Guid? shippingAddressId,
        string? billingCompanyName,
        string? billingVatNumber,
        string? deliveryContactName,
        string? deliveryContactPhone,
        string? deliveryContactEmail,
        bool acceptedTerms,
        CancellationToken ct = default);
}

internal sealed class PaymentServiceClient(HttpClient http, ILogger<PaymentServiceClient> logger) : IPaymentServiceClient
{
    // ── Internal response shape (match PaymentService JSON contract) ──────────

    private sealed class PsPaymentResponse
    {
        public Guid TransactionId { get; set; }
        public string? PaymentUrl { get; set; }
        public int Status { get; set; }   // PaymentStatus enum value
    }

    // ── Interface implementation ──────────────────────────────────────────────

    public async Task<PaymentInitiatedResult?> InitiateAsync(
        string customerId,
        string orderId,
        string orderNumber,
        decimal amount,
        string currency,
        string returnUrl,
        string cancelUrl,
        string idempotencyKey,
        Guid? billingAddressId,
        Guid? shippingAddressId,
        string? billingCompanyName,
        string? billingVatNumber,
        string? deliveryContactName,
        string? deliveryContactPhone,
        string? deliveryContactEmail,
        bool acceptedTerms,
        CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/payment/v1/payments")
            {
                Content = JsonContent.Create(new
                {
                    amount,
                    currency,
                    customerId,
                    orderId,
                    description = $"Manufacturing order {orderNumber}",
                    returnUrl,
                    cancelUrl,
                    metadata = new Dictionary<string, string>
                    {
                        ["orderNumber"] = orderNumber,
                        ["billingAddressId"] = billingAddressId?.ToString("D") ?? string.Empty,
                        ["shippingAddressId"] = shippingAddressId?.ToString("D") ?? string.Empty,
                        ["billingCompanyName"] = billingCompanyName ?? string.Empty,
                        ["billingVatNumber"] = billingVatNumber ?? string.Empty,
                        ["deliveryContactName"] = deliveryContactName ?? string.Empty,
                        ["deliveryContactPhone"] = deliveryContactPhone ?? string.Empty,
                        ["deliveryContactEmail"] = deliveryContactEmail ?? string.Empty,
                        ["acceptedTerms"] = acceptedTerms ? "true" : "false"
                    }
                })
            };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("PaymentService returned {Status} on initiate: {Body}", response.StatusCode, body);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<PsPaymentResponse>(cancellationToken: ct);
            if (result is null) return null;

            return new PaymentInitiatedResult
            {
                TransactionId = result.TransactionId,
                PaymentUrl = result.PaymentUrl ?? string.Empty,
                Status = result.Status.ToString()
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PaymentService initiate failed for order {OrderId}.", orderId);
            return null;
        }
    }
}

// ── Public DTO ────────────────────────────────────────────────────────────────

/// <summary>Result returned after a payment is successfully initiated.</summary>
public sealed class PaymentInitiatedResult
{
    public Guid TransactionId { get; init; }

    /// <summary>URL to redirect the customer to complete payment (e.g. Omise hosted page).</summary>
    public string PaymentUrl { get; init; } = string.Empty;

    /// <summary>Raw status string from PaymentService (e.g. "1" for Pending).</summary>
    public string Status { get; init; } = string.Empty;
}
