using KoLibri.Core.Basis;

namespace KoLibri.Core.Spiel;

/// <summary>
/// Combined read snapshot used by the daily AutoCombat bridge. Pulls
/// together everything the bridge needs to decide whether an Archaeologist
/// needs a spade, whether the Seal Clubber should burn a leaf, whether the
/// Hermit has a clover worth opening, etc.
/// </summary>
public sealed record AccountDailyState
{
    public required AccountRole Role { get; init; }
    public required KoLStatusSnapshot Status { get; init; }
    public required KoLInventorySnapshot Inventory { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>Returns true when the player class matches the supplied id (case-insensitive).</summary>
    public bool IsClass(string classId)
        => !string.IsNullOrWhiteSpace(Status.ClassId)
           && string.Equals(Status.ClassId, classId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the count of <paramref name="itemId"/> in the inventory or 0.</summary>
    public int QuantityOf(string itemId)
    {
        foreach (var item in Inventory.Items)
            if (string.Equals(item.ItemId, itemId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Name, itemId, StringComparison.OrdinalIgnoreCase))
                return item.Quantity;
        return 0;
    }

    /// <summary>Returns true when the named effect is currently active.</summary>
    public bool HasEffect(string effect)
    {
        foreach (var active in Status.Effects)
            if (string.Equals(active, effect, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

/// <summary>
/// Builds an <see cref="AccountDailyState"/> from the live account
/// readers. Lives outside the readers themselves so tests can mock it
/// without touching HTTP.
/// </summary>
public interface IAccountDailyStateReader
{
    Task<AccountDailyState> ReadAsync(AccountRole role, CancellationToken cancellationToken);
}

public sealed class AccountDailyStateReader : IAccountDailyStateReader
{
    private readonly Func<AccountRole, IAccountReadClient> _clientFactory;

    public AccountDailyStateReader(Func<AccountRole, IAccountReadClient> clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public async Task<AccountDailyState> ReadAsync(AccountRole role, CancellationToken cancellationToken)
    {
        var client = _clientFactory(role);
        var statusTask = client.FetchStatusAsync(forceRefresh: false, cancellationToken);
        var inventoryTask = client.FetchInventoryAsync(forceRefresh: false, cancellationToken);
        await Task.WhenAll(statusTask, inventoryTask).ConfigureAwait(false);
        return new AccountDailyState
        {
            Role = role,
            Status = await statusTask.ConfigureAwait(false),
            Inventory = await inventoryTask.ConfigureAwait(false),
            ObservedAt = DateTimeOffset.UtcNow
        };
    }
}
