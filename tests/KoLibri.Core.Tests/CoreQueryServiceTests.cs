using Xunit;
using KoLibri.Core.Markt;

namespace KoLibri.Core.Tests;

public sealed class CoreQueryServiceTests
{
    private static readonly Uri Source = new("https://fixture.invalid/mall");
    private static readonly DateTimeOffset At = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ArbitrageQueryRequiresExplicitTradeabilityAndTransferCost()
    {
        var service = new CoreArbitrageQueryService(options: new ArbitrageQueryOptions
        {
            Purchase = new UnderpricedPurchaseLimits { MinimumMarginPercent = 0m }
        });
        var snapshot = Snapshot(At);

        var result = service.Query(new[] { new ArbitrageObservation(snapshot) }, new ArbitrageQuery
        {
            IncludeRejected = true,
            AsOf = At
        }, At);

        var row = Assert.Single(result.Candidates, x => x.SellerId == "seller-a");
        Assert.Equal(CoreArbitrageDecision.Reject, row.Decision);
        Assert.Contains("tradeability_unknown", row.ReasonCodes);
        Assert.Contains("transfer_cost_unknown", row.ReasonCodes);
        Assert.False(row.IsAccepted);
    }

    [Fact]
    public void ArbitrageQueryUsesPlannerAndMarksAcceptedRowsPreviewOnly()
    {
        var service = new CoreArbitrageQueryService(options: new ArbitrageQueryOptions
        {
            Purchase = new UnderpricedPurchaseLimits
            {
                MinimumMarginPercent = 0m,
                MinimumAbsoluteProfit = 0m
            }
        });
        var result = service.Query(new[]
        {
            new ArbitrageObservation(Snapshot(At))
            {
                Tradeability = new UnderpricedItemTradeability(),
                TransferCost = 0m
            }
        }, new ArbitrageQuery { AsOf = At }, At);

        var row = Assert.Single(result.Candidates, x => x.SellerId == "seller-a");
        Assert.Equal(CoreArbitrageDecision.Buy, row.Decision);
        Assert.True(row.RequiresApproval);
        Assert.True(row.TransferCostKnown);
        Assert.Equal(0m, row.TransferCost);
        Assert.True(row.ExpectedNetProfit > 0m);
    }

    [Fact]
    public void StaleAndNonTradeableRowsAreHeldOrRejectedWithReasonCodes()
    {
        var service = new CoreArbitrageQueryService(options: new ArbitrageQueryOptions
        {
            MaximumSnapshotAge = TimeSpan.FromMinutes(5),
            Purchase = new UnderpricedPurchaseLimits { MinimumMarginPercent = 0m }
        });
        var old = At.AddHours(-1);
        var result = service.Query(new[]
        {
            new ArbitrageObservation(Snapshot(old, stale: true))
            {
                Tradeability = new UnderpricedItemTradeability(false, isAccountBound: true),
                TransferCost = 10m
            }
        }, new ArbitrageQuery { IncludeRejected = true, AsOf = At }, At);

        var row = Assert.Single(result.Candidates, x => x.SellerId == "seller-a");
        Assert.True(row.IsStale);
        Assert.Contains("stale_snapshot", row.ReasonCodes);
        Assert.Contains("non_tradeable", row.ReasonCodes);
        Assert.Contains("account_bound", row.ReasonCodes);
        Assert.Equal(CoreQueryState.Stale, result.State);
    }

    [Fact]
    public void ShopQueryDelegatesToRepricePlannerAndRemainsReadOnly()
    {
        var own = new OwnShopListing("42", 500, 2, null, At, "Example");
        var service = new CoreShopQueryService(options: new ShopQueryOptions
        {
            Policy = new RepricePolicy
            {
                MinimumPrice = 100m,
                RequireReviewForHistoricalPremium = false
            }
        });
        var result = service.Query(new[]
        {
            new ShopObservation(own, Snapshot(At))
            {
                Tradeability = new UnderpricedItemTradeability()
            }
        }, new ShopQuery { AsOf = At }, At);

        var row = Assert.Single(result.Candidates);
        Assert.Equal("42", row.ItemId);
        Assert.True(row.RecommendedPrice is > 0);
        Assert.True(row.IsPreviewOnly);
        Assert.Equal(500, own.Price);
    }

    private static PriceSnapshot Snapshot(DateTimeOffset fetchedAt, bool stale = false)
        => new(
            "42",
            new[]
            {
                new MallListing("42", "seller-a", "A", 100, 2, null, fetchedAt, Source),
                new MallListing("42", "seller-b", "B", 300, 2, null, fetchedAt, Source)
            },
            new[] { new PricegunSale(fetchedAt, 500, 2) },
            fetchedAt: fetchedAt,
            pricegunFetchedAt: fetchedAt,
            mallIsStale: stale,
            pricegunIsStale: stale);
}
