using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Data;
using KolArbitrage.Core.Daily;
using KolArbitrage.Core.Integration;

namespace KoLibri.Desktop;

/// <summary>
/// One inventory row as the desktop shows it: the KoL item id, the display
/// name and the quantity, plus the catalog data the detail pane needs.
/// </summary>
public sealed record DesktopInventoryItem
{
    public required string ItemId { get; init; }
    public required string Name { get; init; }
    public required int Quantity { get; init; }
    public string? ImageUrl { get; init; }
    /// <summary>KoLmafia's access flags: q consumable, g gadget,
    /// t transferable, d discardable.</summary>
    public IReadOnlyList<string> AccessFlags { get; init; } = Array.Empty<string>();
    /// <summary>What the item can be used for.</summary>
    public IReadOnlyList<string> Uses { get; init; } = Array.Empty<string>();
    /// <summary>Cheapest mall listing, when the item is listed.</summary>
    public long? CheapestMallPrice { get; init; }
}

/// <summary>One inventory category, derived from the catalog's access flags.</summary>
public sealed record DesktopInventoryCategory
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required Func<DesktopInventoryItem, bool> Matches { get; init; }
}

/// <summary>The live inventory of one account.</summary>
public sealed record DesktopInventorySnapshot
{
    public required AccountRole Role { get; init; }
    public required IReadOnlyList<DesktopInventoryItem> Items { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public bool IsStale { get; init; }
}

/// <summary>
/// Loads live account data for the desktop: inventory, status and the mall
/// listings that price the inventory.
///
/// The provider owns the session handling so the view models never touch
/// credentials or HTTP directly. A missing session is a normal state, not
/// an exception: the provider returns an empty snapshot with an error
/// message and the view shows it.
/// </summary>
public sealed class DesktopLiveDataProvider
{
    private readonly AccountSessionManager _accounts;

    public DesktopLiveDataProvider(AccountSessionManager? accounts = null)
    {
        _accounts = accounts ?? AccountSessionManager.CreateDefault();
    }

    /// <summary>The live inventory of the given account.</summary>
    public async Task<DesktopInventorySnapshot> LoadInventoryAsync(
        AccountRole role,
        bool forceRefresh = false,
        KoLmafiaCatalog? catalog = null,
        CancellationToken cancellationToken = default)
    {
        var client = await CreateReadClientAsync(role, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return new DesktopInventorySnapshot
            {
                Role = role,
                Items = Array.Empty<DesktopInventoryItem>(),
                ObservedAt = DateTimeOffset.UtcNow
            };
        }
        var inventory = await client.FetchInventoryAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        // The catalog has ~12 000 items and the inventory ~1 400; a linear
        // lookup per row would be millions of comparisons. Index it once.
        var index = catalog is null
            ? null
            : catalog.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                .GroupBy(i => i.Id!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var items = inventory.Items.Select(item =>
        {
            var record = index is not null && index.TryGetValue(item.ItemId, out var found)
                ? found
                : null;
            return new DesktopInventoryItem
            {
                ItemId = item.ItemId,
                // KoL's inventory API only returns ids and counts. The
                // catalog carries the real name, so prefer it and fall back
                // to what the response gave us.
                Name = string.IsNullOrWhiteSpace(record?.Name) ? item.Name : record!.Name,
                Quantity = item.Quantity,
                ImageUrl = record?.Image,
                AccessFlags = record?.AccessFlags ?? Array.Empty<string>(),
                Uses = BuildUses(record)
            };
        }).ToArray();
        return new DesktopInventorySnapshot
        {
            Role = role,
            Items = items,
            ObservedAt = inventory.FetchedAt,
            IsStale = inventory.IsStale
        };
    }

    /// <summary>The live status of the given account.</summary>
    public async Task<KoLStatusSnapshot?> LoadStatusAsync(
        AccountRole role,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var client = await CreateReadClientAsync(role, cancellationToken).ConfigureAwait(false);
        if (client is null) return null;
        return await client.FetchStatusAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The cheapest mall listing for an item, or null when nothing
    /// is listed or the account has no session.</summary>
    public async Task<long?> LoadCheapestMallPriceAsync(
        AccountRole role,
        string itemName,
        CancellationToken cancellationToken = default)
    {
        var mall = await CreateMallClientAsync(role, cancellationToken).ConfigureAwait(false);
        if (mall is null || string.IsNullOrWhiteSpace(itemName)) return null;
        try
        {
            var snapshot = await mall.SearchAsync(itemName, cancellationToken: cancellationToken).ConfigureAwait(false);
            var cheapest = snapshot.Listings
                .Where(l => l.Price > 0)
                .Select(l => (long?)l.Price)
                .DefaultIfEmpty(null)
                .Min();
            return cheapest;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> BuildUses(MafiaItemRecord? record)
    {
        if (record is null) return Array.Empty<string>();
        var uses = new List<string>();
        if (record.PrimaryUse is { Count: > 0 }) uses.AddRange(record.PrimaryUse);
        if (record.SecondaryUse is { Count: > 0 }) uses.AddRange(record.SecondaryUse);
        return uses;
    }

    /// <summary>Reads the live status and inventory and derives the daily
    /// snapshot from them — the same numbers the CLI's `daily breakfast`
    /// uses.</summary>
    public async Task<LiveDailySnapshot?> LoadDailySnapshotAsync(
        AccountRole role,
        DailyRoutineSnapshot baseline,
        bool forceRefresh = false,
        KoLmafiaCatalog? catalog = null,
        CancellationToken cancellationToken = default)
    {
        var client = await CreateReadClientAsync(role, cancellationToken).ConfigureAwait(false);
        if (client is null) return null;
        var builder = new LiveDailySnapshotBuilder(client);
        return await builder.BuildAsync(baseline, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IAccountReadClient?> CreateReadClientAsync(
        AccountRole role,
        CancellationToken cancellationToken)
    {
        var http = await CreateAuthenticatedHttpAsync(role, cancellationToken).ConfigureAwait(false);
        return http is null ? null : new KoLAccountReadClient(http);
    }

    private async Task<IMallReadClient?> CreateMallClientAsync(
        AccountRole role,
        CancellationToken cancellationToken)
    {
        var http = await CreateAuthenticatedHttpAsync(role, cancellationToken).ConfigureAwait(false);
        return http is null ? null : new KoLMallReadClient(http);
    }

    private async Task<HttpClient?> CreateAuthenticatedHttpAsync(
        AccountRole role,
        CancellationToken cancellationToken)
    {
        await _accounts.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var session = await _accounts.GetSessionAsync(role, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (session is null || string.IsNullOrWhiteSpace(session.Credentials.CookieHeader))
            return null;
        return KoLLoginClient.CreateAuthenticatedClient(
            session.Credentials,
            new Uri("https://www.kingdomofloathing.com/"));
    }
}
