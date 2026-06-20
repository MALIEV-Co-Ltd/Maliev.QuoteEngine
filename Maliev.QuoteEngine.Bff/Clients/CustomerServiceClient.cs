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

    /// <summary>Ensures the customer has a company billing identity for invoice creation.</summary>
    Task<CustomerProfileResponse?> EnsureCompanyBillingIdentityAsync(
        Guid customerId,
        string companyName,
        string? vatNumber,
        string? phone,
        CancellationToken ct = default);

    /// <summary>Updates CustomerService-owned profile fields used by Make Studio account context.</summary>
    Task<CustomerProfileResponse?> UpdateCustomerProfileAsync(
        Guid customerId,
        string displayName,
        string? phone,
        string? companyName,
        string? vatNumber,
        string preferredLanguage,
        string timezone,
        CancellationToken ct = default);

    /// <summary>Gets customer-owned addresses.</summary>
    Task<HttpResponseMessage> GetCustomerAddressesAsync(Guid customerId, CancellationToken cancellationToken);

    /// <summary>Creates a customer-owned address.</summary>
    Task<HttpResponseMessage> CreateCustomerAddressAsync(object request, CancellationToken cancellationToken);

    /// <summary>Updates a customer-owned address.</summary>
    Task<HttpResponseMessage> UpdateCustomerAddressAsync(Guid addressId, object request, CancellationToken cancellationToken);

    /// <summary>Deletes a customer-owned address.</summary>
    Task<HttpResponseMessage> DeleteCustomerAddressAsync(Guid addressId, object request, CancellationToken cancellationToken);

    /// <summary>Gets customer-owned account documents.</summary>
    Task<IReadOnlyList<CustomerDocumentDto>?> GetCustomerDocumentsAsync(
        Guid customerId,
        CancellationToken cancellationToken);

    /// <summary>Creates a customer-owned account document record.</summary>
    Task<CustomerDocumentDto?> CreateCustomerDocumentAsync(
        Guid customerId,
        CustomerDocumentUploadRequest request,
        CancellationToken cancellationToken);

    /// <summary>Gets customer-owned NDA records.</summary>
    Task<IReadOnlyList<CustomerNdaDto>?> GetCustomerNdasAsync(
        Guid customerId,
        CancellationToken cancellationToken);

    /// <summary>Gets durable customer-scoped memories.</summary>
    Task<CustomerMemoryQueryResponse> GetCustomerMemoriesAsync(
        Guid customerId,
        string? query,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Observes or reinforces one durable customer-scoped memory.</summary>
    Task<CustomerMemoryResponse?> ObserveCustomerMemoryAsync(
        Guid customerId,
        CustomerMemoryObserveRequest request,
        CancellationToken cancellationToken);
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
        public Guid? CompanyId { get; set; }
        public uint Xmin { get; set; }
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

    private sealed class CsCompanyResponse
    {
        public Guid Id { get; set; }
    }

    private sealed class CsDocumentResponse
    {
        public Guid Id { get; set; }
        public string OwnerType { get; set; } = string.Empty;
        public Guid OwnerId { get; set; }
        public string DocumentType { get; set; } = string.Empty;
        public string FileReference { get; set; } = string.Empty;
        public string Filename { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string MimeType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class CsCreateDocumentRequest
    {
        public string OwnerType { get; set; } = "Customer";
        public Guid OwnerId { get; set; }
        public string DocumentType { get; set; } = string.Empty;
        public string FileReference { get; set; } = string.Empty;
        public string Filename { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string MimeType { get; set; } = string.Empty;
    }

    private sealed class CsNdaResponse
    {
        public Guid Id { get; set; }
        public Guid CustomerId { get; set; }
        public Guid? DocumentReferenceId { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset? SignedAt { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
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

    public async Task<CustomerProfileResponse?> EnsureCompanyBillingIdentityAsync(
        Guid customerId,
        string companyName,
        string? vatNumber,
        string? phone,
        CancellationToken ct = default)
    {
        var customer = await GetRawCustomerByIdAsync(customerId, ct);
        if (customer?.CompanyId is not null)
        {
            return MapCustomer(customer);
        }

        if (customer is null)
        {
            return null;
        }

        var company = await CreateCompanyAsync(
            string.IsNullOrWhiteSpace(companyName) ? customer.Name : companyName.Trim(),
            vatNumber,
            string.IsNullOrWhiteSpace(phone) ? customer.Mobile : phone,
            customer.Email,
            ct);
        if (company is null)
        {
            return null;
        }

        using var response = await http.PatchAsJsonAsync($"/customer/v1/customers/{customerId:D}", new
        {
            firstName = string.IsNullOrWhiteSpace(customer.FirstName) ? "Make" : customer.FirstName,
            lastName = string.IsNullOrWhiteSpace(customer.LastName) ? "Studio" : customer.LastName,
            email = customer.Email,
            mobile = string.IsNullOrWhiteSpace(phone) ? customer.Mobile : phone,
            segment = string.IsNullOrWhiteSpace(customer.Segment) ? "Enterprise" : customer.Segment,
            tier = string.IsNullOrWhiteSpace(customer.Tier) ? "Gold" : customer.Tier,
            preferredLanguage = string.IsNullOrWhiteSpace(customer.PreferredLanguage) ? "en" : customer.PreferredLanguage,
            timezone = string.IsNullOrWhiteSpace(customer.Timezone) ? "Asia/Bangkok" : customer.Timezone,
            paymentTerms = "Due on receipt",
            status = "Active",
            companyId = company.Id,
            xmin = customer.Xmin
        }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning(
                "CustomerService returned {Status} while linking company billing identity for customer {CustomerId}: {Body}",
                response.StatusCode,
                customerId,
                body);
            return null;
        }

        var updated = await response.Content.ReadFromJsonAsync<CsCustomerResponse>(cancellationToken: ct);
        return updated is null ? null : MapCustomer(updated);
    }

    public async Task<CustomerProfileResponse?> UpdateCustomerProfileAsync(
        Guid customerId,
        string displayName,
        string? phone,
        string? companyName,
        string? vatNumber,
        string preferredLanguage,
        string timezone,
        CancellationToken ct = default)
    {
        var customer = await GetRawCustomerByIdAsync(customerId, ct);
        if (customer is null)
        {
            return null;
        }

        var companyId = customer.CompanyId;
        if (companyId is null && !string.IsNullOrWhiteSpace(companyName))
        {
            var company = await CreateCompanyAsync(
                companyName.Trim(),
                vatNumber,
                string.IsNullOrWhiteSpace(phone) ? customer.Mobile : phone,
                customer.Email,
                ct);
            companyId = company?.Id;
        }

        var (firstName, lastName) = SplitName(displayName, customer.Email);
        using var response = await http.PatchAsJsonAsync($"/customer/v1/customers/{customerId:D}", new
        {
            firstName,
            lastName,
            email = customer.Email,
            mobile = string.IsNullOrWhiteSpace(phone) ? customer.Mobile : phone,
            segment = string.IsNullOrWhiteSpace(customer.Segment) ? "Self-service manufacturing" : customer.Segment,
            tier = string.IsNullOrWhiteSpace(customer.Tier) ? "Customer" : customer.Tier,
            preferredLanguage = string.IsNullOrWhiteSpace(preferredLanguage) ? customer.PreferredLanguage : preferredLanguage,
            timezone = string.IsNullOrWhiteSpace(timezone) ? customer.Timezone : timezone,
            paymentTerms = "Due on receipt",
            status = "Active",
            companyId,
            xmin = customer.Xmin
        }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning(
                "CustomerService returned {Status} while updating profile for customer {CustomerId}: {Body}",
                response.StatusCode,
                customerId,
                body);
            return null;
        }

        var updated = await response.Content.ReadFromJsonAsync<CsCustomerResponse>(cancellationToken: ct);
        return updated is null ? null : MapCustomer(updated);
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

    public async Task<IReadOnlyList<CustomerDocumentDto>?> GetCustomerDocumentsAsync(
        Guid customerId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(
                $"/customer/v1/documents?ownerType=Customer&ownerId={customerId:D}",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "CustomerService returned {Status} while listing documents for customer {CustomerId}: {Body}",
                    response.StatusCode,
                    customerId,
                    body);
                return null;
            }

            var documents = await response.Content.ReadFromJsonAsync<List<CsDocumentResponse>>(
                cancellationToken: cancellationToken);
            return documents?.Select(MapDocument).ToList() ?? [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CustomerService document list failed for customer {CustomerId}.", customerId);
            return null;
        }
    }

    public async Task<CustomerDocumentDto?> CreateCustomerDocumentAsync(
        Guid customerId,
        CustomerDocumentUploadRequest request,
        CancellationToken cancellationToken)
    {
        var document = new CsCreateDocumentRequest
        {
            OwnerId = customerId,
            DocumentType = request.Kind.Trim(),
            FileReference = request.StoragePath.Trim(),
            Filename = request.FileName.Trim(),
            FileSize = request.FileSizeBytes,
            MimeType = request.ContentType.Trim()
        };

        try
        {
            using var response = await http.PostAsJsonAsync("/customer/v1/documents", document, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "CustomerService returned {Status} while creating document for customer {CustomerId}: {Body}",
                    response.StatusCode,
                    customerId,
                    body);
                return null;
            }

            var created = await response.Content.ReadFromJsonAsync<CsDocumentResponse>(
                cancellationToken: cancellationToken);
            return created is null ? null : MapDocument(created);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CustomerService document create failed for customer {CustomerId}.", customerId);
            return null;
        }
    }

    public async Task<IReadOnlyList<CustomerNdaDto>?> GetCustomerNdasAsync(
        Guid customerId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync($"/customer/v1/ndas/customer/{customerId:D}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "CustomerService returned {Status} while listing NDAs for customer {CustomerId}: {Body}",
                    response.StatusCode,
                    customerId,
                    body);
                return null;
            }

            var ndas = await response.Content.ReadFromJsonAsync<List<CsNdaResponse>>(
                cancellationToken: cancellationToken);
            return ndas?.Select(MapNda).ToList() ?? [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CustomerService NDA list failed for customer {CustomerId}.", customerId);
            return null;
        }
    }

    public async Task<CustomerMemoryQueryResponse> GetCustomerMemoriesAsync(
        Guid customerId,
        string? query,
        int limit,
        CancellationToken cancellationToken)
    {
        var normalizedLimit = Math.Clamp(limit, 1, 25);
        var path = $"/customer/v1/customers/{customerId:D}/memories?limit={normalizedLimit}";
        if (!string.IsNullOrWhiteSpace(query))
        {
            path += $"&query={Uri.EscapeDataString(query.Trim())}";
        }

        try
        {
            var response = await http.GetFromJsonAsync<CustomerMemoryQueryResponse>(path, cancellationToken);
            return response ?? new CustomerMemoryQueryResponse { CustomerId = customerId, Limit = normalizedLimit };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CustomerService memory query failed for customer {CustomerId}.", customerId);
            return new CustomerMemoryQueryResponse { CustomerId = customerId, Limit = normalizedLimit };
        }
    }

    public async Task<CustomerMemoryResponse?> ObserveCustomerMemoryAsync(
        Guid customerId,
        CustomerMemoryObserveRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                $"/customer/v1/customers/{customerId:D}/memories/observe",
                request,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "CustomerService returned {Status} while observing memory for customer {CustomerId}.",
                    response.StatusCode,
                    customerId);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<CustomerMemoryResponse>(cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CustomerService memory observe failed for customer {CustomerId}.", customerId);
            return null;
        }
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

    private static CustomerDocumentDto MapDocument(CsDocumentResponse result)
    {
        var uploadedAt = result.CreatedAt == default
            ? result.UpdatedAt == default ? DateTimeOffset.UtcNow : result.UpdatedAt
            : result.CreatedAt;
        return new CustomerDocumentDto(
            result.Id,
            string.IsNullOrWhiteSpace(result.Filename) ? "customer-document" : result.Filename,
            string.IsNullOrWhiteSpace(result.DocumentType) ? "Requirement" : result.DocumentType,
            uploadedAt,
            string.IsNullOrWhiteSpace(result.FileReference) ? null : result.FileReference,
            string.IsNullOrWhiteSpace(result.MimeType) ? null : result.MimeType,
            result.FileSize);
    }

    private static CustomerNdaDto MapNda(CsNdaResponse result)
    {
        var updatedAt = result.UpdatedAt == default
            ? result.SignedAt ?? result.CreatedAt
            : result.UpdatedAt;
        return new CustomerNdaDto(
            result.Id,
            "Mutual NDA",
            string.IsNullOrWhiteSpace(result.Status) ? "Unknown" : result.Status,
            updatedAt == default ? DateTimeOffset.UtcNow : updatedAt,
            result.ExpiresAt);
    }

    private async Task<CsCustomerResponse?> GetRawCustomerByIdAsync(Guid customerId, CancellationToken ct)
    {
        try
        {
            return await http.GetFromJsonAsync<CsCustomerResponse>(
                $"/customer/v1/customers/{customerId:D}", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CustomerService GetById failed for {CustomerId}.", customerId);
            return null;
        }
    }

    private async Task<CsCompanyResponse?> CreateCompanyAsync(
        string companyName,
        string? vatNumber,
        string? phone,
        string? email,
        CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/customer/v1/companies", new
        {
            name = companyName,
            vatNumber = NormalizeCompanyVatNumber(vatNumber),
            registrationNumber = $"QE-{Guid.NewGuid():N}"[..13],
            contactEmail = email,
            contactPhone = phone,
            segment = "Enterprise",
            tier = "Gold"
        }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning(
                "CustomerService returned {Status} while creating company billing identity for {CompanyName}: {Body}",
                response.StatusCode,
                companyName,
                body);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<CsCompanyResponse>(cancellationToken: ct);
    }

    private static string? NormalizeCompanyVatNumber(string? vatNumber)
    {
        if (string.IsNullOrWhiteSpace(vatNumber))
        {
            return null;
        }

        var digits = new string(vatNumber.Where(char.IsDigit).ToArray());
        return digits.Length is >= 10 and <= 15 ? digits : vatNumber.Trim();
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
