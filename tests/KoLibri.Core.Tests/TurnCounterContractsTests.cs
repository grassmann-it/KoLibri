using Xunit;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class TurnCounterContractsTests
{
    [Fact]
    public void TurnSnapshotNormalizesCountsAndKeepsServerDay()
    {
        var snapshot = new TurnSnapshot
        {
            AccountId = "  main ",
            GameDay = new DateOnly(2026, 11, 17),
            AdventuresRemaining = -1,
            FreeTurnsRemaining = -2,
            TurnsUsedToday = -3,
            Source = " status ",
            ResponseHash = "  hash "
        }.Normalize();

        Assert.Equal("main", snapshot.AccountId);
        Assert.Equal(new DateOnly(2026, 11, 17), snapshot.GameDay);
        Assert.Equal(0, snapshot.AdventuresRemaining);
        Assert.Equal(0, snapshot.FreeTurnsRemaining);
        Assert.Equal(0, snapshot.TurnsUsedToday);
        Assert.Equal("status", snapshot.Source);
        Assert.Equal("hash", snapshot.ResponseHash);
    }

    [Fact]
    public void CounterNormalizesAndComputesGateWithoutMutatingTheOriginal()
    {
        var counter = new TurnCounter("  wander ", " main ", TurnCounterKind.Wander, 2)
        {
            WarnAt = 2,
            ExemptLocationIds = new[] { "  forest ", "forest" }
        };

        var normalized = counter.Normalize();
        Assert.Equal("wander", normalized.Id);
        Assert.Equal("main", normalized.AccountId);
        Assert.Equal(CounterGate.Warn, normalized.Gate);
        Assert.Single(normalized.ExemptLocationIds);
        Assert.Equal(2, counter.RemainingTurns);

        var consumed = normalized.Consume();
        Assert.Equal(1, consumed.RemainingTurns);
    }

    [Fact]
    public async Task CounterStoreAllowsReadAndRejectsStaleCas()
    {
        var store = new InMemoryCounterStore();
        var first = await store.CompareAndSwapAsync(
            "main", "wander", expected: null,
            new TurnCounter("wander", 3));
        Assert.True(first.Succeeded);
        Assert.Equal(1, first.Saved!.Revision);

        var current = await store.ReadCounterAsync("main", "wander");
        Assert.NotNull(current);
        var second = await store.CompareAndSwapAsync(
            "main", "wander", current,
            current with { RemainingTurns = 2 });
        Assert.True(second.Succeeded);
        Assert.Equal(2, second.Saved!.Revision);

        var stale = await store.CompareAndSwapAsync(
            "main", "wander", current,
            current with { RemainingTurns = 1 });
        Assert.False(stale.Succeeded);
        Assert.Equal(2, stale.Current!.Revision);
        Assert.Equal(2, (await store.ReadCounterAsync("main", "wander"))!.RemainingTurns);
    }

    [Fact]
    public async Task SnapshotCasUsesAccountKeyAndMonotonicRevision()
    {
        var store = new CounterStore();
        var first = await store.CompareAndSwapSnapshotAsync(
            "main", expected: null,
            new TurnSnapshot { AdventuresRemaining = 10 });
        Assert.True(first.Succeeded);
        Assert.Equal(1, first.Saved!.Revision);

        var stale = await store.CompareAndSwapSnapshotAsync(
            "main", expected: null,
            new TurnSnapshot { AdventuresRemaining = 9 });
        Assert.False(stale.Succeeded);
        Assert.Equal(10, stale.Current!.AdventuresRemaining);
    }

    [Fact]
    public void AdventureCounterCanBeProjectedToTurnContracts()
    {
        var adventure = new AdventureCounterContract { AdventuresStarted = 4 };
        var counter = adventure.ToTurnCounter("main");
        var roundTrip = counter.ToAdventureCounterContract();

        Assert.Equal("main", counter.AccountId);
        Assert.Equal(4, counter.RemainingTurns);
        Assert.Equal(4, roundTrip.AdventuresStarted);
    }
}
