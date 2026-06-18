namespace Maliev.QuoteEngine.Bff.Security;

/// <summary>
/// Named rate-limiter policy identifiers for the QuoteEngine BFF.
/// </summary>
public static class BffRateLimiterPolicies
{
    /// <summary>
    /// Limits the cost-bearing, abuse-prone agent endpoints (chat turns, speech cleanup), partitioned by
    /// anonymous-visitor id when a valid cookie is present, otherwise by client IP (the abuse floor).
    /// </summary>
    public const string QuoteAgent = "quote-agent";
}
