using System.Globalization;
using System.Text.Json;
using KoLibri;
using KoLibri.Extras;
using KoLibri.Safety.Access;

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

/// <summary>
/// A suggested new price for one own listing, with the evidence behind it (from <see cref="ShopRepricer"/>).
/// <c>MarketLowest</c> stays for 0.4 clients and equals <c>CompetitorFloor</c>: the cheapest store that is not ours.
/// <c>Direction</c> is Up, Down, Keep or NoData; nothing is written, apply with kol_reprice_shop.
/// </summary>
public sealed record PriceSuggestion(
    string ItemId, string? ItemName, long CurrentPrice, int Quantity, long? MarketLowest,
    long? SuggestedPrice, string Advice, int RecentSoldQuantity, bool MarketStale,
    long? CompetitorFloor = null, int OwnListingsExcluded = 0, string Direction = nameof(RepriceDirection.NoData), long? PricegunMedian = null);

public sealed partial class KolibriClientActions
{
    private readonly BuyerCrmStore _crm = new();

    private static BuyerSummary Summarize(BuyerCrmProfile p) => new(
        p.BuyerKey, p.LoyaltyPoints, p.TotalMeat, p.TotalQuantity, p.SaleCount, p.FirstPurchase, p.LastPurchase, p.IsBlocked, p.Notes);

    public async Task<ShopSalesReport> ShopSalesAsync(AccountRole role, bool refresh, int topBuyers, CancellationToken ct)
    {
        await using var session = await _accounts.OpenAsync(role, ct).ConfigureAwait(false);
        var sales = await SalesAsync(session, refresh, ct).ConfigureAwait(false);
        var database = await _crm.LoadAsync(ct).ConfigureAwait(false);
        var sync = BuyerCrmService.ApplySales(database, sales);
        if (sync.NewSales > 0) await _crm.SaveAsync(sync.Database, ct).ConfigureAwait(false);
        return BuildSalesReport(DateTimeOffset.UtcNow, false, sales, sync, topBuyers);
    }

    /// <summary>The store's sales log as CRM records; buyer names become the CRM's stable keys (same keys as 0.4).</summary>
    private async Task<IReadOnlyList<SaleRecord>> SalesAsync(KolibriSession session, bool refresh, CancellationToken ct)
    {
        var hasher = _crm.CreateHasher();
        var now = DateTimeOffset.UtcNow;
        return (await session.Shop.GetSalesAsync(refresh, ct).ConfigureAwait(false))
            .Select(s => new SaleRecord(hasher.Hash(s.Buyer), s.ItemName, s.Quantity, s.Meat, s.Quantity > 0 ? s.Meat / s.Quantity : s.Meat, s.At, now, s.Buyer))
            .ToArray();
    }

    internal static ShopSalesReport BuildSalesReport(DateTimeOffset fetchedAt, bool isStale, IReadOnlyList<SaleRecord> sales, BuyerCrmSyncResult sync, int topBuyers)
    {
        var items = sales.GroupBy(s => s.ItemName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ShopItemSales(g.Key, g.Count(), g.Sum(s => s.Quantity), g.Sum(s => s.TotalPrice),
                g.Sum(s => s.Quantity) == 0 ? 0 : g.Sum(s => s.TotalPrice) / g.Sum(s => (long)s.Quantity), g.Max(s => s.SoldAt)))
            .OrderByDescending(i => i.TotalMeat).Take(50).ToArray();
        var top = sync.Database.Buyers.OrderByDescending(b => b.TotalMeat).Take(Math.Clamp(topBuyers, 0, 50)).Select(Summarize).ToArray();
        return new ShopSalesReport(fetchedAt, isStale, sales.Count, sales.Sum(s => s.TotalPrice), sync.NewSales, items, top);
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
        await using var session = await _accounts.OpenAsync(role, ct).ConfigureAwait(false);
        var shop = await session.Shop.GetListingsAsync(refresh: true, ct).ConfigureAwait(false);
        var sold = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var g in (await SalesAsync(session, false, ct).ConfigureAwait(false)).GroupBy(s => s.ItemName, StringComparer.OrdinalIgnoreCase))
                sold[g.Key] = g.Sum(s => s.Quantity);
        }
        catch (Exception e) when (e is not OperationCanceledException) { /* sales are optional evidence */ }
        // Own stores = every stored account (main and mallbot), never hard-coded; the name is the fallback.
        var accounts = await _accounts.Client.Accounts.ListAsync(ct).ConfigureAwait(false);
        var ownIds = new HashSet<int>(accounts.Where(a => a.PlayerId is not null).Select(a => a.PlayerId!.Value));
        if (session.Account.PlayerId is { } current) ownIds.Add(current);
        var ownNames = new HashSet<string>(accounts.Select(a => a.PlayerName).Append(session.Account.PlayerName)
            .Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
        var options = new ShopRepricerOptions { OwnStoreNames = ownNames };

        var result = new List<PriceSuggestion>();
        foreach (var listing in shop.OrderByDescending(l => l.Price * l.Quantity).Take(Math.Clamp(maxItems, 1, 40)))
        {
            ct.ThrowIfCancellationRequested();
            var market = await session.Mall.GetOffersAsync(listing.Item, refresh: true, ct).ConfigureAwait(false);
            PriceHistory? history = null;
            try { history = await _accounts.Client.GetPriceHistoryAsync(listing.Item, ct).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { /* Pricegun is optional evidence */ }
            var s = ShopRepricer.Suggest(listing, market, ownIds, history, options);
            var recent = sold.GetValueOrDefault(listing.Name);
            result.Add(new PriceSuggestion(listing.Item.Id.ToString(CultureInfo.InvariantCulture), s.Name, s.CurrentPrice, s.Quantity,
                s.CompetitorFloor, s.SuggestedPrice, s.Advice, recent, false,
                s.CompetitorFloor, s.OwnListingsExcluded, s.Direction.ToString(), s.PricegunMedian));
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
        await using var session = await _accounts.OpenForActionAsync(role, ct).ConfigureAwait(false);
        var items = itemId is { } id
            ? new[] { new InventoryItem(id, _accounts.Client.Data.Item(id)?.Name ?? id.ToString(CultureInfo.InvariantCulture), quantity) }
            : Array.Empty<InventoryItem>();
        var sent = await session.Mail.SendAsync(new MailMessage(profile.CharacterName, text) { Items = items }, ct).ConfigureAwait(false);
        // The mail may have arrived although the reply is lost: keep the budget held as unresolved, spend no points.
        if (sent.Status == MailSendStatus.Unclear)
            throw new UnreconciledPurchaseException("The reward KMail was sent but KoL's answer is missing; it may have arrived. Check the outbox.");
        if (sent.Status != MailSendStatus.Sent) return (false, "KoL did not confirm the message.");
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
                // The buyer ledger is shared by both accounts: rankings only appear when the grant covers both.
                var topBuyers = CrmReadable(gate) ? NullableIntProperty(a, "topBuyers") ?? 10 : 0;
                return await actions.ShopSalesAsync(role, BoolProperty(a, "refresh"), topBuyers, ct).ConfigureAwait(false);
            });

        Register("kol_shop_buyers",
            "Read-only: customers from the local loyalty ledger (1 point per 1,000 Meat plus 1 per sale), best first. Keys only. Use minPoints to find reward candidates. Needs right 'shop.read'.",
            Sch("""{"type":"object","additionalProperties":false,"properties":{"minPoints":{"type":"integer","minimum":0},"limit":{"type":"integer","minimum":1,"maximum":200}}}"""),
            async (a, ct) =>
            {
                RequireCrmAccess(gate);
                return new { buyers = await actions.BuyersAsync(NullableIntProperty(a, "minPoints") ?? 0, NullableIntProperty(a, "limit") ?? 50, ct).ConfigureAwait(false) };
            });

        Register("kol_shop_price_suggestions",
            "Read-only: own listings against the cheapest competitor (stores of the own accounts never count) and recent sales, with a suggested price, direction (Up/Down/Keep) and advice. Prices may go up. Writes nothing; apply with kol_reprice_shop. Needs right 'shop.read'.",
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
        // Role and action rights first: nothing about the buyer (status, points) is revealed before this passes.
        var token = Token();
        RequireRight(gate, token, "message.send", role);
        if (itemId is not null) RequireRight(gate, token, "transfer.items", role);
        RequireCrmAccess(gate);
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

    /// <summary>The buyer ledger has no per-account split, so reading it needs 'shop.read' on every account.</summary>
    private static bool CrmReadable(AccessGate gate)
    {
        var token = McpRequestContext.Token;
        if (string.IsNullOrEmpty(token)) return false;
        foreach (var role in Enum.GetValues<AccountRole>())
        {
            var d = gate.Authorize(token, new AccessRequest("shop.read", role.ToString()));
            if (!d.Allowed) return false;
            d.Reservation?.Release();
        }
        return true;
    }

    private static void RequireCrmAccess(AccessGate gate)
    {
        if (!CrmReadable(gate))
            throw new McpInvalidParamsException("[AccountNotGranted] Buyer data is shared by both accounts and needs 'shop.read' on both.");
    }
}
