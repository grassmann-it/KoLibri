using System.Globalization;

namespace KoLibri.Desktop;

/// <summary>How a daily step run ended: everything, only part of it, or nothing.</summary>
public enum DailyRunState { Complete, Partial, Failed }

/// <summary>Result of running the explicit actions of one daily step.</summary>
public sealed record DailyRunResult(int Done, int Planned, IReadOnlyList<string> Problems, string Unit)
{
    public DailyRunState State => Problems.Count == 0 ? DailyRunState.Complete
        : Done > 0 ? DailyRunState.Partial : DailyRunState.Failed;

    public string Summary
    {
        get
        {
            var head = $"{Done} {Unit}";
            return State switch
            {
                DailyRunState.Complete => head + ".",
                DailyRunState.Partial => $"Partly done: {head}, not everything. {string.Join("; ", Problems)}.",
                _ => $"Nothing done ({head}). {string.Join("; ", Problems)}."
            };
        }
    }
}

/// <summary>Status and inventory read right before or after one daily action, with what KoL said about the action.</summary>
public sealed record DailyStepState(Status Status, Inventory Inventory)
{
    /// <summary>KoL refused the action (or the library did, before sending it).</summary>
    public string? RejectionReason { get; init; }
    public string? ResponseExcerpt { get; init; }
    /// <summary>A request went out but its effect is unknown; the step stops.</summary>
    public bool Unclear { get; init; }
    /// <summary>For a rest: the library confirmed it (changed HP/MP/adventures or KoL's rest answer).</summary>
    public bool Confirmed { get; init; } = true;
}

/// <summary>
/// Runs the explicit actions of a daily step through the same planner and confirmation rules as the inventory:
/// every organ cost is checked against the state read right before the action, an unknown organ state or size
/// asks (declining skips that action), a consume is judged from the stock read right before and after it, and a
/// rejected or unconfirmed action stops the step. Pure with respect to I/O: reads, sends and asks are delegates.
/// </summary>
public static class DailyStepRunner
{
    public static async Task<DailyRunResult> RunAsync(
        DailyStepKind kind,
        IReadOnlyList<CharacterCommand> actions,
        Func<CancellationToken, Task<DailyStepState>> read,
        Func<CharacterCommand, CancellationToken, Task<DailyStepState>> execute,
        ItemClassifier? classifier,
        Func<string, string, Task<bool>> confirm,
        CancellationToken token)
    {
        var done = 0;
        var problems = new List<string>();
        var unit = kind == DailyStepKind.FreeRests ? "rest(s) taken" : "item(s) used";
        var current = await read(token).ConfigureAwait(false);
        foreach (var action in actions)
        {
            token.ThrowIfCancellationRequested();
            if (action is ConsumeCharacterItem consume)
            {
                // `current` is the state read right before this action (the initial read or the previous answer).
                var skipped = await GuardAsync(consume, current, classifier, confirm).ConfigureAwait(false);
                if (skipped is not null) { problems.Add(skipped); continue; }
            }

            var after = await execute(action, token).ConfigureAwait(false);
            if (after.Unclear)
            {
                problems.Add(after.RejectionReason ?? "the answer was lost, so the action may have happened; refresh before repeating");
                break;
            }
            if (after.RejectionReason is { } refused) { problems.Add(refused); break; }

            switch (action)
            {
                case ConsumeCharacterItem consumed:
                    var before = current.Inventory.Count(consumed.ItemId);
                    var now = after.Inventory.Count(consumed.ItemId);
                    var used = Math.Max(0, before - now);
                    if (used != consumed.Quantity)
                        problems.Add($"item {consumed.ItemId}: stock changed by {used} but {consumed.Quantity} were requested"
                            + (after.ResponseExcerpt is { Length: > 0 } excerpt ? $" ({excerpt})" : ""));
                    done += Math.Min(used, consumed.Quantity);
                    break;
                case RestAtCampground:
                    if (!after.Confirmed)
                    {
                        problems.Add("KoL did not confirm the rest");
                        break;
                    }
                    done++;
                    var cost = current.Status.Adventures - after.Status.Adventures;
                    if (cost > 0) problems.Add($"that rest cost {cost} adventure(s), so no more rests were taken");
                    break;
            }
            current = after;
            if (problems.Count > 0 && action is RestAtCampground) break;
        }
        return new DailyRunResult(done, actions.Count, problems, unit);
    }

    /// <summary>Null when the consume may be sent; otherwise the reason it is skipped.</summary>
    private static async Task<string?> GuardAsync(
        ConsumeCharacterItem consume, DailyStepState current, ItemClassifier? classifier,
        Func<string, string, Task<bool>> confirm)
    {
        var held = current.Inventory.Count(consume.ItemId);
        if (held < consume.Quantity)
            return $"item {consume.ItemId}: you hold {held}, {consume.Quantity} needed; nothing was sent";

        var wanted = consume.Kind switch
        {
            ConsumeKind.Eat => ItemOrgan.Food,
            ConsumeKind.Drink => ItemOrgan.Drink,
            _ => ItemOrgan.Spleen
        };
        var c = classifier?.Classify(consume.ItemId) ?? new ItemClassification { ItemId = consume.ItemId.ToString(CultureInfo.InvariantCulture) };
        if (c.Known && c.Organ != wanted)
            return $"{c.Name ?? "item " + consume.ItemId} is not {wanted.ToString().ToLowerInvariant()} in the catalog; nothing was sent";

        var state = OrganState.From(current.Status);
        var check = InventoryActionPlanner.CheckOverfill(c, consume.Quantity, state);
        if (!InventoryActionPlanner.NeedsConfirmation(check)) return null;
        var name = c.Name ?? $"item {consume.ItemId}";
        var ok = await confirm(check.Overfills ? "Overfill warning" : "Organ limit could not be checked",
            $"{name}: {check.Message}\n\n{state.Summary}\n\nSend it anyway?").ConfigureAwait(false);
        return ok ? null : $"{name}: skipped ({check.Message})";
    }
}

/// <summary>Plain-language summary of a daily routine run: complete, partial, failed or nothing to do.</summary>
public static class DailyRunReport
{
    public static string Describe(global::KoLibri.Daily.DailyRunResult? result)
    {
        if (result is null) return "The daily routine could not be completed.";
        var failed = result.FailedSteps.Count == 0 ? "" : " Failed: " + string.Join("; ", result.FailedSteps) + ".";
        return result.Completeness switch
        {
            global::KoLibri.Daily.DailyRunCompleteness.None
                => "Daily routine: nothing to do, no step was sent.",
            global::KoLibri.Daily.DailyRunCompleteness.Complete
                => $"Daily routine executed: {result.SubmittedSteps} step(s).",
            global::KoLibri.Daily.DailyRunCompleteness.Partial
                => $"Daily routine only PARTLY executed: {result.SubmittedSteps} of {result.AttemptedSteps} step(s) went through, the rest failed.{failed}",
            _ => $"Daily routine FAILED: none of {result.AttemptedSteps} step(s) went through.{failed}"
        };
    }
}
