
namespace KoLibri.Core.Spiel;

/// <summary>
/// Read-only bridge from account status/inventory into the recovery planner.
/// Missing HP/MP fields remain conservative: the returned snapshot is marked
/// stale and the planner consequently blocks combat instead of guessing.
/// </summary>
public interface IRecoverySnapshotProvider
{
    ValueTask<RecoverySnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed class AccountRecoverySnapshotProvider : IRecoverySnapshotProvider
{
    private readonly IAccountReadClient _account;
    private readonly bool _forceRefresh;

    public AccountRecoverySnapshotProvider(IAccountReadClient account, bool forceRefresh = true)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _forceRefresh = forceRefresh;
    }

    public async ValueTask<RecoverySnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var statusTask = _account.FetchStatusAsync(_forceRefresh, cancellationToken);
        var inventoryTask = _account.FetchInventoryAsync(_forceRefresh, cancellationToken);
        await Task.WhenAll(statusTask, inventoryTask).ConfigureAwait(false);
        var status = await statusTask.ConfigureAwait(false);
        var inventory = await inventoryTask.ConfigureAwait(false);
        var quantities = inventory.Items
            .Where(item => item.Quantity > 0)
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity), StringComparer.OrdinalIgnoreCase);

        return new RecoverySnapshot
        {
            CurrentHp = Math.Max(0, status.CurrentHp),
            MaximumHp = Math.Max(0, status.MaximumHp),
            CurrentMp = Math.Max(0, status.CurrentMp),
            MaximumMp = Math.Max(0, status.MaximumMp),
            Effects = status.Effects,
            FamiliarId = status.FamiliarId,
            Equipment = status.Equipment,
            ItemQuantities = quantities,
            ObservedAt = status.FetchedAt > inventory.FetchedAt ? status.FetchedAt : inventory.FetchedAt,
            IsStale = status.IsStale || inventory.IsStale || status.MaximumHp <= 0
        };
    }
}
