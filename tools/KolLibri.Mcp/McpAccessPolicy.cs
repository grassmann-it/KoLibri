using System.Text.Json;
using KoLibri.Core.Basis;
using LibKolibri.Access;

namespace KolLibri.Mcp;

/// <summary>Result of the per-call check: allowed or a message for the AI, plus a way to book the outcome.</summary>
public sealed record ToolAuthorization(bool Allowed, string? Message, UsageReservation? Reservation = null, PlanTicket? Plan = null)
{
    public static ToolAuthorization Ok(UsageReservation? reservation = null) => new(true, null, reservation);
    public static ToolAuthorization Deny(string message) => new(false, message);
    /// <summary>Allowed as a preview only: the server runs the tool with execute=false and returns this plan id.</summary>
    public static ToolAuthorization Preview(PlanTicket plan) => new(true, null, null, plan);
}

/// <summary>Decides, for one tool call, whether it may run. Installed by the host; null means unrestricted.</summary>
public delegate ToolAuthorization ToolAuthorizer(string toolName, JsonElement arguments);

/// <summary>
/// Maps MCP tools to operations of the catalog. Tools without a mapping are refused
/// over HTTP, so a newly added tool is closed until someone assigns it a right.
/// </summary>
public static class McpAccessPolicy
{
    private static readonly Dictionary<string, string> ReadTools = new(StringComparer.Ordinal)
    {
        ["kol_status"] = "account.status",
        ["kol_inventory"] = "account.inventory",
        ["kol_login_status"] = "account.status",
        ["kol_price_summary"] = "market.search",
        ["kol_price_recommendation"] = "market.search",
        ["kol_underprice_preview"] = "market.search",
        ["kol_preview_purchase"] = "market.search",
        ["kol_preview_transfer"] = "account.status",
        ["kol_shop_sales"] = "shop.read",
        ["kol_shop_buyers"] = "shop.read",
        ["kol_shop_price_suggestions"] = "shop.read",
        ["kol_workflow_plan"] = "daily.preview",
        ["kol_daily_routine"] = "daily.preview",
        ["kol_daily_campfire_plan"] = "daily.preview",
        ["kol_daily_hermit_plan"] = "daily.preview",
        ["kol_daily_audit"] = "daily.preview",
        ["kol_run_daily_bridge"] = "daily.preview",
        ["kol_plan_daily_adventures"] = "daily.preview",
        ["kol_adventure_locations"] = "account.status",
        ["kol_adventure_bounded_preview"] = "account.status",
        ["kol_adventure_daily_schedule"] = "account.status",
        ["kol_autocombat_preview"] = "account.status",
        ["kol_autocombat_action_preview"] = "account.status",
        ["kol_combat_inspect"] = "account.status",
        ["kol_combat_decision"] = "account.status",
        ["kol_recovery_plan"] = "account.status",
        ["kol_choice_options"] = "account.status",
    };

    /// <summary>Read tools whose handler really works on several roles (<c>roles</c>, "all"). Every other read tool uses one role.</summary>
    private static readonly HashSet<string> MultiRoleTools = new(StringComparer.Ordinal) { "kol_login_status", "kol_daily_audit", "kol_run_daily_bridge" };

    /// <summary>Extra rights a read tool needs on the same role because its answer contains private account data.</summary>
    private static readonly Dictionary<string, string[]> ExtraReadRights = new(StringComparer.Ordinal)
    {
        ["kol_preview_purchase"] = ["account.status", "account.inventory"],
    };

    /// <summary>Tools that read the buyer CRM, which is shared by both accounts: they need the right on every account.</summary>
    private static readonly HashSet<string> AllAccountTools = new(StringComparer.Ordinal) { "kol_shop_buyers" };

    /// <summary>Tools that spend adventures when <c>execute</c> is true; otherwise they only preview.</summary>
    private static readonly Dictionary<string, string[]> AdventureTools = new(StringComparer.Ordinal)
    {
        ["kol_adventure_run_execute"] = ["maxTurns", "maxGatewaySteps"],
        ["kol_choice_chain_execute"] = ["maxChoices"],
    };

    internal static readonly HashSet<string> SelfGated = new(StringComparer.Ordinal) { "kol_mall_buy", "kol_meme_shop", "kol_transfer", "kol_send_message", "kol_use_item", "kol_consume", "kol_rest", "kol_reprice_shop", "kol_reward_buyer" };

    /// <summary>Authorizer for the HTTP host: every call is checked against the token's grant.</summary>
    public static ToolAuthorizer ForToken(AccessGate gate, Func<string?> currentToken, string defaultRole)
        => (tool, args) =>
        {
            var token = currentToken();
            if (string.IsNullOrEmpty(token)) return ToolAuthorization.Deny("Authentication required.");
            try
            {
                return Check(gate, token, tool, args, defaultRole);
            }
            catch (McpInvalidParamsException e)
            {
                return ToolAuthorization.Deny(e.Message);
            }
        };

    private static ToolAuthorization Check(AccessGate gate, string token, string tool, JsonElement args, string defaultRole)
    {
        // These tools run the gate themselves (preview, then execute with a plan id).
        if (SelfGated.Contains(tool)) return ToolAuthorization.Ok();

        var fallback = McpArgs.FallbackRole(defaultRole);

        if (ReadTools.TryGetValue(tool, out var readOp))
        {
            RejectExecute(args);
            // kol_login_status without an explicit role lists whatever the grant covers; the handler filters.
            if (tool == "kol_login_status" && !HasRoleArgument(args))
                return AnyRole(gate, token, readOp);
            IReadOnlyList<AccountRole> roles;
            if (AllAccountTools.Contains(tool)) roles = Enum.GetValues<AccountRole>();
            else if (MultiRoleTools.Contains(tool)) roles = McpArgs.Roles(args, fallback);
            else
            {
                // Single-role handler: authorize exactly the role it will use, and refuse 'roles'.
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("roles", out var many) && many.ValueKind != JsonValueKind.Null)
                    throw new McpInvalidParamsException("This tool takes a single 'role'; 'roles' is not supported.");
                roles = [McpArgs.Role(args, fallback)];
            }
            foreach (var role in roles)
            {
                var d = gate.Authorize(token, new AccessRequest(readOp, role.ToString()));
                if (!d.Allowed) return Convert(d);
                if (ExtraReadRights.TryGetValue(tool, out var extras))
                    foreach (var extra in extras)
                    {
                        var e = gate.Authorize(token, new AccessRequest(extra, role.ToString()));
                        if (!e.Allowed) return Convert(e);
                    }
            }
            return ToolAuthorization.Ok();
        }

        if (AdventureTools.TryGetValue(tool, out var amountArgs))
        {
            var role = McpArgs.Role(args, fallback).ToString();
            if (!McpArgs.Execute(args)) return Convert(gate.Authorize(token, new AccessRequest("account.status", role)));
            var amount = AdventureAmount(args, amountArgs);
            var request = new AccessRequest("adventure.run", role, amount);
            var planId = McpArgs.OptionalString(args, "planId");
            if (planId is null)
            {
                // Step one: check the budget now, hold nothing, hand out a single-use plan; the call only previews.
                var check = gate.Authorize(token, request);
                if (!check.Allowed) return Convert(check);
                check.Reservation?.Release();
                return ToolAuthorization.Preview(gate.IssuePlan(token, tool, McpArgs.Canonical(args)));
            }
            var consumed = gate.ConsumePlan(token, planId, tool, McpArgs.Canonical(args));
            if (!consumed.Allowed) return Convert(consumed);
            return Convert(gate.Authorize(token, request));
        }

        return ToolAuthorization.Deny($"Tool '{tool}' is not available to AI clients.");
    }

    /// <summary>Budget requested by an adventure call: the largest of its explicit caps (steps/choices default 20).</summary>
    private static long AdventureAmount(JsonElement args, string[] amountArgs)
    {
        long amount = 0;
        foreach (var name in amountArgs)
        {
            var v = McpArgs.OptionalInt(args, name);
            if (v is < 1) throw new McpInvalidParamsException($"{name} must be at least 1.");
            if (v is null && name is "maxGatewaySteps" or "maxChoices") v = 20;
            if (v is not null) amount = Math.Max(amount, v.Value);
        }
        return amount == 0 ? 20 : amount;
    }

    private static bool HasRoleArgument(JsonElement args)
        => args.ValueKind == JsonValueKind.Object
           && (args.TryGetProperty("role", out var r) && r.ValueKind != JsonValueKind.Null
               || args.TryGetProperty("roles", out var rs) && rs.ValueKind != JsonValueKind.Null);

    private static ToolAuthorization AnyRole(AccessGate gate, string token, string op)
    {
        ToolAuthorization? last = null;
        foreach (var role in Enum.GetValues<AccountRole>())
        {
            var d = gate.Authorize(token, new AccessRequest(op, role.ToString()));
            if (d.Allowed) return ToolAuthorization.Ok();
            last = Convert(d);
        }
        return last ?? ToolAuthorization.Deny("No account is part of this access.");
    }

    private static void RejectExecute(JsonElement args)
    {
        if (McpArgs.Execute(args))
            throw new McpInvalidParamsException("This tool only previews over MCP; execute=true is not available.");
    }

    /// <summary>Authorizer for stdio: reads only, nothing that changes the account.</summary>
    public static ToolAuthorizer ReadOnly { get; } = (tool, args) =>
    {
        try
        {
            if (ReadTools.ContainsKey(tool)) { RejectExecute(args); return ToolAuthorization.Ok(); }
            if (AdventureTools.ContainsKey(tool) && !McpArgs.Execute(args)) return ToolAuthorization.Ok();
        }
        catch (McpInvalidParamsException e) { return ToolAuthorization.Deny(e.Message); }
        return ToolAuthorization.Deny("Over stdio only read-only tools are available. Use 'kolibri-mcp --http' for approved write access.");
    };

    private static ToolAuthorization Convert(AccessDecision d)
        => d.Allowed ? ToolAuthorization.Ok(d.Reservation) : ToolAuthorization.Deny($"[{d.Code}] {d.Message}");
}

/// <summary>Strictly typed argument parsing shared by the policy and the tool handlers, so both see the same action and roles.</summary>
internal static class McpArgs
{
    public static AccountRole FallbackRole(string name)
        => Enum.TryParse<AccountRole>(name?.Trim(), true, out var r) && Enum.IsDefined(r) ? r : AccountRole.Main;

    /// <summary><c>execute</c>: missing or null is false; anything but a JSON boolean is an error.</summary>
    public static bool Execute(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("execute", out var e)) return false;
        return e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null => false,
            _ => throw new McpInvalidParamsException("execute must be true or false.")
        };
    }

    public static string? OptionalString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String) throw new McpInvalidParamsException($"{name} must be a string.");
        var text = v.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public static long? OptionalInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var n)) throw new McpInvalidParamsException($"{name} must be an integer.");
        return n;
    }

    /// <summary>One concrete role from <c>role</c> (Main or Mallbot).</summary>
    public static AccountRole Role(JsonElement args, AccountRole fallback)
    {
        var text = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("role", out var e) && e.ValueKind != JsonValueKind.Null
            ? (e.ValueKind == JsonValueKind.String ? e.GetString()?.Trim() : throw new McpInvalidParamsException("role must be Main or Mallbot."))
            : null;
        if (string.IsNullOrEmpty(text)) return fallback;
        if (Enum.TryParse<AccountRole>(text, true, out var role) && Enum.IsDefined(role)) return role;
        throw new McpInvalidParamsException("role must be Main or Mallbot.");
    }

    /// <summary>All roles a call touches: <c>role</c> and every entry of <c>roles</c> ("all" = both); fallback when neither is given.</summary>
    public static IReadOnlyList<AccountRole> Roles(JsonElement args, AccountRole fallback)
    {
        var roles = new List<AccountRole>();
        void Add(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new McpInvalidParamsException("role must be Main, Mallbot or all.");
            text = text.Trim();
            if (text.Equals("all", StringComparison.OrdinalIgnoreCase)) { roles.Add(AccountRole.Main); roles.Add(AccountRole.Mallbot); return; }
            if (Enum.TryParse<AccountRole>(text, true, out var r) && Enum.IsDefined(r)) roles.Add(r);
            else throw new McpInvalidParamsException("role must be Main, Mallbot or all.");
        }
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("role", out var one) && one.ValueKind != JsonValueKind.Null)
            {
                if (one.ValueKind != JsonValueKind.String) throw new McpInvalidParamsException("role must be a string.");
                Add(one.GetString());
            }
            if (args.TryGetProperty("roles", out var many) && many.ValueKind != JsonValueKind.Null)
            {
                if (many.ValueKind != JsonValueKind.Array) throw new McpInvalidParamsException("roles must be an array of strings.");
                foreach (var entry in many.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String) throw new McpInvalidParamsException("roles must be an array of strings.");
                    Add(entry.GetString());
                }
            }
        }
        if (roles.Count == 0) roles.Add(fallback);
        return roles.Distinct().ToArray();
    }

    /// <summary>Arguments without planId in stable key order, so preview and execute hash the same.</summary>
    public static string Canonical(JsonElement args)
        => string.Join('&', args.EnumerateObject().Where(p => p.Name != "planId")
            .OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}={p.Value.GetRawText()}"));
}
