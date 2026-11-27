
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
    /// <summary>Classified kind shown in the list (Food, Booze, Spleen, Usable, Quest, Equipment, Misc); set by the inventory view model.</summary>
    public string KindLabel { get; init; } = string.Empty;
}

/// <summary>One inventory category, derived from the catalog's access flags.</summary>
public sealed record DesktopInventoryCategory
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required Func<DesktopInventoryItem, bool> Matches { get; init; }
}

/// <summary>The live status and inventory of one account.</summary>
public sealed record DesktopInventorySnapshot
{
    public required AccountRole Role { get; init; }
    public required IReadOnlyList<DesktopInventoryItem> Items { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public bool IsStale { get; init; }
    public bool HasSession { get; init; } = true;
}

/// <summary>The live mall listings for one searched item.</summary>
public sealed record MallSearchResult
{
    public required string ItemName { get; init; }
    public required IReadOnlyList<MallListing> Listings { get; init; }
    public bool IsStale { get; init; }

    /// <summary>The cheapest listing, or null when nothing is listed.</summary>
    public MallListing? Cheapest => Listings
        .Where(l => l.UnitPrice > 0)
        .OrderBy(l => l.UnitPrice)
        .FirstOrDefault();
}

/// <summary>
/// Loads live account data for the desktop: inventory, status and the mall
/// listings that price the inventory.
///
/// Since 0.5 every request goes through the KoLibri library (<see cref="KolibriSession"/>): allowlist, limiter and
/// the guard hook are the library's. The provider owns no HTTP clients; it maps the library records onto what the
/// views show. A missing session is a normal state, not an exception: reads return null or an empty snapshot with
/// <see cref="DesktopInventorySnapshot.HasSession"/> false and the view says so.
/// </summary>
public sealed class DesktopLiveDataProvider : IDisposable
{
    private readonly DesktopAccounts _accounts;

    public DesktopLiveDataProvider(DesktopAccounts accounts)
    {
        _accounts = accounts;
    }

    public DesktopAccounts Accounts => _accounts;

    /// <summary>The session of a role, or null when it is signed out (see <see cref="DesktopAccounts.TryOpenAsync"/>).</summary>
    public Task<KolibriSession?> SessionAsync(AccountRole role, CancellationToken cancellationToken = default)
        => _accounts.TryOpenAsync(role, cancellationToken);

    /// <summary>The session of a role; signed out is an <see cref="InvalidOperationException"/> with the given hint.</summary>
    public async Task<KolibriSession> RequireSessionAsync(AccountRole role, string whatFor, CancellationToken cancellationToken = default)
        => await SessionAsync(role, cancellationToken).ConfigureAwait(false)
           ?? throw new InvalidOperationException($"Sign in as {role} under Accounts to {whatFor}.");

    /// <summary>The live inventory of the given account.</summary>
    public async Task<DesktopInventorySnapshot> LoadInventoryAsync(
        AccountRole role,
        bool forceRefresh = false,
        KoLmafiaCatalog? catalog = null,
        CancellationToken cancellationToken = default)
    {
        var session = await SessionAsync(role, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return new DesktopInventorySnapshot
            {
                Role = role,
                Items = Array.Empty<DesktopInventoryItem>(),
                HasSession = false,
                ObservedAt = DateTimeOffset.UtcNow
            };
        }
        var inventory = await session.Inventory.GetAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        return ProjectInventory(role, inventory, catalog);
    }

    internal static DesktopInventorySnapshot ProjectInventory(
        AccountRole role, Inventory inventory, KoLmafiaCatalog? catalog)
    {
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
            var id = item.Item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var record = index is not null && index.TryGetValue(id, out var found)
                ? found
                : null;
            return new DesktopInventoryItem
            {
                ItemId = id,
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
            ObservedAt = inventory.FetchedAt
        };
    }

    /// <summary>The live status of the given account (api.php plus the charsheet organ limits), null when signed out.</summary>
    public async Task<Status?> LoadStatusAsync(
        AccountRole role,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var session = await SessionAsync(role, cancellationToken).ConfigureAwait(false);
        return session is null ? null
            : await session.Character.GetStatusAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The worn equipment (from the same cached status read), null when signed out.</summary>
    public async Task<Equipment?> LoadEquipmentAsync(
        AccountRole role,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var session = await SessionAsync(role, cancellationToken).ConfigureAwait(false);
        return session is null ? null
            : await session.Inventory.GetEquipmentAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The cheapest mall listing for an item, or null when nothing
    /// is listed or the account has no session.</summary>
    public async Task<long?> LoadCheapestMallPriceAsync(
        AccountRole role,
        string itemName,
        CancellationToken cancellationToken = default)
    {
        var session = await SessionAsync(role, cancellationToken).ConfigureAwait(false);
        if (session is null || string.IsNullOrWhiteSpace(itemName)) return null;
        try
        {
            var result = await session.Mall.SearchAsync(itemName, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.Items
                .SelectMany(i => i.Listings)
                .Where(l => l.UnitPrice > 0)
                .Select(l => (long?)l.UnitPrice)
                .DefaultIfEmpty(null)
                .Min();
        }
        catch when (!cancellationToken.IsCancellationRequested)
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

    /// <summary>
    /// The live mall listings for a set of items. The result maps the search
    /// term onto its listings so the arbitrage table can show the spread
    /// between the cheapest listing and the median resale price.
    /// </summary>
    public async Task<IReadOnlyList<MallSearchResult>> LoadMallListingsAsync(
        AccountRole role,
        IEnumerable<string> itemNames,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var session = await SessionAsync(role, cancellationToken).ConfigureAwait(false);
        if (session is null) return Array.Empty<MallSearchResult>();
        var results = new List<MallSearchResult>();
        foreach (var name in itemNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            try
            {
                var found = await session.Mall.SearchAsync(name, forceRefresh, cancellationToken).ConfigureAwait(false);
                results.Add(new MallSearchResult
                {
                    ItemName = name,
                    Listings = found.Items.SelectMany(i => i.Listings).ToArray()
                });
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // One failing search must not take the whole table down.
                results.Add(new MallSearchResult
                {
                    ItemName = name,
                    Listings = Array.Empty<MallListing>(),
                    IsStale = true
                });
            }
        }
        return results;
    }

    /// <summary>Mall search that keeps the offers per item.</summary>
    public async Task<global::KoLibri.MallSearchResult> SearchMallItemsAsync(
        AccountRole role, string term, bool forceRefresh, CancellationToken cancellationToken)
    {
        var session = await RequireSessionAsync(role, "search the mall", cancellationToken).ConfigureAwait(false);
        return await session.Mall.SearchAsync(term, forceRefresh, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The current offers of one item (fresh from KoL when <paramref name="forceRefresh"/>).</summary>
    public async Task<MallOffers> LoadOffersAsync(
        AccountRole role, ItemRef item, bool forceRefresh, CancellationToken cancellationToken)
    {
        var session = await RequireSessionAsync(role, "search the mall", cancellationToken).ConfigureAwait(false);
        return await session.Mall.GetOffersAsync(item, forceRefresh, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pricegun median with the age of the data. Throws on failure so the
    /// caller can show the reason; it must never be awaited in a way that
    /// blocks the offers.
    /// </summary>
    public Task<PricegunInfo> LoadPricegunAsync(string itemId, CancellationToken cancellationToken)
        => LoadPricegunAsync(itemId, forceRefresh: false, cancellationToken);

    /// <summary>
    /// Pricegun through the library (its own client without the KoL cookie, cached and throttled there).
    /// <paramref name="forceRefresh"/> is kept for the callers; the library decides when to fetch again.
    /// </summary>
    public async Task<PricegunInfo> LoadPricegunAsync(string itemId, bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!int.TryParse(itemId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
            throw new ArgumentException($"Not a KoL item id: {itemId}", nameof(itemId));
        var history = await _accounts.Client.GetPriceHistoryAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("pricegun.loathers.net did not answer.");
        return new PricegunInfo(Median(history), history.FetchedAt, IsStale: false);
    }

    /// <summary>Reads the own store listings, or returns null for a signed-out role.</summary>
    public async Task<IReadOnlyList<ShopListing>?> LoadOwnShopAsync(
        AccountRole role,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var session = await SessionAsync(role, cancellationToken).ConfigureAwait(false);
        return session is null ? null
            : await session.Shop.GetListingsAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The median resale price of an item, taken from pricegun. The item id
    /// is the KoL numeric id — the mall search works on names, pricegun on
    /// ids, so the caller has to map between them.
    /// </summary>
    public async Task<long?> LoadMedianPriceAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(itemId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)) return null;
        try
        {
            var history = await _accounts.Client.GetPriceHistoryAsync(id, cancellationToken).ConfigureAwait(false);
            return history is null ? null : Median(history);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Median unit price of the recorded sales (as in 0.4), null without sales.</summary>
    internal static long? Median(PriceHistory history)
    {
        if (history.Points.Count == 0) return null;
        var prices = history.Points.Select(p => (decimal)p.Price).OrderBy(p => p).ToArray();
        var median = prices.Length % 2 == 1
            ? prices[prices.Length / 2]
            : (prices[prices.Length / 2 - 1] + prices[prices.Length / 2]) / 2m;
        return (long)median;
    }

    // Clan and chat moved to DesktopCommunity (raw pages through the session).

    public async Task<MailPage?> ReadMailboxPageAsync(AccountRole role, MailFolder folder, int page, CancellationToken cancellationToken)
    {
        var session = await SessionAsync(role, cancellationToken).ConfigureAwait(false);
        return session is null ? null : await session.Mail.GetAsync(folder, page, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveInboxMessageAsync(AccountRole role, string id, CancellationToken cancellationToken)
    {
        var session = await SessionAsync(role, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Sign in to save KoLmail.");
        await session.Mail.SaveAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteMailboxMessageAsync(
        AccountRole role, MailFolder folder, string id, CancellationToken cancellationToken)
    {
        var session = await SessionAsync(role, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Sign in to delete KoLmail.");
        await session.Mail.DeleteAsync(folder, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends one KoLmail; true when KoL confirmed it. Attachments and meat are re-read afterwards by the library.</summary>
    public async Task<bool> SendKoLmailAsync(
        AccountRole role, string recipient, string message, IReadOnlyList<InventoryItem> attachments,
        long meat, CancellationToken cancellationToken)
    {
        var session = await SessionAsync(role, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Sign in to send KoLmail.");
        var result = await session.Mail.SendAsync(new MailMessage(recipient, message) { Items = attachments, Meat = meat }, cancellationToken)
            .ConfigureAwait(false);
        return result.Status == MailSendStatus.Sent;
    }

    /// <summary>True when a stored session exists for the role but is expired (no automatic log-in was attempted or it failed).</summary>
    public Task<bool> HasExpiredSessionAsync(AccountRole role, CancellationToken cancellationToken = default)
        => _accounts.HasExpiredSessionAsync(role, cancellationToken);

    /// <summary>The accounts own the client and its sessions; nothing to release here.</summary>
    public void Dispose()
    {
    }
}
