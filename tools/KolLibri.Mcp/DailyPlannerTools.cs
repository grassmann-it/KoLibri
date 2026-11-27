using System.Text.Json;
using KoLibri.Daily;

namespace KolLibri.Mcp;

/// <summary>
/// Experimental daily planners. In 0.5 they run on the live path of `kolibri daily run` (KoLibri.Daily.DailyRun): the
/// account's status, inventory and today's once-per-day markers decide the plan, not numbers passed in by the caller.
/// Read only: they never send a choice, hermit or shop request.
/// </summary>
public partial class KolibriMcpServer
{
    private void RegisterDailyPlannerTools()
    {
        Register("kol_daily_campfire_plan",
            "Plan the Campfire Leaves / Day Shortener steps from the live account and today's markers; never sends a choice POST.",
            DailyPlanSchema(),
            (a, ct) => DailyPlanAsync(a, "campfire", ct));
        Register("kol_daily_hermit_plan",
            "Plan the Hermit permit / 11-leaf clover steps from the live account and today's markers; never sends a request.",
            DailyPlanSchema(),
            (a, ct) => DailyPlanAsync(a, "hermit", ct));
        Alias("daily-campfire", "kol_daily_campfire_plan");
        Alias("daily_hermit", "kol_daily_hermit_plan");
        Alias("hermit-plan", "kol_daily_hermit_plan");
        Register("kol_daily_routine",
            "Plan the whole daily routine (campfire, hermit, Internet Meme Shop) from the live account and today's markers; preview only.",
            DailyPlanSchema(),
            (a, ct) => DailyPlanAsync(a, "routine", ct));
        Alias("daily-routine", "kol_daily_routine");
    }

    private async Task<object?> DailyPlanAsync(JsonElement arguments, string surface, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var role = RoleFor(arguments, _options.DefaultRole);
        var options = DailyBridgeOptions(arguments);
        await using var session = await _accounts.OpenAsync(role, cancellationToken).ConfigureAwait(false);
        var live = await DailyRun.ReadAsync(session, cancellationToken).ConfigureAwait(false);
        var all = DailyRun.Plan(live, options);
        var steps = all.Where(s => surface switch
        {
            "campfire" => s.Id is "choice-campfire" or "choice-day-shortner",
            "hermit" => s.Id is "choice-clover" or "worthless-item" or "choice-hermit-wish",
            _ => true
        }).ToArray();
        var done = surface switch
        {
            "campfire" => live.CampfireLeafDoneToday && live.DayShortenerDoneToday,
            "hermit" => live.HermitDoneToday,
            _ => false
        };
        return new
        {
            readOnly = true,
            previewOnly = true,
            role = role.ToString(),
            surface,
            generatedAt = DateTimeOffset.UtcNow,
            verdict = steps.Length > 0 ? "Act" : done ? "AlreadyDoneToday" : "NothingToDo",
            live = new
            {
                live.Leaves, live.Clovers, live.Permits, live.WorthlessItems, live.Bacon, live.BaconMachines, live.Meat,
                live.CampfireLeafDoneToday, live.DayShortenerDoneToday, live.HermitDoneToday, live.BaconMachineUsedToday,
                live.MemeItemsDoneToday
            },
            steps = steps.Select(s => new { kind = s.Kind, id = s.Id, option = s.Option }).ToArray(),
            next = steps.Length > 0 ? "Run the steps with `kolibri daily run --execute` (CLI); this tool only plans." : null
        };
    }

    private static DailyRunOptions DailyBridgeOptions(JsonElement arguments)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("skip", out var s) && s.ValueKind == JsonValueKind.Array)
            foreach (var item in s.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value) skip.Add(value.Trim());
        return BuildOptions(arguments) with { Skip = skip };
    }

    private static JsonElement DailyPlanSchema()
        => Schema("{\"type\":\"object\",\"properties\":{"
            + "\"role\":{\"enum\":[\"Main\",\"Mallbot\"]},"
            + "\"hermitClovers\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"campfireReserve\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"adventureValue\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"cloverFloor\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"cloverSale\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"skip\":{\"type\":\"array\",\"items\":{\"enum\":[\"campfire\",\"hermit\",\"internet-shop\"]}}"
            + "},\"additionalProperties\":false}");
}
