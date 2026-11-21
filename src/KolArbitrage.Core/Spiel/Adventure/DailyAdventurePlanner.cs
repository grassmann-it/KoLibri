namespace KolArbitrage.Core.Spiel;

/// <summary>
/// Builds a bounded daily action plan. It deliberately does not call a game endpoint or
/// spend an item; an adapter can execute each returned step and refresh state between steps.
/// </summary>
public sealed class DailyAdventurePlanner
{
    private readonly AdventurePlannerOptions _defaults;

    public DailyAdventurePlanner(AdventurePlannerOptions? defaults = null)
    {
        _defaults = (defaults ?? new AdventurePlannerOptions()).Normalize();
    }

    public DailyAdventurePlan Plan(
        AdventureState snapshot,
        DateOnly? planDate = null,
        AdventurePlannerOptions? options = null)
    {
        var state = snapshot.Normalize();
        var settings = (options ?? _defaults).Normalize();
        var date = planDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        if (state.IsStale)
        {
            return new DailyAdventurePlan
            {
                PlanDate = date,
                Status = DailyAdventurePlanStatus.BlockedStaleSnapshot,
                TargetKnucklebones = settings.DailyKnucklebonesTarget,
                StartingKnucklebones = state.Knucklebones,
                ProjectedKnucklebones = state.Knucklebones,
                Steps = Array.Empty<AdventureStep>(),
                Advisories = new[] { "Account status or inventory is stale; refresh before planning or running adventures." }
            };
        }
        var target = settings.DailyKnucklebonesTarget;
        var projected = (decimal)state.Knucklebones;
        var availableTurns = state.AdventureTurnsRemaining;
        var steps = new List<AdventureStep>();
        var advisories = new List<string>();
        var eggQuantity = Math.Min(state.LuckySurpriseEggs, settings.MaxLuckySurpriseEggsPerPlan);

        // Keep the daily egg policy explicit. If the target is already reached, eggs are
        // only consumed when the caller opted into that behavior.
        var shouldConsumeEggs = settings.ConsumeLuckySurpriseEggs
            && eggQuantity > 0
            && (projected < target || settings.ConsumeEggsWhenTargetAlreadyReached);

        if (shouldConsumeEggs)
        {
            var eggDelta = SafeProduct(
                eggQuantity,
                settings.ExpectedKnucklebonesPerLuckySurpriseEgg);
            steps.Add(new AdventureStep(
                Sequence: steps.Count + 1,
                Action: AdventureActionKind.ConsumeLuckySurpriseEggs,
                Quantity: eggQuantity,
                ExpectedKnucklebonesDelta: eggDelta,
                Reason: "Consume available lucky surprise eggs according to the daily policy.",
                ExpectedAdventureTurnsDelta: SafeProduct(
                    eggQuantity,
                    settings.ExpectedAdventureTurnsPerLuckySurpriseEgg)));
            availableTurns = SafeAddTurns(
                availableTurns,
                SafeProduct(eggQuantity, settings.ExpectedAdventureTurnsPerLuckySurpriseEgg));
            projected = SafeAdd(projected, eggDelta);
        }

        var remaining = Math.Max(0m, target - projected);
        if (remaining > 0)
        {
            if (settings.ExpectedKnucklebonesPerAdventure <= 0)
            {
                advisories.Add("Adventure yield is unknown or zero; refresh the yield estimate before running automation.");
            }
            else if (availableTurns <= 0 || settings.MaxAdventureRunsPerPlan <= 0)
            {
                advisories.Add("No adventure turns are available for the remaining daily target.");
            }
            else
            {
                var runsNeeded = DivideRoundUp(remaining, settings.ExpectedKnucklebonesPerAdventure);
                var runs = Math.Min(runsNeeded, Math.Min(
                    availableTurns,
                    settings.MaxAdventureRunsPerPlan));
                var delta = SafeProduct(runs, settings.ExpectedKnucklebonesPerAdventure);
                var action = state.SkeletonOfCrimbosPastAvailable
                    ? AdventureActionKind.RunSkeletonOfCrimbosPast
                    : AdventureActionKind.RunAdventure;
                var reason = state.SkeletonOfCrimbosPastAvailable
                    ? "Use Skeleton of Crimbos Past while it is available to pursue the daily target."
                    : "Run the configured adventure until the daily target or turn budget is reached.";

                steps.Add(new AdventureStep(
                    Sequence: steps.Count + 1,
                    Action: action,
                    Quantity: runs,
                    ExpectedKnucklebonesDelta: delta,
                    Reason: reason,
                    ExpectedAdventureTurnsDelta: -runs));
                availableTurns -= runs;
                projected = SafeAdd(projected, delta);
            }
        }

        AddResourceAdvisories(state, settings, advisories);
        if (state.LuckySurpriseEggs > settings.MaxLuckySurpriseEggsPerPlan)
        {
            advisories.Add($"{state.LuckySurpriseEggs - settings.MaxLuckySurpriseEggsPerPlan} lucky surprise egg(s) remain queued because of the per-plan fullness cap.");
        }

        var status = DetermineStatus(
            starting: state.Knucklebones,
            projected,
            target,
            settings.ExpectedKnucklebonesPerAdventure,
            availableTurns,
            settings.MaxAdventureRunsPerPlan,
            steps.Any(step => step.Action is AdventureActionKind.RunAdventure or AdventureActionKind.RunSkeletonOfCrimbosPast));

        return new DailyAdventurePlan
        {
            PlanDate = date,
            Status = status,
            TargetKnucklebones = target,
            StartingKnucklebones = state.Knucklebones,
            ProjectedKnucklebones = projected,
            Steps = steps,
            Advisories = advisories
        };
    }

    private static DailyAdventurePlanStatus DetermineStatus(
        int starting,
        decimal projected,
        int target,
        decimal expectedPerAdventure,
        int turns,
        int maxRuns,
        bool ranAdventure)
    {
        if (starting >= target)
        {
            return DailyAdventurePlanStatus.TargetReached;
        }

        if (projected >= target)
        {
            return DailyAdventurePlanStatus.Ready;
        }

        if (ranAdventure)
        {
            return DailyAdventurePlanStatus.PartiallyReady;
        }

        if (expectedPerAdventure <= 0)
        {
            return DailyAdventurePlanStatus.BlockedNoYield;
        }

        if (turns <= 0 || maxRuns <= 0)
        {
            return DailyAdventurePlanStatus.BlockedNoAdventureTurns;
        }

        return DailyAdventurePlanStatus.PartiallyReady;
    }

    private static void AddResourceAdvisories(
        AdventureState state,
        AdventurePlannerOptions settings,
        ICollection<string> advisories)
    {
        if (settings.PreservePirateEncryptionKeys && state.PirateEncryptionKeys > 0)
        {
            advisories.Add($"Keep {state.PirateEncryptionKeys} pirate encryption key(s) reserved for Crimbo crafting/resale analysis.");
        }

        if (settings.DeferForeignBribeBagsToResearch && state.ForeignBribeBags > 0)
        {
            advisories.Add($"Queue {state.ForeignBribeBags} foreign bribe bag(s) for DLC-credit purchase research; do not open implicitly.");
        }

        if (state.ElfGuardPresents > 0 || state.CrimbucaneerBooty > 0)
        {
            advisories.Add("Use elf guard presents and crimbucaneer booty as reference sources when pricing resale inventory.");
        }
    }

    private static int DivideRoundUp(decimal numerator, decimal denominator)
    {
        try
        {
            var result = decimal.Ceiling(numerator / denominator);
            return result >= int.MaxValue ? int.MaxValue : (int)result;
        }
        catch (OverflowException)
        {
            return int.MaxValue;
        }
    }

    private static decimal SafeProduct(int left, decimal right)
    {
        try
        {
            return left * right;
        }
        catch (OverflowException)
        {
            return decimal.MaxValue;
        }
    }

    private static decimal SafeAdd(decimal left, decimal right)
    {
        try
        {
            return left + right;
        }
        catch (OverflowException)
        {
            return decimal.MaxValue;
        }
    }

    private static int SafeProduct(int left, int right)
    {
        var value = (long)left * right;
        return value >= int.MaxValue ? int.MaxValue : (int)value;
    }

    private static int SafeAddTurns(int left, int right)
    {
        var value = (long)left + right;
        return value >= int.MaxValue ? int.MaxValue : (int)value;
    }
}
