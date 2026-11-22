using System.Globalization;

namespace KoLibri.Core.Markt;

/// <summary>Reads the live mall, the own shop and Pricegun for one item and plans the buy. Read-only.</summary>
public static class ItemArbitrageService
{
    public static async Task<ItemArbitragePlan> PlanAsync(
        IMallReadClient mall,
        PricegunReadClient? pricegun,
        int itemId,
        ItemArbitrageSettings? settings = null,
        long? meatAvailable = null,
        IReadOnlySet<string>? ownStoreNames = null,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mall);
        settings ??= new ItemArbitrageSettings();
        settings.Validate();
        var id = itemId.ToString(CultureInfo.InvariantCulture);
        var notes = new List<string>();

        var page = await mall.SearchItemsAsync(id, forceRefresh, cancellationToken).ConfigureAwait(false);
        var group = page.Items.FirstOrDefault(g => g.ItemId == id);
        if (page.IsStale) notes.Add("The mall data is stale (cached); refresh before buying.");
        if (group is { IsTruncated: true })
            notes.Add($"Only the cheapest {group.Listings.Count} of {group.TotalOffers} offers were read.");

        long? own = null;
        try
        {
            var shop = await mall.FetchOwnShopAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
            own = shop.FirstOrDefault(x => x.ItemId == id)?.Price;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            notes.Add("The own shop could not be read: " + e.Message);
        }

        long? median = null;
        if (pricegun is not null)
        {
            try
            {
                var snapshot = await pricegun.FetchAsync(id, forceRefresh, cancellationToken).ConfigureAwait(false);
                median = Spiel.ArbitrageRule.Median(snapshot.Sales.Select(s => s.UnitPrice).ToArray());
                if (median is null) notes.Add("Pricegun has too few sales for a median.");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                notes.Add("Pricegun could not be read: " + e.Message);
            }
        }

        var plan = ItemArbitragePlanner.Plan(new ItemArbitrageInputs
        {
            ItemId = itemId,
            ItemName = group?.ItemName,
            Offers = group?.Listings ?? [],
            OwnShopPrice = own,
            PricegunMedian = median,
            MeatAvailable = meatAvailable,
            OwnStoreNames = ownStoreNames ?? new HashSet<string>()
        }, settings);
        return plan with { Notes = notes.Concat(plan.Notes).ToArray() };
    }
}
