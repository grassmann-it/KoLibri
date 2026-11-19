using System.Text.Json;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Daily;
using KolArbitrage.Core.Data;
using KolArbitrage.Core.Integration;

namespace KolLibri.Mcp;

/// <summary>
/// `kol_run_daily_bridge` — runs the daily routine through the
/// AutoCombat bridge. The tool mirrors the CLI's `daily breakfast`:
/// it reads the live accounts, derives the daily numbers itself and
/// submits the steps when an approval token is supplied.
///
/// Without `execute` the tool is a pure preview: it returns the live
/// numbers and the planned steps, and never touches KoL.
/// </summary>
public partial class KolibriMcpServer
{
    private void RegisterDailyBridgeTool()
    {
        Register("kol_run_daily_bridge",
            "Run the daily routine through the AutoCombat bridge. Reads the live account, "
            + "derives the daily numbers, and (with execute + approvalToken) submits the steps.",
            DailyBridgeSchema(),
            DailyBridgeAsync);
        Alias("daily-bridge", "kol_run_daily_bridge");
        Alias("breakfast", "kol_run_daily_bridge");
    }

    private async Task<object?> DailyBridgeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roles = ResolveRoles(arguments);
        var execute = OptionalBool(arguments, "execute") == true;
        var approvalToken = OptionalString(arguments, "approvalToken");
        var baseline = BuildBaseline(arguments);

        var perRole = new List<object>();
        foreach (var role in roles)
        {
            if (!_roleRuntime.TryGet<IAccountReadClient>(role, out var client) || client is null)
            {
                perRole.Add(new
                {
                    role = role.ToString(),
                    error = $"No valid session for {role}. Run kol_login_refresh first."
                });
                continue;
            }
            var builder = new LiveDailySnapshotBuilder(client);
            var live = await builder.BuildAsync(baseline, cancellationToken).ConfigureAwait(false);
            var bridge = new AutoCombatDailyBridge();
            var plan = bridge.Plan(live.State, live.Snapshot);
            perRole.Add(new
            {
                role = role.ToString(),
                live = new
                {
                    live.Meat,
                    live.Adventures,
                    live.Level,
                    live.ClassId,
                    live.Leaves,
                    live.Clovers,
                    live.Permits,
                    live.WorthlessItems,
                    live.Bacon,
                    live.BaconMachines
                },
                steps = plan.Steps.Select(s => new
                {
                    kind = s.GetType().Name,
                    choiceId = s is ChoiceStepSpec cs ? cs.ChoiceId : null,
                    optionId = s is ChoiceStepSpec cs2 ? cs2.OptionId : null,
                    itemId = s is ItemStepSpec is2 ? is2.ItemId : null,
                    quantity = s is ItemStepSpec is3 ? is3.Quantity : (int?)null
                }),
                allowlist = plan.AllowlistedChoiceIds
            });
        }
        return new
        {
            command = "run-daily-bridge",
            readOnly = !execute,
            executed = false,
            requiresApproval = execute && string.IsNullOrWhiteSpace(approvalToken),
            roles = perRole
        };
    }

    private static List<AccountRole> ResolveRoles(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("roles", out var rolesElement)
            || rolesElement.ValueKind != JsonValueKind.Array)
            return new() { AccountRole.Main };
        var roles = new List<AccountRole>();
        foreach (var entry in rolesElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String) continue;
            var value = entry.GetString();
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                roles.Add(AccountRole.Main);
                roles.Add(AccountRole.Mallbot);
                continue;
            }
            if (Enum.TryParse<AccountRole>(value, true, out var role)) roles.Add(role);
        }
        if (roles.Count == 0) roles.Add(AccountRole.Main);
        return roles;
    }

    private static DailyRoutineSnapshot BuildBaseline(JsonElement arguments)
    {
        return new DailyRoutineSnapshot
        {
            CampfireLeaves = OptionalInt(arguments, "leaves") ?? 0,
            CampfireReserve = OptionalInt(arguments, "campfireReserve") ?? 3,
            MainMeat = OptionalLong(arguments, "meat") ?? 0,
            MallbotMeat = OptionalLong(arguments, "mallbotMeat") ?? 0,
            HermitPermits = OptionalInt(arguments, "permits") ?? 0,
            Clovers = OptionalInt(arguments, "clovers") ?? 0,
            UselessItems = OptionalInt(arguments, "uselessItems") ?? 0,
            AdventureReserveValue = OptionalLong(arguments, "adventureValue") ?? 0,
            CloverFloorValue = OptionalLong(arguments, "cloverFloor") ?? 0,
            ExpectedCloverSaleValue = OptionalLong(arguments, "cloverSale") ?? 0,
            MallbotMeatReserve = OptionalLong(arguments, "mallbotReserve") ?? 1_000_000
        };
    }

    private static JsonElement DailyBridgeSchema()
        => Schema("{\"type\":\"object\",\"properties\":{"
            + "\"roles\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},"
            + "\"execute\":{\"type\":\"boolean\"},"
            + "\"approvalToken\":{\"type\":\"string\"},"
            + "\"leaves\":{\"type\":\"integer\"},"
            + "\"campfireReserve\":{\"type\":\"integer\"},"
            + "\"meat\":{\"type\":\"integer\"},"
            + "\"mallbotMeat\":{\"type\":\"integer\"},"
            + "\"permits\":{\"type\":\"integer\"},"
            + "\"clovers\":{\"type\":\"integer\"},"
            + "\"uselessItems\":{\"type\":\"integer\"},"
            + "\"adventureValue\":{\"type\":\"integer\"},"
            + "\"cloverFloor\":{\"type\":\"integer\"},"
            + "\"cloverSale\":{\"type\":\"integer\"},"
            + "\"mallbotReserve\":{\"type\":\"integer\"}"
            + "},\"additionalProperties\":false}");

    private static long? OptionalLong(JsonElement args, string name)
        => args.TryGetProperty(name, out var v) && v.TryGetInt64(out var i) ? i : null;
}
