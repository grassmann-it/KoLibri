namespace KolArbitrage.Core.Markt;

/// <summary>Where a price observation came from. Keep this list stable because it is persisted in logs.</summary>
public enum PriceSource
{
    Shop = 0,
    Pricegun = 1,
    PirateEncryptionKey = 2,
    ElfGuardPresent = 3,
    CrimbucaneerBooty = 4,
    Mallbot = 5,
    Manual = 6,
    Mall = 7
}

public enum QuoteSide
{
    Buy = 0,
    Sell = 1
}

/// <summary>A price fetched for one item at one point in time.</summary>
public sealed record PriceObservation
{
    public string ItemId { get; }
    public PriceSource Source { get; }
    public QuoteSide Side { get; }
    public decimal Price { get; }
    public DateTimeOffset ObservedAt { get; }
    public string Currency { get; }
    public int Quantity { get; }
    public decimal Confidence { get; }
    public string? ListingId { get; }

    public PriceObservation(
        string itemId,
        PriceSource source,
        QuoteSide side,
        decimal price,
        DateTimeOffset observedAt,
        string currency = "meat",
        int quantity = 1,
        decimal confidence = 1m,
        string? listingId = null)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            throw new ArgumentException("Item id is required.", nameof(itemId));
        if (price < 0)
            throw new ArgumentOutOfRangeException(nameof(Price), "Price cannot be negative.");
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(Quantity), "Quantity must be positive.");
        if (confidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(Confidence), "Confidence must be between 0 and 1.");
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency is required.", nameof(Currency));

        ItemId = itemId.Trim();
        Source = source;
        Side = side;
        Price = price;
        ObservedAt = observedAt;
        Currency = currency.Trim();
        Quantity = quantity;
        Confidence = confidence;
        ListingId = listingId;
    }
}
