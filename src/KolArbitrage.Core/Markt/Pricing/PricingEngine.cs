namespace KolArbitrage.Core.Markt;

/// <summary>
/// Combines cached source quotes into conservative buy and sell recommendations.
/// It never places an order; adapters can use the result to require approval or automate within limits.
/// </summary>
public sealed class PricingEngine
{
    private readonly IPriceProvider _provider;
    private readonly PriceCache _cache;
    private readonly PricePolicy _policy;
    private readonly TimeProvider _clock;

    public PricingEngine(
        IPriceProvider provider,
        PriceCache? cache = null,
        PricePolicy? policy = null,
        TimeProvider? clock = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _cache = cache ?? new PriceCache();
        _policy = policy ?? new PricePolicy();
        _policy.Validate();
        _clock = clock ?? TimeProvider.System;
    }

    public PriceCache Cache => _cache;

    public async Task<PriceSummary> SummarizeAsync(
        string itemId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var (snapshot, stale) = await LoadAsync(itemId, forceRefresh, cancellationToken).ConfigureAwait(false);
        var usable = snapshot.Observations.Where(IsUsable).ToArray();
        var buys = usable.Where(x => x.Side == QuoteSide.Buy).ToArray();
        var sells = usable.Where(x => x.Side == QuoteSide.Sell).ToArray();

        return new PriceSummary(
            snapshot.ItemId,
            buys.Length == 0 ? null : buys.Min(x => x.Price),
            sells.Length == 0 ? null : sells.Min(x => x.Price),
            WeightedMedian(sells),
            WeightedMedian(buys),
            usable.Length,
            snapshot.FetchedAt,
            stale);
    }

    public async Task<PricingRecommendation> RecommendAsync(
        string itemId,
        decimal? acquisitionCost = null,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (acquisitionCost is < 0)
            throw new ArgumentOutOfRangeException(nameof(acquisitionCost));

        var (snapshot, stale) = await LoadAsync(itemId, forceRefresh, cancellationToken).ConfigureAwait(false);
        var quotes = snapshot.Observations.Where(IsUsable).ToArray();
        var buys = quotes.Where(x => x.Side == QuoteSide.Buy).ToArray();
        var sells = quotes.Where(x => x.Side == QuoteSide.Sell).ToArray();
        var market = WeightedMedian(sells) ?? WeightedMedian(buys);
        var lowestBuy = buys.Length == 0 ? (decimal?)null : buys.Min(x => x.Price);
        var lowestSell = sells.Length == 0 ? (decimal?)null : sells.Min(x => x.Price);

        // A buy opportunity is an offer below the weighted market by the configured threshold.
        var reference = market ?? lowestBuy;
        var recommendedBuy = lowestBuy is not null && reference is not null &&
                             lowestBuy.Value <= reference.Value * (1m - _policy.UnderpricedPercent)
            ? lowestBuy
            : null;
        var effectiveCost = acquisitionCost ?? recommendedBuy;

        decimal? recommendedSell = null;
        decimal? profit = null;
        if (effectiveCost is not null)
        {
            var floor = effectiveCost.Value * (1m + _policy.MinimumMarginPercent);
            if (lowestSell is null)
            {
                recommendedSell = floor;
            }
            else
            {
                var undercut = Math.Min(
                    lowestSell.Value - _policy.UndercutTick,
                    lowestSell.Value * (1m - _policy.UndercutPercent));
                recommendedSell = Math.Max(floor, undercut);
            }

            profit = recommendedSell.Value - effectiveCost.Value;
        }

        var confidence = quotes.Length == 0 ? 0m :
            Math.Clamp(quotes.Average(x => _policy.WeightFor(x)), 0m, 1m);
        var profitable = effectiveCost is not null && recommendedSell is not null &&
                         recommendedSell.Value >= effectiveCost.Value * (1m + _policy.MinimumMarginPercent);
        var rationale = BuildRationale(quotes, recommendedBuy, recommendedSell, effectiveCost, stale, profitable);

        return new PricingRecommendation(
            snapshot.ItemId,
            acquisitionCost,
            recommendedBuy,
            recommendedSell,
            market,
            profit,
            recommendedBuy is not null,
            profitable,
            stale,
            confidence,
            quotes,
            rationale);
    }

    private async Task<(CachedPrices Snapshot, bool IsStale)> LoadAsync(
        string itemId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var normalized = itemId.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) throw new ArgumentException("Item id is required.", nameof(itemId));
        var now = _clock.GetUtcNow();

        if (!forceRefresh && _cache.TryGetFresh(normalized, now, _policy.CacheTtl, out var fresh))
            return (fresh, false);

        try
        {
            var observations = (await _provider.FetchAsync(normalized, cancellationToken).ConfigureAwait(false))
                .Where(x => string.Equals(x.ItemId, normalized, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            _cache.Set(normalized, observations, _clock.GetUtcNow());
            return (_cache.TryGetFresh(normalized, _clock.GetUtcNow(), _policy.CacheTtl, out var cached)
                ? cached
                : new CachedPrices(normalized, observations, _clock.GetUtcNow()), false);
        }
        catch when (!cancellationToken.IsCancellationRequested &&
                    _cache.TryGet(normalized, now, _policy.CacheTtl, _policy.MaxStale, out var stale))
        {
            return (stale, true);
        }
    }

    private bool IsUsable(PriceObservation observation) =>
        _policy.WeightFor(observation) >= _policy.MinimumConfidence && observation.Price >= 0;

    private decimal? WeightedMedian(IReadOnlyCollection<PriceObservation> quotes)
    {
        if (quotes.Count == 0) return null;
        var ordered = quotes
            .Select(x => (x.Price, Weight: _policy.WeightFor(x)))
            .Where(x => x.Weight > 0)
            .OrderBy(x => x.Price)
            .ToArray();
        if (ordered.Length == 0) return null;

        var total = ordered.Sum(x => x.Weight);
        var target = total / 2m;
        var cumulative = 0m;
        foreach (var quote in ordered)
        {
            cumulative += quote.Weight;
            if (cumulative >= target) return quote.Price;
        }
        return ordered[^1].Price;
    }

    private static string BuildRationale(
        IReadOnlyCollection<PriceObservation> quotes,
        decimal? buy,
        decimal? sell,
        decimal? cost,
        bool stale,
        bool profitable)
    {
        if (quotes.Count == 0) return "Keine verwertbaren Preisquellen im Cache oder Provider.";
        var freshness = stale ? "Stale-Cache verwendet." : "Aktuelle Cache-Daten verwendet.";
        if (buy is not null && sell is not null && profitable)
            return $"Unterpreisiges Angebot bei {buy:0.##}; Zielverkauf {sell:0.##} mit Mindestmarge. {freshness}";
        if (cost is not null && !profitable)
            return $"Kein sicherer Verkauf mit Mindestmarge über Kosten {cost:0.##}. {freshness}";
        return $"Marktpreis aus {quotes.Count} gewichteten Quellen. {freshness}";
    }
}
