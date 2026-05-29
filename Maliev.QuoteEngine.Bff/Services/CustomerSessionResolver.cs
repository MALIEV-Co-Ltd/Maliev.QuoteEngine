using System.Security.Claims;

namespace Maliev.QuoteEngine.Bff.Services;

public sealed class CustomerSessionResolver(
    IHttpContextAccessor httpContextAccessor,
    QuoteEnginePrototypeStore store,
    IHostEnvironment environment)
{
    /// <summary>
    /// Returns the authenticated customer ID, or the prototype customer in dev/test.
    /// Never falls back to prototype data in production.
    /// </summary>
    public Guid ResolveCustomerId()
    {
        if (TryResolveCustomerId(out var customerId))
            return customerId;

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            return store.PrototypeCustomer.CustomerId;

        throw new InvalidOperationException("Customer identity not found. User must be authenticated.");
    }

    /// <summary>
    /// Resolves the customer ID strictly from the authenticated user's claims.
    /// Returns false when the user is not signed in.
    /// </summary>
    public bool TryResolveCustomerId(out Guid customerId)
    {
        var user = httpContextAccessor.HttpContext?.User;
        var rawCustomerId = user?.FindFirstValue("customer_id")
            ?? user?.FindFirstValue("customerId")
            ?? user?.FindFirstValue(ClaimTypes.NameIdentifier);

        return Guid.TryParse(rawCustomerId, out customerId) && customerId != Guid.Empty;
    }
}
