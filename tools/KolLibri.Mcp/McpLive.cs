using System.Globalization;
using KoLibri;

namespace KolLibri.Mcp;

/// <summary>The two accounts of the MCP server; in 0.5 they are the KoLibri account ids "main" and "mallbot".</summary>
public enum AccountRole { Main, Mallbot }

/// <summary>
/// The accounts behind the MCP tools (0.5: replaces LibKolibriRuntime/RoleScopedRuntime/McpRuntimeBinder).
/// Every tool call opens a fresh session from the stored account, so a login, logout or renewal done elsewhere
/// (CLI, desktop) is used at once and a replaced account's cookie is never used again.
/// </summary>
public sealed class McpAccounts : IAsyncDisposable
{
    private readonly KolibriClient _client;

    public McpAccounts(KolibriOptions? options = null)
        => _client = KolibriClient.Create(options ?? new KolibriOptions { DataDirectory = BundledDataDirectory() });

    /// <summary>For tests: an existing client (fake handler, memory store).</summary>
    internal McpAccounts(KolibriClient client) => _client = client;

    public KolibriClient Client => _client;

    public static string IdOf(AccountRole role) => role == AccountRole.Main ? "main" : "mallbot";

    /// <summary>A session of the role; the caller disposes it. Throws when the role has no stored login.</summary>
    public async Task<KolibriSession> OpenAsync(AccountRole role, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _client.OpenAsync(IdOf(role), cancellationToken).ConfigureAwait(false);
        }
        catch (KolibriException exception) when (exception is not SessionExpiredException && exception.Message.StartsWith("No stored account", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"No valid session for {role}. Log in with the CLI first (kolibri login {role.ToString().ToLowerInvariant()}).");
        }
    }

    /// <summary>
    /// Like 0.4's LibKolibri OpenAsync before an action: one fresh status read first; a session KoL no longer accepts is
    /// renewed there with the saved password (reads may log in again, actions never do).
    /// </summary>
    public async Task<KolibriSession> OpenForActionAsync(AccountRole role, CancellationToken cancellationToken = default)
    {
        var session = await OpenAsync(role, cancellationToken).ConfigureAwait(false);
        try
        {
            await session.Character.GetStatusAsync(refresh: true, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>True when the role has a stored account with a session cookie.</summary>
    public async Task<bool> HasSessionAsync(AccountRole role, CancellationToken cancellationToken = default)
        => (await _client.Accounts.FindAsync(IdOf(role), cancellationToken).ConfigureAwait(false)) is { HasSession: true };

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private static string? BundledDataDirectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data");
        return Directory.Exists(path) ? path : null;
    }
}

/// <summary>The JSON shapes of 0.4 (KoLStatusSnapshot, KoLInventorySnapshot, PriceSummary), built from the 0.5 records.</summary>
internal static class McpDocuments
{
    public static object Status(Status s, Equipment? equipment, int? playerId) => new
    {
        playerName = s.PlayerName,
        adventures = s.Adventures,
        meat = s.Meat,
        level = s.Level,
        fetchedAt = s.FetchedAt,
        isStale = false,
        adventuresKnown = true,
        currentHp = s.Hp,
        maximumHp = s.MaxHp,
        currentMp = s.Mp,
        maximumMp = s.MaxMp,
        effects = s.Effects.Select(e => e.Name).ToArray(),
        fullness = s.Fullness.Current,
        inebriety = s.Inebriety.Current,
        spleen = s.Spleen.Current,
        storagePullsRemaining = s.StoragePullsRemaining,
        pathName = s.PathName,
        roninTurnsRemaining = s.RoninTurnsRemaining,
        timedEffects = s.Effects.Select(e => new { name = e.Name, turnsRemaining = e.Turns, id = e.Id }).ToArray(),
        playerId = playerId?.ToString(CultureInfo.InvariantCulture),
        familiarId = s.FamiliarId?.ToString(CultureInfo.InvariantCulture),
        fullnessLimit = s.Fullness.Limit,
        inebrietyLimit = s.Inebriety.Limit,
        spleenLimit = s.Spleen.Limit,
        equipment = equipment?.Slots.Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value!.Value.Id.ToString(CultureInfo.InvariantCulture))
    };

    public static object Inventory(Inventory inventory) => new
    {
        items = inventory.Items.Select(i => new { itemId = i.Item.Id.ToString(CultureInfo.InvariantCulture), name = i.Name, quantity = i.Quantity }).ToArray(),
        fetchedAt = inventory.FetchedAt,
        isStale = false
    };

    /// <summary>0.4 BuildMallSummary: lowest sell price and quantity-weighted median of the listings.</summary>
    public static object PriceSummary(string itemId, MallOffers offers)
    {
        var values = offers.Listings.Where(x => x.UnitPrice >= 0 && x.Quantity > 0).OrderBy(x => x.UnitPrice).ToArray();
        return new
        {
            itemId,
            lowestBuyPrice = (decimal?)null,
            lowestSellPrice = values.Length == 0 ? (decimal?)null : values[0].UnitPrice,
            weightedMedianSellPrice = WeightedMedian(values.Select(x => ((decimal)x.UnitPrice, (long)x.Quantity)).ToArray()),
            weightedMedianBuyPrice = (decimal?)null,
            quoteCount = values.Length,
            asOf = offers.FetchedAt,
            isStale = false
        };
    }

    private static decimal? WeightedMedian(IReadOnlyList<(decimal Value, long Weight)> values)
    {
        var ordered = values.Where(x => x.Weight > 0).OrderBy(x => x.Value).ToArray();
        if (ordered.Length == 0) return null;
        var target = ordered.Sum(x => x.Weight) / 2m;
        long running = 0;
        foreach (var value in ordered)
        {
            running += value.Weight;
            if (running >= target) return value.Value;
        }
        return ordered[^1].Value;
    }
}
