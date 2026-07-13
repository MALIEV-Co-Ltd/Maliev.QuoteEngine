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

    /// <summary>Limits creation of downstream resumable upload sessions.</summary>
    public const string UploadInitiate = "quote-upload-initiate";

    /// <summary>Limits inexpensive upload completion acknowledgements.</summary>
    public const string UploadFinalize = "quote-upload-finalize";

    /// <summary>Limits signed cross-application upload handoff fan-out.</summary>
    public const string UploadHandoff = "quote-upload-handoff";

    /// <summary>Limits concurrent resumable upload streams per trusted client IP.</summary>
    public const string UploadStream = "quote-upload-stream";

    /// <summary>Limits deterministic estimate requests and their downstream pricing fan-out.</summary>
    public const string Estimate = "quote-estimate";

    /// <summary>Limits browser-computed DFM reports and browser runtime telemetry.</summary>
    public const string BrowserReport = "quote-browser-report";

    /// <summary>Limits sketch uploads that buffer and forward image data.</summary>
    public const string SketchUpload = "quote-sketch-upload";
}
