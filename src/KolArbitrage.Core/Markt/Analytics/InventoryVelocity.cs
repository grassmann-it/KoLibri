using System.Text;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Markt;

/// <summary>
/// Calculates sales velocity from the local shop ledger. This class is
/// deliberately read-only; it never refreshes the ledger or changes a shop.
/// </summary>
public sealed class InventoryVelocityCalculator
{
    private readonly TimeSpan _lookbackWindow;

    public InventoryVelocityCalculator(TimeSpan? lookbackWindow = null)
    {
        _lookbackWindow = lookbackWindow ?? TimeSpan.FromDays(30);
        if (_lookbackWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lookbackWindow));
    }

    public TimeSpan LookbackWindow => _lookbackWindow;

    public InventoryVelocity Calculate(
        InventoryAgingItem item,
        IEnumerable<ShopSale>? sales,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Calculate(item.ItemId, item.DisplayName, item.Quantity, sales, now);
    }

    public InventoryVelocity Calculate(
        KoLInventoryItem item,
        IEnumerable<ShopSale>? sales,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Calculate(item.ItemId, item.Name, item.Quantity, sales, now);
    }

    public InventoryVelocity Calculate(
        string itemId,
        string displayName,
        int inventoryQuantity,
        IEnumerable<ShopSale>? sales,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("Item id is required.", nameof(itemId));
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Display name is required.", nameof(displayName));
        var end = now ?? DateTimeOffset.UtcNow;
        var start = end - _lookbackWindow;
        var itemKey = Normalize(displayName);
        var idKey = Normalize(itemId);
        var sold = 0;
        var count = 0;

        foreach (var sale in sales ?? Array.Empty<ShopSale>())
        {
            if (sale is null || sale.Quantity <= 0 || string.IsNullOrWhiteSpace(sale.ItemName))
                continue;

            var saleKey = Normalize(sale.ItemName);
            if (!StringComparer.Ordinal.Equals(saleKey, itemKey)
                && !StringComparer.Ordinal.Equals(saleKey, idKey))
                continue;

            // The activity page sometimes lacks a sale timestamp. ObservedAt
            // is the best local arrival time in that case.
            var timestamp = sale.SoldAt ?? sale.ObservedAt;
            if (timestamp < start || timestamp > end)
                continue;

            sold = SaturatingAdd(sold, sale.Quantity);
            count++;
        }

        var days = (decimal)_lookbackWindow.TotalDays;
        var unitsPerDay = sold / days;
        var quantity = Math.Max(0, inventoryQuantity);
        decimal? daysOfSupply = unitsPerDay > 0m ? quantity / unitsPerDay : null;
        var sellThroughDenominator = (decimal)quantity + sold;
        var sellThrough = sellThroughDenominator > 0m
            ? Math.Clamp(sold / sellThroughDenominator, 0m, 1m)
            : 0m;

        return new InventoryVelocity
        {
            ItemId = itemId.Trim(),
            DisplayName = displayName.Trim(),
            InventoryQuantity = quantity,
            LookbackWindow = _lookbackWindow,
            WindowStart = start,
            WindowEnd = end,
            SoldQuantity = sold,
            SalesCount = count,
            UnitsPerDay = unitsPerDay,
            DaysOfSupply = daysOfSupply,
            SellThroughRate = sellThrough
        };
    }

    public IReadOnlyList<InventoryVelocity> CalculateMany(
        IEnumerable<InventoryAgingItem> items,
        IEnumerable<ShopSale>? sales,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var materialized = sales?.ToArray() ?? Array.Empty<ShopSale>();
        return items.Select(item => Calculate(item, materialized, now)).ToArray();
    }

    internal static string Normalize(string? value)
        => (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    private static int SaturatingAdd(int left, int right)
    {
        if (right <= 0) return left;
        return left > int.MaxValue - right ? int.MaxValue : left + right;
    }
}
