namespace Maliev.QuoteEngine.Bff.Options;

internal sealed class QuoteAgentRetentionOptions
{
    public const string Section = "QuoteAgent:Retention";

    public static readonly TimeSpan MaximumAnonymousRetention = TimeSpan.FromDays(30);

    public static readonly TimeSpan MaximumCustomerRetention = TimeSpan.FromDays(365);

    public TimeSpan Anonymous { get; set; } = MaximumAnonymousRetention;

    public TimeSpan Customer { get; set; } = MaximumCustomerRetention;

    public bool HasValidBounds =>
        Anonymous > TimeSpan.Zero &&
        Anonymous <= MaximumAnonymousRetention &&
        Customer >= Anonymous &&
        Customer <= MaximumCustomerRetention;
}
