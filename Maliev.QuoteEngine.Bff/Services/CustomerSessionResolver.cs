using System.Security.Claims;

namespace Maliev.QuoteEngine.Bff.Services;

public sealed class CustomerSessionResolver(IHttpContextAccessor httpContextAccessor, QuoteEnginePrototypeStore store)
{
    public Guid ResolveCustomerId()
    {
        return TryResolveCustomerId(out var customerId)
            ? customerId
            : store.PrototypeCustomer.CustomerId;
    }

    public bool TryResolveCustomerId(out Guid customerId)
    {
        var context = httpContextAccessor.HttpContext;
        var user = context?.User;
        var rawCustomerId = user?.FindFirstValue("customer_id")
            ?? user?.FindFirstValue("customerId")
            ?? user?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? (context?.Request.Cookies.TryGetValue("maliev_quote_customer", out var cookieCustomerId) == true
                ? cookieCustomerId
                : null);

        return Guid.TryParse(rawCustomerId, out customerId) && customerId != Guid.Empty;
    }
}
