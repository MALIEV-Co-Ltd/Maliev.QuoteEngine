using System.Security.Claims;

namespace Maliev.QuoteEngine.Bff.Services;

public sealed class CustomerSessionResolver(IHttpContextAccessor httpContextAccessor, QuoteEnginePrototypeStore store)
{
    public Guid ResolveCustomerId()
    {
        var user = httpContextAccessor.HttpContext?.User;
        var rawCustomerId = user?.FindFirstValue("customer_id")
            ?? user?.FindFirstValue("customerId")
            ?? user?.FindFirstValue(ClaimTypes.NameIdentifier);

        return Guid.TryParse(rawCustomerId, out var customerId)
            ? customerId
            : store.PrototypeCustomer.CustomerId;
    }
}
