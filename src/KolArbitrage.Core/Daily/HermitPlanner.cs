using KolArbitrage.Core.Adventure;

namespace KolArbitrage.Core.Daily;

/// <summary>
/// Read-only inputs for the Hermit permit / 11-Leaf Clover planner.
/// Reserves are measured in expected meat or adventure yield, not in items.
/// </summary>
public sealed record HermitSnapshot
{
    public int Permits { get; init; }
    public int Clovers { get; init; }
    public int WishesAvailable { get; init; }

    /// <summary>Adventure value the player would gain if they kept the clover today.</summary>
    public long ReserveAdventureValue { get; init; }

    /// <summary>Floor meat value the clover must beat to be worth opening today.</summary>
    public long MinimumCloverSaleValue { get; init; }

    /// <summary>What the clover will probably fetch on the mall right now.</summary>
    public long ExpectedCloverSaleValue { get; init; }

    /// <summary>Quantity of wishes the planner should consider buying.</summary>
    public int WishBuyQuantity { get; init; }

    /// <summary>Unit price of a wish on the mall.</summary>
    public long WishUnitPrice { get; init; }

    public HermitSnapshot Normalize() => this;
}

/// <summary>
/// Output of <see cref="HermitPlanner"/>. Every step is a typed, bounded
/// choice or purchase that the bridge can route through the runners.
/// </summary>
public sealed record HermitPlan
{
    public required string Verdict { get; init; }
    public required bool ShouldUseClover { get; init; }
    public required bool ShouldBuyWishes { get; init; }
    public required HermitPlanStep[] Steps { get; init; }
    public required string Rationale { get; init; }
    public required HermitSnapshot Snapshot { get; init; }
}

public sealed record HermitPlanStep
{
    public required string Kind { get; init; } // "clover" | "wish-buy" | "info"
    public required string ChoiceId { get; init; }
    public required string OptionId { get; init; }
    public required string Description { get; init; }
}

/// <summary>
/// Read-only planner for the daily Hermit routine. Reserves are computed
/// from <see cref="HermitSnapshot.ReserveAdventureValue"/> per
/// <c>WORKFLOW_PLAN.md</c> step 8: clovers are scarce utility and the
/// planner refuses to spend them when the spread is not positive.
/// </summary>
public sealed class HermitPlanner
{
    private const string ChoiceClover = "choice-clover";
    private const string OptionOpenClover = "1";
    private const string ChoiceHermitPermit = "choice-hermit-permit";
    private const string OptionBuyWish = "1";

    public HermitPlannerOptions Options { get; }

    public HermitPlanner(HermitPlannerOptions? options = null)
        => Options = options ?? new HermitPlannerOptions();

    public HermitPlan Plan(HermitSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var s = snapshot.Normalize();
        var steps = new List<HermitPlanStep>();

        if (s.Permits == 0)
        {
            return new HermitPlan
            {
                Verdict = "NoPermits",
                ShouldUseClover = false,
                ShouldBuyWishes = false,
                Steps = steps.ToArray(),
                Rationale = "No hermit permits available; nothing to plan.",
                Snapshot = s
            };
        }

        if (s.Clovers > 0)
        {
            // WORKFLOW_PLAN step 8: keep the clover unless its spread is
            // strictly positive against the adventure value we lose by
            // not using it.
            var spread = s.ExpectedCloverSaleValue - s.MinimumCloverSaleValue;
            var profitable = spread > 0 && s.ExpectedCloverSaleValue > s.ReserveAdventureValue;
            if (profitable)
            {
                steps.Add(new HermitPlanStep
                {
                    Kind = "clover",
                    ChoiceId = ChoiceClover,
                    OptionId = OptionOpenClover,
                    Description = $"Open one 11-leaf clover (expected sale {s.ExpectedCloverSaleValue}, reserve {s.MinimumCloverSaleValue})."
                });
            }
        }

        if (s.WishBuyQuantity > 0 && s.WishUnitPrice > 0)
        {
            var affordable = s.WishUnitPrice * s.WishBuyQuantity <= Options.MeatBudgetForWishes;
            if (affordable && s.WishesAvailable > 0)
            {
                steps.Add(new HermitPlanStep
                {
                    Kind = "wish-buy",
                    ChoiceId = ChoiceHermitPermit,
                    OptionId = OptionBuyWish,
                    Description = $"Buy {s.WishBuyQuantity} wish(es) at {s.WishUnitPrice} meat each."
                });
            }
        }

        var verdict = (steps.Count, steps.Any(x => x.Kind == "clover"), steps.Any(x => x.Kind == "wish-buy")) switch
        {
            (0, _, _) => "NoOp",
            (_, true, true) => "UseCloverAndBuyWishes",
            (_, true, false) => "UseClover",
            (_, false, true) => "BuyWishes",
            _ => "NoOp"
        };

        var rationale = BuildRationale(s, steps);
        return new HermitPlan
        {
            Verdict = verdict,
            ShouldUseClover = steps.Any(x => x.Kind == "clover"),
            ShouldBuyWishes = steps.Any(x => x.Kind == "wish-buy"),
            Steps = steps.ToArray(),
            Rationale = rationale,
            Snapshot = s
        };
    }

    private static string BuildRationale(HermitSnapshot s, IReadOnlyList<HermitPlanStep> steps)
    {
        var parts = new List<string>
        {
            $"permits={s.Permits}, clovers={s.Clovers}, wishes={s.WishesAvailable}.",
            $"adventureReserve={s.ReserveAdventureValue}, cloverFloor={s.MinimumCloverSaleValue}, cloverSale={s.ExpectedCloverSaleValue}."
        };
        parts.Add(steps.Count == 0 ? "Spread not positive; keep clover." : $"{steps.Count} step(s) prepared.");
        return string.Join(" ", parts);
    }
}

/// <summary>
/// Tunable thresholds for <see cref="HermitPlanner"/>.
/// </summary>
public sealed record HermitPlannerOptions
{
    /// <summary>Hard cap for the daily wish budget. The planner refuses to exceed it.</summary>
    public long MeatBudgetForWishes { get; init; } = 1_000_000;
}
