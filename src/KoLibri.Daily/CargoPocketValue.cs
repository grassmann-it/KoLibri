using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KoLibri.Daily;

/// <summary>One item a Cargo Cultist Shorts pocket gives (KoLmafia cultshorts.txt + items data).</summary>
public sealed record CargoPocketItem(int ItemId, string Name, int Quantity, bool Tradeable, int Autosell);

/// <summary>One of the 666 pockets: the items or the Meat it gives, or the monster it starts a fight with.</summary>
public sealed record CargoPocket(int Number, string Type, IReadOnlyList<CargoPocketItem> Items, int Meat, string? Monster, string? Other)
{
    public bool StartsFight => Monster is not null;

    /// <summary>Gives Meat or items: the pockets "auto" can value. Effect, stat and puzzle pockets count 0.</summary>
    public bool HasValue => Meat > 0 || Items.Count > 0;

    public string Content => Monster is not null ? "Kampf: " + Monster
        : Meat > 0 ? Meat.ToString("N0", CultureInfo.InvariantCulture) + " Meat"
        : Items.Count > 0 ? string.Join(" + ", Items.Select(i => i.Quantity > 1 ? $"{i.Quantity}× {i.Name}" : i.Name))
        : $"{Type}: {Other}";
}

/// <summary>How the daily run picks the Cargo pocket: by value (default), from a configured list, or not at all.</summary>
public enum CargoPocketMode { Auto, List, Off }

/// <summary>One ranked pocket: what it gives, the unit price used and where it came from.</summary>
public sealed record CargoCandidate(int Pocket, string Content, int Quantity, long? UnitPrice, long Value, string Source, bool IsMeat);

/// <summary>The ranking of the pockets still full (top 5 and the chosen one).</summary>
public sealed record CargoRanking(IReadOnlyList<CargoCandidate> Top, CargoCandidate? Chosen, bool PocketsKnown, int PriceLookups, string? Note)
{
    public static CargoRanking Empty(string note) => new(Array.Empty<CargoCandidate>(), null, false, 0, note);
}

/// <summary>A price lookup: unit price or null (no sales / no listing).</summary>
public delegate Task<long?> ItemPriceLookup(int itemId, CancellationToken cancellationToken);

/// <summary>
/// Values the Cargo pockets ("--cargo-pocket auto"): Meat pockets by their fixed amount, item pockets by units × market
/// price (Pricegun, then the mall for the top candidates; an item without a market price counts its autosell price), effect
/// and stat pockets 0. Fight pockets are never candidates. On a tie the Meat pocket wins (its value is certain).
/// </summary>
public static class CargoPocketValue
{
    public const int TopCount = 5;
    /// <summary>Pricegun lookups per run (one request per item, rate-limited); the rest comes from the price cache.</summary>
    public const int DefaultPriceLookups = 40;
    /// <summary>Mall searches per run, only for top candidates Pricegun has no price for.</summary>
    public const int MallLookups = TopCount;
    /// <summary>Cached prices older than today are still used (marked with their day) for this long.</summary>
    public static readonly int StaleDays = 14;

    private static readonly IReadOnlyDictionary<int, CargoPocket> ByNumber = CargoPocketData.All.ToDictionary(p => p.Number);

    public static IReadOnlyList<CargoPocket> All => CargoPocketData.All;

    public static CargoPocket? Pocket(int number) => ByNumber.TryGetValue(number, out var p) ? p : null;

    /// <summary>Order in which unpriced items are looked up: the user's favourites (chess pieces, Yeg's Motel items, bells) first.</summary>
    private static int TypePriority(string type) => type switch
    {
        "Chess" => 0, "Yeg" => 1, "Bell" => 2, "Avatar" => 3, "Oyster" => 4, "Item" or "Item2" => 5, "Cash" => 6, "Choco" => 7,
        _ => 8
    };

    /// <summary>
    /// Ranks the pockets that are still full (<paramref name="available"/>; null = unknown, all pockets count) with prices from
    /// <paramref name="book"/>. Pure apart from the lookups the book makes.
    /// </summary>
    public static async Task<CargoRanking> RankAsync(IReadOnlySet<int>? available, CargoPriceBook book, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        var pockets = CargoPocketData.All.Where(p => !p.StartsFight && p.HasValue && (available is null || available.Contains(p.Number))).ToArray();
        if (pockets.Length == 0) return CargoRanking.Empty("keine volle Tasche mit Meat oder Items mehr");

        // 1. Pricegun (cache first, then up to the lookup budget; favourites first).
        var items = pockets.SelectMany(p => p.Items.Select(i => (Item: i, p.Type))).Where(x => x.Item.Tradeable)
            .GroupBy(x => x.Item.ItemId).Select(g => (g.First().Item, Priority: g.Min(x => TypePriority(x.Type)))).ToArray();
        await book.PriceAsync(items.OrderBy(x => x.Priority).ThenBy(x => x.Item.ItemId).Select(x => x.Item.ItemId).ToArray(), cancellationToken).ConfigureAwait(false);

        var ranked = Rank(pockets, book);
        // 2. Mall for top candidates whose tradeable items have no market price yet.
        var missing = ranked.Take(TopCount * 2).SelectMany(c => ByNumber[c.Pocket].Items)
            .Where(i => i.Tradeable && book.Get(i.ItemId)?.UnitPrice is null).Select(i => i.ItemId).Distinct().ToArray();
        if (missing.Length > 0 && await book.MallAsync(missing, cancellationToken).ConfigureAwait(false) > 0)
            ranked = Rank(pockets, book);

        var chosen = ranked.FirstOrDefault(c => c.Value > 0);
        book.Save();
        return new CargoRanking(ranked.Take(TopCount).ToArray(), chosen, available is not null, book.Lookups,
            available is null ? "Taschenstand unbekannt (noch nie gelesen): beim Ausführen wird die Taschen-Seite gelesen und neu gerechnet" : null);
    }

    private static IReadOnlyList<CargoCandidate> Rank(IEnumerable<CargoPocket> pockets, CargoPriceBook book)
        => pockets.Select(p => Candidate(p, book))
            .OrderByDescending(c => c.Value).ThenByDescending(c => c.IsMeat).ThenBy(c => c.Pocket).ToArray();

    internal static CargoCandidate Candidate(CargoPocket pocket, CargoPriceBook book)
    {
        if (pocket.Meat > 0) return new CargoCandidate(pocket.Number, pocket.Content, pocket.Meat, 1, pocket.Meat, "Meat (fest)", true);
        long total = 0;
        long? unit = null;
        var sources = new List<string>();
        foreach (var item in pocket.Items)
        {
            var price = item.Tradeable ? book.Get(item.ItemId) : null;
            long each;
            string source;
            if (price?.UnitPrice is > 0 and var market) { each = market; source = price.Source; }
            else if (item.Autosell > 0) { each = item.Autosell; source = item.Tradeable ? "Autosell (kein Marktpreis)" : "Autosell (nicht handelbar)"; }
            else { each = 0; source = item.Tradeable ? "kein Preis" : "nicht handelbar"; }
            total += each * item.Quantity;
            unit ??= each;
            if (!sources.Contains(source)) sources.Add(source);
        }
        var quantity = pocket.Items.Sum(i => i.Quantity);
        return new CargoCandidate(pocket.Number, pocket.Content, quantity, pocket.Items.Count == 1 ? unit : null, total, string.Join(", ", sources), false);
    }
}

/// <summary>A price known for an item: unit price (null = looked up, no price), source text and the KoL day it was read.</summary>
public sealed record CargoItemPrice(long? UnitPrice, string Source, DateOnly Day);

/// <summary>
/// Item prices for the pocket ranking, cached in a JSON file under the state directory (one entry per item and KoL day).
/// Today's entries are used as they are; older ones (up to <see cref="CargoPocketValue.StaleDays"/>) are used with their day
/// and refreshed first when lookups are left. Lookups are capped per run (rate limiter of Pricegun and KoL).
/// </summary>
public sealed class CargoPriceBook
{
    private readonly string? _path;
    private readonly DateOnly _today;
    private readonly ItemPriceLookup? _pricegun;
    private readonly ItemPriceLookup? _mall;
    private readonly int _budget;
    private readonly Dictionary<int, CargoItemPrice> _prices = new();
    private int _mallUsed;

    public CargoPriceBook(string? path, DateOnly today, ItemPriceLookup? pricegun, ItemPriceLookup? mall = null, int priceLookups = CargoPocketValue.DefaultPriceLookups)
    {
        _path = path;
        _today = today;
        _pricegun = pricegun;
        _mall = mall;
        _budget = Math.Max(0, priceLookups);
        Load();
    }

    /// <summary>Pricegun and mall requests made by this book.</summary>
    public int Lookups { get; private set; }

    /// <summary>
    /// No more requests (cache and prices already read only). Set while a KoL choice is open: KoL would answer any other page,
    /// a mall search included, with the choice.
    /// </summary>
    public bool Offline { get; set; }

    public CargoItemPrice? Get(int itemId) => _prices.TryGetValue(itemId, out var p) ? p : null;

    /// <summary>Sets a price (tests, or prices the caller already knows).</summary>
    public void Set(int itemId, long? unitPrice, string source) => _prices[itemId] = new CargoItemPrice(unitPrice, source, _today);

    /// <summary>Looks up the items without today's price: unknown ones first (in the given order), then the stale ones by value.</summary>
    public async Task PriceAsync(IReadOnlyList<int> itemIds, CancellationToken cancellationToken)
    {
        if (_pricegun is null || Offline) return;
        var unknown = itemIds.Where(id => !_prices.ContainsKey(id)).ToList();
        var stale = itemIds.Where(id => _prices.TryGetValue(id, out var p) && p.Day < _today)
            .OrderByDescending(id => _prices[id].UnitPrice ?? 0).ToList();
        foreach (var id in unknown.Concat(stale))
        {
            if (Lookups >= _budget) break;
            cancellationToken.ThrowIfCancellationRequested();
            Lookups++;
            long? price;
            try { price = await _pricegun(id, cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested) { continue; }
            if (price is null && _prices.TryGetValue(id, out var old) && old.UnitPrice is not null) continue; // keep the older price
            _prices[id] = new CargoItemPrice(price, price is null ? "Pricegun: keine Verkäufe" : "Pricegun", _today);
        }
    }

    /// <summary>Mall search for items without a market price (at most <see cref="CargoPocketValue.MallLookups"/> per run). Returns how many got a price.</summary>
    public async Task<int> MallAsync(IReadOnlyList<int> itemIds, CancellationToken cancellationToken)
    {
        if (_mall is null || Offline) return 0;
        var found = 0;
        foreach (var id in itemIds)
        {
            if (_mallUsed >= CargoPocketValue.MallLookups) break;
            if (_prices.TryGetValue(id, out var p) && p.Day == _today && p.Source.StartsWith("Mall", StringComparison.Ordinal)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            _mallUsed++;
            Lookups++;
            long? price;
            try { price = await _mall(id, cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { continue; }
            if (price is > 0)
            {
                _prices[id] = new CargoItemPrice(price, "Mall (billigstes Angebot)", _today);
                found++;
            }
            else _prices[id] = new CargoItemPrice(null, "Mall: kein Angebot", _today);
        }
        return found;
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(_path)) is not JsonObject root) return;
            foreach (var (key, node) in root)
            {
                if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || node is not JsonObject o) continue;
                if (!DateOnly.TryParseExact((string?)o["day"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
                if (day < _today.AddDays(-CargoPocketValue.StaleDays)) continue;
                long? price = o["price"] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;
                var source = (string?)o["source"] ?? "Pricegun";
                _prices[id] = new CargoItemPrice(price, day == _today ? source : $"{source} vom {day:yyyy-MM-dd}", day);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { _prices.Clear(); }
    }

    /// <summary>Writes the prices read today (older entries are kept as they were).</summary>
    public void Save()
    {
        if (_path is null) return;
        try
        {
            var root = new JsonObject();
            foreach (var (id, p) in _prices.OrderBy(kv => kv.Key))
            {
                var source = p.Day == _today ? p.Source : p.Source.Split(" vom ")[0];
                root[id.ToString(CultureInfo.InvariantCulture)] = new JsonObject
                {
                    ["price"] = p.UnitPrice, ["source"] = source, ["day"] = p.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                };
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a cache that cannot be written only costs lookups */ }
    }
}

/// <summary>The pockets still full, as last read from the pocket page (so the preview does not open the choice).</summary>
public static class CargoPocketState
{
    public static IReadOnlySet<int>? Load(string path, string account)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return null;
            if (root[account] is not JsonArray list) return null;
            return list.Select(n => n is JsonValue v && v.TryGetValue<int>(out var p) ? p : 0).Where(p => p > 0).ToHashSet();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    public static void Save(string path, string account, IReadOnlySet<int> available)
    {
        try
        {
            JsonObject root;
            try { root = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o ? o : new JsonObject(); }
            catch (JsonException) { root = new JsonObject(); }
            var list = new JsonArray();
            foreach (var p in available.OrderBy(p => p)) list.Add(p);
            root[account] = list;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, root.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
