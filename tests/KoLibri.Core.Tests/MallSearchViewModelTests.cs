using KoLibri.Core.Markt;
using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Core.Tests;

public sealed class MallSearchViewModelTests
{
    private static readonly Uri Source = new("https://www.kingdomofloathing.com/mall.php");

    private static MallListing Listing(string item, string shop, long price, int qty = 5) =>
        new(item, shop, $"Shop {shop}", price, qty, null, DateTimeOffset.UtcNow, Source) { ItemName = "x" };

    private sealed class FakeBackend : IMallSearchBackend
    {
        public List<(string Term, bool Force)> Searches { get; } = new();
        public int PricegunCalls;
        public bool PricegunFails;
        public Func<string, MallSearchPage> Page = term => new MallSearchPage(term, Array.Empty<MallItemOffers>(), DateTimeOffset.UtcNow, Source);

        public Task<MallSearchPage> SearchAsync(string term, bool force, CancellationToken ct)
        {
            Searches.Add((term, force));
            return Task.FromResult(Page(term));
        }

        public Task<PricegunInfo> LoadPricegunAsync(string itemId, CancellationToken ct)
        {
            PricegunCalls++;
            return PricegunFails
                ? throw new HttpRequestException("pricegun down")
                : Task.FromResult(new PricegunInfo(26_000, DateTimeOffset.UtcNow.AddMinutes(-5), false));
        }
    }

    private static MallSearchPage OneItem(string term) => new(term, new[]
    {
        new MallItemOffers("10881", "11-leaf clover",
            new[] { Listing("10881", "1", 25_000), Listing("10881", "2", 29_000, 1460) }, 2)
    }, DateTimeOffset.UtcNow, Source);

    private static IReadOnlyList<(string, string)> Catalog(string text) =>
        text is "10881" or "11-leaf clover" ? new[] { ("10881", "11-leaf clover") } : Array.Empty<(string, string)>();

    [Fact]
    public async Task An_item_id_resolves_to_an_exact_name_search_and_rows_keep_ids()
    {
        var backend = new FakeBackend { Page = OneItem };
        var vm = new MallSearchViewModel(backend, Catalog) { SearchText = "10881" };
        await vm.SearchAsync();
        Assert.Equal("\"11-leaf clover\"", backend.Searches.Single().Term);
        Assert.Equal(2, vm.Offers.Count);
        var first = vm.Offers[0];
        Assert.Equal(("10881", "1", 25_000L, 1), (first.ItemId, first.ShopId, first.UnitPrice, first.Rank));
        Assert.Equal(26_000, first.PricegunMedian);
        Assert.Equal("1,460 in stock", vm.Offers[1].StockText);
        Assert.All(vm.Offers, o => Assert.False(o.CanBuy)); // no purchase hook yet
    }

    [Fact]
    public async Task A_partial_name_with_several_items_asks_to_pick_one()
    {
        var backend = new FakeBackend
        {
            Page = term => new MallSearchPage(term, new[]
            {
                new MallItemOffers("1", "A clover", new[] { Listing("1", "1", 10) }, 1),
                new MallItemOffers("2", "B clover", new[] { Listing("2", "2", 5) }, 1)
            }, DateTimeOffset.UtcNow, Source)
        };
        var vm = new MallSearchViewModel(backend, Catalog) { SearchText = "clover" };
        await vm.SearchAsync();
        Assert.True(vm.NeedsPick);
        Assert.Empty(vm.Offers);
        await vm.PickItemAsync(vm.Choices[1]);
        Assert.False(vm.NeedsPick);
        Assert.Equal("2", vm.Offers.Single().ItemId);
        Assert.Single(backend.Searches);
    }

    [Fact]
    public async Task A_pricegun_failure_does_not_hide_the_offers()
    {
        var backend = new FakeBackend { Page = OneItem, PricegunFails = true };
        var vm = new MallSearchViewModel(backend, Catalog) { SearchText = "11-leaf clover" };
        await vm.SearchAsync();
        Assert.Equal(2, vm.Offers.Count);
        Assert.Null(vm.Offers[0].PricegunMedian);
        Assert.Contains("Pricegun unavailable", vm.PricegunNote);
        Assert.False(vm.HasSearchError);
    }

    [Fact]
    public async Task Pricegun_is_loaded_once_per_item_and_refresh_repeats_the_active_search()
    {
        var backend = new FakeBackend { Page = OneItem };
        var vm = new MallSearchViewModel(backend, Catalog) { SearchText = "11-leaf clover" };
        await vm.SearchAsync();
        await vm.RefreshAsync();
        Assert.Equal(1, backend.PricegunCalls);
        Assert.Equal(new[] { false, true }, backend.Searches.Select(s => s.Force));
    }

    [Fact]
    public async Task A_search_error_is_shown_in_the_tab()
    {
        var backend = new FakeBackend { Page = _ => throw new MallSearchParseException("unrecognised page") };
        var vm = new MallSearchViewModel(backend, Catalog) { SearchText = "thing" };
        await vm.SearchAsync();
        Assert.True(vm.HasSearchError);
        Assert.Contains("unrecognised page", vm.SearchError);
    }

    [Fact]
    public async Task An_unknown_item_id_is_reported_without_a_request()
    {
        var backend = new FakeBackend();
        var vm = new MallSearchViewModel(backend, Catalog) { SearchText = "999999" };
        await vm.SearchAsync();
        Assert.Empty(backend.Searches);
        Assert.True(vm.HasSearchError);
    }
}
