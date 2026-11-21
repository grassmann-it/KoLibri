using System.Text.Json;
using KolArbitrage.Core.Basis;
using LibKolibri;
using LibKolibri.Access;

namespace KolLibri.Mcp;

/// <summary>What the write tools need from a signed-in account. Real: <see cref="KolibriClientActions"/>.</summary>
public interface IAccountActions
{
    Task<long> MeatAsync(AccountRole role, CancellationToken ct);
    /// <summary>Buys under a price ceiling and reserve; returns items bought and meat spent as confirmed by KoL.</summary>
    Task<(int Quantity, long Spent, string Verdict)> BuyAsync(
        AccountRole role, int itemId, string itemName, int quantity, long maxPrice, long reserve, CancellationToken ct);
    Task<(int StepsAccepted, int BaconBought, string Verdict, IReadOnlyList<string> Notes)> MemeShopAsync(
        AccountRole role, bool topUp, long maxBaconPrice, long reserve, CancellationToken ct);
}

/// <summary>Runs the actions through <see cref="KolibriClient"/> with the stored sessions.</summary>
public sealed class KolibriClientActions : IAccountActions
{
    public async Task<long> MeatAsync(AccountRole role, CancellationToken ct)
    {
        using var client = await KolibriClient.OpenAsync(role, cancellationToken: ct).ConfigureAwait(false);
        return (await client.GetStatusAsync(true, ct).ConfigureAwait(false)).Meat;
    }

    public async Task<(int, long, string)> BuyAsync(AccountRole role, int itemId, string itemName, int quantity, long maxPrice, long reserve, CancellationToken ct)
    {
        using var client = await KolibriClient.OpenAsync(role, cancellationToken: ct).ConfigureAwait(false);
        var o = await client.BuyCheapAsync(itemId, itemName, quantity, maxPrice, reserve, ct).ConfigureAwait(false);
        return (o.Quantity, o.Spent, o.Verdict);
    }

    public async Task<(int, int, string, IReadOnlyList<string>)> MemeShopAsync(AccountRole role, bool topUp, long maxBaconPrice, long reserve, CancellationToken ct)
    {
        using var client = await KolibriClient.OpenAsync(role, cancellationToken: ct).ConfigureAwait(false);
        var r = await client.RunMemeShopAsync(topUp, maxBaconPrice, reserve, ct).ConfigureAwait(false);
        return (r.Steps.Count(s => s.Accepted), r.BaconTopUp?.Quantity ?? 0, r.Verdict,
            r.Steps.Select(s => $"{s.Description}: {(s.Accepted ? "ok" : "not done")} - {s.Message}").ToArray());
    }
}

public partial class KolibriMcpServer
{
    private const int MaxBuyQuantity = 1_000;

    /// <summary>
    /// Registers the tools that spend meat. Each is two-step: a call without <c>planId</c> only
    /// previews and returns a plan id; the call with that id (same arguments) executes, once.
    /// Rights, limits and budgets come from the caller's grant; without a token nothing runs.
    /// </summary>
    public void EnableWriteTools(AccessGate gate, IAccountActions actions)
    {
        Register("kol_mall_buy",
            "Buy an item from the mall under a price ceiling. Step 1: call without planId to preview worst-case cost and get a planId. "
            + "Step 2: call again with the same arguments plus planId to buy. Needs right 'mall.buy'.",
            Schema("""
            {"type":"object","additionalProperties":false,"required":["itemId","itemName","quantity","maxPricePerUnit"],"properties":{
              "role":{"type":"string","enum":["Main","Mallbot"]},
              "itemId":{"type":"integer","minimum":1},"itemName":{"type":"string"},
              "quantity":{"type":"integer","minimum":1,"maximum":1000},
              "maxPricePerUnit":{"type":"integer","minimum":1},"planId":{"type":"string"}}}
            """),
            (args, ct) => MallBuyAsync(gate, actions, args, ct));

        Register("kol_meme_shop",
            "Run today's Internet Meme Shop (BACON machine and daily items), optionally topping up BACON in the mall. "
            + "Two-step like kol_mall_buy. Needs right 'daily.run'.",
            Schema("""
            {"type":"object","additionalProperties":false,"properties":{
              "role":{"type":"string","enum":["Main","Mallbot"]},
              "topUpBacon":{"type":"boolean"},"maxBaconPrice":{"type":"integer","minimum":1},"planId":{"type":"string"}}}
            """),
            (args, ct) => MemeShopAsync(gate, actions, args, ct));
    }

    private async Task<object?> MallBuyAsync(AccessGate gate, IAccountActions actions, JsonElement args, CancellationToken ct)
    {
        var token = McpRequestContext.Token ?? throw new McpInvalidParamsException("Authentication required.");
        var role = RoleFor(args, _options.DefaultRole);
        var itemId = NullableIntProperty(args, "itemId") ?? throw new McpInvalidParamsException("itemId is required.");
        var itemName = OptionalString(args, "itemName") ?? throw new McpInvalidParamsException("itemName is required.");
        var quantity = NullableIntProperty(args, "quantity") ?? 0;
        var maxPrice = (long?)NullableIntProperty(args, "maxPricePerUnit") ?? 0;
        if (quantity is < 1 or > MaxBuyQuantity || maxPrice < 1)
            throw new McpInvalidParamsException($"quantity must be 1..{MaxBuyQuantity} and maxPricePerUnit at least 1.");
        var worstCase = checked(quantity * maxPrice);
        var meat = await actions.MeatAsync(role, ct).ConfigureAwait(false);
        var request = new AccessRequest("mall.buy", role.ToString(), worstCase, maxPrice, meat - worstCase);

        var planId = OptionalString(args, "planId");
        if (planId is null)
        {
            var check = gate.Authorize(token, request);
            check.Reservation?.Release();            // a preview holds nothing
            if (!check.Allowed) throw new McpInvalidParamsException($"[{check.Code}] {check.Message}");
            var plan = gate.IssuePlan(token, "mall.buy", CanonicalArgs(args));
            return new { preview = true, itemId, itemName, quantity, maxPricePerUnit = maxPrice, worstCaseMeat = worstCase,
                meatNow = meat, planId = plan.PlanId, expiresAt = plan.ExpiresAt,
                next = "Call kol_mall_buy again with the same arguments and this planId to buy." };
        }

        var consumed = gate.ConsumePlan(token, planId, "mall.buy", CanonicalArgs(args));
        if (!consumed.Allowed) throw new McpInvalidParamsException($"[{consumed.Code}] {consumed.Message}");
        var decision = gate.Authorize(token, request);
        if (!decision.Allowed) throw new McpInvalidParamsException($"[{decision.Code}] {decision.Message}");
        var reservation = decision.Reservation!;
        try
        {
            var reserve = gate.ActiveGrant(token)?.Rights.GetValueOrDefault(Rights.MallBuy)?.MeatReserve ?? 0;
            var (bought, spent, verdict) = await actions.BuyAsync(role, itemId, itemName, quantity, maxPrice, reserve, ct).ConfigureAwait(false);
            reservation.Commit(spent, $"{itemName} x{bought}");
            return new { preview = false, itemId, itemName, requested = quantity, bought, spentMeat = spent, verdict };
        }
        catch
        {
            reservation.Release();
            throw;
        }
    }

    private async Task<object?> MemeShopAsync(AccessGate gate, IAccountActions actions, JsonElement args, CancellationToken ct)
    {
        var token = McpRequestContext.Token ?? throw new McpInvalidParamsException("Authentication required.");
        var role = RoleFor(args, _options.DefaultRole);
        var topUp = BoolProperty(args, "topUpBacon");
        var maxBacon = (long?)NullableIntProperty(args, "maxBaconPrice") ?? 1_500;
        var request = new AccessRequest("daily.run", role.ToString(), 1, topUp ? maxBacon : null);

        var planId = OptionalString(args, "planId");
        if (planId is null)
        {
            var check = gate.Authorize(token, request);
            check.Reservation?.Release();
            if (!check.Allowed) throw new McpInvalidParamsException($"[{check.Code}] {check.Message}");
            var plan = gate.IssuePlan(token, "daily.run", CanonicalArgs(args));
            return new { preview = true, role = role.ToString(), topUpBacon = topUp, maxBaconPrice = topUp ? maxBacon : (long?)null,
                planId = plan.PlanId, expiresAt = plan.ExpiresAt,
                next = "Call kol_meme_shop again with the same arguments and this planId to run it." };
        }

        var consumed = gate.ConsumePlan(token, planId, "daily.run", CanonicalArgs(args));
        if (!consumed.Allowed) throw new McpInvalidParamsException($"[{consumed.Code}] {consumed.Message}");
        var decision = gate.Authorize(token, request);
        if (!decision.Allowed) throw new McpInvalidParamsException($"[{decision.Code}] {decision.Message}");
        var reservation = decision.Reservation!;
        try
        {
            var reserve = gate.ActiveGrant(token)?.Rights.GetValueOrDefault(Rights.DailyRun)?.MeatReserve ?? 200_000;
            var r = await actions.MemeShopAsync(role, topUp, maxBacon, reserve, ct).ConfigureAwait(false);
            // A run counts once something actually happened on KoL's side.
            reservation.Commit(r.StepsAccepted > 0 || r.BaconBought > 0 ? 1 : 0, r.Verdict);
            return new { preview = false, role = role.ToString(), verdict = r.Verdict, stepsAccepted = r.StepsAccepted,
                baconBought = r.BaconBought, notes = r.Notes };
        }
        catch
        {
            reservation.Release();
            throw;
        }
    }

    /// <summary>Arguments without planId in stable key order, so preview and execute hash the same.</summary>
    private static string CanonicalArgs(JsonElement args)
    {
        var items = args.EnumerateObject().Where(p => p.Name != "planId")
            .OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}={p.Value.GetRawText()}");
        return string.Join('&', items);
    }
}
