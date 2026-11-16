using KolArbitrage.Core.Integration;
using KolArbitrage.Core.Pricing;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class MarketPriceEstimatorTests
{
    private static readonly Uri Source = new("https://fixture.invalid/mall");
    private static readonly DateTimeOffset At = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UsesIndependentTopNAndExcludesOwnPriceRows()
    {
        var listings = Enumerable.Range(1, 6)
            .Select(i => new MallListing("42", $"store-{i}", null, i * 100, i, null, At, Source))
            .ToArray();
        var own = new[] { new OwnShopListing("42", 100, 1, null, At) };
        var snapshot = new PriceSnapshot("42", listings, null, own, At);

        var estimate = new MarketPriceEstimator(new MarketPriceEstimatorOptions { TopN = 5 })
            .Estimate(snapshot, At);

        Assert.True(estimate.OwnStoreContaminatesMarket);
        Assert.Equal(200m, estimate.LowestPrice);
        Assert.Equal(300m, estimate.SecondPrice);
        Assert.Equal(600m, estimate.FifthPrice);
        Assert.Equal(2 + 3 + 4 + 5 + 6, estimate.CumulativeQuantityTopN);
        Assert.Equal(400m, estimate.MallMedian);
    }

    [Fact]
    public void BlendsHistoryAndAppliesMallSaleFees()
    {
        var listings = new[]
        {
            new MallListing("42", "a", null, 100, 1, null, At, Source),
            new MallListing("42", "b", null, 120, 1, null, At, Source),
            new MallListing("42", "c", null, 140, 1, null, At, Source)
        };
        var sales = new[]
        {
            new PricegunSale(At, 200, 1),
            new PricegunSale(At, 220, 3),
            new PricegunSale(At, 240, 1)
        };
        var estimate = new MarketPriceEstimator().Estimate(new PriceSnapshot("42", listings, sales, At), At);

        Assert.Equal(220m, estimate.PricegunMedian);
        Assert.Equal(220m, estimate.PricegunWeightedAverage);
        Assert.Equal(120m, estimate.ConservativeSellPrice);
        Assert.Equal(108.80m, estimate.NetSale(1));
    }

    [Fact]
    public void MarksOldSourcesStaleAndReducesConfidence()
    {
        var old = At.AddHours(-2);
        var listing = new MallListing("42", "a", null, 100, 1, null, old, Source);
        var estimate = new MarketPriceEstimator(new MarketPriceEstimatorOptions { MaxAge = TimeSpan.FromMinutes(20) })
            .Estimate(new PriceSnapshot("42", new[] { listing }, fetchedAt: old), At);

        Assert.True(estimate.IsStale);
        Assert.True(estimate.Confidence < 0.35m);
    }
}
