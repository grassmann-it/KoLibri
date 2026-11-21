using Xunit;
using KolArbitrage.Core.Markt;

namespace KolArbitrage.Core.Tests;

public sealed class RepricePlannerTests
{
    private static readonly Uri Source = new("https://fixture.invalid/mall");
    private static readonly DateTimeOffset At = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UsesIndependentSecondAnchorAndKeepsAllMarketEvidenceInPreview()
    {
        var listings = Enumerable.Range(1, 6)
            .Select(i => new MallListing("42", $"store-{i}", null, i * 100, 1, null, At, Source))
            .ToArray();
        var own = new[] { new OwnShopListing("42", 100, 2, null, At, "ours") };
        var sales = new[]
        {
            new PricegunSale(At, 800, 1),
            new PricegunSale(At, 900, 1),
            new PricegunSale(At, 1000, 1)
        };
        var estimate = new MarketPriceEstimator(new MarketPriceEstimatorOptions { TopN = 5 })
            .Estimate(new PriceSnapshot("42", listings, sales, own, At), At);

        var row = Assert.Single(new RepricePlanner(new RepricePolicy
        {
            RequireReviewForHistoricalPremium = false
        }).Plan(own, estimate, At).Recommendations);

        Assert.Equal(200m, row.FloorPrice);
        Assert.Equal(300m, row.SecondPrice);
        Assert.Equal(600m, row.FifthPrice);
        Assert.Equal(400m, row.MedianPrice);
        Assert.Equal(900m, row.HistoricalMedian);
        Assert.Equal(300m, row.ReferencePrice);
        Assert.Equal(298m, row.RecommendedPrice);
        Assert.Equal("second", row.ReferenceKind);
        Assert.True(row.OwnStoreExcluded);
        Assert.Contains(RepriceReasonCode.OwnStoreExcluded, row.ReasonCodes);
        Assert.Contains(RepriceReasonCode.HistoricalPremium, row.ReasonCodes);
        Assert.True(row.IsPreviewOnly);
        Assert.NotNull(row.PriceUpdate);
        Assert.Equal(298L, row.PriceUpdate!.NewPrice);
    }

    [Fact]
    public void IncludesFixedAndSaleFeesInNetPreviewAndHonorsMinimumPrice()
    {
        var listings = new[]
        {
            new MallListing("42", "store", null, 100, 1, null, At, Source),
            new MallListing("42", "store-2", null, 101, 1, null, At, Source)
        };
        var estimate = new MarketPriceEstimator().Estimate(new PriceSnapshot("42", listings, fetchedAt: At), At);
        var own = new[] { new OwnShopListing("42", 500, 2, null, At) };

        var row = Assert.Single(new RepricePlanner(new RepricePolicy
        {
            MinimumPrice = 150m,
            ListingFee = 10m,
            SaleFeeRate = 0.01m,
            RequireReviewForHistoricalPremium = false
        }).Plan(own, estimate, At).Recommendations);

        Assert.Equal(150m, row.RecommendedPrice);
        Assert.Contains(RepriceReasonCode.MinimumPriceApplied, row.ReasonCodes);
        Assert.Equal(3m, row.SaleFee);
        Assert.Equal(13m, row.TotalFees);
        Assert.Equal(300m, row.GrossProceeds);
        Assert.Equal(287m, row.NetProceeds);
    }

    [Fact]
    public void HoldsWhenOnlyOwnFloorRemainsAndIndependentReferenceIsRequired()
    {
        var own = new[] { new OwnShopListing("42", 999, 1, null, At) };
        var estimate = new MarketPriceEstimator().Estimate(
            new PriceSnapshot("42", new[]
            {
                new MallListing("42", "ours", "our store", 999, 1, null, At, Source)
            }, ownShopListings: own, fetchedAt: At), At);

        var row = Assert.Single(new RepricePlanner().Plan(own, estimate, At).Recommendations);

        Assert.Equal(RepriceRecommendationStatus.Hold, row.Status);
        Assert.Null(row.RecommendedPrice);
        Assert.Contains(RepriceReasonCode.NoMarketReference, row.ReasonCodes);
    }
}
