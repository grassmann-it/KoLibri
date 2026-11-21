using System.Globalization;
using System.Text.Json;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using LibKolibri;
using LibKolibri.Access;

namespace KolLibri.Mcp;

/// <summary>Sales of one item in the own store's log.</summary>
public sealed record ShopItemSales(string ItemName, int SaleCount, int Quantity, long TotalMeat, long WeightedUnitPrice, DateTimeOffset? LastSale);

/// <summary>One buyer as the AI may see them: a stable key, never the character name.</summary>
public sealed record BuyerSummary(
    string BuyerKey, int LoyaltyPoints, long TotalMeat, int TotalQuantity, int SaleCount,
    DateTimeOffset? FirstPurchase, DateTimeOffset? LastPurchase, bool IsBlocked, string? Notes);

public sealed record ShopSalesReport(
    DateTimeOffset FetchedAt, bool IsStale, int SaleCount, long TotalMeat, int NewSalesInCrm,
    IReadOnlyList<ShopItemSales> Items, IReadOnlyList<BuyerSummary> TopBuyers);

/// <summary>A suggested new price for one own listing, with the evidence behind it.</summary>
public sealed record PriceSuggestion(
    string ItemId, string? ItemName, long CurrentPrice, int Quantity, long? MarketLowest,
    long? SuggestedPrice, string Advice, int RecentSoldQuantity, bool MarketStale);

public static class ShopAdvice
{
    /// <summary>Same rule as the CLI: never undercut ourselves, go just below the floor otherwise.</summary>
    public static long? Suggest(long current, long? marketLowest)
    {
        if (marketLowest is null) return null;
        if (current <= marketLowest.Value) return current;
        var below = Math.Min(marketLowest.Value - 1, (long)Math.Floor(marketLowest.Value * 0.995m));
        return Math.Max(100, below);
    }

    public static string Describe(long current, long? lowest, long? suggested, int recentSold)
    {
        if (lowest is null) return "No market data; keep the price.";
        if (suggested is null || suggested == current)
            return recentSold == 0 && current * 10 < lowest.Value * 9 ? "Cheaper than the market and nothing sold recently; consider raising."
                : current < lowest.Value ? "Already the cheapest; consider a small raise if it sells quickly." : "Matches the market floor; keep.";
        return recentSold > 0 ? "Above the market floor but still selling; lower slightly to stay ahead."
            : "Above the market floor and nothing sold recently; lower.";
    }
}

public sealed partial class KolibriClientActions
{
    private readonly BuyerCrmStore _crm = new();

    private static BuyerSummary Summarize(BuyerCrmProfile p) => new(
        p.BuyerKey, p.LoyaltyPoints, p.TotalMeat, p.TotalQuantity, p.SaleCount, p.FirstPurchase, p.LastPurchase, p.IsBlocked, p.Notes);

    public async Task<ShopSalesReport> ShopSalesAsync(AccountRole role, bool refresh, int topBuyers, CancellationToken ct)
    {
        using var client = await KolibriClient.OpenAsync(role, cancellationToken: ct).ConfigureAwait(false);
        var snapshot = await client.GetShopSalesAsync(_crm, refresh, ct).ConfigureAwait(false);
        var database = await _crm.LoadAsync(ct).ConfigureAwait(false);
        var sync = BuyerCrmService.ApplySales(database, snapshot.Sales);
        if (sync.NewSales > 0) await _crm.SaveAsync(sync.Database, ct).ConfigureAwait(false);
        return BuildSalesReport(snapshot, sync, topBuyers);
    }

    internal static ShopSalesReport BuildSalesReport(ShopSalesSnapshot snapshot, BuyerCrmSyncResult sync, int topBuyers)
    {
        var items = snapshot.Sales.GroupBy(s => s.ItemName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ShopItemSales(g.Key, g.Count(), g.Sum(s => s.Quantity), g.Sum(s => s.TotalPrice),
                g.Sum(s => s.Quantity) == 0 ? 0 : g.Sum(s => s.TotalPrice) / g.Sum(s => (long)s.Quantity), g.Max(s => s.SoldAt)))
            .OrderByDescending(i => i.TotalMeat).Take(50).ToArray();
        var top = sync.Database.Buyers.OrderByDescending(b => b.TotalMeat).Take(Math.Clamp(topBuyers, 0, 50)).Select(Summarize).ToArray();
        return new ShopSalesReport(snapshot.FetchedAt, snapshot.IsStale, snapshot.Sales.Count, snapshot.Sales.Sum(s => s.TotalPrice),
            sync.NewSales, items, top);
    }

    public async Task<IReadOnlyList<BuyerSummary>> BuyersAsync(int minPoints, int limit, CancellationToken ct)
    {
        var database = await _crm.LoadAsync(ct).ConfigureAwait(false);
        return database.Buyers.Where(b => b.LoyaltyPoints >= minPoints).OrderByDescending(b => b.LoyaltyPoints)
            .ThenByDescending(b => b.TotalMeat).Take(Math.Clamp(limit, 1, 200)).Select(Summarize).ToArray();
    }

    public async Task<BuyerSummary?> BuyerAsync(string buyerKey, CancellationToken ct)
    {
        var database = await _crm.LoadAsync(ct).ConfigureAwait(false);
        var p = database.Buyers.FirstOrDefault(b => string.Equals(b.BuyerKey, buyerKey, StringComparison.Ordinal));
        return p is null ? null : Summarize(p);
    }

    public async Task<IReadOnlyList<PriceSuggestion>> PriceSuggestionsAsync(AccountRole role, int maxItems, CancellationToken ct)
    {
        using var client = await KolibriClient.OpenAsync(role, cancellationToken: ct).ConfigureAwait(false);
        var shop = await client.GetOwnShopAsync(true, ct).ConfigureAwait(false);
        var sold = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var sales = await client.GetShopSalesAsync(_crm, false, ct).ConfigureAwait(false);
            foreach (var g in sales.Sales.GroupBy(s => s.ItemName, StringComparer.OrdinalIgnoreCase)) sold[g.Key] = g.Sum(s => s.Quantity);
        }
        catch (Exception e) when (e is not OperationCanceledException) { /* sales are optional evidence */ }

        var result = new List<PriceSuggestion>();
        foreach (var listing in shop.OrderByDescending(l => l.Price * l.Quantity).Take(Math.Clamp(maxItems, 1, 40)))
        {
            ct.ThrowIfCancellationRequested();
            var name = listing.DisplayName ?? listing.ItemId;
            var market = await client.SearchMallAsync(name, ct).ConfigureAwait(false);
            long? lowest = market.Listings.Count == 0 ? null : market.Listings.Min(l => l.Price);
            var suggested = ShopAdvice.Suggest(listing.Price, lowest);
            var recent = sold.GetValueOrDefault(name);
            result.Add(new PriceSuggestion(listing.ItemId, listing.DisplayName, listing.Price, listing.Quantity, lowest, suggested,
                ShopAdvice.Describe(listing.Price, lowest, suggested, recent), recent, market.IsStale));
        }
        return result;
    }

    public async Task<(bool Sent, string? Refusal)> RewardAsync(
        AccountRole role, string buyerKey, string text, int? itemId, int quantity, int pointsCost, CancellationToken ct)
    {
        var database = await _crm.LoadAsync(ct).ConfigureAwait(false);
        var profile = database.Buyers.FirstOrDefault(b => string.Equals(b.BuyerKey, buyerKey, StringComparison.Ordinal));
        if (profile is null) return (false, "Unknown buyer key.");
        if (profile.IsBlocked) return (false, "This buyer is blocked locally.");
        if (profile.LoyaltyPoints < pointsCost) return (false, $"The buyer has {profile.LoyaltyPoints} points, {pointsCost} needed.");
        if (string.IsNullOrWhiteSpace(profile.CharacterName) || profile.CharacterName == profile.BuyerKey)
            return (false, "The character name for this buyer is not known locally.");
        using var client = await KolibriClient.OpenAsync(role, cancellationToken: ct).ConfigureAwait(false);
        IReadOnlyList<KoLibri.Core.Spiel.KoLMailAttachment> items = itemId is { } id
            ? [new KoLibri.Core.Spiel.KoLMailAttachment(id, quantity)] : [];
        var sent = await client.SendKMailAsync(profile.CharacterName, text, items, 0, ct).ConfigureAwait(false);
        if (!sent) return (false, "KoL did not confirm the message.");
        if (pointsCost > 0)
            await _crm.SaveAsync(BuyerCrmService.SpendPoints(database, buyerKey, pointsCost), ct).ConfigureAwait(false);
        return (true, null);
    }
}

public partial class KolibriMcpServer
{
    private void EnableShopTools(AccessGate gate, IAccountActions actions)
    {
        Register("kol_shop_sales",
            "Read-only: sales of the own mall store from its log, grouped per item, plus the top buyers by Meat. Buyers are stable keys, never character names. Syncs the local loyalty ledger. Needs right 'shop.read'.",
            Sch("""{"type":"object","additionalProperties":false,"properties":{@ROLE@,"refresh":{"type":"boolean"},"topBuyers":{"type":"integer","minimum":0,"maximum":50}}}"""),
            async (a, ct) =>
            {
                var role = RoleFor(a, _options.DefaultRole);
                return await actions.ShopSalesAsync(role, BoolProperty(a, "refresh"), NullableIntProperty(a, "topBuyers") ?? 10, ct).ConfigureAwait(false);
            });

        Register("kol_shop_buyers",
            "Read-only: customers from the local loyalty ledger (1 point per 1,000 Meat plus 1 per sale), best first. Keys only. Use minPoints to find reward candidates. Needs right 'shop.read'.",
            Sch("""{"type":"object","additionalProperties":false,"properties":{"minPoints":{"type":"integer","minimum":0},"limit":{"type":"integer","minimum":1,"maximum":200}}}"""),
            async (a, ct) => new { buyers = await actions.BuyersAsync(NullableIntProperty(a, "minPoints") ?? 0, NullableIntProperty(a, "limit") ?? 50, ct).ConfigureAwait(false) });

        Register("kol_shop_price_suggestions",
            "Read-only: own listings against the current mall floor and recent sales, with a suggested price and advice. Apply with kol_reprice_shop. Needs right 'shop.read'.",
            Sch("""{"type":"object","additionalProperties":false,"properties":{@ROLE@,"maxItems":{"type":"integer","minimum":1,"maximum":40}}}"""),
            async (a, ct) =>
            {
                var role = RoleFor(a, _options.DefaultRole);
                return new { role = role.ToString(), suggestions = await actions.PriceSuggestionsAsync(role, NullableIntProperty(a, "maxItems") ?? 20, ct).ConfigureAwait(false) };
            });

        Register("kol_reward_buyer",
            "Reward a customer from the loyalty ledger with a KMail and optionally an item gift, optionally spending points. The AI only sees the buyer key; the name stays local. Blocked buyers are refused. Two-step. Needs right 'message.send' (and 'transfer.items' with a gift).",
            Sch("""{"type":"object","additionalProperties":false,"required":["buyerKey","message"],"properties":{@ROLE@,"buyerKey":{"type":"string"},"message":{"type":"string","maxLength":2000},"itemId":{"type":"integer","minimum":1},"quantity":{"type":"integer","minimum":1,"maximum":1000},"pointsCost":{"type":"integer","minimum":0},@PLAN@}}"""),
            (a, ct) => RewardBuyerAsync(gate, actions, a, ct));
    }

    private async Task<object?> RewardBuyerAsync(AccessGate gate, IAccountActions actions, JsonElement args, CancellationToken ct)
    {
        var role = RoleFor(args, _options.DefaultRole);
        var key = OptionalString(args, "buyerKey")?.Trim() ?? "";
        var text = OptionalString(args, "message") ?? "";
        var itemId = NullableIntProperty(args, "itemId");
        var quantity = itemId is null ? 0 : NullableIntProperty(args, "quantity") ?? 1;
        var points = NullableIntProperty(args, "pointsCost") ?? 0;
        if (key.Length == 0) throw new McpInvalidParamsException("buyerKey is required.");
        if (text.Length is < 1 or > 2000) throw new McpInvalidParamsException("message must be 1..2000 characters.");
        var buyer = await actions.BuyerAsync(key, ct).ConfigureAwait(false) ?? throw new McpInvalidParamsException("Unknown buyer key.");
        if (buyer.IsBlocked) throw new McpInvalidParamsException("This buyer is blocked locally; no reward.");
        if (buyer.LoyaltyPoints < points) throw new McpInvalidParamsException($"The buyer has {buyer.LoyaltyPoints} points, {points} needed.");

        var requests = new List<AccessRequest> { new("message.send", role.ToString(), 1) };
        if (itemId is not null) requests.Add(new("transfer.items", role.ToString(), quantity));
        return await TwoStepAsync(gate, args, "kol_reward_buyer", requests,
            () => Task.FromResult<object>(new { role = role.ToString(), buyerKey = key, buyer.LoyaltyPoints, pointsCost = points, message = text, itemId, quantity }),
            async held =>
            {
                var (sent, refusal) = await actions.RewardAsync(role, key, text, itemId, quantity, points, ct).ConfigureAwait(false);
                if (sent) foreach (var h in held) h.Commit(h.Amount, h.Right);
                return new { preview = false, buyerKey = key, sent, refusal };
            }).ConfigureAwait(false);
    }
}
