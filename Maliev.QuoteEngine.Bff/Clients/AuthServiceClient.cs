using System.Net.Http.Json;

namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>
/// Downstream AuthService client. Customer sign-in completes against AuthService
/// directly — the QuoteEngine BFF runs the browser OAuth flow and credential
/// exchange itself, with no dependency on the Maliev.Web frontend.
/// </summary>
public interface IAuthServiceClient
{
    /// <summary>Signs in with AuthService email/password login.</summary>
    Task<HttpResponseMessage> LoginAsync(object request, CancellationToken cancellationToken);

    /// <summary>Issues a one-time nonce for the QuoteEngine customer GIS exchange.</summary>
    Task<HttpResponseMessage> IssueCustomerGoogleNonceAsync(object request, CancellationToken cancellationToken);

    /// <summary>Exchanges a verified customer Google identity for a MALIEV customer session.</summary>
    Task<HttpResponseMessage> ExchangeCustomerGoogleAsync(object request, CancellationToken cancellationToken);
}

internal sealed class AuthServiceClient(HttpClient httpClient) : IAuthServiceClient
{
    public Task<HttpResponseMessage> LoginAsync(object request, CancellationToken cancellationToken) =>
        httpClient.PostAsJsonAsync("/auth/v1/login", request, cancellationToken);

    public Task<HttpResponseMessage> IssueCustomerGoogleNonceAsync(object request, CancellationToken cancellationToken) =>
        httpClient.PostAsJsonAsync("/auth/v1/exchange/google/customer/nonce", request, cancellationToken);

    public Task<HttpResponseMessage> ExchangeCustomerGoogleAsync(object request, CancellationToken cancellationToken) =>
        httpClient.PostAsJsonAsync("/auth/v1/exchange/google/customer", request, cancellationToken);
}

/// <summary>
/// Raw CustomerService registration call used by studio sign-up. Kept separate
/// from <see cref="ICustomerServiceClient"/> so the customer's own password can
/// be forwarded without widening that interface.
/// </summary>
public interface ICustomerRegistrationClient
{
    /// <summary>Registers a customer account with the customer's chosen password.</summary>
    Task<HttpResponseMessage> RegisterCustomerAsync(object request, CancellationToken cancellationToken);
}

internal sealed class CustomerRegistrationClient(HttpClient httpClient) : ICustomerRegistrationClient
{
    public Task<HttpResponseMessage> RegisterCustomerAsync(object request, CancellationToken cancellationToken) =>
        httpClient.PostAsJsonAsync("/customer/v1/customers/register", request, cancellationToken);
}
