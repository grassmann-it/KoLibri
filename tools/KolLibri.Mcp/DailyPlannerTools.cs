using System.Text.Json;
using KoLibri.Core.Spiel;

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

        Register("kol_daily_routine",
            "Bundle every daily planner into a single preview-only report (campfire, hermit, internet-shop, bacon, foreign bribes).",
            DailyRoutineSchema(),
            DailyRoutineAsync);
        Alias("daily-routine", "kol_daily_routine");
    }

    private Task<object?> DailyRoutineAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new DailyRoutineSnapshot
        {
            CampfireLeaves = NullableIntProperty(arguments, "leaves") ?? 0,
            CampfireReserve = NullableIntProperty(arguments, "campfireReserve") ?? 3,
            DayShortnerPrice = NullableLongProperty(arguments, "shortnerPrice") ?? long.MaxValue,
            MainMeat = NullableLongProperty(arguments, "mainMeat") ?? 0,
            HermitPermits = NullableIntProperty(arguments, "permits") ?? 0,
            Clovers = NullableIntProperty(arguments, "clovers") ?? 0,
            AdventureReserveValue = NullableLongProperty(arguments, "adventureValue") ?? 0,
            CloverFloorValue = NullableLongProperty(arguments, "cloverFloor") ?? 0,
            ExpectedCloverSaleValue = NullableLongProperty(arguments, "cloverSale") ?? 0,
            WishBuyQuantity = NullableIntProperty(arguments, "wishBuy") ?? 0,
            WishUnitPrice = NullableLongProperty(arguments, "wishPrice") ?? 0,
            AlreadyPulledItemIds = ParseStringArray(arguments, "alreadyPulled") ?? Array.Empty<string>(),
            MallbotMeat = NullableLongProperty(arguments, "mallbotMeat") ?? 0,
            ForeignBribeBagPrice = NullableLongProperty(arguments, "bagPrice") ?? long.MaxValue,
            ForeignBribeBagStock = NullableIntProperty(arguments, "bagStock") ?? 0,
            PilotBagQuantity = NullableIntProperty(arguments, "pilotBags") ?? 40,
            MallbotMeatReserve = NullableLongProperty(arguments, "mallbotReserve") ?? 1_000_000
        };
        var report = new DailyRoutineRunner().Run(snapshot);
        return Task.FromResult<object?>(new
        {
            readOnly = true,
            previewOnly = true,
            generatedAt = report.GeneratedAt,
            verdict = report.Verdict,
            counts = report.Counts,
            approvableSteps = report.ApprovableSteps,
            advisories = report.Advisories,
            campfire = new
            {
                report.Campfire.Verdict,
                report.Campfire.ShouldBurn,
                report.Campfire.ShouldBuyDayShortner,
                report.Campfire.Rationale,
                report.Campfire.Steps
            },
            hermit = new
            {
                report.Hermit.Verdict,
                report.Hermit.ShouldUseClover,
                report.Hermit.ShouldBuyWishes,
                report.Hermit.Rationale,
                report.Hermit.Steps
            },
            internetShop = new
            {
                report.InternetShop.Verdict,
                report.InternetShop.Rationale,
                report.InternetShop.Steps,
                report.InternetShop.Skipped
            },
            foreignBribes = report.ForeignBribes
        });
    }

    private static JsonElement DailyRoutineSchema()
        => Schema("{\"type\":\"object\",\"properties\":{"
            + "\"leaves\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"campfireReserve\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"shortnerPrice\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"mainMeat\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"permits\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"clovers\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"adventureValue\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"cloverFloor\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"cloverSale\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"wishBuy\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"wishPrice\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"alreadyPulled\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},"
            + "\"ownedMachines\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"mallPrice\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"baconMedian\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"mallbotMeat\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"bagPrice\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"bagStock\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"pilotBags\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"mallbotReserve\":{\"type\":\"integer\",\"minimum\":0}"
            + "},\"additionalProperties\":false}");

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
