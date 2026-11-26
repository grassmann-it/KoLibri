namespace KoLibri.Desktop;

/// <summary>
/// One adventure job: the turn budget, the optional item stop and the fight limits. The batch form
/// and the location menu both build it the same way, so neither can start a different run.
/// </summary>
public sealed record AdventureJob(int Turns, string? ItemId, int MaxFightRounds, int MinimumHpPercent)
{
    /// <summary>The combat plan for fights in this job; null means attack.</summary>
    public KoLibri.Core.Spiel.AutoCombatPolicy? CombatPolicy { get; init; }

    /// <summary>The one default turn budget (matches the "turnBudget" setting default).</summary>
    public const int DefaultTurns = 100;

    /// <summary>Blank item text means no item stop; otherwise a positive item id is required.</summary>
    public static bool TryParseItem(string? text, out string? itemId, out string? error)
    {
        itemId = null; error = null;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return true;
        if (!int.TryParse(trimmed, out var id) || id <= 0)
        {
            error = "Enter a positive item ID or leave it blank.";
            return false;
        }
        itemId = trimmed;
        return true;
    }

    /// <summary>Builds the job from the form values and the combat limits; null with an error for bad input.</summary>
    public static AdventureJob? TryCreate(
        decimal? turns, string? itemText, int maxFightRounds, int minimumHpPercent, out string? error)
    {
        if (!TryParseItem(itemText, out var item, out error)) return null;
        var count = (int)Math.Clamp(turns ?? DefaultTurns, 1, 1_000);
        return new AdventureJob(count, item, maxFightRounds, minimumHpPercent);
    }
}
