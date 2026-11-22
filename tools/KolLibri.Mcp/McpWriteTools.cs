using System.Text.Json;
using KoLibri.Core.Basis;
using LibKolibri;
using LibKolibri.Access;

namespace KolLibri.Mcp;

/// <summary>One requested price change in the own mall store.</summary>
public sealed record PriceChange(string ItemId, long ExpectedPrice, long NewPrice);

/// <summary>What KoL's store page showed for one requested change.</summary>
public sealed record RepriceLine(string ItemId, long ExpectedPrice, long? ObservedPrice, long NewPrice, string Status, string? Message);

/// <summary>What the write tools need from a signed-in account. Real: <see cref="KolibriClientActions"/>.</summary>
/// <summary>A purchase POST was sent but KoL's answer is unknown. The spend stays reserved until the user reconciles it.</summary>
public sealed class UnreconciledPurchaseException(string message) : Exception(message);

public interface IAccountActions
{
    Task<long> MeatAsync(AccountRole role, CancellationToken ct);
    /// <summary>Buys under a price ceiling and reserve; returns items bought and meat spent as confirmed by KoL.</summary>
    Task<(int Quantity, long Spent, string Verdict)> BuyAsync(
        AccountRole role, int itemId, string itemName, int quantity, long maxPrice, long reserve, CancellationToken ct);
    Task<(int StepsAccepted, int BaconBought, long BaconSpent, string Verdict, IReadOnlyList<string> Notes)> MemeShopAsync(
        AccountRole role, bool topUp, long maxBaconPrice, long reserve, CancellationToken ct);
    /// <summary>KMail with items and/or meat to the user's other account; true when KoL confirmed it.</summary>
    Task<bool> TransferAsync(AccountRole from, int? itemId, int quantity, long meat, CancellationToken ct);
    Task<bool> MessageAsync(AccountRole from, string recipient, string text, CancellationToken ct);
    Task<(int Used, string? Rejection)> UseItemAsync(AccountRole role, int itemId, int quantity, CancellationToken ct);
    Task<IReadOnlyList<RepriceLine>> RepriceAsync(AccountRole role, IReadOnlyList<PriceChange> changes, bool commit, CancellationToken ct);
    Task<(int Consumed, string? Rejection)> ConsumeAsync(AccountRole role, int itemId, string kind, int quantity, CancellationToken ct);
    /// <summary>Rests; Done counts only rests with an observed effect, AdventuresSpent is what KoL charged.</summary>
    Task<(int Done, int AdventuresSpent, string? Problem)> RestAsync(AccountRole role, int times, CancellationToken ct);
    Task<ShopSalesReport> ShopSalesAsync(AccountRole role, bool refresh, int topBuyers, CancellationToken ct);
    Task<IReadOnlyList<BuyerSummary>> BuyersAsync(int minPoints, int limit, CancellationToken ct);
    Task<BuyerSummary?> BuyerAsync(string buyerKey, CancellationToken ct);
    Task<IReadOnlyList<PriceSuggestion>> PriceSuggestionsAsync(AccountRole role, int maxItems, CancellationToken ct);
    Task<(bool Sent, string? Refusal)> RewardAsync(AccountRole role, string buyerKey, string text, int? itemId, int quantity, int pointsCost, CancellationToken ct);
}

/// <summary>Runs the actions through <see cref="KolibriClient"/> with the stored sessions.</summary>
public sealed partial class KolibriClientActions : IAccountActions
{
    public async Task<long> MeatAsync(AccountRole role, CancellationToken ct)
    {
        using var client = await KolibriClient.OpenAsync(role, cancellationToken: ct).ConfigureAwait(false);
        return (await client.GetStatusAsync(true, ct).ConfigureAwait(false)).Meat;
    }

    public async Task<(int, long, string)> BuyAsync(AccountRole role, int itemId, string itemName, int quantity, long maxPrice, long reserve, CancellationToken ct)
    {
        using var client = await KolibriClient.OpenAsync(role, cancellationToken: ct).ConfigureAwait(false);
        if (maxPrice < 1) throw new ArgumentOutOfRangeException(nameof(maxPrice), "A positive price limit is required.");
        // One confirmed MCP buy sends at most one purchase POST.
        var o = await client.BuyCheapAsync(itemId, itemName, quantity, maxPrice, reserve, ct, singlePurchase: true).ConfigureAwait(false);
        if (o.Unclear) throw new UnreconciledPurchaseException(o.Rationale);
        return (o.Quantity, o.Spent, o.Verdict);
    }

    public async Task<(int, int, long, string, IReadOnlyList<string>)> MemeShopAsync(AccountRole role, bool topUp, long maxBaconPrice, long reserve, CancellationToken ct)
    {
        using var client = await KolibriClient.OpenAsync(role, cancellationToken: ct).ConfigureAwait(false);
        var r = await client.RunMemeShopAsync(topUp, maxBaconPrice, reserve, ct).ConfigureAwait(false);
        return (r.Steps.Count(s => s.Accepted), r.BaconTopUp?.Quantity ?? 0, r.BaconTopUp?.Spent ?? 0, r.Verdict,
            r.Steps.Select(s => $"{s.Description}: {(s.Accepted ? "ok" : "not done")} - {s.Message}").ToArray());
    }

    public async Task<bool> TransferAsync(AccountRole from, int? itemId, int quantity, long meat, CancellationToken ct)
    {
        using var client = await KolibriClient.OpenAsync(from, cancellationToken: ct).ConfigureAwait(false);
        var to = await client.OtherAccountNameAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The other account is not stored; nothing was sent.");
        IReadOnlyList<KoLibri.Core.Spiel.KoLMailAttachment> items = itemId is { } id
            ? [new KoLibri.Core.Spiel.KoLMailAttachment(id, quantity)] : [];
        return await client.SendKMailAsync(to, "Sent with KoLibri.", items, meat, ct).ConfigureAwait(false);
    }

    public async Task<bool> MessageAsync(AccountRole from, string recipient, string text, CancellationToken ct)
    {
        using var client = await KolibriClient.OpenAsync(from, cancellationToken: ct).ConfigureAwait(false);
        return await client.SendKMailAsync(recipient, text, null, 0, ct).ConfigureAwait(false);
    }

    public async Task<(int, string?)> UseItemAsync(AccountRole role, int itemId, int quantity, CancellationToken ct)
    {
        using var client = await KolibriClient.OpenAsync(role, cancellationToken: ct).ConfigureAwait(false);
        return await client.UseItemAsync(itemId, quantity, ct).ConfigureAwait(false);
    }

    public async Task<(int, string?)> ConsumeAsync(AccountRole role, int itemId, string kind, int quantity, CancellationToken ct)
    {
        using var client = await KolibriClient.OpenAsync(role, cancellationToken: ct).ConfigureAwait(false);
        var k = kind switch { "food" => KoLibri.Core.Spiel.CharacterConsumableKind.Food, "drink" => KoLibri.Core.Spiel.CharacterConsumableKind.Drink,
            "spleen" => KoLibri.Core.Spiel.CharacterConsumableKind.Spleen, _ => throw new ArgumentException("kind must be food, drink or spleen.") };
        return await client.ConsumeAsync(itemId, k, quantity, ct).ConfigureAwait(false);
    }

    public async Task<(int, int, string?)> RestAsync(AccountRole role, int times, CancellationToken ct)
    {
        using var client = await KolibriClient.OpenAsync(role, cancellationToken: ct).ConfigureAwait(false);
        var o = await client.RestAsync(times, ct).ConfigureAwait(false);
        return (o.Rests.Count, o.AdventuresSpent, o.Problem);
    }

    public async Task<IReadOnlyList<RepriceLine>> RepriceAsync(AccountRole role, IReadOnlyList<PriceChange> changes, bool commit, CancellationToken ct)
    {
        using var client = await KolibriClient.OpenAsync(role, cancellationToken: ct).ConfigureAwait(false);
        var result = await client.RepriceShopAsync(
            changes.Select(c => new KoLibri.Core.Markt.ShopPriceUpdate(c.ItemId, c.ExpectedPrice, c.NewPrice)), commit, ct)
            .ConfigureAwait(false);
        return result.Results.Select(r => new RepriceLine(r.ItemId, r.ExpectedCurrentPrice, r.ObservedCurrentPrice, r.NewPrice,
            r.Status.ToString(), r.Message)).ToArray();
    }
}

public partial class KolibriMcpServer
{
    private const int MaxBuyQuantity = 1_000;

    /// <summary>
    /// Registers the tools that change the game. Each is two-step: a call without <c>planId</c> only
    /// previews and returns a plan id; the call with that id (same arguments) executes, once.
    /// Rights, limits and budgets come from the caller's grant; without a token nothing runs.
    /// </summary>
    public void EnableWriteTools(AccessGate gate, IAccountActions actions)
    {
        Register("kol_mall_buy",
            "Buy an item from the mall under a price ceiling. Call without planId to preview worst-case cost and get a planId, then again with the same arguments plus planId to buy. Needs right 'mall.buy'.",
            Sch("""{"type":"object","additionalProperties":false,"required":["itemId","itemName","quantity","maxPricePerUnit"],"properties":{@ROLE@,"itemId":{"type":"integer","minimum":1},"itemName":{"type":"string"},"quantity":{"type":"integer","minimum":1,"maximum":1000},"maxPricePerUnit":{"type":"integer","minimum":1},@PLAN@}}"""),
            (a, ct) => MallBuyAsync(gate, actions, a, ct));

        Register("kol_meme_shop",
            "Run today's Internet Meme Shop, optionally topping up BACON in the mall (then also needs right 'mall.buy'; the spend counts against its budget). Two-step. Needs right 'daily.run'.",
            Sch("""{"type":"object","additionalProperties":false,"properties":{@ROLE@,"topUpBacon":{"type":"boolean"},"maxBaconPrice":{"type":"integer","minimum":1},@PLAN@}}"""),
            (a, ct) => MemeShopAsync(gate, actions, a, ct));

        Register("kol_transfer",
            "Send items and/or meat from one of the user's accounts to the other one by KMail. Two-step. Needs 'transfer.items' and/or 'transfer.meat' and both accounts in the approval.",
            Sch("""{"type":"object","additionalProperties":false,"properties":{@ROLE@,"itemId":{"type":"integer","minimum":1},"quantity":{"type":"integer","minimum":1},"meat":{"type":"integer","minimum":1},@PLAN@}}"""),
            (a, ct) => TransferAsync(gate, actions, a, ct));

        Register("kol_send_message",
            "Send a text KMail. The user sees the recipient and text in the preview log. Two-step. Needs right 'message.send'.",
            Sch("""{"type":"object","additionalProperties":false,"required":["recipient","message"],"properties":{@ROLE@,"recipient":{"type":"string"},"message":{"type":"string","maxLength":2000},@PLAN@}}"""),
            (a, ct) => MessageAsync(gate, actions, a, ct));

        Register("kol_use_item",
            "Use an inventory item one or more times (at most 50). Two-step. Needs right 'item.use'.",
            Sch("""{"type":"object","additionalProperties":false,"required":["itemId"],"properties":{@ROLE@,"itemId":{"type":"integer","minimum":1},"quantity":{"type":"integer","minimum":1,"maximum":50},@PLAN@}}"""),
            (a, ct) => UseItemAsync(gate, actions, a, ct));

        Register("kol_consume",
            "Eat, drink or take a spleen item from the inventory (kind food|drink|spleen, at most 100). KoL's own limits apply (too full, too drunk, spleen). Two-step. Needs right 'item.use'.",
            Sch("""{"type":"object","additionalProperties":false,"required":["itemId","kind"],"properties":{@ROLE@,"itemId":{"type":"integer","minimum":1},"kind":{"type":"string","enum":["food","drink","spleen"]},"quantity":{"type":"integer","minimum":1,"maximum":100},@PLAN@}}"""),
            (a, ct) => ConsumeAsync(gate, actions, a, ct));

        Register("kol_rest",
            "Rest at the campground 1..20 times. A rest can cost adventures and no free-rest state is readable, so it needs rights 'item.use' and 'adventure' (each rest is booked against both). Refused or unconfirmed rests are not counted. Two-step.",
            Sch("""{"type":"object","additionalProperties":false,"properties":{@ROLE@,"times":{"type":"integer","minimum":1,"maximum":20},@PLAN@}}"""),
            (a, ct) => RestAsync(gate, actions, a, ct));

        Register("kol_reprice_shop",
            "Change prices in the own mall store (at most 20 items). Preview shows what KoL's store page currently has. Needs right 'shop.reprice'.",
            Sch("""{"type":"object","additionalProperties":false,"required":["updates"],"properties":{@ROLE@,"updates":{"type":"array","minItems":1,"maxItems":20,"items":{"type":"object","additionalProperties":false,"required":["itemId","expectedPrice","newPrice"],"properties":{"itemId":{"type":"integer","minimum":1},"expectedPrice":{"type":"integer","minimum":0},"newPrice":{"type":"integer","minimum":100}}}},@PLAN@}}"""),
            (a, ct) => RepriceAsync(gate, actions, a, ct));

        EnableShopTools(gate, actions);
    }

    private static JsonElement Sch(string template)
        => Schema(template
            .Replace("@ROLE@", "\"role\":{\"type\":\"string\",\"enum\":[\"Main\",\"Mallbot\"]}")
            .Replace("@PLAN@", "\"planId\":{\"type\":\"string\"}"));

    // ---- shared two-step flow ----

    private static string Token() => McpRequestContext.Token ?? throw new McpInvalidParamsException("Authentication required.");

    private static List<UsageReservation> AuthorizeAll(AccessGate gate, string token, IEnumerable<AccessRequest> requests)
    {
        var held = new List<UsageReservation>();
        foreach (var request in requests)
        {
            var d = gate.Authorize(token, request);
            if (!d.Allowed)
            {
                foreach (var r in held) r.Release();
                throw new McpInvalidParamsException($"[{d.Code}] {d.Message}");
            }
            if (d.Reservation is not null) held.Add(d.Reservation);
        }
        return held;
    }

    /// <summary>Preview without planId (nothing held, nothing sent); execute with it (once, same arguments).</summary>
    private async Task<object?> TwoStepAsync(
        AccessGate gate, JsonElement args, string tool, IReadOnlyList<AccessRequest> requests,
        Func<Task<object>> preview, Func<IReadOnlyList<UsageReservation>, Task<object?>> execute)
    {
        var token = Token();
        var planId = OptionalString(args, "planId");
        if (planId is null)
        {
            foreach (var r in AuthorizeAll(gate, token, requests)) r.Release();
            var details = await preview().ConfigureAwait(false);
            var ticket = gate.IssuePlan(token, tool, CanonicalArgs(args));
            return new { preview = true, planId = ticket.PlanId, expiresAt = ticket.ExpiresAt, details,
                next = $"Call {tool} again with the same arguments and this planId to execute." };
        }

        var consumed = gate.ConsumePlan(token, planId, tool, CanonicalArgs(args));
        if (!consumed.Allowed) throw new McpInvalidParamsException($"[{consumed.Code}] {consumed.Message}");
        var held = AuthorizeAll(gate, token, requests);
        var unreconciled = false;
        try { return await execute(held).ConfigureAwait(false); }
        catch (UnreconciledPurchaseException e)
        {
            // The POST may have gone through: keep the budget held instead of freeing it.
            unreconciled = true;
            throw new McpInvalidParamsException($"Outcome unclear, the spend stays reserved until reconciled. {e.Message}");
        }
        finally { if (!unreconciled) foreach (var r in held) r.Release(); }   // no-op for everything already committed
    }

    /// <summary>Role/action check before any account access: no amounts, nothing held.</summary>
    private static void RequireRight(AccessGate gate, string token, string operation, AccountRole role, long? unitPrice = null)
    {
        var d = gate.Authorize(token, new AccessRequest(operation, role.ToString(), 0, unitPrice));
        if (!d.Allowed) throw new McpInvalidParamsException($"[{d.Code}] {d.Message}");
        d.Reservation?.Release();
    }

    private static void RequireOtherAccount(AccessGate gate, string token, AccountRole from)
    {
        var other = from == AccountRole.Main ? AccountRole.Mallbot : AccountRole.Main;
        var d = gate.Authorize(token, new AccessRequest("account.status", other.ToString()));
        if (!d.Allowed) throw new McpInvalidParamsException($"[{d.Code}] Transfers need both accounts in the approval. {d.Message}");
    }

    // ---- tools ----

    private async Task<object?> MallBuyAsync(AccessGate gate, IAccountActions actions, JsonElement args, CancellationToken ct)
    {
        var token = Token();
        var role = RoleFor(args, _options.DefaultRole);
        var itemId = NullableIntProperty(args, "itemId") ?? throw new McpInvalidParamsException("itemId is required.");
        var itemName = OptionalString(args, "itemName") ?? throw new McpInvalidParamsException("itemName is required.");
        var quantity = NullableIntProperty(args, "quantity") ?? 0;
        var maxPrice = (long?)NullableIntProperty(args, "maxPricePerUnit") ?? 0;
        if (quantity is < 1 or > MaxBuyQuantity || maxPrice < 1)
            throw new McpInvalidParamsException($"quantity must be 1..{MaxBuyQuantity} and maxPricePerUnit at least 1.");
        var worstCase = checked(quantity * maxPrice);
        RequireRight(gate, token, "mall.buy", role, maxPrice);   // role, right and price limit before the account is opened
        var meat = await actions.MeatAsync(role, ct).ConfigureAwait(false);
        var request = new AccessRequest("mall.buy", role.ToString(), worstCase, maxPrice, meat - worstCase);

        return await TwoStepAsync(gate, args, "kol_mall_buy", [request],
            () => Task.FromResult<object>(new { itemId, itemName, quantity, maxPricePerUnit = maxPrice, worstCaseMeat = worstCase, meatNow = meat }),
            async held =>
            {
                var reserve = gate.ActiveGrant(token)?.Rights.GetValueOrDefault(Rights.MallBuy)?.MeatReserve ?? 0;
                var (bought, spent, verdict) = await actions.BuyAsync(role, itemId, itemName, quantity, maxPrice, reserve, ct).ConfigureAwait(false);
                held[0].Commit(spent, $"{itemName} x{bought}");
                return new { preview = false, itemId, itemName, requested = quantity, bought, spentMeat = spent, verdict };
            }).ConfigureAwait(false);
    }

    private async Task<object?> MemeShopAsync(AccessGate gate, IAccountActions actions, JsonElement args, CancellationToken ct)
    {
        var token = Token();
        var role = RoleFor(args, _options.DefaultRole);
        var topUp = BoolProperty(args, "topUpBacon");
        var maxBacon = (long?)NullableIntProperty(args, "maxBaconPrice") ?? 1_500;
        if (maxBacon < 1) throw new McpInvalidParamsException("maxBaconPrice must be at least 1.");
        var requests = new List<AccessRequest> { new("daily.run", role.ToString(), 1, topUp ? maxBacon : null) };
        long cap = 0;
        if (topUp)
        {
            // BACON bought in the mall is mall spending: it needs that right and stays inside its budget.
            cap = gate.RemainingToday(token, Rights.MallBuy)
                ?? throw new McpInvalidParamsException("[RightNotGranted] Topping up BACON also needs right 'mall.buy'.");
            requests.Add(new AccessRequest("mall.buy", role.ToString(), cap, maxBacon));
        }

        return await TwoStepAsync(gate, args, "kol_meme_shop", requests,
            () => Task.FromResult<object>(new { role = role.ToString(), topUpBacon = topUp, maxBaconPrice = topUp ? maxBacon : (long?)null, baconBudgetMeat = topUp ? cap : (long?)null }),
            async held =>
            {
                var grant = gate.ActiveGrant(token);
                var reserve = grant?.Rights.GetValueOrDefault(Rights.DailyRun)?.MeatReserve ?? 200_000;
                if (topUp)
                {
                    var meat = await actions.MeatAsync(role, ct).ConfigureAwait(false);
                    reserve = Math.Max(Math.Max(reserve, grant?.Rights.GetValueOrDefault(Rights.MallBuy)?.MeatReserve ?? 0), meat - cap);
                }
                var r = await actions.MemeShopAsync(role, topUp, maxBacon, reserve, ct).ConfigureAwait(false);
                held[0].Commit(r.StepsAccepted > 0 || r.BaconBought > 0 ? 1 : 0, r.Verdict);
                if (topUp) held[1].Commit(r.BaconSpent, $"BACON x{r.BaconBought}");
                return new { preview = false, role = role.ToString(), verdict = r.Verdict, stepsAccepted = r.StepsAccepted,
                    baconBought = r.BaconBought, baconSpentMeat = r.BaconSpent, notes = r.Notes };
            }).ConfigureAwait(false);
    }

    private async Task<object?> TransferAsync(AccessGate gate, IAccountActions actions, JsonElement args, CancellationToken ct)
    {
        var token = Token();
        var from = RoleFor(args, _options.DefaultRole);
        var itemId = NullableIntProperty(args, "itemId");
        var quantity = NullableIntProperty(args, "quantity") ?? 0;
        var meat = (long?)NullableIntProperty(args, "meat") ?? 0;
        if (itemId is null && meat == 0) throw new McpInvalidParamsException("Give an item (itemId and quantity) or meat.");
        if (itemId is not null && quantity < 1) throw new McpInvalidParamsException("quantity is required with itemId.");
        if (itemId is null) quantity = 0;
        if (itemId is not null) RequireRight(gate, token, "transfer.items", from);
        if (meat > 0) RequireRight(gate, token, "transfer.meat", from);
        RequireOtherAccount(gate, token, from);

        var requests = new List<AccessRequest>();
        if (itemId is not null) requests.Add(new("transfer.items", from.ToString(), quantity));
        if (meat > 0)
        {
            var have = await actions.MeatAsync(from, ct).ConfigureAwait(false);
            requests.Add(new("transfer.meat", from.ToString(), meat, MeatAfter: have - meat));
        }
        var to = from == AccountRole.Main ? AccountRole.Mallbot : AccountRole.Main;

        return await TwoStepAsync(gate, args, "kol_transfer", requests,
            () => Task.FromResult<object>(new { from = from.ToString(), to = to.ToString(), itemId, quantity, meat }),
            async held =>
            {
                var confirmed = await actions.TransferAsync(from, itemId, quantity, meat, ct).ConfigureAwait(false);
                if (confirmed) foreach (var h in held) h.Commit(h.Amount, h.Right);
                return new { preview = false, from = from.ToString(), to = to.ToString(), itemId, quantity, meat, confirmed };
            }).ConfigureAwait(false);
    }

    private async Task<object?> MessageAsync(AccessGate gate, IAccountActions actions, JsonElement args, CancellationToken ct)
    {
        var from = RoleFor(args, _options.DefaultRole);
        var recipient = OptionalString(args, "recipient")?.Trim() ?? "";
        var text = OptionalString(args, "message") ?? "";
        if (recipient.Length is < 1 or > 30 || !recipient.All(c => char.IsLetterOrDigit(c) || c is ' ' or '_' or '-'))
            throw new McpInvalidParamsException("recipient must be a KoL player name.");
        if (text.Length is < 1 or > 2000) throw new McpInvalidParamsException("message must be 1..2000 characters.");

        return await TwoStepAsync(gate, args, "kol_send_message", [new AccessRequest("message.send", from.ToString(), 1)],
            () => Task.FromResult<object>(new { from = from.ToString(), recipient, message = text }),
            async held =>
            {
                var sent = await actions.MessageAsync(from, recipient, text, ct).ConfigureAwait(false);
                if (sent) held[0].Commit(1, recipient);
                return new { preview = false, recipient, sent };
            }).ConfigureAwait(false);
    }

    private async Task<object?> UseItemAsync(AccessGate gate, IAccountActions actions, JsonElement args, CancellationToken ct)
    {
        var role = RoleFor(args, _options.DefaultRole);
        var itemId = NullableIntProperty(args, "itemId") ?? throw new McpInvalidParamsException("itemId is required.");
        var quantity = NullableIntProperty(args, "quantity") ?? 1;
        if (quantity is < 1 or > 50) throw new McpInvalidParamsException("quantity must be 1..50.");

        return await TwoStepAsync(gate, args, "kol_use_item", [new AccessRequest("item.use", role.ToString(), quantity)],
            () => Task.FromResult<object>(new { role = role.ToString(), itemId, quantity }),
            async held =>
            {
                var (used, rejection) = await actions.UseItemAsync(role, itemId, quantity, ct).ConfigureAwait(false);
                held[0].Commit(used, $"item {itemId}");
                return new { preview = false, itemId, requested = quantity, used, rejection };
            }).ConfigureAwait(false);
    }

    private async Task<object?> ConsumeAsync(AccessGate gate, IAccountActions actions, JsonElement args, CancellationToken ct)
    {
        var role = RoleFor(args, _options.DefaultRole);
        var itemId = NullableIntProperty(args, "itemId") ?? throw new McpInvalidParamsException("itemId is required.");
        var kind = OptionalString(args, "kind") ?? "";
        if (kind is not ("food" or "drink" or "spleen")) throw new McpInvalidParamsException("kind must be food, drink or spleen.");
        var quantity = NullableIntProperty(args, "quantity") ?? 1;
        if (quantity is < 1 or > 100) throw new McpInvalidParamsException("quantity must be 1..100.");

        return await TwoStepAsync(gate, args, "kol_consume", [new AccessRequest("item.use", role.ToString(), quantity)],
            () => Task.FromResult<object>(new { role = role.ToString(), itemId, kind, quantity }),
            async held =>
            {
                var (consumed, rejection) = await actions.ConsumeAsync(role, itemId, kind, quantity, ct).ConfigureAwait(false);
                held[0].Commit(consumed, $"{kind} item {itemId}");
                return new { preview = false, itemId, kind, requested = quantity, consumed, complete = consumed == quantity, rejection };
            }).ConfigureAwait(false);
    }

    private async Task<object?> RestAsync(AccessGate gate, IAccountActions actions, JsonElement args, CancellationToken ct)
    {
        var role = RoleFor(args, _options.DefaultRole);
        var times = NullableIntProperty(args, "times") ?? 1;
        if (times is < 1 or > 20) throw new McpInvalidParamsException("times must be 1..20.");

        return await TwoStepAsync(gate, args, "kol_rest", [new AccessRequest("item.use", role.ToString(), times), new AccessRequest("adventure.run", role.ToString(), times)],
            () => Task.FromResult<object>(new { role = role.ToString(), times, note = "Resting may cost adventures; both rights are reserved for the worst case." }),
            async held =>
            {
                var (done, spent, problem) = await actions.RestAsync(role, times, ct).ConfigureAwait(false);
                held[0].Commit(done, "rest");
                held[1].Commit(spent, "rest");
                return new { preview = false, requested = times, rested = done, adventuresSpent = spent, complete = done == times && problem is null, problem };
            }).ConfigureAwait(false);
    }

    private async Task<object?> RepriceAsync(AccessGate gate, IAccountActions actions, JsonElement args, CancellationToken ct)
    {
        var role = RoleFor(args, _options.DefaultRole);
        if (!args.TryGetProperty("updates", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is < 1 or > 20)
            throw new McpInvalidParamsException("updates must hold 1..20 entries.");
        var changes = new List<PriceChange>();
        foreach (var u in list.EnumerateArray())
        {
            var id = NullableIntProperty(u, "itemId");
            var expected = (long?)NullableIntProperty(u, "expectedPrice");
            var next = (long?)NullableIntProperty(u, "newPrice");
            if (id is null || expected is null || next is null || next < 100)
                throw new McpInvalidParamsException("Each update needs itemId, expectedPrice and newPrice (at least 100).");
            changes.Add(new PriceChange(id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), expected.Value, next.Value));
        }
        var biggest = changes.Max(c => c.ExpectedPrice <= 0 ? 100 : (int)Math.Min(1_000_000, Math.Abs(c.NewPrice - c.ExpectedPrice) * 100 / c.ExpectedPrice));

        return await TwoStepAsync(gate, args, "kol_reprice_shop",
            [new AccessRequest("shop.reprice", role.ToString(), changes.Count, ChangePercent: biggest)],
            async () => new { role = role.ToString(), biggestChangePercent = biggest,
                storeNow = await actions.RepriceAsync(role, changes, commit: false, ct).ConfigureAwait(false) },
            async held =>
            {
                var lines = await actions.RepriceAsync(role, changes, commit: true, ct).ConfigureAwait(false);
                var updated = lines.Count(l => l.Status == "Updated");
                held[0].Commit(updated, "reprice");
                return new { preview = false, updated, results = lines };
            }).ConfigureAwait(false);
    }

    /// <summary>Arguments without planId in stable key order, so preview and execute hash the same.</summary>
    private static string CanonicalArgs(JsonElement args)
    {
        var items = args.EnumerateObject().Where(p => p.Name != "planId")
            .OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}={p.Value.GetRawText()}");
        return string.Join('&', items);
    }
}
