namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>
/// Raised when a caller supplies a storage resource that is not registered to the authorized agent
/// session. The HTTP boundary maps this to the same empty not-found response for every path.
/// </summary>
internal sealed class QuoteAgentSessionResourceNotFoundException : Exception;
