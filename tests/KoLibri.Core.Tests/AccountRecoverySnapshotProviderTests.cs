using Xunit;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class AccountRecoverySnapshotProviderTests
{
    [Fact]
    public async Task Maps_status_and_inventory_into_recovery_snapshot()
    {
        var at = DateTimeOffset.UtcNow;
        var status = new KoLStatusSnapshot("main", 10, 1000, 13, at)
        {
            CurrentHp = 42,
            MaximumHp = 100,
            CurrentMp = 12,
            MaximumMp = 50,
            Effects = new[] { "Well-Oiled" },
            FamiliarId = "pair-of-stomping-boots",
            Equipment = new Dictionary<string, string> { ["weapon"] = "sword" }
        };
        var inventory = new KoLInventorySnapshot(
            new[] { new KoLInventoryItem("milk", "Milk", 2), new KoLInventoryItem("milk", "Milk", 3) }, at.AddSeconds(-1));

        var snapshot = await new AccountRecoverySnapshotProvider(new Fake(status, inventory)).ReadAsync();

        Assert.Equal(42, snapshot.CurrentHp);
        Assert.Equal(100, snapshot.MaximumHp);
        Assert.Equal(12, snapshot.CurrentMp);
        Assert.Equal(50, snapshot.MaximumMp);
        Assert.Equal(new[] { "Well-Oiled" }, snapshot.Effects);
        Assert.Equal("pair-of-stomping-boots", snapshot.FamiliarId);
        Assert.Equal("sword", snapshot.Equipment["weapon"]);
        Assert.Equal(5, snapshot.ItemQuantities["milk"]);
        Assert.False(snapshot.IsStale);
    }

    [Fact]
    public async Task Missing_max_hp_is_stale_and_cannot_be_treated_as_safe()
    {
        var at = DateTimeOffset.UtcNow;
        var snapshot = await new AccountRecoverySnapshotProvider(new Fake(
            new KoLStatusSnapshot("main", 1, 0, 1, at),
            new KoLInventorySnapshot(Array.Empty<KoLInventoryItem>(), at))).ReadAsync();

        Assert.True(snapshot.IsStale);
        var plan = new RecoveryPlanner().Plan(snapshot);
        Assert.False(plan.CanEnterCombat);
        Assert.Contains(RecoveryStopReason.InvalidSnapshot, plan.StopReasons);
    }

    private sealed class Fake(KoLStatusSnapshot status, KoLInventorySnapshot inventory) : IAccountReadClient
    {
        public Task<KoLStatusSnapshot> FetchStatusAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(status);

        public Task<KoLInventorySnapshot> FetchInventoryAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(inventory);
    }
}
