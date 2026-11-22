using Xunit;
using KoLibri.Core.Markt;

namespace KoLibri.Core.Tests;

public sealed class ItemArbitragePlannerTests
{
    private static readonly Uri Source = new("https://kol.test/mall.php");

    private static MallListing Offer(string store, long price, int stock, int? limit = 1,
        MallLimitKind kind = MallLimitKind.Limited, bool known = true)
        => new("10881", store, "Shop " + store, price, stock, limit, DateTimeOffset.UtcNow, Source)
        { LimitKind = kind, QuantityKnown = known };

    private static ItemArbitrageInputs Inputs(IEnumerable<MallListing> offers, long? own = 32_995, long? median = 30_000, long? meat = null)
        => new() { ItemId = 10881, ItemName = "11-leaf clover", Offers = offers.ToArray(), OwnShopPrice = own, PricegunMedian = median, MeatAvailable = meat };

    [Fact]
    public void Reference_is_the_lower_of_own_shop_price_and_pricegun_median_and_the_ceiling_is_the_margin_below()
    {
        var plan = ItemArbitragePlanner.Plan(Inputs([]));
        Assert.Equal(30_000, plan.ReferencePrice);
        Assert.Equal("Pricegun median", plan.ReferenceSource);
        Assert.Equal(27_000, plan.BuyCeiling);

        Assert.Equal("own shop price", ItemArbitragePlanner.Plan(Inputs([], own: 28_000)).ReferenceSource);
        Assert.Equal(25_200, ItemArbitragePlanner.Plan(Inputs([], own: 28_000)).BuyCeiling);
    }

    [Fact]
    public void Offers_above_the_ceiling_are_skipped_with_a_reason_and_max_price_only_lowers_the_ceiling()
    {
        var offers = new[] { Offer("1", 25_000, 100, null, MallLimitKind.Unlimited), Offer("2", 29_029, 110) };
        var plan = ItemArbitragePlanner.Plan(Inputs(offers));
        Assert.Equal(["1"], plan.Steps.Select(s => s.StoreId));
        var skip = Assert.Single(plan.Skipped);
        Assert.Equal(ArbitrageSkipReason.AboveCeiling, skip.Reason);
        Assert.Contains("ceiling", skip.Message);

        var capped = ItemArbitragePlanner.Plan(Inputs(offers), new ItemArbitrageSettings { MaxPrice = 24_000 });
        Assert.Empty(capped.Steps);
        Assert.Equal(24_000, capped.BuyCeiling);
        var raised = ItemArbitragePlanner.Plan(Inputs(offers), new ItemArbitrageSettings { MaxPrice = 99_999 });
        Assert.Equal(27_000, raised.BuyCeiling);
    }

    [Fact]
    public void Never_more_than_the_daily_limit_per_shop_and_unknown_limits_plan_one_unit()
    {
        var plan = ItemArbitragePlanner.Plan(Inputs([
            Offer("1", 25_000, 1_460, 3),
            Offer("2", 25_500, 50, null, MallLimitKind.Unknown),
            Offer("3", 26_000, 7, null, MallLimitKind.Unlimited)]));
        Assert.Equal([3, 1, 7], plan.Steps.Select(s => s.Quantity));
        Assert.Contains("unknown", plan.Steps[1].Note);
        Assert.Equal(11, plan.TotalUnits);
    }

    [Fact]
    public void Budget_limits_units_and_total_and_later_offers_say_why_they_were_skipped()
    {
        var offers = new[] { Offer("1", 25_000, 100, null, MallLimitKind.Unlimited), Offer("2", 26_000, 100, null, MallLimitKind.Unlimited) };
        var plan = ItemArbitragePlanner.Plan(Inputs(offers), new ItemArbitrageSettings { Budget = 130_000 });
        Assert.Equal(5, plan.Steps[0].Quantity);
        Assert.Equal(125_000, plan.TotalCost);
        Assert.True(plan.TotalCost <= 130_000);
        Assert.Equal(ArbitrageSkipReason.BudgetExhausted, Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void Meat_reserve_keeps_meat_back()
    {
        var offers = new[] { Offer("1", 25_000, 100, null, MallLimitKind.Unlimited) };
        var plan = ItemArbitragePlanner.Plan(Inputs(offers, meat: 160_000), new ItemArbitrageSettings { MeatReserve = 60_000 });
        Assert.Equal(4, plan.TotalUnits);
        Assert.True(160_000 - plan.TotalCost >= 60_000);

        var none = ItemArbitragePlanner.Plan(Inputs(offers, meat: 50_000), new ItemArbitrageSettings { MeatReserve = 60_000 });
        Assert.Empty(none.Steps);
        Assert.Equal(ArbitrageSkipReason.ReserveReached, Assert.Single(none.Skipped).Reason);
    }

    [Fact]
    public void Units_per_run_stock_own_shop_and_store_problems_are_skipped_with_reasons()
    {
        var plan = ItemArbitragePlanner.Plan(Inputs([
            Offer("1", 25_000, 10, null, MallLimitKind.Unlimited),
            Offer("2", 25_100, 10, null, MallLimitKind.Unlimited),
            Offer("3", 24_000, 0),
            Offer("4", 24_100, 0, known: false),
            Offer("5", 24_200, 5) with { LimitKind = MallLimitKind.Limited },
            Offer("x", 24_300, 5)]),
            new ItemArbitrageSettings { UnitsPerRun = 10 });
        var reasons = plan.Skipped.ToDictionary(s => s.StoreId, s => s.Reason);
        Assert.Equal(ArbitrageSkipReason.OutOfStock, reasons["3"]);
        Assert.Equal(ArbitrageSkipReason.StockUnknown, reasons["4"]);
        Assert.Equal(ArbitrageSkipReason.NoStoreId, reasons["x"]);
        // Offer 5 (1 per day) is bought first, so only 9 of the unlimited offer 1 remain under the 10-unit cap.
        Assert.Equal(10, plan.TotalUnits);
        Assert.Equal(ArbitrageSkipReason.UnitsPerRunReached, reasons["2"]);
        Assert.All(plan.Skipped, s => Assert.False(string.IsNullOrWhiteSpace(s.Message)));

        var own = ItemArbitragePlanner.Plan(
            Inputs([Offer("1", 25_000, 5, null, MallLimitKind.Unlimited)]) with { OwnStoreIds = new HashSet<string> { "1" } });
        Assert.Equal(ArbitrageSkipReason.OwnShop, Assert.Single(own.Skipped).Reason);
    }

    [Fact]
    public void Profit_uses_the_net_price_after_mall_tax_and_no_reference_means_no_plan()
    {
        var plan = ItemArbitragePlanner.Plan(Inputs([Offer("1", 25_000, 2, null, MallLimitKind.Unlimited)]),
            new ItemArbitrageSettings { MallTaxPercent = 10 });
        Assert.Equal(27_000, plan.NetResalePrice);
        Assert.Equal(24_300, plan.BuyCeiling);
        Assert.Empty(plan.Steps);

        var profitable = ItemArbitragePlanner.Plan(Inputs([Offer("1", 25_000, 2, null, MallLimitKind.Unlimited)]));
        Assert.Equal(5_000, profitable.Steps[0].ProfitPerUnit);
        Assert.Equal(10_000, profitable.TotalExpectedProfit);

        var none = ItemArbitragePlanner.Plan(Inputs([Offer("1", 1, 1)], own: null, median: null));
        Assert.Empty(none.Steps);
        Assert.Null(none.BuyCeiling);
        Assert.Single(none.Skipped);
    }

    [Theory]
    [InlineData(-1.0, 0.0, 1)]
    [InlineData(95.0, 0.0, 1)]
    [InlineData(10.0, 60.0, 1)]
    [InlineData(10.0, 0.0, 0)]
    public void Settings_outside_their_limits_are_refused(double margin, double tax, int units)
        => Assert.Throws<ArgumentOutOfRangeException>(() => ItemArbitragePlanner.Plan(Inputs([]),
            new ItemArbitrageSettings { MarginPercent = margin, MallTaxPercent = tax, UnitsPerRun = units }));

    [Fact]
    public async Task The_service_reads_the_mall_by_item_id_and_plans_from_the_live_offers()
    {
        var mall = new FakeMall(Offer("1", 25_000, 5, null, MallLimitKind.Unlimited));
        var plan = await ItemArbitrageService.PlanAsync(mall, null, 10881,
            new ItemArbitrageSettings { ReferencePrice = 30_000 });
        Assert.Equal("10881", mall.LastTerm);
        Assert.Equal(5, plan.TotalUnits);
        Assert.Equal("configured", plan.ReferenceSource);
    }

    private sealed class FakeMall(params MallListing[] listings) : IMallReadClient
    {
        public string? LastTerm { get; private set; }
        public Task<MallSnapshot> SearchAsync(string itemId, bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new MallSnapshot(itemId, listings, DateTimeOffset.UtcNow, Source));
        public Task<MallSearchPage> SearchItemsAsync(string term, bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            LastTerm = term;
            return Task.FromResult(new MallSearchPage(term,
                [new MallItemOffers("10881", "11-leaf clover", listings, listings.Length)], DateTimeOffset.UtcNow, Source));
        }
        public Task<IReadOnlyList<OwnShopListing>> FetchOwnShopAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OwnShopListing>>([]);
    }
}
