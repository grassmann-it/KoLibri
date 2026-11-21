using Xunit;
using KolArbitrage.Core.Markt;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

public sealed class IotMTradeabilityTests
{
    [Theory]
    [InlineData("boxed Pocket item")]
    [InlineData("scabbarded Pocket item")]
    [InlineData("shrink-wrapped Pocket item")]
    [InlineData("Pocket loot box")]
    [InlineData("discreetly-wrapped Pocket item")]
    [InlineData("(unopened) Pocket item")]
    public void RecognizesPackagedDisplayNameMarkers(string name)
    {
        var item = new ItemMasterRecord("100", name, access: "t");

        Assert.True(item.IsPackaged);
        Assert.True(item.IsMallTradeable);
        Assert.False(item.IsGiftTransferable);
    }

    [Fact]
    public void TradeOnlyItemRemainsMallCandidateWithoutGiftTransfer()
    {
        var entry = new IotMCatalogEntry("100", "trade-only IotM")
        {
            IsTradeable = true,
            IsGiftable = false
        };
        var market = new IotMMarketSnapshot("100")
        {
            AcquisitionPrice = 100m,
            ExpectedSellPrice = 10_000m,
            Liquidity = new IotMLiquidity { IsLiquid = true, Score = 1m }
        };
        var options = new IotMScannerOptions
        {
            MinimumAbsoluteSpread = 1_000m,
            MinimumMarginPercent = 0.10m
        };

        var result = new IotMScanner(options).Evaluate(
            entry, new MrAccessoryBenchmark("mr-a", 20_000m), market);

        Assert.Equal(IotMRecommendation.Buy, result.Recommendation);
        Assert.True(entry.IsMallTradeable);
        Assert.False(entry.IsGiftTransferable);
        Assert.DoesNotContain("giftable", result.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GiftableButNonTradeableItemIsNotMallCandidate()
    {
        var entry = new IotMCatalogEntry("101", "gift-only IotM")
        {
            IsTradeable = false,
            IsGiftable = true
        };

        var result = new IotMScanner(new IotMScannerOptions()).Evaluate(
            entry, new MrAccessoryBenchmark("mr-a", 20_000m));

        Assert.Equal(IotMRecommendation.Skip, result.Recommendation);
        Assert.False(entry.IsMallTradeable);
        Assert.True(entry.IsGiftTransferable);
        Assert.Contains("Mall", result.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MrStoreSourceCarriesDynamicManifestWithoutItemList()
    {
        var fetchedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var manifest = new MrStoreCatalogManifest(
            new Uri("https://catalog.example.test/mrstore.json"),
            version: "2026-01-02",
            fetchedAt: fetchedAt);
        var source = new MrStoreCatalogSource(_ => Task.FromResult(
            new MrStoreCatalogSnapshot(
                new[] { new IotMCatalogEntry("123", "a future item") }, manifest)));

        var snapshot = await source.ReadAsync();

        Assert.Equal("2026-01-02", snapshot.Version);
        Assert.Equal(fetchedAt, snapshot.FetchedAt);
        Assert.Equal(manifest.SourceUri, snapshot.SourceUri);
        Assert.Single(snapshot.Entries);
        Assert.True(snapshot.IsValid(out var error), error);
        Assert.Single(snapshot.ToIotMCatalogSnapshot().Entries);
    }
}
