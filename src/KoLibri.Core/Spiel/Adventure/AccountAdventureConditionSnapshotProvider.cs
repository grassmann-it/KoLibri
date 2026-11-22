
namespace KoLibri.Core.Spiel;

/// <summary>
/// Read-only bridge from the account status/inventory API to typed adventure
/// conditions. It refuses condition kinds for which the current API has no
/// authoritative field instead of returning a misleading zero.
/// </summary>
public sealed class AccountAdventureConditionSnapshotProvider : IAdventureConditionSnapshotProvider
{
    private readonly IAccountReadClient _account;
    private readonly IReadOnlyList<AdventureCondition> _conditions;
    private readonly bool _forceRefresh;

    public AccountAdventureConditionSnapshotProvider(
        IAccountReadClient account,
        IEnumerable<AdventureCondition> conditions,
        bool forceRefresh = true)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _conditions = (conditions ?? throw new ArgumentNullException(nameof(conditions)))
            .Select(x => x.Normalize()).ToArray();
        _forceRefresh = forceRefresh;
        ValidateSupportedKinds(_conditions);
    }

    public async ValueTask<AdventureConditionSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var statusTask = _account.FetchStatusAsync(_forceRefresh, cancellationToken);
        var inventoryTask = _account.FetchInventoryAsync(_forceRefresh, cancellationToken);
        await Task.WhenAll(statusTask, inventoryTask).ConfigureAwait(false);
        var status = await statusTask.ConfigureAwait(false);
        var inventory = await inventoryTask.ConfigureAwait(false);
        if (status.IsStale || inventory.IsStale)
        {
            return new AdventureConditionSnapshot
            {
                IsStale = true,
                UnavailableReason = "The account condition snapshot is stale; refresh before running adventure automation."
            };
        }

        var needsHp = _conditions.Any(c => c.Kind == AdventureConditionKind.HitPoints);
        var needsMp = _conditions.Any(c => c.Kind == AdventureConditionKind.ManaPoints);
        if (needsHp && status.MaximumHp <= 0 || needsMp && status.MaximumMp <= 0)
        {
            // The status page did not report the value: unknown must block, never read as 0.
            return new AdventureConditionSnapshot
            {
                IsStale = false,
                UnavailableReason = "The account status did not report HP/MP; the live HP/MP condition cannot be evaluated."
            };
        }

        var items = inventory.Items
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => Math.Max(0, group.Sum(item => item.Quantity)), StringComparer.OrdinalIgnoreCase);
        return new AdventureConditionSnapshot
        {
            Items = items,
            Stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["level"] = Math.Max(0, status.Level) },
            HitPoints = Math.Max(0, status.CurrentHp),
            ManaPoints = Math.Max(0, status.CurrentMp),
            TurnsRemaining = Math.Max(0, status.Adventures),
            AdventuresRemaining = Math.Max(0, status.Adventures)
        };
    }

    private static void ValidateSupportedKinds(IEnumerable<AdventureCondition> conditions)
    {
        foreach (var condition in conditions)
        {
            if (condition.Kind is AdventureConditionKind.ChoiceCount)
                throw new ArgumentException(
                    $"The account read API does not expose {condition.Kind}; use a combat/choice snapshot provider.",
                    nameof(conditions));
            if (condition.Kind == AdventureConditionKind.Stat
                && !string.Equals(condition.Key, "level", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"The account read API only exposes the 'level' stat, not '{condition.Key}'.",
                    nameof(conditions));
        }
    }
}
