using System.Text.Json;
using KoLibri.Daily;

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
            + "derives the daily numbers, and with execute=true submits the steps: the first call previews and returns a planId, the second call with the same arguments plus planId runs once (approvalToken only for an ungated host).",
            DailyBridgeSchema(),
            DailyBridgeAsync);
        Alias("daily-bridge", "kol_run_daily_bridge");
        Alias("breakfast", "kol_run_daily_bridge");
    }

    private async Task<object?> DailyBridgeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roles = McpArgs.Roles(arguments, _options.DefaultRole);
        if (McpArgs.Execute(arguments))
            throw new McpInvalidParamsException("kol_run_daily_bridge only previews over MCP; execute=true is not available.");
        var execute = false;
        var approvalToken = OptionalString(arguments, "approvalToken");
        var options = BuildOptions(arguments);
        var perRole = new List<object>();
        foreach (var role in roles)
        {
            if (!await _accounts.HasSessionAsync(role, cancellationToken).ConfigureAwait(false))
            {
                perRole.Add(new { role = role.ToString(), error = $"No valid session for {role}. Run kol_login_refresh first." });
                continue;
            }
            // The live path of `kolibri daily run`: status, inventory and today's markers (two reads, nothing sent).
            await using var session = await _accounts.OpenAsync(role, cancellationToken).ConfigureAwait(false);
            var live = await DailyRun.ReadAsync(session, cancellationToken).ConfigureAwait(false);
            var status = await session.Character.GetStatusAsync(false, cancellationToken).ConfigureAwait(false);
            var steps = DailyRun.Plan(live, options);
            perRole.Add(new
            {
                role = role.ToString(),
                live = new
                {
                    live.Meat,
                    status.Adventures,
                    status.Level,
                    ClassId = (string?)null,
                    live.Leaves,
                    live.Clovers,
                    live.Permits,
                    live.WorthlessItems,
                    live.Bacon,
                    live.BaconMachines
                },
                steps = steps.Select(s => new
                {
                    kind = s.Kind == "choice" ? "ChoiceStepSpec" : "ItemStepSpec",
                    choiceId = s.Kind == "choice" ? s.Id : null,
                    optionId = s.Kind == "choice" ? s.Option : null,
                    itemId = s.Kind == "item" ? s.Id : null,
                    quantity = s.Kind == "item" && int.TryParse(s.Option, out var q) ? q : (int?)null
                }),
                allowlist = steps.Where(s => s.Kind == "choice").Select(s => s.Id + ":" + s.Option).ToArray()
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

    /// <summary>The 0.4 arguments that are not live numbers (leaves, meat, permits … always come from the account).</summary>
    internal static DailyRunOptions BuildOptions(JsonElement arguments) => new()
    {
        CampfireReserve = OptionalInt(arguments, "campfireReserve") ?? 3,
        HermitCloversWanted = OptionalInt(arguments, "hermitClovers") ?? 0,
        AdventureReserveValue = OptionalLong(arguments, "adventureValue") ?? 0,
        CloverFloorValue = OptionalLong(arguments, "cloverFloor") ?? 0,
        ExpectedCloverSaleValue = OptionalLong(arguments, "cloverSale") ?? 0
    };

    private static JsonElement DailyBridgeSchema()
        => Schema("{\"type\":\"object\",\"properties\":{"
            + "\"role\":{\"enum\":[\"Main\",\"Mallbot\",\"all\"]},"
            + "\"roles\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},"
            + "\"execute\":{\"type\":\"boolean\"},"
            + "\"approvalToken\":{\"type\":\"string\"},"
            + "\"leaves\":{\"type\":\"integer\"},"
            + "\"hermitClovers\":{\"type\":\"integer\"},"
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
