using KoLibri.Extras;
using Xunit;

namespace KoLibri.Tests;

public sealed class ShopRepricerTests
{
    private const int Mallbot = 111, Main = 222, Other = 333, Other2 = 444;
    private static readonly IReadOnlySet<int> Own = new HashSet<int> { Mallbot, Main };

    private static ShopListing Mine(long price, int qty = 5) => new(new ItemRef(7), "Thing", qty, price, 0);
    private static MallListing L(int store, long price, int qty = 10, string? name = null)
        => new(store, name ?? "store" + store, new ItemRef(7), price, qty, null);
    private static MallOffers Offers(params MallListing[] l) => new(new ItemRef(7), "Thing", l, DateTimeOffset.UnixEpoch);
    private static PriceHistory Hist(params long[] prices)
        => new(new ItemRef(7), null, null, prices.Select(p => new PricePoint(DateTimeOffset.UnixEpoch, p, 1)).ToArray(), DateTimeOffset.UnixEpoch);

    [Fact]
    public void Own_store_is_not_a_competitor()
    {
        var s = ShopRepricer.Suggest(Mine(1000), Offers(L(Mallbot, 900), L(Other, 2000)), Own, null);
        Assert.Equal(2000, s.CompetitorFloor);
        Assert.Equal(Other, s.CompetitorFloorStoreId);
        Assert.Equal(1, s.OwnListingsExcluded);
        Assert.Equal(1, s.CompetitorCount);
    }

    [Fact]
    public void Suggests_raise_when_cheaper_than_competitor_floor()
    {
        var s = ShopRepricer.Suggest(Mine(500), Offers(L(Other, 2000), L(Other2, 2500)), Own, null);
        Assert.Equal(RepriceDirection.Up, s.Direction);
        Assert.Equal(1990, s.SuggestedPrice); // min(1999, floor(2000*0.995)=1990)
    }

    [Fact]
    public void Suggests_lower_when_above_floor()
    {
        var s = ShopRepricer.Suggest(Mine(5000), Offers(L(Other, 2000)), Own, null);
        Assert.Equal(RepriceDirection.Down, s.Direction);
        Assert.Equal(1990, s.SuggestedPrice);
    }

    [Fact]
    public void Never_below_100()
    {
        var s = ShopRepricer.Suggest(Mine(500), Offers(L(Other, 50)), Own, null);
        Assert.Equal(100, s.SuggestedPrice);
        Assert.Equal(RepriceDirection.Down, s.Direction);
        var t = ShopRepricer.Suggest(Mine(500), Offers(L(Other, 150)), Own, null, new ShopRepricerOptions { MinPrice = 10 });
        Assert.True(t.SuggestedPrice >= 100);
    }

    [Fact]
    public void MaxRaise_caps_up_moves()
    {
        var s = ShopRepricer.Suggest(Mine(1000), Offers(L(Other, 100000)), Own, null, new ShopRepricerOptions { MaxRaisePercent = 20 });
        Assert.Equal(RepriceDirection.Up, s.Direction);
        Assert.Equal(1200, s.SuggestedPrice);
        var down = ShopRepricer.Suggest(Mine(5000), Offers(L(Other, 2000)), Own, null, new ShopRepricerOptions { MaxRaisePercent = 20 });
        Assert.Equal(1990, down.SuggestedPrice); // Bremse gilt nur nach oben
    }

    [Fact]
    public void No_competitor_keeps_and_reports_median()
    {
        var s = ShopRepricer.Suggest(Mine(800), Offers(L(Mallbot, 800), L(Other, 100, qty: 0)), Own, Hist(900, 1000, 1100, 1000, 1000));
        Assert.Equal(RepriceDirection.Keep, s.Direction);
        Assert.Null(s.SuggestedPrice);
        Assert.Equal(1000, s.PricegunMedian);
        Assert.Contains("kein Konkurrent, Median 1000", s.Advice);
        Assert.Equal(1, s.OwnListingsExcluded);
    }

    [Fact]
    public void Other_own_account_store_is_excluded_too()
    {
        var s = ShopRepricer.Suggest(Mine(3000), Offers(L(Main, 1000), L(Mallbot, 1100), L(Other, 2000)), Own, null);
        Assert.Equal(2, s.OwnListingsExcluded);
        Assert.Equal(2000, s.CompetitorFloor);
    }

    [Fact]
    public void Unknown_store_id_falls_back_to_own_character_name()
    {
        var s = ShopRepricer.Suggest(Mine(3000), Offers(L(999, 1000, name: "Mallbot"), L(Other, 2000)), Own, null,
            new ShopRepricerOptions { OwnStoreNames = new HashSet<string> { "mallbot" } });
        Assert.Equal(2000, s.CompetitorFloor);
        Assert.Equal(1, s.OwnListingsExcluded);
    }

    [Fact]
    public void Sold_out_competitor_does_not_count()
    {
        var s = ShopRepricer.Suggest(Mine(3000), Offers(L(Other, 500, qty: 0), L(Other2, 2000)), Own, null);
        Assert.Equal(2000, s.CompetitorFloor);
    }
}
