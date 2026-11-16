using KolArbitrage.Core.Integration;

namespace KolArbitrage.Core.Pricing;

/// <summary>
/// The read-only inputs used by the market estimator. Keeping Mall and
/// Pricegun timestamps together lets callers show which source is stale.
/// </summary>
public sealed class PriceSnapshot
{
    public PriceSnapshot(
        string itemId,
        IEnumerable<MallListing>? mallListings,
        IEnumerable<PricegunSale>? pricegunSales,
        DateTimeOffset fetchedAt,
        bool isStale = false)
        : this(itemId, mallListings, pricegunSales, null, fetchedAt, fetchedAt,
            isStale, isStale, isStale)
    {
    }

    public PriceSnapshot(
        string itemId,
        IEnumerable<MallListing>? mallListings,
        DateTimeOffset fetchedAt,
        bool isStale = false)
        : this(itemId, mallListings, null, null, fetchedAt, fetchedAt,
            isStale, isStale, isStale)
    {
    }

    public PriceSnapshot(
        string itemId,
        IEnumerable<MallListing>? mallListings = null,
        IEnumerable<PricegunSale>? pricegunSales = null,
        IEnumerable<OwnShopListing>? ownShopListings = null,
        DateTimeOffset? fetchedAt = null,
        DateTimeOffset? pricegunFetchedAt = null,
        bool mallIsStale = false,
        bool pricegunIsStale = false,
        bool isStale = false,
        Uri? mallSourceUri = null,
        Uri? pricegunSourceUri = null,
        IEnumerable<string>? ownStoreIds = null)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            throw new ArgumentException("Item id is required.", nameof(itemId));
        ItemId = itemId.Trim();
        MallListings = (mallListings ?? Array.Empty<MallListing>()).ToArray();
        PricegunSales = (pricegunSales ?? Array.Empty<PricegunSale>()).ToArray();
        OwnShopListings = (ownShopListings ?? Array.Empty<OwnShopListing>()).ToArray();
        OwnStoreIds = (ownStoreIds ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        FetchedAt = fetchedAt ?? DateTimeOffset.UtcNow;
        PricegunFetchedAt = pricegunFetchedAt ?? FetchedAt;
        MallIsStale = mallIsStale;
        PricegunIsStale = pricegunIsStale;
        ExplicitlyStale = isStale;
        IsStale = isStale || mallIsStale || pricegunIsStale;
        MallSourceUri = mallSourceUri;
        PricegunSourceUri = pricegunSourceUri;
    }

    /// <summary>Creates a combined snapshot from the two read-only adapters.</summary>
    public PriceSnapshot(
        MallSnapshot mall,
        PricegunSnapshot? pricegun = null,
        IEnumerable<OwnShopListing>? ownShopListings = null,
        IEnumerable<string>? ownStoreIds = null)
        : this(
            mall?.ItemId ?? throw new ArgumentNullException(nameof(mall)),
            mall.Listings,
            pricegun?.Sales,
            ownShopListings,
            mall.FetchedAt,
            pricegun?.FetchedAt ?? mall.FetchedAt,
            mall.IsStale,
            pricegun?.IsStale ?? false,
            false,
            mall.SourceUri,
            pricegun?.SourceUri,
            ownStoreIds)
    {
    }

    public PriceSnapshot(MallSnapshot mall, PricegunSnapshot? pricegun, bool isStale)
        : this(
            mall?.ItemId ?? throw new ArgumentNullException(nameof(mall)),
            mall.Listings,
            pricegun?.Sales,
            null,
            mall.FetchedAt,
            pricegun?.FetchedAt ?? mall.FetchedAt,
            mall.IsStale || isStale,
            (pricegun?.IsStale ?? false) || isStale,
            isStale,
            mall.SourceUri,
            pricegun?.SourceUri)
    {
    }

    public string ItemId { get; }
    public IReadOnlyList<MallListing> MallListings { get; }
    public IReadOnlyList<MallListing> Listings => MallListings;
    public IReadOnlyList<MallListing> Mall => MallListings;
    public IReadOnlyList<PricegunSale> PricegunSales { get; }
    public IReadOnlyList<PricegunSale> Sales => PricegunSales;
    public IReadOnlyList<PricegunSale> Pricegun => PricegunSales;
    public IReadOnlyList<OwnShopListing> OwnShopListings { get; }
    public IReadOnlyList<OwnShopListing> OwnListings => OwnShopListings;
    public IReadOnlySet<string> OwnStoreIds { get; }
    public DateTimeOffset FetchedAt { get; }
    public DateTimeOffset MallFetchedAt => FetchedAt;
    public DateTimeOffset PricegunFetchedAt { get; }
    public bool MallIsStale { get; }
    public bool PricegunIsStale { get; }
    /// <summary>True when the caller marked the whole raw snapshot stale.</summary>
    public bool ExplicitlyStale { get; }
    public bool IsStale { get; }
    public Uri? MallSourceUri { get; }
    public Uri? PricegunSourceUri { get; }

    public MarketEstimate Estimate(MarketPriceEstimatorOptions? options = null, DateTimeOffset? now = null)
        => new MarketPriceEstimator(options).Estimate(this, now);
}
