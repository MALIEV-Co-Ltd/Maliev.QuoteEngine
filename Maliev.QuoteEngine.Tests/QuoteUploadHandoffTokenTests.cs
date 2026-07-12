using Maliev.QuoteEngine.Bff.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteUploadHandoffTokenTests
{
    [Fact]
    public void Staging_without_a_signing_key_fails_closed_instead_of_using_the_public_fallback()
    {
        var verifier = new QuoteUploadHandoffToken(
            new ConfigurationBuilder().Build(),
            new QuoteEngineWebApplicationFactory.TestHostEnvironment(Environments.Staging));

        Assert.Throws<InvalidOperationException>(() => verifier.TryRead("payload.signature", out _));
    }
}
