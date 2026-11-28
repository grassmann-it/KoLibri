using System.Globalization;
using KoLibri;
using KoLibri.Extras;

internal static partial class KolibriCli
{
    private static async Task<int> ShopAsync(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("add", StringComparison.OrdinalIgnoreCase))
            return await ShopAddAsync(args[1..]);
        var (client, session) = await OpenAsync(ParseRole(GetOption(args, "--role") ?? "mallbot"));
        await using var _ = client;
        await using var __ = session;
        var shop = await session.Shop.GetListingsAsync(refresh: HasFlag(args, "--refresh"));
        var now = DateTimeOffset.UtcNow;
        WriteJson(new
        {
            fetchedAt = now,
            count = shop.Count,
            listings = shop.Select(l => new
            {
                itemId = l.Item.Id.ToString(CultureInfo.InvariantCulture),
                price = l.Price,
                quantity = l.Quantity,
                purchaseLimit = l.LimitPerDay > 0 ? l.LimitPerDay : (int?)null,
                observedAt = now,
                displayName = l.Name
            }).ToArray()
        });
        return 0;
    }

    private static async Task<int> CrmAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "mallbot");
        var (client, session) = await OpenAsync(role);
        await using var _ = client;
        await using var __ = session;
        var crmStore = new BuyerCrmStore(client.Options.StateDirectory);
        var buyerHasher = crmStore.CreateHasher();
        var knownAccountKeys = (await client.Accounts.ListAsync())
            .Select(account => buyerHasher.Hash(account.PlayerName))
            .ToHashSet(StringComparer.Ordinal);
        var fetchedAt = DateTimeOffset.UtcNow;
        var sales = (await session.Shop.GetSalesAsync(refresh: HasFlag(args, "--refresh")))
            .Select(s => new SaleRecord(buyerHasher.Hash(s.Buyer), s.ItemName, s.Quantity, s.Meat,
                s.Quantity > 0 ? s.Meat / s.Quantity : s.Meat, s.At, fetchedAt, s.Buyer))
            .ToArray();
        var database = await crmStore.LoadAsync();
        var sync = BuyerCrmService.ApplySales(database, sales);
        if (sync.NewSales > 0)
            await crmStore.SaveAsync(sync.Database);
        var report = ShopBuyerAnalytics.Analyze(sales, new ShopBuyerAnalyticsOptions
        {
            KnownAccountBuyerKeys = knownAccountKeys
        });
        WriteJson(new
        {
            role = role.ToString(),
            FetchedAt = fetchedAt,
            IsStale = false,
            crmSync = new
            {
                sync.NewSales,
                sync.DuplicateSales,
                sync.PointsGranted,
                profileCount = sync.Database.Buyers.Count
            },
            report
        });
        return 0;
    }

    private static async Task<int> CrmBlockAsync(string[] args)
    {
        var buyerKey = GetOption(args, "--key")
            ?? (args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null)
            ?? throw new ArgumentException("--key BUYER_KEY is missing.");
        var blocked = !bool.TryParse(GetOption(args, "--blocked"), out var parsed) || parsed;
        var store = new BuyerCrmStore(ConfiguredStateDirectory);
        var database = await store.LoadAsync();
        var updated = BuyerCrmService.SetBlocked(database, buyerKey, blocked, GetOption(args, "--note"));
        await store.SaveAsync(updated);
        WriteJson(new { buyerKey, blocked, saved = true });
        return 0;
    }

    private static async Task<int> MallAsync(string[] args)
    {
        var itemText = GetOption(args, "--item") ?? throw new ArgumentException("--item ITEM_ID is missing.");
        if (!int.TryParse(itemText, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId) || itemId <= 0)
            throw new ArgumentException("--item must be a numeric KoL item id.");
        var (client, session) = await OpenAsync(ParseRole(GetOption(args, "--role") ?? "mallbot"));
        await using var _ = client;
        await using var __ = session;
        var offers = await session.Mall.GetOffersAsync(itemId, refresh: HasFlag(args, "--refresh"));
        var source = new Uri(client.Options.BaseUri, $"mall.php?pudnuggler={Uri.EscapeDataString(offers.Name)}");
        WriteJson(new
        {
            itemId = itemText,
            listings = offers.Listings.Select(l => new
            {
                itemId = l.Item.Id.ToString(CultureInfo.InvariantCulture),
                storeId = l.StoreId.ToString(CultureInfo.InvariantCulture),
                storeName = l.StoreName,
                price = l.UnitPrice,
                quantity = l.Quantity,
                purchaseLimit = l.LimitPerDay,
                observedAt = offers.FetchedAt,
                sourceUri = source,
                itemName = offers.Name,
                quantityKnown = l.QuantityKnown,
                limitKind = l.LimitPerDay is > 0 ? "Limited" : l.LimitKnown ? "Unlimited" : "Unknown"
            }).ToArray(),
            fetchedAt = offers.FetchedAt,
            sourceUri = source,
            isStale = false
        });
        return 0;
    }

    private static async Task<int> PricegunAsync(string[] args)
    {
        var itemText = GetOption(args, "--item")
            ?? (args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null)
            ?? throw new ArgumentException("--item ITEM_ID is missing.");
        if (!int.TryParse(itemText, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId) || itemId <= 0)
            throw new ArgumentException("--item must be a numeric KoL item id.");
        await using var client = CreateClient();
        // Pricegun needs no KoL session; each CLI run starts with an empty cache, so --refresh is implied.
        var history = await client.GetPriceHistoryAsync(itemId)
            ?? throw new InvalidOperationException("pricegun.loathers.net could not be read.");
        var sales = history.Points.Select(p => new { soldAt = p.At, unitPrice = p.Price, quantity = p.Volume }).ToArray();
        var prices = history.Points.Select(x => (decimal)x.Price).OrderBy(x => x).ToArray();
        var totalQuantity = history.Points.Sum(x => x.Volume);
        var weightedAverage = totalQuantity == 0
            ? (decimal?)null
            : history.Points.Sum(x => (decimal)x.Price * x.Volume) / totalQuantity;
        var median = prices.Length == 0
            ? (decimal?)null
            : prices.Length % 2 == 1
                ? prices[prices.Length / 2]
                : (prices[prices.Length / 2 - 1] + prices[prices.Length / 2]) / 2m;
        WriteJson(new
        {
            ItemId = itemText,
            DisplayName = client.Data.Item(itemId)?.Name,
            history.FetchedAt,
            IsStale = false,
            saleCount = sales.Length,
            totalQuantity,
            minPrice = prices.Length == 0 ? (decimal?)null : prices[0],
            latest = sales.OrderByDescending(x => x.soldAt).FirstOrDefault(),
            medianPrice = median,
            weightedAveragePrice = weightedAverage,
            maxPrice = prices.Length == 0 ? (decimal?)null : prices[^1],
            sales
        });
        return 0;
    }

    internal sealed record PriceUpdate(string ItemId, long ExpectedCurrentPrice, long NewPrice);

    internal sealed record PriceUpdateResult(
        string ItemId, long ExpectedCurrentPrice, long? ObservedCurrentPrice, long NewPrice,
        int? ExpectedPurchaseLimit, int? ObservedPurchaseLimit, string Status, string? Message = null);

    /// <summary>The 0.4 preview rules: Missing, Conflict (price moved), Hold (already there), Ready.</summary>
    internal static IReadOnlyList<PriceUpdateResult> PlanPriceUpdates(IReadOnlyList<PriceUpdate> updates, IReadOnlyList<ShopListing> listings)
        => updates.Select(update =>
        {
            var current = listings.FirstOrDefault(l => l.Item.Id.ToString(CultureInfo.InvariantCulture) == update.ItemId);
            int? limit = current is null ? null : current.LimitPerDay;
            if (current is null)
                return new PriceUpdateResult(update.ItemId, update.ExpectedCurrentPrice, null, update.NewPrice, null, null, "Missing", "Item is not in the own shop.");
            if (current.Price != update.ExpectedCurrentPrice)
                return new PriceUpdateResult(update.ItemId, update.ExpectedCurrentPrice, current.Price, update.NewPrice, null, limit, "Conflict",
                    "Own-shop price or limit changed since the plan was created.");
            if (current.Price == update.NewPrice)
                return new PriceUpdateResult(update.ItemId, update.ExpectedCurrentPrice, current.Price, update.NewPrice, null, limit, "Hold", "Already at requested price.");
            return new PriceUpdateResult(update.ItemId, update.ExpectedCurrentPrice, current.Price, update.NewPrice, null, limit, "Ready");
        }).ToArray();

    private static async Task<int> ApplyPricesAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "mallbot");
        var specifications = GetOptions(args, "--set");
        if (specifications.Count == 0)
            throw new ArgumentException("At least one --set ITEM_ID:EXPECTED:NEW is required.");

        var updates = specifications.Select(ParsePriceUpdate).ToArray();
        if (updates.GroupBy(u => u.ItemId, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new ArgumentException("Each item may appear only once in a price update batch.");
        var (client, session) = await OpenAsync(role);
        await using var _ = client;
        await using var __ = session;
        var results = PlanPriceUpdates(updates, await session.Shop.GetListingsAsync(refresh: true));
        var ready = results.Where(r => r.Status == "Ready").ToArray();
        var committed = false;
        if (HasFlag(args, "--commit") && ready.Length > 0 && !results.Any(r => r.Status is "Missing" or "Conflict"))
        {
            var outcome = await session.Shop.RepriceAsync(ready.Select(r => new ShopPriceChange(int.Parse(r.ItemId, CultureInfo.InvariantCulture), r.NewPrice)
            {
                // The user's EXPECTED, not a second read: a price that moved since then is a conflict, not overwritten.
                ExpectedPrice = r.ExpectedCurrentPrice
            }).ToArray());
            committed = outcome.Status != ActionStatus.Rejected;
            var after = await session.Shop.GetListingsAsync(refresh: true);
            results = results.Select(r =>
            {
                if (r.Status != "Ready") return r;
                var now = after.FirstOrDefault(l => l.Item.Id.ToString(CultureInfo.InvariantCulture) == r.ItemId);
                if (now is null) return r with { ObservedCurrentPrice = null, Status = "VerificationFailed", Message = "Item disappeared after the update." };
                if (now.Price != r.NewPrice && now.Price != r.ExpectedCurrentPrice)
                    return r with
                    {
                        ObservedCurrentPrice = now.Price, ObservedPurchaseLimit = now.LimitPerDay, Status = "Conflict",
                        Message = $"The shop price is {now.Price}, not the expected {r.ExpectedCurrentPrice}; nothing was changed for this item."
                    };
                return r with
                {
                    ObservedCurrentPrice = now.Price,
                    ObservedPurchaseLimit = now.LimitPerDay,
                    Status = now.Price == r.NewPrice ? "Updated" : "VerificationFailed",
                    Message = now.Price == r.NewPrice ? null : outcome.Rejection ?? "Fresh shop read did not show the requested price."
                };
            }).ToArray();
        }
        var succeeded = committed
            ? results.All(r => r.Status is "Updated" or "Hold")
            : results.All(r => r.Status is "Ready" or "Hold");
        WriteJson(new
        {
            role = role.ToString(),
            committed,
            succeeded,
            ObservedAt = DateTimeOffset.UtcNow,
            Results = results
        });
        return succeeded ? 0 : 3;
    }

    private static PriceUpdate ParsePriceUpdate(string specification)
    {
        var fields = specification.Split(':', StringSplitOptions.TrimEntries);
        if (fields.Length != 3
            || !long.TryParse(fields[0], out _)
            || !long.TryParse(fields[1], out var expected)
            || !long.TryParse(fields[2], out var proposed))
            throw new ArgumentException(
                $"Invalid --set '{specification}'. Expected ITEM_ID:EXPECTED_PRICE:NEW_PRICE.");
        if (!fields[0].All(char.IsDigit)) throw new ArgumentException("ItemId must be a numeric KoL item id.");
        if (expected < 0) throw new ArgumentOutOfRangeException(nameof(specification), "The expected price must not be negative.");
        if (proposed < 100) throw new ArgumentOutOfRangeException(nameof(specification), "KoL mall prices must be at least 100 Meat.");
        return new PriceUpdate(fields[0], expected, proposed);
    }
}
