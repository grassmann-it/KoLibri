using System.Globalization;
using KoLibri;
using KoLibri.Extras;

internal static partial class KolibriCli
{
    private static readonly string[] RepriceValueOptions = ["--role", "--filter", "--item", "--max-raise", "--limit"];
    private static readonly string[] RepriceFlags = [];

    /// <summary>
    /// P3-W2: price report for the own shop on KoLibri.Extras.ShopRepricer. Read-only: it sends only GETs/searches and
    /// prints ready-made "ITEM:CURRENT:NEW" strings for <c>apply-prices --set</c>; apply-prices stays the only way to write.
    /// <c>compare</c> is the same report for the given items (filter all) with the 0.4 field names added.
    /// </summary>
    internal static async Task<int> RepriceCommandAsync(string[] args, bool compareMode)
    {
        var name = compareMode ? "compare" : "reprice";
        StrictOptions.Only(args, RepriceValueOptions, RepriceFlags);
        var role = ParseRole(StrictOptions.Value(args, "--role") ?? "mallbot");
        var items = GetOptions(args, "--item").Select(ParseRepriceItemId).Distinct().ToArray();
        if (compareMode && items.Length == 0) throw new ArgumentException("compare needs at least one --item ITEM_ID.");
        var filter = compareMode ? "all" : (StrictOptions.Value(args, "--filter") ?? "tiny-and-high").ToLowerInvariant();
        if (filter is not ("tiny" or "high" or "all" or "tiny-and-high"))
            throw new ArgumentException($"--filter must be tiny, high, all or tiny-and-high, got '{filter}'.");
        double? maxRaise = null;
        if (StrictOptions.Value(args, "--max-raise") is { } raiseText)
        {
            if (!double.TryParse(raiseText, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) || pct < 0 || double.IsNaN(pct) || double.IsInfinity(pct))
                throw new ArgumentException($"--max-raise must be a non-negative percentage, got '{raiseText}'.");
            maxRaise = pct;
        }
        var limit = StrictOptions.Int(args, "--limit", int.MaxValue, 1, 100_000);

        var (client, session) = await OpenAsync(role);
        await using var _ = client;
        await using var __ = session;

        // Own stores = every stored account (never hard-coded); name fallback for accounts without a known player id.
        var accounts = await client.Accounts.ListAsync();
        var ownIds = new HashSet<int>(accounts.Where(a => a.PlayerId is not null).Select(a => a.PlayerId!.Value));
        if (session.Account.PlayerId is { } current) ownIds.Add(current);
        var ownNames = new HashSet<string>(accounts.Select(a => a.PlayerName).Append(session.Account.PlayerName)
            .Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
        var options = new ShopRepricerOptions { MaxRaisePercent = maxRaise, OwnStoreNames = ownNames };

        var shop = await session.Shop.GetListingsAsync(refresh: true);
        var candidates = shop.Where(l => items.Length > 0
                ? items.Contains(l.Item.Id)
                : filter switch
                {
                    "tiny" => IsTiny(l),
                    "high" => l.Price >= 10_000,
                    "all" => true,
                    _ => IsTiny(l) || l.Price >= 10_000
                })
            .Take(limit).ToArray();
        var missing = items.Where(id => shop.All(l => l.Item.Id != id)).ToArray();

        var suggestions = new List<RepriceSuggestion>();
        foreach (var listing in candidates) // sequential on purpose: the library's rate limiter paces the requests
        {
            var offers = await session.Mall.GetOffersAsync(listing.Item, refresh: true);
            PriceHistory? history = null;
            try { history = await client.GetPriceHistoryAsync(listing.Item); }
            catch (Exception e) when (e is not OperationCanceledException) { /* Pricegun is optional */ }
            suggestions.Add(ShopRepricer.Suggest(listing, offers, ownIds, history, options));
        }

        var ordered = suggestions.OrderByDescending(s => Math.Abs((s.SuggestedPrice ?? s.CurrentPrice) - s.CurrentPrice)).ToArray();
        var applyPrices = ordered
            .Where(s => s.Direction is RepriceDirection.Up or RepriceDirection.Down && s.SuggestedPrice is not null)
            .Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.ItemId}:{s.CurrentPrice}:{s.SuggestedPrice}"))
            .ToArray();
        var results = ordered.Select(s =>
        {
            var row = new Dictionary<string, object?>
            {
                ["itemId"] = s.ItemId.ToString(CultureInfo.InvariantCulture),
                ["name"] = s.Name,
                ["currentPrice"] = s.CurrentPrice,
                ["quantity"] = s.Quantity,
                ["competitorFloor"] = s.CompetitorFloor,
                ["competitorFloorStoreId"] = s.CompetitorFloorStoreId,
                ["competitorCount"] = s.CompetitorCount,
                ["ownListingsExcluded"] = s.OwnListingsExcluded,
                ["pricegunMedian"] = s.PricegunMedian,
                ["suggestedPrice"] = s.SuggestedPrice,
                ["delta"] = s.SuggestedPrice is { } p ? p - s.CurrentPrice : (long?)null,
                ["direction"] = s.Direction.ToString(),
                ["advice"] = s.Advice,
            };
            if (compareMode) // 0.4 field names for scripts
            {
                row["displayName"] = s.Name;
                row["mallLowest"] = s.CompetitorFloor;
            }
            return row;
        }).ToArray();

        WriteJson(new
        {
            command = name,
            role = role.ToString(),
            filter = items.Length > 0 ? "items" : filter,
            candidateCount = candidates.Length,
            ownStoreIds = ownIds.OrderBy(x => x).ToArray(),
            notInShop = missing,
            results,
            applyPrices
        });
        return 0;
    }

    private static bool IsTiny(ShopListing l) => l.Name.Contains("tiny plastic", StringComparison.OrdinalIgnoreCase);

    private static int ParseRepriceItemId(string text)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : throw new ArgumentException($"--item must be a numeric KoL item id, got '{text}'.");
}
