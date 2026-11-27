namespace KoLibri.Extras;

public enum RepriceDirection { Up, Down, Keep, NoData }

public sealed record ShopRepricerOptions
{
    /// <summary>Untergrenze für Vorschläge; nie unter 100.</summary>
    public long MinPrice { get; init; } = 100;
    /// <summary>Bremse für Ausreißer: größter erlaubter Aufschlag in Prozent. Null = aus.</summary>
    public double? MaxRaisePercent { get; init; }
    /// <summary>Fallback, wenn eine Store-ID unbekannt ist: Store-Name == eigener Charaktername (ohne Groß/Klein).</summary>
    public IReadOnlySet<string> OwnStoreNames { get; init; } = new HashSet<string>();
}

public sealed record RepriceSuggestion(
    int ItemId, string Name, long CurrentPrice, int Quantity,
    long? CompetitorFloor, int? CompetitorFloorStoreId, int CompetitorCount, int OwnListingsExcluded,
    long? PricegunMedian, long? SuggestedPrice, RepriceDirection Direction, string Advice);

/// <summary>
/// Reine Preislogik (keine I/O). Konkurrenz sind nur Listings mit Menge &gt; 0 fremder Stores; eigene Stores
/// (Mallbot und Main) zählen nie. Der Vorschlag gilt in beide Richtungen: Preise dürfen steigen, kein Top-3-Zwang.
/// </summary>
public static class ShopRepricer
{
    public static RepriceSuggestion Suggest(
        ShopListing own, MallOffers offers, IReadOnlySet<int> ownStoreIds, PriceHistory? history, ShopRepricerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(offers);
        ArgumentNullException.ThrowIfNull(ownStoreIds);
        options ??= new ShopRepricerOptions();
        var minPrice = Math.Max(100, options.MinPrice);

        var live = offers.Listings.Where(l => l.Quantity > 0).ToArray();
        bool IsOwn(MallListing l) => ownStoreIds.Contains(l.StoreId)
            || options.OwnStoreNames.Any(n => string.Equals(n, l.StoreName, StringComparison.OrdinalIgnoreCase));
        var excluded = live.Count(IsOwn);
        var competitors = live.Where(l => !IsOwn(l)).ToArray();
        var median = history is null ? null : PriceRule.Median(history.Points.Select(p => p.Price).ToArray());
        var name = string.IsNullOrWhiteSpace(own.Name) ? offers.Name : own.Name;

        RepriceSuggestion Result(long? floor, int? floorStore, long? suggested, RepriceDirection dir, string advice)
            => new(own.Item.Id, name, own.Price, own.Quantity, floor, floorStore, competitors.Length, excluded, median, suggested, dir, advice);

        if (competitors.Length == 0)
            return Result(null, null, null, RepriceDirection.Keep,
                median is { } m ? $"kein Konkurrent, Median {m}" : "kein Konkurrent, kein Median");

        var cheapest = competitors.OrderBy(l => l.UnitPrice).First();
        var floor = cheapest.UnitPrice;
        var target = Math.Max(minPrice, Math.Min(floor - 1, (long)Math.Floor(floor * 0.995)));
        var note = "";
        if (options.MaxRaisePercent is { } pct && own.Price > 0 && target > own.Price)
        {
            var cap = (long)Math.Floor(own.Price * (1 + pct / 100.0));
            if (target > cap) { target = Math.Max(cap, own.Price); note = $" (Aufschlag auf {pct:0.##}% begrenzt)"; }
        }

        var dir = target > own.Price ? RepriceDirection.Up : target < own.Price ? RepriceDirection.Down : RepriceDirection.Keep;
        var advice = dir switch
        {
            RepriceDirection.Up => $"günstiger als der Konkurrenz-Floor {floor}: auf {target} anheben",
            RepriceDirection.Down => $"über dem Konkurrenz-Floor {floor}: auf {target} senken",
            _ => $"Preis passt zum Konkurrenz-Floor {floor}",
        };
        return Result(floor, cheapest.StoreId, target, dir, advice + note);
    }
}
