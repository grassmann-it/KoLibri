using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace KoLibri.Desktop;

// Desktop-owned adventure types that replace the 0.4 KoLibri.Core ones the UI used (0.5 step 3).

/// <summary>One option of a choice the UI shows (same shape as 0.4's KoLibri.Core.Spiel.ChoiceOptionInfo).</summary>
public sealed record ChoiceOptionInfo(int Number, string Text);

/// <summary>
/// A choice a run stopped on because no policy answered it (same shape as 0.4's
/// KoLibri.Core.Spiel.NeedsDecisionInfo, minus the AI instruction text).
/// </summary>
public sealed record NeedsDecisionInfo
{
    public required string RunId { get; init; }
    public string? AdventureId { get; init; }
    public string? ChoiceId { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<ChoiceOptionInfo> Options { get; init; } = Array.Empty<ChoiceOptionInfo>();
    public string? Reason { get; init; }
    public string AccountRole { get; init; } = string.Empty;

    public static NeedsDecisionInfo From(ChoiceEncounter choice, string runId, string? adventureId, string? reason, AccountRole role) => new()
    {
        RunId = runId,
        AdventureId = adventureId,
        ChoiceId = choice.ChoiceId.ToString(CultureInfo.InvariantCulture),
        Title = choice.Title,
        Options = choice.Options.Select(o => new ChoiceOptionInfo(o.Option, o.Text)).ToArray(),
        Reason = reason,
        AccountRole = role.ToString()
    };
}

/// <summary>One looted item of an adventure batch (same shape as 0.4's QuestDrop).</summary>
public sealed record AdventureDrop
{
    public required string ItemName { get; init; }
    public int Quantity { get; init; } = 1;
}

/// <summary>Why a bounded batch stopped; the names of 0.4's BoundedAdventureRunStopReason, mapped from <see cref="RunStopReason"/>.</summary>
public enum BoundedAdventureRunStopReason
{
    ReadyToRun,
    Complete,
    ConditionsSatisfied,
    ConditionsBlocked,
    StepLimitReached,
    DeadlineReached,
    GatewayStopped,
    AwaitingManualChoice,
    NeedsDecision,
    FightRoundLimitReached,
    HpBelowMinimum,
    CombatPolicyStopped
}

/// <summary>One manual action in the adventure tab: a fight action or a choice option.</summary>
public abstract record AdventureStep;

public sealed record FightStep(CombatAction Action) : AdventureStep;

public sealed record ChoiceStep(int ChoiceId, int Option) : AdventureStep;

public enum AdventureStepStatus
{
    /// <summary>A fight or choice is open and waits for the next action.</summary>
    Open,
    /// <summary>The adventure is over (won, lost, fled, non-combat, choice answered).</summary>
    Complete,
    /// <summary>KoL stopped the step (blocked, unreadable page, refused option).</summary>
    Aborted,
    /// <summary>Nothing was sent (the action does not fit the open encounter).</summary>
    NotSent
}

/// <summary>What one start, step, recheck or batch of the adventure tab left behind.</summary>
public sealed record AdventureStepOutcome(AdventureStepStatus Status, Encounter? Encounter, string? Message)
{
    public string? RunId { get; init; }
    /// <summary>The last fight round of this step (HP/MP as KoLibri read them).</summary>
    public FightRound? Round { get; init; }
    public IReadOnlyList<InventoryItem> Drops { get; init; } = Array.Empty<InventoryItem>();
    /// <summary>KoL's answer could not be read: press Recheck encounter before the next action.</summary>
    public bool RequiresRefresh { get; init; }
    /// <summary>Path of the redacted copy of an unreadable page, if one was saved.</summary>
    public string? SavedPagePath { get; init; }
    public string StopReason { get; init; } = string.Empty;

    public bool IsComplete => Status == AdventureStepStatus.Complete;
    public bool IsAborted => Status == AdventureStepStatus.Aborted;
    public string SavedPageNote => SavedPagePath is null ? string.Empty : $" The page was saved (redacted) to {SavedPagePath}.";

    public static string KindOf(Encounter? encounter) => encounter switch
    {
        FightEncounter => "Fight",
        ChoiceEncounter => "Choice",
        NonCombatEncounter => "NonCombat",
        BlockedEncounter blocked => "Blocked:" + blocked.Reason,
        null => "None",
        _ => encounter.GetType().Name
    };
}

/// <summary>
/// The desktop's fight rule around a KoLibri strategy (as 0.4's BoundedAdventureRunner): hand the fight back
/// below the HP floor (or with unknown HP) and after the round limit. Remembers why it stopped.
/// </summary>
internal sealed class DesktopCombatStrategy(ICombatStrategy inner, int minimumHpPercent, int maxRounds = 0) : ICombatStrategy
{
    public BoundedAdventureRunStopReason? StopReason { get; private set; }

    public CombatAction Next(CombatContext context)
    {
        if (maxRounds > 0 && context.History.Count >= maxRounds)
            return Stop(BoundedAdventureRunStopReason.FightRoundLimitReached);
        if (minimumHpPercent > 0 && (context.MaxHp <= 0 || 100L * context.Hp < (long)minimumHpPercent * context.MaxHp))
            return Stop(BoundedAdventureRunStopReason.HpBelowMinimum);
        var action = inner.Next(context);
        if (action.Kind == CombatActionKind.Stop) return Stop(BoundedAdventureRunStopReason.CombatPolicyStopped);
        StopReason = null;
        return action;
    }

    private CombatAction Stop(BoundedAdventureRunStopReason reason)
    {
        StopReason = reason;
        return CombatAction.Stop;
    }

    /// <summary>The rule text of the plan (0.4 AutoCombatPolicy DSL or JSON), or attack when there is none.</summary>
    public static ICombatStrategy FromRules(string? rules)
        => string.IsNullOrWhiteSpace(rules) ? CombatStrategies.AttackOnly : CombatStrategies.FromRules(rules);
}

/// <summary>One skill or item the open fight page offers.</summary>
public sealed record FightPageAction(CombatActionKind Kind, int Id, string Name, int? MpCost);

/// <summary>
/// Reads the skill and item dropdowns of an open fight page (whichskill / whichitem). A small desktop
/// replacement for the action list of 0.4's KoLCombatObservationParser; display only, nothing is sent from here.
/// </summary>
public static class FightPageActions
{
    private static readonly Regex Select = new(
        @"<select[^>]*name\s*=\s*[""']?(?<name>whichskill|whichitem)[""']?[^>]*>(?<body>.*?)</select>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Option = new(
        @"<option[^>]*value\s*=\s*[""']?(?<id>\d+)[""']?[^>]*>(?<text>[^<]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Cost = new(@"\((?<mp>\d+)\s+(?:[A-Za-z]+\s+)?(?:Points?|MP)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<FightPageAction> Parse(string? html)
    {
        var result = new List<FightPageAction>();
        if (string.IsNullOrEmpty(html)) return result;
        foreach (Match select in Select.Matches(html))
        {
            var kind = select.Groups["name"].Value.Equals("whichskill", StringComparison.OrdinalIgnoreCase)
                ? CombatActionKind.Skill : CombatActionKind.Item;
            foreach (Match option in Option.Matches(select.Groups["body"].Value))
            {
                if (!int.TryParse(option.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) continue;
                var text = WebUtility.HtmlDecode(option.Groups["text"].Value).Trim();
                if (text.Length == 0) continue;
                var cost = kind == CombatActionKind.Skill ? Cost.Match(text) : Match.Empty;
                int? mp = cost.Success && int.TryParse(cost.Groups["mp"].Value, out var n) ? n : null;
                if (result.All(a => a.Kind != kind || a.Id != id))
                    result.Add(new FightPageAction(kind, id, text, mp));
            }
        }
        return result;
    }
}
