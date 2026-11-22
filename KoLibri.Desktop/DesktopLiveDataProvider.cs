using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;

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
        .Where(l => l.Price > 0)
        .OrderBy(l => l.Price)
        .FirstOrDefault();
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
public sealed class DesktopLiveDataProvider : IDisposable
{
    private readonly AccountSessionManager _accounts;
    private readonly Func<AccountSession, HttpClient> _createAccountHttp;
    private readonly Dictionary<AccountRole, AccountClients> _clients = new();
    private readonly HttpClient _pricegunHttp = new();
    private readonly PricegunReadClient _pricegun;

    private sealed record AccountClients(
        AccountSession Session, HttpClient Http, KoLAccountReadClient Account, IMallReadClient Mall);

    public DesktopLiveDataProvider(AccountSessionManager? accounts = null,
        Func<AccountSession, HttpClient>? createAccountHttp = null)
    {
        _accounts = accounts ?? AccountSessionManager.CreateDefault();
        _createAccountHttp = createAccountHttp ?? (session => KoLLoginClient.CreateAuthenticatedClient(
            session.Credentials, new Uri("https://www.kingdomofloathing.com/")));
        _pricegun = new PricegunReadClient(_pricegunHttp);
    }

    /// <summary>The live inventory of the given account.</summary>
    public async Task<DesktopInventorySnapshot> LoadInventoryAsync(
        AccountRole role,
        bool forceRefresh = false,
        KoLmafiaCatalog? catalog = null,
        CancellationToken cancellationToken = default)
    {
        var inventory = await ReadAccountAsync(role, forceRefresh, (reader, refresh, token) =>
            reader.FetchInventoryAsync(refresh, token), cancellationToken).ConfigureAwait(false);
        if (inventory is null)
        {
            return new DesktopInventorySnapshot
            {
                Role = role,
                Items = Array.Empty<DesktopInventoryItem>(),
                HasSession = false,
                ObservedAt = DateTimeOffset.UtcNow
            };
        }
        return ProjectInventory(role, inventory, catalog);
    }

    internal static DesktopInventorySnapshot ProjectInventory(
        AccountRole role, KoLInventorySnapshot inventory, KoLmafiaCatalog? catalog)
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
        return await ReadAccountAsync(role, forceRefresh, (reader, refresh, token) =>
            reader.FetchStatusAsync(refresh, token), cancellationToken).ConfigureAwait(false);
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

    /// <summary>Reads the live status and inventory and derives the daily
    /// snapshot from them — the same numbers the CLI's `daily breakfast`
    /// uses.</summary>
    public async Task<LiveDailySnapshot?> LoadDailySnapshotAsync(
        AccountRole role,
        DailyRoutineSnapshot baseline,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var client = await CreateReadClientAsync(role, cancellationToken).ConfigureAwait(false);
        if (client is null) return null;
        var builder = new LiveDailySnapshotBuilder(client);
        return await builder.BuildAsync(baseline, cancellationToken, forceRefresh, role).ConfigureAwait(false);
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
        var mall = await CreateMallClientAsync(role, cancellationToken).ConfigureAwait(false);
        if (mall is null) return Array.Empty<MallSearchResult>();
        var results = new List<MallSearchResult>();
        foreach (var name in itemNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            try
            {
                var snapshot = await mall.SearchAsync(name, forceRefresh, cancellationToken).ConfigureAwait(false);
                results.Add(new MallSearchResult
                {
                    ItemName = name,
                    Listings = snapshot.Listings,
                    IsStale = snapshot.IsStale
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

    public async Task<MallSnapshot> SearchMallAsync(
        AccountRole role, string query, CancellationToken cancellationToken)
    {
        var mall = await CreateMallClientAsync(role, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Sign in as {role} under Accounts to search the mall.");
        return await mall.SearchAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Mall search that keeps the offers per item.</summary>
    public async Task<MallSearchPage> SearchMallItemsAsync(
        AccountRole role, string term, bool forceRefresh, CancellationToken cancellationToken)
    {
        var mall = await CreateMallClientAsync(role, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Sign in as {role} under Accounts to search the mall.");
        return await mall.SearchItemsAsync(term, forceRefresh, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pricegun median with the age of the data. Throws on failure so the
    /// caller can show the reason; it must never be awaited in a way that
    /// blocks the offers.
    /// </summary>
    public async Task<PricegunInfo> LoadPricegunAsync(string itemId, CancellationToken cancellationToken)
    {
        var snapshot = await _pricegun.FetchAsync(itemId, forceRefresh: false, cancellationToken).ConfigureAwait(false);
        if (snapshot.Sales.Count == 0) return new PricegunInfo(null, snapshot.FetchedAt, snapshot.IsStale);
        var prices = snapshot.Sales.Select(sale => (decimal)sale.UnitPrice).OrderBy(price => price).ToArray();
        var median = prices.Length % 2 == 1
            ? prices[prices.Length / 2]
            : (prices[prices.Length / 2 - 1] + prices[prices.Length / 2]) / 2m;
        return new PricegunInfo((long)median, snapshot.FetchedAt, snapshot.IsStale);
    }

    /// <summary>Reads the own store listings, or returns null for a signed-out role.</summary>
    public async Task<IReadOnlyList<OwnShopListing>?> LoadOwnShopAsync(
        AccountRole role,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var mall = await CreateMallClientAsync(role, cancellationToken).ConfigureAwait(false);
        return mall is null ? null
            : await mall.FetchOwnShopAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
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
        if (string.IsNullOrWhiteSpace(itemId)) return null;
        try
        {
            var snapshot = await _pricegun
                .FetchAsync(itemId, forceRefresh: false, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.Sales.Count == 0) return null;
            var prices = snapshot.Sales
                .Select(sale => (decimal)sale.UnitPrice)
                .OrderBy(price => price)
                .ToArray();
            var median = prices.Length % 2 == 1
                ? prices[prices.Length / 2]
                : (prices[prices.Length / 2 - 1] + prices[prices.Length / 2]) / 2m;
            return (long)median;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public Task<KoLClanProfile?> ReadClanProfileAsync(AccountRole role, CancellationToken cancellationToken)
        => ReadAccountAsync(role, false, (reader, _, token) => reader.FetchClanProfileAsync(token), cancellationToken);

    public Task<IReadOnlyList<KoLClanMember>?> ReadClanMembersAsync(
        AccountRole role, string clanId, CancellationToken cancellationToken)
        => ReadAccountAsync(role, false, (reader, _, token) => reader.FetchClanMembersAsync(clanId, token), cancellationToken);

    public Task<KoLClanLoungeSnapshot?> ReadClanLoungeAsync(AccountRole role, CancellationToken cancellationToken)
        => ReadAccountAsync(role, false, (reader, _, token) => reader.FetchClanLoungeAsync(token), cancellationToken);

    public async Task<bool> UseClanLookingGlassAsync(AccountRole role, CancellationToken cancellationToken)
    {
        var clients = await GetClientsAsync(role, cancellationToken).ConfigureAwait(false);
        if (clients?.Session.Credentials.AccessToken is not { Length: > 0 } hash)
            throw new InvalidOperationException("Sign in to use the clan looking glass.");

        return await clients.Account.UseClanLookingGlassAsync(hash, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KoLCrimboTreeResult> UseClanCrimboTreeAsync(AccountRole role, CancellationToken cancellationToken)
    {
        var clients = await GetClientsAsync(role, cancellationToken).ConfigureAwait(false);
        if (clients?.Session.Credentials.AccessToken is not { Length: > 0 } hash)
            throw new InvalidOperationException("Sign in to check the clan Crimbo Tree.");

        return await clients.Account.UseClanCrimboTreeAsync(hash, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KoLClanKlawResult> PlayClanKlawAsync(AccountRole role, CancellationToken cancellationToken)
    {
        var clients = await GetClientsAsync(role, cancellationToken).ConfigureAwait(false);
        if (clients?.Session.Credentials.AccessToken is not { Length: > 0 } hash)
            throw new InvalidOperationException("Sign in to play the clan Klaw.");

        return await clients.Account.PlayClanKlawAsync(hash, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> UseClanHotTubAsync(
        AccountRole role, bool allowRemovingCurse, CancellationToken cancellationToken)
    {
        var clients = await GetClientsAsync(role, cancellationToken).ConfigureAwait(false);
        if (clients?.Session.Credentials.AccessToken is not { Length: > 0 } hash)
            throw new InvalidOperationException("Sign in to use the clan Hot Tub.");

        return await clients.Account.UseClanHotTubAsync(hash, allowRemovingCurse, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KoLClanPoolResult> PlayClanPoolAsync(
        AccountRole role, KoLClanPoolStance stance, CancellationToken cancellationToken)
    {
        var clients = await GetClientsAsync(role, cancellationToken).ConfigureAwait(false);
        if (clients?.Session.Credentials.AccessToken is not { Length: > 0 } hash)
            throw new InvalidOperationException("Sign in to play clan pool.");

        return await clients.Account.PlayClanPoolAsync(hash, stance, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KoLClanShowerResult> TakeClanShowerAsync(
        AccountRole role, KoLClanShowerTemperature temperature, CancellationToken cancellationToken)
    {
        var clients = await GetClientsAsync(role, cancellationToken).ConfigureAwait(false);
        if (clients?.Session.Credentials.AccessToken is not { Length: > 0 } hash)
            throw new InvalidOperationException("Sign in to use the April Shower.");

        return await clients.Account.TakeClanShowerAsync(hash, temperature, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KoLClanSwimResult> SwimInClanPoolAsync(
        AccountRole role, KoLClanSwimWorkout workout, CancellationToken cancellationToken)
    {
        var clients = await GetClientsAsync(role, cancellationToken).ConfigureAwait(false);
        if (clients?.Session.Credentials.AccessToken is not { Length: > 0 } hash)
            throw new InvalidOperationException("Sign in to use the clan swimming pool.");

        return await clients.Account.SwimInClanPoolAsync(hash, workout, cancellationToken).ConfigureAwait(false);
    }

    public Task<KoLClanPoolChoice?> ReadClanPoolChoiceAsync(AccountRole role, CancellationToken cancellationToken)
        => ReadAccountAsync(role, false, (reader, _, token) => reader.ReadClanPoolChoiceAsync(token), cancellationToken);

    public async Task<KoLClanPoolChoice> SubmitClanPoolChoiceAsync(
        AccountRole role, KoLClanPoolMove? move, CancellationToken cancellationToken)
    {
        var clients = await GetClientsAsync(role, cancellationToken).ConfigureAwait(false);
        if (clients?.Session.Credentials.AccessToken is not { Length: > 0 } hash)
            throw new InvalidOperationException("Sign in to use the clan swimming pool.");

        return await (move is { } action
            ? clients.Account.MoveInClanPoolAsync(hash, action, cancellationToken)
            : clients.Account.CannonballAsync(hash, cancellationToken)).ConfigureAwait(false);
    }

    public Task<string?> ReadClanPhotocopyMonsterAsync(AccountRole role, CancellationToken cancellationToken)
        => ReadAccountAsync(role, false, (reader, _, token) => reader.ReadClanPhotocopyMonsterAsync(token), cancellationToken);

    public async Task<bool> TransferClanFaxAsync(AccountRole role, bool send, CancellationToken cancellationToken)
    {
        var clients = await GetClientsAsync(role, cancellationToken).ConfigureAwait(false);
        if (clients?.Session.Credentials.AccessToken is not { Length: > 0 } hash)
            throw new InvalidOperationException("Sign in to use the clan fax machine.");

        return await clients.Account.TransferClanFaxAsync(hash, send, cancellationToken).ConfigureAwait(false);
    }

    public Task<KoLClanStashSnapshot?> ReadClanStashAsync(AccountRole role, CancellationToken cancellationToken)
        => ReadAccountAsync(role, false, (reader, _, token) => reader.FetchClanStashAsync(token), cancellationToken);

    public async Task<bool> TransferClanStashItemAsync(
        AccountRole role, string itemId, int quantity, bool deposit, CancellationToken cancellationToken)
    {
        var clients = await GetClientsAsync(role, cancellationToken).ConfigureAwait(false);
        if (clients?.Session.Credentials.AccessToken is not { Length: > 0 } hash)
            throw new InvalidOperationException("Sign in to transfer an item to or from the clan stash.");

        return await (deposit
            ? clients.Account.DepositClanStashItemAsync(itemId, quantity, hash, cancellationToken)
            : clients.Account.TakeClanStashItemAsync(itemId, quantity, hash, cancellationToken)).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<KoLChatContact>?> ReadChatContactsAsync(AccountRole role, CancellationToken cancellationToken)
        => ReadAccountAsync(role, false, (reader, _, token) => reader.FetchChatContactsAsync(token), cancellationToken);

    public Task<KoLChatBatch?> ReadChatAsync(AccountRole role, long cursor, CancellationToken cancellationToken)
        => ReadAccountAsync(role, false, (reader, _, token) => reader.FetchChatAsync(cursor, token), cancellationToken);

    public async Task<KoLChatBatch> SendChatAsync(
        AccountRole role, string target, string message, long cursor, CancellationToken cancellationToken)
    {
        var clients = await GetClientsAsync(role, cancellationToken).ConfigureAwait(false);
        if (clients?.Session.Credentials.AccessToken is not { Length: > 0 } hash)
            throw new InvalidOperationException("Sign in to send KoL chat.");
        return await clients.Account.SendChatAsync(hash, target, message, cursor, cancellationToken).ConfigureAwait(false);
    }

    public Task<KoLMailboxSnapshot?> ReadMailboxPageAsync(AccountRole role, KoLMailFolder folder, int page, CancellationToken cancellationToken)
        => ReadAccountAsync(role, true, (reader, _, token) =>
            reader.FetchMailboxPageAsync(folder, page, token), cancellationToken);

    public async Task SaveInboxMessageAsync(AccountRole role, string id, CancellationToken cancellationToken)
    {
        var clients = await GetClientsAsync(role, cancellationToken).ConfigureAwait(false);
        if (clients?.Session.Credentials.AccessToken is not { Length: > 0 } hash)
            throw new InvalidOperationException("Sign in to save KoLmail.");

        await clients.Account.SaveInboxMessageAsync(id, hash, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteMailboxMessageAsync(
        AccountRole role, KoLMailFolder folder, string id, CancellationToken cancellationToken)
    {
        var clients = await GetClientsAsync(role, cancellationToken).ConfigureAwait(false);
        if (clients?.Session.Credentials.AccessToken is not { Length: > 0 } hash)
            throw new InvalidOperationException("Sign in to delete KoLmail.");

        await clients.Account.DeleteMailboxMessageAsync(folder, id, hash, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SendKoLmailAsync(
        AccountRole role, string recipient, string message, IReadOnlyList<KoLMailAttachment> attachments,
        long meat, CancellationToken cancellationToken)
    {
        var clients = await GetClientsAsync(role, cancellationToken).ConfigureAwait(false);
        if (clients?.Session.Credentials.AccessToken is not { Length: > 0 } hash)
            throw new InvalidOperationException("Sign in to send KoLmail.");

        try
        {
            return await KoLMailbox.SendAsync(clients.Http, hash, recipient, message, attachments, meat, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (attachments.Count > 0 || meat > 0)
                clients.Account.Invalidate();
        }
    }

    private async Task<T?> ReadAccountAsync<T>(
        AccountRole role, bool forceRefresh,
        Func<KoLAccountReadClient, bool, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken) where T : class?
    {
        var clients = await GetClientsAsync(role, cancellationToken).ConfigureAwait(false);
        if (clients is null) return null;
        try
        {
            return await read(clients.Account, forceRefresh, cancellationToken).ConfigureAwait(false);
        }
        catch (KoLHtmlResponseException exception) when (exception.Kind == KoLHtmlResponseKind.LoginPage)
        {
            var session = await ReconnectAsync(clients.Session, cancellationToken).ConfigureAwait(false);
            if (session is null) throw;
            var renewed = await GetClientsAsync(role, cancellationToken).ConfigureAwait(false);
            if (renewed is null || renewed.Session.Profile != session.Profile) return null;
            return await read(renewed.Account, true, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<AccountSession?> ReconnectAsync(AccountSession session, CancellationToken cancellationToken)
        => _accounts.ReconnectAsync(session,
            (username, password, token) => new KoLLoginClient().LoginAsync(username, password, true, token),
            cancellationToken);

    private async Task<IAccountReadClient?> CreateReadClientAsync(AccountRole role, CancellationToken cancellationToken)
        => (await GetClientsAsync(role, cancellationToken).ConfigureAwait(false))?.Account;

    private async Task<IMallReadClient?> CreateMallClientAsync(AccountRole role, CancellationToken cancellationToken)
        => (await GetClientsAsync(role, cancellationToken).ConfigureAwait(false))?.Mall;

    private async Task<AccountClients?> GetClientsAsync(AccountRole role, CancellationToken cancellationToken)
    {
        var session = await _accounts.GetSessionAsync(role, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (session is null && await _accounts.GetSessionAsync(role, includeExpired: true,
                cancellationToken: cancellationToken).ConfigureAwait(false) is { } expired)
            session = await ReconnectAsync(expired, cancellationToken).ConfigureAwait(false);
        lock (_clients)
        {
            if (_clients.TryGetValue(role, out var cached))
            {
                if (cached.Session == session) return cached;
                _clients.Remove(role);
                cached.Http.Dispose();
            }

            if (session is null || string.IsNullOrWhiteSpace(session.Credentials.CookieHeader))
                return null;

            var http = _createAccountHttp(session);
            var clients = new AccountClients(session, http, new KoLAccountReadClient(http), new KoLMallReadClient(http));
            _clients.Add(role, clients);
            return clients;
        }
    }

    public void Dispose()
    {
        lock (_clients)
        {
            foreach (var clients in _clients.Values) clients.Http.Dispose();
            _clients.Clear();
        }
        _pricegunHttp.Dispose();
    }
}
