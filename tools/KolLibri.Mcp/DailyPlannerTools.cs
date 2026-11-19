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
        var snapshot = new HermitSnapshot
        {
            Permits = NullableIntProperty(arguments, "permits") ?? 0,
            Clovers = NullableIntProperty(arguments, "clovers") ?? 0,
            WishesAvailable = NullableIntProperty(arguments, "wishes") ?? 0,
            ReserveAdventureValue = NullableLongProperty(arguments, "adventureValue") ?? 0,
            MinimumCloverSaleValue = NullableLongProperty(arguments, "cloverFloor") ?? 0,
            ExpectedCloverSaleValue = NullableLongProperty(arguments, "cloverSale") ?? 0,
            WishBuyQuantity = NullableIntProperty(arguments, "wishBuy") ?? 0,
            WishUnitPrice = NullableLongProperty(arguments, "wishPrice") ?? 0
        };
        var plan = new HermitPlanner().Plan(snapshot);
        return Task.FromResult<object?>(new
        {
            readOnly = true,
            previewOnly = true,
            verdict = plan.Verdict,
            shouldUseClover = plan.ShouldUseClover,
            shouldBuyWishes = plan.ShouldBuyWishes,
            rationale = plan.Rationale,
            steps = plan.Steps,
            snapshot = plan.Snapshot
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
            + "\"wishes\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"adventureValue\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"cloverFloor\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"cloverSale\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"wishBuy\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"wishPrice\":{\"type\":\"integer\",\"minimum\":0}"
            + "},\"additionalProperties\":false}");
}
