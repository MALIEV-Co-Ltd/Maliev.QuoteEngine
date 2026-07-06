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

    [Fact]
    public void Seeds_prototype_sales_history_so_orders_and_quotes_surfaces_are_populated()
    {
        var store = new QuoteEnginePrototypeStore();
        var customerId = store.PrototypeCustomer.CustomerId;

        var orders = store.GetOrders(customerId);
        var quotes = store.GetQuotes(customerId);

        Assert.NotEmpty(orders);
        Assert.NotEmpty(quotes);
        Assert.Contains(orders, o => o.Status == "Paid");
        Assert.All(quotes, q => Assert.Equal("THB", q.Currency));
    }

    [Fact]
    public void Seeded_order_has_detail_that_can_be_marked_paid()
    {
        var store = new QuoteEnginePrototypeStore();
        var customerId = store.PrototypeCustomer.CustomerId;

        var detail = store.GetOrderDetail(customerId, "MO-DEMO-0001");
        Assert.NotNull(detail);
        Assert.Equal("MO-DEMO-0001", detail!.OrderNumber);
        Assert.NotEmpty(detail.StatusHistory);
        Assert.NotEmpty(detail.ManufacturingMilestones);

        Assert.True(store.TryResolveOrderNumber(customerId, detail.OrderId, out var resolved));
        Assert.Equal("MO-DEMO-0001", resolved);

        Assert.True(store.MarkOrderPaid(customerId, "MO-DEMO-0001"));
        var paid = store.GetOrderDetail(customerId, "MO-DEMO-0001");
        Assert.Equal("Paid", paid!.PaymentStatus);
        Assert.Equal("Paid", paid.CurrentStatus);
        Assert.Contains(store.GetOrders(customerId), o => o.OrderNumber == "MO-DEMO-0001" && o.Status == "Paid");
    }

    [Fact]
    public void Order_detail_is_hidden_from_a_foreign_customer()
    {
        var store = new QuoteEnginePrototypeStore();

        Assert.Null(store.GetOrderDetail(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), "MO-DEMO-0001"));
        Assert.False(store.TryResolveOrderNumber(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"), out _));
    }
}
