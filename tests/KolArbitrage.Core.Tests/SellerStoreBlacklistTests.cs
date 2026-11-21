using Xunit;
using KolArbitrage.Core.Markt;

namespace KolArbitrage.Core.Tests;

public sealed class SellerStoreBlacklistTests
{
    [Fact]
    public void MatchIsCaseInsensitiveAndExpiredEntriesAreIgnored()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var database = new SellerBlacklistDatabase
        {
            Entries =
            [
                new SellerBlacklistEntry
                {
                    StoreId = "  STORE-42 ",
                    Reason = "suspect pricing",
                    ExpiresAt = now.AddMinutes(10)
                },
                new SellerBlacklistEntry
                {
                    SellerId = "seller-old",
                    Reason = "expired",
                    ExpiresAt = now
                }
            ]
        };

        var active = SellerBlacklistMatcher.Find(database, new SellerStoreIdentity(storeId: "store-42"), now);
        var expired = SellerBlacklistMatcher.Find(database, new SellerStoreIdentity(sellerId: "seller-old"), now);

        Assert.True(active.IsBlocked);
        Assert.Equal("suspect pricing", active.Reason);
        Assert.False(expired.IsBlocked);
    }

    [Fact]
    public async Task JsonStoreRoundTripsAndServiceReplacesSameTarget()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kolibri-blacklist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = System.IO.Path.Combine(directory, "blacklist.json");
            var store = new JsonFileSellerBlacklistStore(path);
            var service = new SellerBlacklistService(store);
            await service.BlockStoreAsync("store-1", "first reason");
            await service.BlockStoreAsync("store-1", "updated reason");

            var loaded = await new JsonFileSellerBlacklistStore(path).LoadAsync();
            Assert.Single(loaded.Entries);
            Assert.Equal("updated reason", loaded.Entries[0].Reason);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OwnStoreIsExcludedBeforeBlacklistAndIndependentListingsRemain()
    {
        var own = new SellerStoreIdentity(storeId: "own-7", storeName: "Our shop");
        var database = new SellerBlacklistDatabase
        {
            Entries =
            [new SellerBlacklistEntry { StoreName = "blocked shop", Reason = "manual review" }]
        };
        var policy = new OwnStoreExclusionPolicy(own, database);
        var listings = new[]
        {
            Listing("own-7", "Our shop"),
            Listing("other-1", "Blocked shop"),
            Listing("other-2", "Independent shop")
        };

        var filtered = policy.Filter(listings);

        Assert.Equal(new[] { "other-2" }, filtered.Select(x => x.StoreId));
        Assert.True(policy.Evaluate(listings[0]).IsOwnStore);
        Assert.True(policy.Evaluate(listings[1]).IsBlacklisted);
    }

    private static MallListing Listing(string storeId, string storeName) =>
        new("item-1", storeId, storeName, 100, 1, null,
            DateTimeOffset.UtcNow, new Uri("https://example.invalid/mall"));
}
