using System.Globalization;
using KoLibri.Daily;

namespace KoLibri.Desktop;

/// <summary>
/// The live numbers behind the Daily tab: status, inventory and the daily-run counts (with the session's
/// once-per-day markers, keyed by the player id KoL reports, as in 0.4). Replaces 0.4's LiveDailySnapshot.
/// </summary>
public sealed record DesktopDailySnapshot
{
    public required AccountRole Role { get; init; }
    public required Status Status { get; init; }
    public required Inventory Inventory { get; init; }
    public required DailyRunLive Run { get; init; }

    public int Leaves => Run.Leaves;
    public int Clovers => Run.Clovers;
    public int Permits => Run.Permits;
    public int WorthlessItems => Run.WorthlessItems;
    public int Bacon => Run.Bacon;
    public int BaconMachines => Run.BaconMachines;
    public long Meat => Status.Meat;
    public int Adventures => Status.Adventures;
    public int Level => Status.Level;

    public int Count(int itemId) => Inventory.Count(itemId);

    /// <summary>Reads status, inventory (fresh, through <see cref="DailyRun.ReadAsync"/>) and the session's daily markers.</summary>
    public static async Task<DesktopDailySnapshot> ReadAsync(AccountRole role, KolibriSession session, CancellationToken cancellationToken = default)
    {
        var run = await DailyRun.ReadAsync(session, cancellationToken).ConfigureAwait(false);
        // DailyRun just read both with refresh: these come from the session cache.
        var status = await session.Character.GetStatusAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        var inventory = await session.Inventory.GetAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        return new DesktopDailySnapshot { Role = role, Status = status, Inventory = inventory, Run = run };
    }
}

/// <summary>The KoL day (rollover 03:30 Arizona) without a session. Same rule as <see cref="DailyMarkers.GameDay"/>.</summary>
public static class KolDay
{
    /// <summary>Copied from KoLibri.Core (0.5 step 3) DailyDoneStore.KolDay.</summary>
    public static DateOnly Of(DateTimeOffset now)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.CreateCustomTimeZone("EST", TimeSpan.FromHours(-5), "EST", "EST"); }
        var local = TimeZoneInfo.ConvertTime(now, zone);
        return DateOnly.FromDateTime(local.AddHours(-3.5).DateTime);
    }
}

// ------------------------------------------------------------------------------------------------
// Daily adventure planner: copied from KoLibri.Core (0.5 step 3) AdventureModels / DailyAdventurePlanner.
// Pure: it plans steps and never sends a request.
// ------------------------------------------------------------------------------------------------

public enum AdventureActionKind
{
    ConsumeLuckySurpriseEggs,
    RunSkeletonOfCrimbosPast,
    RunAdventure
}

public enum DailyAdventurePlanStatus
{
    TargetReached,
    Ready,
    PartiallyReady,
    BlockedStaleSnapshot,
    BlockedNoAdventureTurns,
    BlockedNoYield
}

/// <summary>The subset of account state needed to build a safe daily plan.</summary>
public sealed record AdventureState
{
    public bool IsStale { get; init; }
    public int Knucklebones { get; init; }
    public int AdventureTurnsRemaining { get; init; }
    public int LuckySurpriseEggs { get; init; }
    public int ForeignBribeBags { get; init; }
    public int PirateEncryptionKeys { get; init; }
    public int ElfGuardPresents { get; init; }
    public int CrimbucaneerBooty { get; init; }

    /// <summary>True when the Skeleton of Crimbos Past adventure is currently available.</summary>
    public bool SkeletonOfCrimbosPastAvailable { get; init; }

    public AdventureState Normalize()
        => this with
        {
            Knucklebones = Math.Max(0, Knucklebones),
            AdventureTurnsRemaining = Math.Max(0, AdventureTurnsRemaining),
            LuckySurpriseEggs = Math.Max(0, LuckySurpriseEggs),
            ForeignBribeBags = Math.Max(0, ForeignBribeBags),
            PirateEncryptionKeys = Math.Max(0, PirateEncryptionKeys),
            ElfGuardPresents = Math.Max(0, ElfGuardPresents),
            CrimbucaneerBooty = Math.Max(0, CrimbucaneerBooty)
        };
}

public sealed record AdventurePlannerOptions
{
    public int DailyKnucklebonesTarget { get; init; } = 100;
    public decimal ExpectedKnucklebonesPerAdventure { get; init; } = 0.5m;
    public decimal ExpectedKnucklebonesPerLuckySurpriseEgg { get; init; }
    public int ExpectedAdventureTurnsPerLuckySurpriseEgg { get; init; } = 1;
    public bool ConsumeLuckySurpriseEggs { get; init; } = true;
    public bool ConsumeEggsWhenTargetAlreadyReached { get; init; } = true;
    public int MaxLuckySurpriseEggsPerPlan { get; init; } = 15;
    public int MaxAdventureRunsPerPlan { get; init; } = 500;
    public bool PreservePirateEncryptionKeys { get; init; } = true;
    public bool DeferForeignBribeBagsToResearch { get; init; } = true;

    public AdventurePlannerOptions Normalize()
        => this with
        {
            DailyKnucklebonesTarget = Math.Max(0, DailyKnucklebonesTarget),
            ExpectedKnucklebonesPerAdventure = Math.Max(0m, ExpectedKnucklebonesPerAdventure),
            ExpectedKnucklebonesPerLuckySurpriseEgg = Math.Max(0m, ExpectedKnucklebonesPerLuckySurpriseEgg),
            ExpectedAdventureTurnsPerLuckySurpriseEgg = Math.Max(0, ExpectedAdventureTurnsPerLuckySurpriseEgg),
            MaxLuckySurpriseEggsPerPlan = Math.Max(0, MaxLuckySurpriseEggsPerPlan),
            MaxAdventureRunsPerPlan = Math.Max(0, MaxAdventureRunsPerPlan)
        };
}

public sealed record DailyAdventureStep(
    int Sequence,
    AdventureActionKind Action,
    int Quantity,
    decimal ExpectedKnucklebonesDelta,
    string Reason,
    bool RequiresFreshState = true,
    int ExpectedAdventureTurnsDelta = 0);

public sealed record DailyAdventurePlan
{
    public required DateOnly PlanDate { get; init; }
    public required DailyAdventurePlanStatus Status { get; init; }
    public required int TargetKnucklebones { get; init; }
    public required int StartingKnucklebones { get; init; }
    public required decimal ProjectedKnucklebones { get; init; }
    public required IReadOnlyList<DailyAdventureStep> Steps { get; init; }
    public required IReadOnlyList<string> Advisories { get; init; }

    public decimal RemainingToTarget => Math.Max(0m, TargetKnucklebones - ProjectedKnucklebones);
    public bool IsActionable => Steps.Count > 0;
}

/// <summary>Builds a bounded daily action plan; sends nothing.</summary>
public sealed class DailyAdventurePlanner
{
    private readonly AdventurePlannerOptions _defaults;

    public DailyAdventurePlanner(AdventurePlannerOptions? defaults = null)
        => _defaults = (defaults ?? new AdventurePlannerOptions()).Normalize();

    public DailyAdventurePlan Plan(AdventureState snapshot, DateOnly? planDate = null, AdventurePlannerOptions? options = null)
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
                Steps = Array.Empty<DailyAdventureStep>(),
                Advisories = new[] { "Account status or inventory is stale; refresh before planning or running adventures." }
            };
        }
        var target = settings.DailyKnucklebonesTarget;
        var projected = (decimal)state.Knucklebones;
        var availableTurns = state.AdventureTurnsRemaining;
        var steps = new List<DailyAdventureStep>();
        var advisories = new List<string>();
        var eggQuantity = Math.Min(state.LuckySurpriseEggs, settings.MaxLuckySurpriseEggsPerPlan);

        var shouldConsumeEggs = settings.ConsumeLuckySurpriseEggs
            && eggQuantity > 0
            && (projected < target || settings.ConsumeEggsWhenTargetAlreadyReached);

        if (shouldConsumeEggs)
        {
            var eggDelta = SafeProduct(eggQuantity, settings.ExpectedKnucklebonesPerLuckySurpriseEgg);
            steps.Add(new DailyAdventureStep(
                Sequence: steps.Count + 1,
                Action: AdventureActionKind.ConsumeLuckySurpriseEggs,
                Quantity: eggQuantity,
                ExpectedKnucklebonesDelta: eggDelta,
                Reason: "Consume available lucky surprise eggs according to the daily policy.",
                ExpectedAdventureTurnsDelta: SafeProduct(eggQuantity, settings.ExpectedAdventureTurnsPerLuckySurpriseEgg)));
            availableTurns = SafeAddTurns(availableTurns, SafeProduct(eggQuantity, settings.ExpectedAdventureTurnsPerLuckySurpriseEgg));
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
                var runs = Math.Min(runsNeeded, Math.Min(availableTurns, settings.MaxAdventureRunsPerPlan));
                var delta = SafeProduct(runs, settings.ExpectedKnucklebonesPerAdventure);
                var action = state.SkeletonOfCrimbosPastAvailable
                    ? AdventureActionKind.RunSkeletonOfCrimbosPast
                    : AdventureActionKind.RunAdventure;
                var reason = state.SkeletonOfCrimbosPastAvailable
                    ? "Use Skeleton of Crimbos Past while it is available to pursue the daily target."
                    : "Run the configured adventure until the daily target or turn budget is reached.";
                steps.Add(new DailyAdventureStep(
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

        if (settings.PreservePirateEncryptionKeys && state.PirateEncryptionKeys > 0)
            advisories.Add($"Keep {state.PirateEncryptionKeys} pirate encryption key(s) reserved for Crimbo crafting/resale analysis.");
        if (settings.DeferForeignBribeBagsToResearch && state.ForeignBribeBags > 0)
            advisories.Add($"Queue {state.ForeignBribeBags} foreign bribe bag(s) for DLC-credit purchase research; do not open implicitly.");
        if (state.ElfGuardPresents > 0 || state.CrimbucaneerBooty > 0)
            advisories.Add("Use elf guard presents and crimbucaneer booty as reference sources when pricing resale inventory.");
        if (state.LuckySurpriseEggs > settings.MaxLuckySurpriseEggsPerPlan)
            advisories.Add($"{state.LuckySurpriseEggs - settings.MaxLuckySurpriseEggsPerPlan} lucky surprise egg(s) remain queued because of the per-plan fullness cap.");

        var ranAdventure = steps.Any(step => step.Action is AdventureActionKind.RunAdventure or AdventureActionKind.RunSkeletonOfCrimbosPast);
        var status = state.Knucklebones >= target ? DailyAdventurePlanStatus.TargetReached
            : projected >= target ? DailyAdventurePlanStatus.Ready
            : ranAdventure ? DailyAdventurePlanStatus.PartiallyReady
            : settings.ExpectedKnucklebonesPerAdventure <= 0 ? DailyAdventurePlanStatus.BlockedNoYield
            : availableTurns <= 0 || settings.MaxAdventureRunsPerPlan <= 0 ? DailyAdventurePlanStatus.BlockedNoAdventureTurns
            : DailyAdventurePlanStatus.PartiallyReady;

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

    private static int DivideRoundUp(decimal numerator, decimal denominator)
    {
        try
        {
            var result = decimal.Ceiling(numerator / denominator);
            return result >= int.MaxValue ? int.MaxValue : (int)result;
        }
        catch (OverflowException) { return int.MaxValue; }
    }

    private static decimal SafeProduct(int left, decimal right)
    {
        try { return left * right; }
        catch (OverflowException) { return decimal.MaxValue; }
    }

    private static decimal SafeAdd(decimal left, decimal right)
    {
        try { return left + right; }
        catch (OverflowException) { return decimal.MaxValue; }
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

/// <summary>The daily-item options of the desktop (saved Cargo pockets and chess opt-in), shared by the briefing and the run.</summary>
public static class DesktopDailyItems
{
    /// <summary>The folder of the client's daily markers (the default KoLibri folder when the client has none configured).</summary>
    public static string StateDirectoryOf(DesktopLiveDataProvider provider)
        => provider.Accounts.Client.Options.StateDirectory ?? DailyItems.DefaultStateDirectory();

    /// <summary>The saved pockets; a broken list counts as "no pocket" (the run button reports it).</summary>
    public static DailyRunOptions Options(DesktopSettings settings, int hermitClovers = 0, string? stateDirectory = null)
    {
        IReadOnlyList<int> pockets;
        try { pockets = CargoPockets.Parse(settings.DailyCargoPockets); }
        catch (FormatException) { pockets = Array.Empty<int>(); }
        var mode = settings.DailyCargoMode switch { "off" => CargoPocketMode.Off, "list" => CargoPocketMode.List, _ => CargoPocketMode.Auto };
        return new DailyRunOptions
        {
            HermitCloversWanted = hermitClovers,
            // "Nach Wert" ignores a saved list; "Liste" uses it (empty = nothing picked).
            CargoPockets = mode == CargoPocketMode.List ? pockets : Array.Empty<int>(),
            CargoMode = mode,
            PlayChess = settings.DailyPlayChess,
            StateDirectory = stateDirectory
        };
    }
}
