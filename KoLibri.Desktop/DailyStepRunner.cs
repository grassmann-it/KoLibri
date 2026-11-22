using System.Globalization;
using KoLibri.Core.Spiel;

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
        IReadOnlyList<CharacterAction> actions,
        Func<CancellationToken, Task<CharacterActionSnapshot>> read,
        Func<CharacterAction, CancellationToken, Task<CharacterActionSnapshot>> execute,
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
                // `current` is the snapshot read right before this action (the initial read or the previous answer).
                var skipped = await GuardAsync(consume, current, classifier, confirm).ConfigureAwait(false);
                if (skipped is not null) { problems.Add(skipped); continue; }
            }

            CharacterActionSnapshot after;
            try { after = await execute(action, token).ConfigureAwait(false); }
            catch (CharacterActionOutcomeUnknownException unknown)
            {
                problems.Add(unknown.Message);
                break;
            }
            if (after.RejectionReason is { } refused) { problems.Add(refused); break; }

            switch (action)
            {
                case ConsumeCharacterItem consumed:
                    var before = HeldOf(current.Inventory, consumed.ItemId);
                    var now = HeldOf(after.Inventory, consumed.ItemId);
                    var used = after.Inventory.IsStale ? 0 : Math.Max(0, before - now);
                    if (after.Inventory.IsStale)
                        problems.Add($"item {consumed.ItemId}: KoL did not return a fresh inventory, so it may have been used; refresh before repeating");
                    else if (used != consumed.Quantity)
                        problems.Add($"item {consumed.ItemId}: stock changed by {used} but {consumed.Quantity} were requested"
                            + (after.ResponseExcerpt is { Length: > 0 } excerpt ? $" ({excerpt})" : ""));
                    done += Math.Min(used, consumed.Quantity);
                    break;
                case RestAtCampground:
                    if (!CharacterActionClient.ConfirmsRest(current, after))
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

    private static int HeldOf(KoLInventorySnapshot inventory, int id)
        => inventory.Items.Where(i => i.ItemId == id.ToString(CultureInfo.InvariantCulture)).Sum(i => i.Quantity);

    /// <summary>Null when the consume may be sent; otherwise the reason it is skipped.</summary>
    private static async Task<string?> GuardAsync(
        ConsumeCharacterItem consume, CharacterActionSnapshot current, ItemClassifier? classifier,
        Func<string, string, Task<bool>> confirm)
    {
        if (current.Inventory.IsStale || current.Status.IsStale)
            return $"item {consume.ItemId}: KoL did not return a fresh status and inventory, nothing was sent";
        var held = HeldOf(current.Inventory, consume.ItemId);
        if (held < consume.Quantity)
            return $"item {consume.ItemId}: you hold {held}, {consume.Quantity} needed; nothing was sent";

        var wanted = consume.Kind switch
        {
            CharacterConsumableKind.Food => ItemOrgan.Food,
            CharacterConsumableKind.Drink => ItemOrgan.Drink,
            _ => ItemOrgan.Spleen
        };
        var c = classifier?.Classify(consume.ItemId) ?? new ItemClassification { ItemId = consume.ItemId.ToString(CultureInfo.InvariantCulture) };
        if (c.Known && c.Organ != wanted)
            return $"{c.Name ?? "item " + consume.ItemId} is not {wanted.ToString().ToLowerInvariant()} in the catalog; nothing was sent";

        var state = new OrganState(current.Status.Fullness, current.Status.Inebriety, current.Status.Spleen);
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
    public static string Describe(KoLibri.Core.Spiel.AutoCombatDailyBridge.BridgeExecutionResult? result)
    {
        if (result is null) return "The daily routine could not be completed.";
        var failed = result.FailedSteps.Count == 0 ? "" : " Failed: " + string.Join("; ", result.FailedSteps) + ".";
        return result.Completeness switch
        {
            KoLibri.Core.Spiel.AutoCombatDailyBridge.BridgeCompleteness.None
                => "Daily routine: nothing to do, no step was sent.",
            KoLibri.Core.Spiel.AutoCombatDailyBridge.BridgeCompleteness.Complete
                => $"Daily routine executed: {result.SubmittedSteps} step(s). Details are in the audit log.",
            KoLibri.Core.Spiel.AutoCombatDailyBridge.BridgeCompleteness.Partial
                => $"Daily routine only PARTLY executed: {result.SubmittedSteps} of {result.AttemptedSteps} step(s) went through, the rest failed.{failed}",
            _ => $"Daily routine FAILED: none of {result.AttemptedSteps} step(s) went through.{failed}"
        };
    }
}
