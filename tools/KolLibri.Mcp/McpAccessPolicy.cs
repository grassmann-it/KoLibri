using System.Text.Json;
using LibKolibri.Access;

namespace KolLibri.Mcp;

/// <summary>Result of the per-call check: allowed or a message for the AI, plus a way to book the outcome.</summary>
public sealed record ToolAuthorization(bool Allowed, string? Message, UsageReservation? Reservation = null)
{
    public static ToolAuthorization Ok(UsageReservation? reservation = null) => new(true, null, reservation);
    public static ToolAuthorization Deny(string message) => new(false, message);
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

    /// <summary>Tools that spend adventures when <c>execute</c> is true; otherwise they only preview.</summary>
    private static readonly Dictionary<string, string[]> AdventureTools = new(StringComparer.Ordinal)
    {
        ["kol_adventure_run_execute"] = ["maxTurns", "maxGatewaySteps"],
        ["kol_choice_chain_execute"] = ["maxChoices"],
        ["kol_plan_daily_adventures_execute"] = ["maxTurns", "maxSteps"],
    };

    internal static readonly HashSet<string> SelfGated = new(StringComparer.Ordinal) { "kol_mall_buy", "kol_meme_shop", "kol_transfer", "kol_send_message", "kol_use_item", "kol_reprice_shop" };

    /// <summary>Authorizer for the HTTP host: every call is checked against the token's grant.</summary>
    public static ToolAuthorizer ForToken(AccessGate gate, Func<string?> currentToken, string defaultRole)
        => (tool, args) =>
        {
            var token = currentToken();
            if (string.IsNullOrEmpty(token)) return ToolAuthorization.Deny("Authentication required.");
            var role = RoleOf(args, defaultRole);

            // These tools run the gate themselves (preview, then execute with a plan id).
            if (SelfGated.Contains(tool)) return ToolAuthorization.Ok();

            if (ReadTools.TryGetValue(tool, out var readOp))
                return Convert(gate.Authorize(token, new AccessRequest(readOp, role)));

            if (AdventureTools.TryGetValue(tool, out var amountArgs))
            {
                var execute = args.TryGetProperty("execute", out var e) && e.ValueKind == JsonValueKind.True;
                if (!execute) return Convert(gate.Authorize(token, new AccessRequest("account.status", role)));
                var amount = amountArgs.Select(n => IntArg(args, n)).FirstOrDefault(v => v is not null) ?? 20;
                return Convert(gate.Authorize(token, new AccessRequest("adventure.run", role, amount)));
            }

            return ToolAuthorization.Deny($"Tool '{tool}' is not available to AI clients.");
        };

    /// <summary>Authorizer for stdio: reads only, nothing that changes the account.</summary>
    public static ToolAuthorizer ReadOnly { get; } = (tool, args) =>
    {
        if (ReadTools.ContainsKey(tool)) return ToolAuthorization.Ok();
        if (AdventureTools.ContainsKey(tool)
            && !(args.TryGetProperty("execute", out var e) && e.ValueKind == JsonValueKind.True))
            return ToolAuthorization.Ok();
        return ToolAuthorization.Deny("Over stdio only read-only tools are available. Use 'kolibri-mcp --http' for approved write access.");
    };

    private static ToolAuthorization Convert(AccessDecision d)
        => d.Allowed ? ToolAuthorization.Ok(d.Reservation) : ToolAuthorization.Deny($"[{d.Code}] {d.Message}");

    private static string RoleOf(JsonElement args, string fallback)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()!.Trim()
            : fallback;

    private static long? IntArg(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : null;
}
