using System.Globalization;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Markt;

/// <summary>What happened to a store listing request, judged by the store read before and after.</summary>
public sealed record ShopAddOutcome(
    bool Sent, bool Confirmed, bool Unclear, string? Problem,
    int ItemId, int Quantity, int Price, int QuantityBefore, int? QuantityAfter, long? PriceAfter, string? StoreMessage);

/// <summary>
/// Puts items into the own mall store with <see cref="StoreLister"/> and confirms them by reading the store
/// back (<c>backoffice.php?which=1</c>): the listed quantity must rise by exactly the amount, at the price asked.
/// An item already listed at another price is refused before anything is sent, so the call never reprices.
/// </summary>
public static class ShopStocker
{
    public static int ListedQuantity(IReadOnlyList<OwnShopListing> shop, int itemId)
    {
        var key = itemId.ToString(CultureInfo.InvariantCulture);
        return shop.Where(l => l.ItemId == key).Sum(l => l.Quantity);
    }

    public static OwnShopListing? Listing(IReadOnlyList<OwnShopListing> shop, int itemId)
    {
        var key = itemId.ToString(CultureInfo.InvariantCulture);
        return shop.FirstOrDefault(l => l.ItemId == key);
    }

    /// <summary>Null when the item may be listed at <paramref name="price"/>, otherwise why not.</summary>
    public static string? CheckBefore(IReadOnlyList<OwnShopListing> shop, int itemId, int price)
        => Listing(shop, itemId) is { } existing && existing.Price != price
            ? $"Item {itemId} is already listed at {existing.Price:N0} meat; adding stock at {price:N0} would change or conflict with that price. Use reprice first; nothing was sent."
            : null;

    /// <summary>Null when the store read shows the listing (+quantity at the price), otherwise why not.</summary>
    public static string? Verify(IReadOnlyList<OwnShopListing> before, IReadOnlyList<OwnShopListing> after, int itemId, int quantity, int price)
    {
        var added = ListedQuantity(after, itemId) - ListedQuantity(before, itemId);
        var listing = Listing(after, itemId);
        if (listing is null) return "The store does not list the item after the request.";
        if (added != quantity) return $"The store shows {added:+#;-#;0} instead of +{quantity}.";
        if (listing.Price != price) return $"The store lists the item at {listing.Price:N0} instead of {price:N0} meat.";
        return null;
    }

    public static async Task<ShopAddOutcome> AddAsync(
        StoreLister lister, AccountCredentials credentials, Func<CancellationToken, Task<IReadOnlyList<OwnShopListing>>> readShop,
        int itemId, int quantity, int price, int dailyLimit = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lister);
        ArgumentNullException.ThrowIfNull(readShop);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);
        ArgumentOutOfRangeException.ThrowIfNegative(dailyLimit);
        var before = await readShop(cancellationToken).ConfigureAwait(false);
        var qtyBefore = ListedQuantity(before, itemId);
        if (CheckBefore(before, itemId, price) is { } refused)
            return new(false, false, false, refused, itemId, quantity, price, qtyBefore, qtyBefore, Listing(before, itemId)?.Price, null);

        ListItemResult result;
        try { result = await lister.ListAsync(credentials, itemId, price, quantity, dailyLimit, cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            return new(true, false, true, $"The store request got no usable answer ({e.Message}); check the store before repeating.",
                itemId, quantity, price, qtyBefore, null, null, null);
        }
        IReadOnlyList<OwnShopListing> after;
        try { after = await readShop(cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            return new(true, false, true, $"The store was not readable after the request ({e.Message}); check it before repeating.",
                itemId, quantity, price, qtyBefore, null, null, result.Message);
        }
        var problem = Verify(before, after, itemId, quantity, price);
        var qtyAfter = ListedQuantity(after, itemId);
        // Unchanged store: nothing was listed (clear). Any other mismatch: something happened that was not asked for.
        return new(true, problem is null, problem is not null && qtyAfter != qtyBefore, problem,
            itemId, quantity, price, qtyBefore, qtyAfter, Listing(after, itemId)?.Price, result.Message);
    }
}
