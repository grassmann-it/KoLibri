namespace KolArbitrage.Core.Daily;

/// <summary>
/// Combined snapshot used by <see cref="DailyRoutineRunner"/>. Every value
/// is supplied by the caller; the routine never fetches KoL itself in
/// preview mode.
/// </summary>
public sealed record DailyRoutineSnapshot
{
    // Campfire
    public int CampfireLeaves { get; init; }
    public int CampfireReserve { get; init; } = 3;
    public long DayShortnerPrice { get; init; } = long.MaxValue;
    public long MainMeat { get; init; }

    // Hermit
    public int HermitPermits { get; init; }
    public int Clovers { get; init; }
    public long AdventureReserveValue { get; init; }
    public long CloverFloorValue { get; init; }
    public long ExpectedCloverSaleValue { get; init; }
    public int WishBuyQuantity { get; init; }
    public long WishUnitPrice { get; init; }

    // Internet shop
    public IReadOnlyList<string> AlreadyPulledItemIds { get; init; } = Array.Empty<string>();

    // Bacons
    public int OwnedMachines { get; init; }
    public long MallPricePerMachine { get; init; }
    public long PricegunMedianBaconPrice { get; init; }
    public long MallbotMeat { get; init; }

    // Foreign bribes
    public long ForeignBribeBagPrice { get; init; } = long.MaxValue;
    public int ForeignBribeBagStock { get; init; }
    public int PilotBagQuantity { get; init; } = 40;
    public long MallbotMeatReserve { get; init; } = 1_000_000;
}

/// <summary>
/// Output of <see cref="DailyRoutineRunner"/>. The report groups one
/// sub-plan per daily surface and aggregates a top-level verdict the
/// operator can scan in the morning.
/// </summary>
public sealed record DailyRoutineReport
{
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string Verdict { get; init; }
    public required DailyRoutineCounts Counts { get; init; }
    public required CampfireLeavesPlan Campfire { get; init; }
    public required HermitPlan Hermit { get; init; }
    public required InternetShopPlan InternetShop { get; init; }
    public required BaconPlan Bacon { get; init; }
    public required ForeignBribePreview ForeignBribes { get; init; }
    public required IReadOnlyList<string> ApprovableSteps { get; init; }
    public required IReadOnlyList<string> Advisories { get; init; }
}

public sealed record DailyRoutineCounts(int ApprovableSteps, int SkippedSteps);

/// <summary>
/// Lightweight bag-of-bribes preview that surfaces the existing
/// <see cref="ForeignBribePlanner"/> inputs without taking a hard
/// dependency on the integration layer from this assembly.
/// </summary>
public sealed record ForeignBribePreview
{
    public required string Verdict { get; init; }
    public required int PlannedQuantity { get; init; }
    public required long PlannedSpend { get; init; }
    public required string Rationale { get; init; }
}

/// <summary>
/// Read-only orchestrator that runs every daily planner against a
/// <see cref="DailyRoutineSnapshot"/> and returns a single
/// <see cref="DailyRoutineReport"/>. ApprovableSteps is a flat list of
/// human-readable strings, one per prepared action, suitable for a
/// preview-only report or the morning checklist.
/// </summary>
public sealed class DailyRoutineRunner
{
    public DailyRoutineRunnerOptions Options { get; }

    public DailyRoutineRunner(DailyRoutineRunnerOptions? options = null)
        => Options = options ?? new DailyRoutineRunnerOptions();

    public DailyRoutineReport Run(DailyRoutineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var advisories = new List<string>();
        var steps = new List<string>();

        var campfire = new CampfireLeavesPlanner().Plan(new CampfireLeavesSnapshot
        {
            CampfireLeaves = snapshot.CampfireLeaves,
            MinimumReserve = snapshot.CampfireReserve,
            DayShortnerPrice = snapshot.DayShortnerPrice,
            AvailableMeat = snapshot.MainMeat,
            IsUsefulDay = true
        });
        AddSteps(campfire.Steps, steps);

        var hermit = new HermitPlanner().Plan(new HermitSnapshot
        {
            Permits = snapshot.HermitPermits,
            Clovers = snapshot.Clovers,
            WishesAvailable = 0,
            ReserveAdventureValue = snapshot.AdventureReserveValue,
            MinimumCloverSaleValue = snapshot.CloverFloorValue,
            ExpectedCloverSaleValue = snapshot.ExpectedCloverSaleValue,
            WishBuyQuantity = snapshot.WishBuyQuantity,
            WishUnitPrice = snapshot.WishUnitPrice
        });
        AddSteps(hermit.Steps, steps);

        var internet = new DailyInternetShopPlanner().Plan(new InternetShopSnapshot
        {
            AlreadyPulledItemIds = snapshot.AlreadyPulledItemIds,
            AvailableMeat = snapshot.MainMeat
        });
        AddSteps(internet.Steps, steps);

        var bacon = new BaconArbitragePlanner().Plan(new BaconSnapshot
        {
            OwnedMachines = snapshot.OwnedMachines,
            MallPricePerMachine = snapshot.MallPricePerMachine,
            PricegunMedianSellPrice = snapshot.PricegunMedianBaconPrice,
            AvailableMeat = snapshot.MallbotMeat
        });
        if (bacon.ShouldBuy)
            steps.Add($"Buy {bacon.SuggestedQuantity} Infinite Bacon Machine(s) at {snapshot.MallPricePerMachine} meat each (median {snapshot.PricegunMedianBaconPrice}).");

        var bribes = PlanBribes(snapshot);
        if (bribes.PlannedQuantity > 0)
            steps.Add($"Mallbot buys {bribes.PlannedQuantity} bag(s) of foreign bribes for {bribes.PlannedSpend} meat (open on Main).");

        if (steps.Count == 0)
            advisories.Add("Nothing to do today; revisit the snapshot and verify account state.");

        var verdict = steps.Count switch
        {
            0 => "NoOp",
            <= 2 => "LightRoutine",
            <= 5 => "StandardRoutine",
            _ => "HeavyRoutine"
        };

        return new DailyRoutineReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Verdict = verdict,
            Counts = new DailyRoutineCounts(steps.Count, advisories.Count),
            Campfire = campfire,
            Hermit = hermit,
            InternetShop = internet,
            Bacon = bacon,
            ForeignBribes = bribes,
            ApprovableSteps = steps,
            Advisories = advisories
        };
    }

    private static void AddSteps(IEnumerable<object> steps, List<string> sink)
    {
        foreach (var step in steps)
        {
            var description = step.GetType().GetProperty("Description")?.GetValue(step) as string;
            var kind = step.GetType().GetProperty("Kind")?.GetValue(step) as string;
            if (!string.IsNullOrWhiteSpace(description))
                sink.Add($"{kind ?? "step"}: {description}");
        }
    }

    private static ForeignBribePreview PlanBribes(DailyRoutineSnapshot snapshot)
    {
        if (snapshot.ForeignBribeBagPrice <= 0
            || snapshot.ForeignBribeBagPrice == long.MaxValue
            || snapshot.ForeignBribeBagStock <= 0)
        {
            return new ForeignBribePreview
            {
                Verdict = "UnknownPrice",
                PlannedQuantity = 0,
                PlannedSpend = 0,
                Rationale = "No bag-of-foreign-bribes price available; skipping."
            };
        }

        var affordable = Math.Max(0L, snapshot.MallbotMeat - snapshot.MallbotMeatReserve);
        var maxByBudget = (int)Math.Min(snapshot.PilotBagQuantity, affordable / snapshot.ForeignBribeBagPrice);
        var quantity = Math.Min(snapshot.ForeignBribeBagStock, maxByBudget);
        if (quantity <= 0)
        {
            return new ForeignBribePreview
            {
                Verdict = "OverBudget",
                PlannedQuantity = 0,
                PlannedSpend = 0,
                Rationale = $"Mallbot meat {snapshot.MallbotMeat} leaves the {snapshot.MallbotMeatReserve} reserve intact; skipping."
            };
        }
        return new ForeignBribePreview
        {
            Verdict = "Buy",
            PlannedQuantity = quantity,
            PlannedSpend = quantity * snapshot.ForeignBribeBagPrice,
            Rationale = $"Mallbot buys {quantity} bag(s) at {snapshot.ForeignBribeBagPrice} meat."
        };
    }
}

public sealed record DailyRoutineRunnerOptions
{
    public DateTimeOffset? NowOverride { get; init; }
}
