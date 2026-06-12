// Maliev.QuoteEngine.Bff/Clients/CustomerServiceClient.cs
using System.Net;
using System.Net.Http.Json;
using Maliev.QuoteEngine.Shared.Account;

namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>
/// Calls CustomerService to retrieve customer profile data.
/// </summary>
public interface ICustomerServiceClient
{
    /// <summary>Returns the customer profile for the given ID, or null if not found.</summary>
    Task<CustomerProfileResponse?> GetByIdAsync(Guid customerId, CancellationToken ct = default);

    /// <summary>Returns the customer profile for the given email address, or null if not found.</summary>
    Task<CustomerProfileResponse?> GetByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>Ensures CustomerService has a customer for the email address and returns the canonical profile.</summary>
    Task<CustomerProfileResponse?> EnsureCustomerAsync(
        string email,
        string displayName,
        string phone = "",
        CancellationToken ct = default);

    /// <summary>Gets customer-owned addresses.</summary>
    Task<HttpResponseMessage> GetCustomerAddressesAsync(Guid customerId, CancellationToken cancellationToken);

    /// <summary>Creates a customer-owned address.</summary>
    Task<HttpResponseMessage> CreateCustomerAddressAsync(object request, CancellationToken cancellationToken);

    /// <summary>Updates a customer-owned address.</summary>
    Task<HttpResponseMessage> UpdateCustomerAddressAsync(Guid addressId, object request, CancellationToken cancellationToken);

    /// <summary>Deletes a customer-owned address.</summary>
    Task<HttpResponseMessage> DeleteCustomerAddressAsync(Guid addressId, object request, CancellationToken cancellationToken);
}

internal sealed class CustomerServiceClient(HttpClient http, ILogger<CustomerServiceClient> logger) : ICustomerServiceClient
{
    private sealed class CsCustomerResponse
    {
        public Guid Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Mobile { get; set; }
        public string? CompanyName { get; set; }
        public string? VatNumber { get; set; }
        public string PreferredLanguage { get; set; } = "en";
        public string? ProfileImageUrl { get; set; }
        public string PreferredCurrency { get; set; } = "THB";
        public string Timezone { get; set; } = "Asia/Bangkok";
        public string Segment { get; set; } = "Self-service manufacturing";
        public string Tier { get; set; } = "Customer";
        public string NdaStatus { get; set; } = "Active";
        public DateTimeOffset? NdaExpiresAt { get; set; }
    }

    private sealed class CsPagedCustomerResponse
    {
        public List<CsCustomerResponse> Items { get; set; } = [];
    }

    private sealed class CsRegisterCustomerRequest
    {
        public string Email { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string? Phone { get; set; }
        public string RegistrationMethod { get; set; } = "Email";
        public string PreferredLanguage { get; set; } = "en";
        public string Timezone { get; set; } = "Asia/Bangkok";
        public string Password { get; set; } = "PrototypeOnly123!";
    }

    public async Task<CustomerProfileResponse?> GetByIdAsync(Guid customerId, CancellationToken ct = default)
    {
        try
        {
            var result = await http.GetFromJsonAsync<CsCustomerResponse>(
                $"/customer/v1/customers/{customerId:D}", ct);
            if (result is null) return null;

            return MapCustomer(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CustomerService GetById failed for {CustomerId}.", customerId);
            return null;
        }
    }

    public async Task<CustomerProfileResponse?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        try
        {
            var normalizedEmail = NormalizeEmail(email);
            var response = await http.GetFromJsonAsync<CsPagedCustomerResponse>(
                $"/customer/v1/customers?email={Uri.EscapeDataString(normalizedEmail)}&page=1&pageSize=5", ct);

            return response?.Items
                .Select(MapCustomer)
                .FirstOrDefault(customer => string.Equals(customer.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CustomerService GetByEmail failed for {Email}.", email);
            return null;
        }
    }

    public async Task<CustomerProfileResponse?> EnsureCustomerAsync(
        string email,
        string displayName,
        string phone = "",
        CancellationToken ct = default)
    {
        var existing = await GetByEmailAsync(email, ct);
        if (existing is not null)
        {
            return existing;
        }

        var normalizedEmail = NormalizeEmail(email);
        var (firstName, lastName) = SplitName(displayName, normalizedEmail);
        var request = new CsRegisterCustomerRequest
        {
            Email = normalizedEmail,
            FirstName = firstName,
            LastName = lastName,
            Phone = string.IsNullOrWhiteSpace(phone) ? null : phone,
            PreferredLanguage = "en",
            Timezone = "Asia/Bangkok"
        };

        try
        {
            using var response = await http.PostAsJsonAsync("/customer/v1/customers/register", request, ct);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                return await GetByEmailAsync(normalizedEmail, ct);
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning(
                    "CustomerService returned {Status} while ensuring customer {Email}: {Body}",
                    response.StatusCode,
                    normalizedEmail,
                    body);
                return null;
            }

            var created = await response.Content.ReadFromJsonAsync<CsCustomerResponse>(cancellationToken: ct);
            return created is null ? null : MapCustomer(created);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CustomerService EnsureCustomer failed for {Email}.", normalizedEmail);
            return null;
        }
    }

    public Task<HttpResponseMessage> GetCustomerAddressesAsync(Guid customerId, CancellationToken cancellationToken) =>
        http.GetAsync($"/customer/v1/addresses?ownerType=Customer&ownerId={customerId:D}", cancellationToken);

    public Task<HttpResponseMessage> CreateCustomerAddressAsync(object request, CancellationToken cancellationToken) =>
        http.PostAsJsonAsync("/customer/v1/addresses", request, cancellationToken);

    public Task<HttpResponseMessage> UpdateCustomerAddressAsync(Guid addressId, object request, CancellationToken cancellationToken) =>
        http.PatchAsJsonAsync($"/customer/v1/addresses/{addressId:D}", request, cancellationToken);

    public Task<HttpResponseMessage> DeleteCustomerAddressAsync(Guid addressId, object request, CancellationToken cancellationToken)
    {
        var message = new HttpRequestMessage(HttpMethod.Delete, $"/customer/v1/addresses/{addressId:D}")
        {
            Content = JsonContent.Create(request)
        };
        return http.SendAsync(message, cancellationToken);
    }

    private static CustomerProfileResponse MapCustomer(CsCustomerResponse result)
    {
        return new CustomerProfileResponse(
            result.Id,
            string.IsNullOrWhiteSpace(result.Name)
                ? $"{result.FirstName} {result.LastName}".Trim()
                : result.Name,
            result.Email,
            result.Mobile ?? string.Empty,
            result.CompanyName ?? string.Empty,
            result.PreferredLanguage,
            result.ProfileImageUrl,
            string.IsNullOrWhiteSpace(result.PreferredCurrency) ? "THB" : result.PreferredCurrency,
            string.IsNullOrWhiteSpace(result.Timezone) ? "Asia/Bangkok" : result.Timezone,
            string.IsNullOrWhiteSpace(result.Segment) ? "Self-service manufacturing" : result.Segment,
            string.IsNullOrWhiteSpace(result.Tier) ? "Customer" : result.Tier,
            string.IsNullOrWhiteSpace(result.NdaStatus) ? "Active" : result.NdaStatus,
            result.NdaExpiresAt ?? DateTimeOffset.UtcNow.AddDays(90),
            result.VatNumber ?? string.Empty);
    }

    private static string NormalizeEmail(string email)
    {
        return string.IsNullOrWhiteSpace(email) ? "customer@example.com" : email.Trim().ToLowerInvariant();
    }

    private static (string FirstName, string LastName) SplitName(string displayName, string email)
    {
        var fallback = email.Split('@', 2)[0]
            .Replace(".", " ", StringComparison.Ordinal)
            .Replace("_", " ", StringComparison.Ordinal)
            .Trim();
        var name = string.IsNullOrWhiteSpace(displayName) ? fallback : displayName.Trim();
        var parts = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => ("Quote", "Customer"),
            1 => (parts[0], "Customer"),
            _ => (parts[0], parts[1])
        };
    }
}
