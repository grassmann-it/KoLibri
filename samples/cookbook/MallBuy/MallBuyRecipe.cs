using KoLibri;

namespace Cookbook.MallBuy;

public sealed class MallBuyOptions
{
    public int ItemId { get; init; }
    public int Quantity { get; init; } = 1;
    /// <summary>Preisobergrenze pro Stück (Pflicht, > 0).</summary>
    public long MaxUnitPrice { get; init; }
    /// <summary>Meat, das nach dem Kauf übrig bleiben muss.</summary>
    public long MeatReserve { get; init; }
    /// <summary>Ohne Yes wird nur das Angebot berechnet und gezeigt.</summary>
    public bool Yes { get; init; }
}

public static class Recipe
{
    public static async Task<int> RunAsync(KolibriSession session, MallBuyOptions options, TextWriter output, CancellationToken ct = default)
    {
        if (options.ItemId <= 0 || options.Quantity <= 0 || options.MaxUnitPrice <= 0)
        {
            output.WriteLine("Es braucht Item-Id, Menge und eine Preisobergrenze (> 0).");
            return 2;
        }

        var order = new PurchaseOrder(new ItemRef(options.ItemId), options.Quantity, options.MaxUnitPrice)
        {
            MeatReserve = options.MeatReserve,
        };
        var quote = await session.Mall.QuoteAsync(order, ct);

        output.WriteLine($"Angebot: {quote.PlannedQuantity} von {options.Quantity} Stück, Gesamtpreis {quote.TotalCost:N0} Meat.");
        foreach (var l in quote.Plan)
            output.WriteLine($"  {l.Quantity} x {l.UnitPrice:N0} Meat bei {l.StoreName} (Shop {l.StoreId})");
        if (quote.Shortfall is { Length: > 0 } shortfall) output.WriteLine($"Hinweis: {shortfall}");
        if (quote.PlannedQuantity == 0) return 1;

        if (!options.Yes)
        {
            output.WriteLine("Trockenlauf: nichts gekauft. Mit --yes kaufen.");
            return 0;
        }

        var result = await session.Mall.BuyAsync(quote, ct);
        output.WriteLine($"{result.Status}: {result.QuantityBought} gekauft für {result.MeatSpent:N0} Meat.");
        return result.Status is PurchaseStatus.Bought ? 0 : 1;
    }
}
