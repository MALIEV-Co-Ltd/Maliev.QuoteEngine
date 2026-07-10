using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Tests;

public sealed class PrototypeStoreTests
{
    [Theory]
    [InlineData(1, 215, 215, false)]
    [InlineData(5, 595, 119, false)]
    [InlineData(7, 785, 112.14, true)]
    public void Estimate_charges_setup_once_per_line_and_derives_unit_price_from_total(
        int quantity,
        decimal expectedTotal,
        decimal expectedUnitPrice,
        bool expectedApproximateUnitPrice)
    {
        var store = new QuoteEnginePrototypeStore();
        var request = new QuoteEstimateRequest
        {
            QuoteSessionId = $"prototype-setup-once-{quantity}",
            LeadTimeCode = "STANDARD",
            Parts =
            [
                new QuotePartDraftDto
                {
                    PartId = Guid.NewGuid(),
                    FileId = Guid.NewGuid(),
                    UploadId = $"prototype-setup-once-{quantity}",
                    FileName = "prototype-part.step",
                    ProcessId = "fdm",
                    MaterialId = "pla",
                    FinishId = "fdm-matte",
                    FinishCode = "MATTE",
                    ToleranceId = "fdm-standard",
                    ToleranceCode = "FDM_STANDARD",
                    InspectionLevel = "STANDARD",
                    Quantity = quantity,
                    VolumeCc = 1m,
                    DfmAcknowledged = true
                }
            ]
        };

        var estimate = store.Estimate(request);

        Assert.Equal(expectedTotal, estimate.Subtotal);
        Assert.Equal(expectedTotal, estimate.Total);
        var line = Assert.Single(estimate.Lines);
        Assert.Equal(expectedTotal, line.LineTotal);
        Assert.Equal(expectedUnitPrice, line.UnitPrice);
        Assert.Equal(expectedApproximateUnitPrice, line.Notes.Contains("approximate", StringComparison.OrdinalIgnoreCase));
        if (expectedApproximateUnitPrice)
        {
            Assert.Contains($"line total remains {expectedTotal:0.##} THB", line.Notes, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("authoritative", line.Notes, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Quote_estimate_provenance_defaults_fail_closed()
    {
        var estimate = new QuoteEstimateResponse(
            "unknown-provenance",
            100m,
            0m,
            100m,
            "THB",
            true,
            []);

        Assert.Equal("unknown", estimate.PricingSource);
        Assert.False(estimate.IsAuthoritative);
    }

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
