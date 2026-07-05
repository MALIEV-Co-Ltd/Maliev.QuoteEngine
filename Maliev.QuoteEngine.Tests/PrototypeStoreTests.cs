using Maliev.QuoteEngine.Bff.Services;

namespace Maliev.QuoteEngine.Tests;

public sealed class PrototypeStoreTests
{
    [Fact]
    public void TryGetProfile_resolves_the_prototype_demo_customer()
    {
        var store = new QuoteEnginePrototypeStore();

        var found = store.TryGetProfile(store.PrototypeCustomer.CustomerId, out var profile);

        Assert.True(found);
        Assert.NotNull(profile);
        Assert.Equal(store.PrototypeCustomer.CustomerId, profile!.CustomerId);
        Assert.Equal("Demo Customer", profile.DisplayName);
    }

    [Fact]
    public void TryGetProfile_returns_false_for_an_unknown_customer()
    {
        var store = new QuoteEnginePrototypeStore();

        var found = store.TryGetProfile(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), out var profile);

        Assert.False(found);
        Assert.Null(profile);
    }
}
