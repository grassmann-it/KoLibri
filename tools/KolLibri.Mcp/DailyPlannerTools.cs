using System.Text.Json;
using KolArbitrage.Core.Daily;

namespace KolLibri.Mcp;

/// <summary>
/// Read-only daily planner tools. The bounded runner only submits live POSTs
/// when an explicit choice id/option pair is supplied; these helpers expose
/// the deterministic plan in JSON form so MCP hosts can render it without
/// touching the transport.
/// </summary>
public partial class KolibriMcpServer
{
    private void RegisterDailyPlannerTools()
    {
        Register("kol_daily_campfire_plan",
            "Build a Campfire Leaves / Day Shortner daily plan from read-only numbers; never sends a choice POST.",
            DailyCampfireSchema(),
            DailyCampfireAsync);
        Register("kol_daily_hermit_plan",
            "Build a Hermit permit / 11-Leaf Clover daily plan with reserve logic; never sends a choice POST.",
            DailyHermitSchema(),
            DailyHermitAsync);

        Alias("daily-campfire", "kol_daily_campfire_plan");
        Alias("daily_hermit", "kol_daily_hermit_plan");
        Alias("hermit-plan", "kol_daily_hermit_plan");
    }

    private Task<object?> DailyCampfireAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new CampfireLeavesSnapshot
        {
            CampfireLeaves = NullableIntProperty(arguments, "leaves") ?? 0,
            MinimumReserve = NullableIntProperty(arguments, "reserve") ?? 3,
            DayShortnerPrice = NullableLongProperty(arguments, "shortnerPrice") ?? long.MaxValue,
            AvailableMeat = NullableLongProperty(arguments, "meat") ?? 0,
            IsUsefulDay = !BoolProperty(arguments, "notUseful"),
            Knucklebones = NullableIntProperty(arguments, "knucklebones") ?? 0
        };
        var planner = new CampfireLeavesPlanner();
        var plan = planner.Plan(snapshot);
        return Task.FromResult<object?>(new
        {
            readOnly = true,
            previewOnly = true,
            shouldBurn = plan.ShouldBurn,
            verdict = plan.Verdict,
            shouldBuyDayShortner = plan.ShouldBuyDayShortner,
            rationale = plan.Rationale,
            steps = plan.Steps,
            snapshot = plan.Snapshot
        });
    }

    private Task<object?> DailyHermitAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var permits = NullableIntProperty(arguments, "permits") ?? 0;
        var clovers = NullableIntProperty(arguments, "clovers") ?? 0;
        var advValue = NullableIntProperty(arguments, "adventureValue") ?? 0;
        var floor = NullableIntProperty(arguments, "cloverFloor") ?? 0;
        var reserve = NullableIntProperty(arguments, "reserve") ?? 0;

        string verdict;
        if (permits == 0) verdict = "NoHermitPermits";
        else if (clovers == 0) verdict = "NoClovers";
        else if (advValue <= reserve) verdict = "KeepCloverReserve";
        else if (floor <= 0 || advValue > floor) verdict = "UseClover";
        else verdict = "KeepCloverReserve";

        return Task.FromResult<object?>(new
        {
            readOnly = true,
            previewOnly = true,
            verdict,
            rationale = $"permits={permits}, clovers={clovers}, adventureValue={advValue}, cloverFloor={floor}, reserve={reserve}.",
            permits,
            clovers
        });
    }

    private static JsonElement DailyCampfireSchema()
        => Schema("{\"type\":\"object\",\"properties\":{"
            + "\"leaves\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"reserve\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"shortnerPrice\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"meat\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"knucklebones\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"notUseful\":{\"type\":\"boolean\"}"
            + "},\"additionalProperties\":false}");

    private static JsonElement DailyHermitSchema()
        => Schema("{\"type\":\"object\",\"properties\":{"
            + "\"permits\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"clovers\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"adventureValue\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"cloverFloor\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"reserve\":{\"type\":\"integer\",\"minimum\":0}"
            + "},\"additionalProperties\":false}");
}
