using Xunit;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

public sealed class InventoryStateContractsTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Inventory",
        "inventory-state.json");

    [Fact]
    public void ParsesPhysicalBucketsAndProjectsUsableItemsFromOfflineFixture()
    {
        var catalog = new ItemMaster(new[]
        {
            new ItemMasterRecord("101", "Catalog usable item", use: "usable", access: "t", autosellPrice: 1),
            new ItemMasterRecord("102", "Catalog item", access: "t", autosellPrice: 2),
            new ItemMasterRecord("103", "Catalog storage item", use: "usable", access: "t"),
            new ItemMasterRecord("104", "Catalog hat", access: "t"),
            new ItemMasterRecord("105", "Catalog familiar", access: "t")
        });

        var snapshot = KoLInventoryStateAdapter.FromJson(File.ReadAllText(FixturePath), catalog);

        Assert.Equal(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero), snapshot.FetchedAt);
        Assert.False(snapshot.IsStale);
        Assert.True(snapshot.Inventory.Count == 2);
        Assert.Single(snapshot.Closet);
        Assert.Single(snapshot.Storage);
        Assert.Single(snapshot.Equipment);
        Assert.Single(snapshot.Familiar);
        Assert.Equal(6, snapshot.InventoryQuantity("101") + snapshot.ClosetQuantity("101"));
        Assert.Equal(6, snapshot.UsableQuantity("101"));
        Assert.Equal(5, snapshot.UsableQuantity("103"));
        Assert.Equal(3, snapshot.Usable.Count);
        Assert.Equal(3, snapshot.Buckets[InventoryBucket.Usable].Count);

        var item = snapshot.Inventory.Single(x => x.ItemId == "101");
        Assert.Equal("Catalog usable item", item.Name);
        Assert.Equal(42.5m, item.CostBasis);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), item.FirstSeenAt);
        Assert.True(item.IsUsable);

        var catalogCost = snapshot.Inventory.Single(x => x.ItemId == "102");
        Assert.Equal("Catalog item", catalogCost.Name);
        Assert.Equal(12.5m, catalogCost.CostBasis);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), catalogCost.FirstSeenAt);
    }

    [Fact]
    public void LegacySnapshotAdapterKeepsReadOnlyShapeAndAppliesCostIndex()
    {
        var first = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var second = first.AddDays(3);
        var firstSnapshot = new KoLInventorySnapshot(
            new[] { new KoLInventoryItem("7", "Seven", 2) }, first);
        var secondSnapshot = new KoLInventorySnapshot(
            new[] { new KoLInventoryItem("7", "Seven", 4) }, second, IsStale: true);

        var ledger = KoLInventoryStateAdapter.BuildCostBasisLedger(new[] { firstSnapshot, secondSnapshot });
        var state = KoLInventoryStateAdapter.FromSnapshot(
            secondSnapshot,
            options: new InventoryStateAdapterOptions { CostBasisLedger = ledger });

        Assert.Equal(4, state.InventoryQuantity("7"));
        Assert.True(state.IsStale);
        Assert.Equal(second, state.FetchedAt);
        Assert.Equal(first, state.Inventory.Single().FirstSeenAt);
        var legacy = state.ToLegacySnapshot();
        Assert.Equal(secondSnapshot.FetchedAt, legacy.FetchedAt);
        Assert.Equal(secondSnapshot.IsStale, legacy.IsStale);
        Assert.Equal(secondSnapshot.Items.Single(), legacy.Items.Single());

        var changed = ledger.With("8", costBasis: 3m, firstSeenAt: first);
        Assert.False(ledger.ContainsKey("8"));
        Assert.True(changed.ContainsKey("8"));
    }

    [Fact]
    public async Task ReadAdapterDelegatesOnlyToExistingReadClient()
    {
        var fetched = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var fake = new FakeAccountReadClient(new KoLInventorySnapshot(
            new[] { new KoLInventoryItem("1", "One", 1) }, fetched));
        var reader = new KoLInventoryStateReadClient(fake);

        var state = await reader.FetchInventoryStateAsync(forceRefresh: true);

        Assert.Equal(1, fake.InventoryCalls);
        Assert.Equal(1, state.InventoryQuantity("1"));
    }

    private sealed class FakeAccountReadClient(KoLInventorySnapshot inventory) : IAccountReadClient
    {
        public int InventoryCalls { get; private set; }

        public Task<KoLStatusSnapshot> FetchStatusAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new KoLStatusSnapshot("fixture", 0, 0, 1, inventory.FetchedAt));

        public Task<KoLInventorySnapshot> FetchInventoryAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            InventoryCalls++;
            return Task.FromResult(inventory);
        }
    }
}
