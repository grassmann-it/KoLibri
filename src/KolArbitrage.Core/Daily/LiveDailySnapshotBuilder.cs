using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Integration;

namespace KolArbitrage.Core.Daily;

/// <summary>
/// Builds a <see cref="DailyRoutineSnapshot"/> from live account data
/// instead of CLI flags. This is what the "breakfast" run uses: the
/// operator says "do the daily routine", KoLibri reads the accounts and
/// works out the numbers itself.
///
/// The reader is deliberately conservative — it only fills a field when the
/// live data actually supports it, so a missing endpoint results in a
/// conservative plan rather than an optimistic one.
/// </summary>
public sealed class LiveDailySnapshotBuilder
{
    /// <summary>KoL item ids the daily routine depends on.</summary>
    public static class Ids
    {
        public const int InflammableLeaf = 11341;
        public const int ElevenLeafClover = 10881;
        public const int HermitPermit = 42;
        public const int WorthlessTrinket = 43;
        public const int WorthlessGewgaw = 44;
        public const int WorthlessKnickKnack = 45;
        public const int Bacon = 8763;
        public const int BaconMachine = 9025;
    }

    private readonly IAccountReadClient _account;

    public LiveDailySnapshotBuilder(IAccountReadClient account)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
    }

    /// <summary>Reads the live status + inventory and derives the daily
    /// snapshot from them.</summary>
    public async Task<LiveDailySnapshot> BuildAsync(
        DailyRoutineSnapshot baseline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var status = await _account.FetchStatusAsync(true, cancellationToken).ConfigureAwait(false);
        var inventory = await _account.FetchInventoryAsync(true, cancellationToken).ConfigureAwait(false);

        int QuantityOf(params int[] itemIds)
        {
            var total = 0;
            foreach (var item in inventory.Items)
            {
                if (!int.TryParse(item.ItemId, out var id)) continue;
                if (Array.IndexOf(itemIds, id) < 0) continue;
                total += item.Quantity;
            }
            return total;
        }

        var leaves = QuantityOf(Ids.InflammableLeaf);
        var clovers = QuantityOf(Ids.ElevenLeafClover);
        var permits = QuantityOf(Ids.HermitPermit);
        var worthless = QuantityOf(
            Ids.WorthlessTrinket, Ids.WorthlessGewgaw, Ids.WorthlessKnickKnack);
        var bacon = QuantityOf(Ids.Bacon);
        var machines = QuantityOf(Ids.BaconMachine);

        var snapshot = baseline with
        {
            CampfireLeaves = leaves,
            MainMeat = status.Meat,
            HermitPermits = permits,
            Clovers = clovers,
            UselessItems = worthless
        };
        var state = new AccountDailyState
        {
            Role = AccountRole.Main,
            Status = status,
            Inventory = inventory,
            ObservedAt = inventory.FetchedAt
        };
        return new LiveDailySnapshot
        {
            Snapshot = snapshot,
            State = state,
            Leaves = leaves,
            Clovers = clovers,
            Permits = permits,
            WorthlessItems = worthless,
            Bacon = bacon,
            BaconMachines = machines,
            Meat = status.Meat,
            Adventures = status.Adventures,
            Level = status.Level,
            ClassId = status.ClassId ?? string.Empty
        };
    }
}

/// <summary>The live numbers behind a daily run.</summary>
public sealed record LiveDailySnapshot
{
    public required DailyRoutineSnapshot Snapshot { get; init; }
    public required AccountDailyState State { get; init; }
    public required int Leaves { get; init; }
    public required int Clovers { get; init; }
    public required int Permits { get; init; }
    public required int WorthlessItems { get; init; }
    public required int Bacon { get; init; }
    public required int BaconMachines { get; init; }
    public required long Meat { get; init; }
    public required int Adventures { get; init; }
    public required int Level { get; init; }
    public required string ClassId { get; init; }
}
