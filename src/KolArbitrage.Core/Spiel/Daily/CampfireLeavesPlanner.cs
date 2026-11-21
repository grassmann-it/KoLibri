
namespace KolArbitrage.Core.Spiel;

/// <summary>
/// Inputs the planner needs to decide whether to spend a campfire leaf today
/// and whether to buy a Day Shortner. All numbers are read-only snapshots.
/// </summary>
public sealed record CampfireLeavesSnapshot
{
    /// <summary>How many campfire leaves the player currently holds.</summary>
    public int CampfireLeaves { get; init; }

    /// <summary>Minimum reserve below which the planner refuses to burn.</summary>
    public int MinimumReserve { get; init; } = 3;

    /// <summary>Current day-shifter price on the mall in meat.</summary>
    public long DayShortnerPrice { get; init; } = long.MaxValue;

    /// <summary>Meat the player has available to spend today.</summary>
    public long AvailableMeat { get; init; }

    /// <summary>Whether today is a useful target day for burning (knucklebones or under-100). Defaults to true.</summary>
    public bool IsUsefulDay { get; init; } = true;

    /// <summary>Player knucklebone count; the planner encourages burning under 100.</summary>
    public int Knucklebones { get; init; }

    public CampfireLeavesSnapshot Normalize() => this;
}

/// <summary>
/// Output of <see cref="CampfireLeavesPlanner"/>. Includes both the verdict
/// and the discrete actions the operator should preview/commit.
/// </summary>
public sealed record CampfireLeavesPlan
{
    public required bool ShouldBurn { get; init; }
    public required string Verdict { get; init; }
    public required string Rationale { get; init; }
    public required bool ShouldBuyDayShortner { get; init; }
    public required CampfireLeavesPlanStep[] Steps { get; init; }
    public required CampfireLeavesSnapshot Snapshot { get; init; }
}

/// <summary>
/// One operator-visible step: either a preview (read-only) or a proposed
/// choice POST. The bridge turns the latter into a <c>BoundedChoiceChainRunner</c>
/// call against the allowlisted choice ids.
/// </summary>
public sealed record CampfireLeavesPlanStep
{
    public required string Kind { get; init; } // "burn" | "day-shortner" | "info"
    public required string ChoiceId { get; init; }
    public required string OptionId { get; init; }
    public required string Description { get; init; }
}

/// <summary>
/// Deterministic planner for the daily Campfire Leaves / Day Shifter loop.
/// Holds no HTTP state, no checkpoint store, and is safe to invoke from a
/// preview tool. Approval + commit are handled by the bridge.
/// </summary>
public sealed class CampfireLeavesPlanner
{
    private const string ChoiceCampfire = "choice-campfire";
    private const string OptionBurn = "1";
    private const string ChoiceDayShortnerConfirm = "choice-day-shortner";
    private const string OptionConfirmBuy = "1";

    public CampfireLeavesPlannerOptions Options { get; }

    public CampfireLeavesPlanner(CampfireLeavesPlannerOptions? options = null)
        => Options = options ?? new CampfireLeavesPlannerOptions();

    public CampfireLeavesPlan Plan(CampfireLeavesSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var s = snapshot.Normalize();
        var steps = new List<CampfireLeavesPlanStep>();
        var advisories = new List<string>();

        var canBurn = s.CampfireLeaves > s.MinimumReserve;
        var useful = s.IsUsefulDay || s.Knucklebones < Options.KnuckleboneTarget;
        var shouldBurn = canBurn && useful;

        var dayShortnerAffordable = s.DayShortnerPrice <= s.AvailableMeat - Options.MeatReserveAfterShortner
                                     && s.DayShortnerPrice < long.MaxValue;
        var leavesAfterBurn = shouldBurn ? s.CampfireLeaves - 1 : s.CampfireLeaves;
        var dayShortnerUseful = !s.IsUsefulDay || leavesAfterBurn <= s.MinimumReserve;
        var shouldBuy = dayShortnerAffordable && dayShortnerUseful;

        if (shouldBurn)
        {
            steps.Add(new CampfireLeavesPlanStep
            {
                Kind = "burn",
                ChoiceId = ChoiceCampfire,
                OptionId = OptionBurn,
                Description = $"Burn one campfire leaf to advance the day (leaves remaining after burn: {s.CampfireLeaves - 1})."
            });
        }

        if (shouldBuy)
        {
            steps.Add(new CampfireLeavesPlanStep
            {
                Kind = "day-shortner",
                ChoiceId = ChoiceDayShortnerConfirm,
                OptionId = OptionConfirmBuy,
                Description = $"Confirm purchase of a Day Shortner for {s.DayShortnerPrice} meat."
            });
        }

        var rationale = BuildRationale(shouldBurn, shouldBuy, s);
        var verdict = (shouldBurn, shouldBuy) switch
        {
            (true, true) => "BurnAndBuy",
            (true, false) => "BurnOnly",
            (false, true) => "BuyOnly",
            _ => "NoOp"
        };

        return new CampfireLeavesPlan
        {
            ShouldBurn = shouldBurn,
            Verdict = verdict,
            Rationale = rationale,
            ShouldBuyDayShortner = shouldBuy,
            Steps = steps.ToArray(),
            Snapshot = s
        };
    }

    private static string BuildRationale(bool burn, bool buy, CampfireLeavesSnapshot s)
    {
        var parts = new List<string>
        {
            $"Leaves: {s.CampfireLeaves} (reserve {s.MinimumReserve}).",
            $"Knucklebones: {s.Knucklebones}."
        };
        parts.Add(burn
            ? "Useful day → burn one leaf."
            : s.CampfireLeaves <= s.MinimumReserve
                ? "Reserve guards the burn."
                : "Day not useful → keep leaves.");
        parts.Add(buy
            ? $"Day Shortner affordable at {s.DayShortnerPrice} meat."
            : $"Day Shortner too expensive or unnecessary ({s.DayShortnerPrice} meat).");
        return string.Join(" ", parts);
    }
}

/// <summary>
/// Tunable thresholds for <see cref="CampfireLeavesPlanner"/>.
/// </summary>
public sealed record CampfireLeavesPlannerOptions
{
    /// <summary>Players typically target 100 knucklebones; below that the planner leans toward burning.</summary>
    public int KnuckleboneTarget { get; init; } = 100;

    /// <summary>Minimum meat the planner must keep available after a Day Shifter purchase.</summary>
    public long MeatReserveAfterShortner { get; init; } = 200_000;
}
