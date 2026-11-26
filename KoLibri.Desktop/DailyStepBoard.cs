using System.Globalization;

namespace KoLibri.Desktop;

public enum DailyStepKind { Adventures, LuckyEgg, FreeRests, Booze, Spleen, CloverCheck }

/// <summary>One row of the per-account daily step list.</summary>
public sealed record DailyStepRow(
    DailyStepKind Kind, string Title, string Detail, string Status, string ActionLabel,
    bool CanRun, string? DisabledReason = null)
{
    public string Id => StepId(Kind);
    public bool IsSkipped => Status == "skipped";
    public bool IsDone => Status == "done";

    public static string StepId(DailyStepKind kind) => kind switch
    {
        DailyStepKind.Adventures => "adventures",
        DailyStepKind.LuckyEgg => "lucky-egg",
        DailyStepKind.FreeRests => "free-rests",
        DailyStepKind.Booze => "booze",
        DailyStepKind.Spleen => "spleen",
        _ => "clover-check"
    };

    /// <summary>Text for "Copy": the row as one line.</summary>
    public string CopyText => $"{Title}: {Detail} [{Status}]";
}

/// <summary>Builds the daily step list from live numbers and the saved settings. Pure: it sends nothing.</summary>
public static class DailyStepBoard
{
    public const int KnucklebonesItemId = 12051;
    public const int LuckySurpriseEggItemId = 995;

    /// <summary>The current KoL day (rollover 03:30 US Eastern) as a date; skips and other daily markers share it.</summary>
    public static DateOnly KolToday(DateTimeOffset? now = null)
        => DateOnly.ParseExact(DailyDoneStore.KolDay(now ?? DateTimeOffset.UtcNow), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The one set of planner options for the knucklebone target, used by the Daily and Adventures views.</summary>
    public static AdventurePlannerOptions PlannerOptions(DesktopSettings settings)
        => new() { DailyKnucklebonesTarget = (int)settings.Get("dailyKnucklebonesTarget") };

    public static IReadOnlyList<DailyStepRow> Build(LiveDailySnapshot live, DesktopSettings settings, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(settings);
        var role = live.State.Role.ToString();
        var items = live.State.Inventory.Items;
        int Count(int id) => items.Where(i => i.ItemId == id.ToString(CultureInfo.InvariantCulture)).Sum(i => i.Quantity);

        var rows = new List<DailyStepRow>();

        var target = (int)settings.Get("dailyKnucklebonesTarget");
        var bones = Count(KnucklebonesItemId);
        var plan = new DailyAdventurePlanner().Plan(
            new AdventureState
            {
                IsStale = live.State.Status.IsStale || live.State.Inventory.IsStale,
                Knucklebones = bones,
                AdventureTurnsRemaining = live.Adventures,
                LuckySurpriseEggs = Count(LuckySurpriseEggItemId),
            },
            today,
            PlannerOptions(settings));
        var adventureDetail = $"Knucklebones {bones:N0} / {target:N0} · {live.Adventures:N0} adventures";
        rows.Add(bones >= target
            ? new(DailyStepKind.Adventures, "Adventure target", adventureDetail + " · target reached", "done", "Plan", true)
            : live.Adventures <= 0
                ? new(DailyStepKind.Adventures, "Adventure target", adventureDetail, "blocked", "Plan", false, "No adventures left today.")
                : new(DailyStepKind.Adventures, "Adventure target", adventureDetail + $" · {plan.Steps.Count} planned step(s)", "open", "Plan", true));

        var eggs = Count(LuckySurpriseEggItemId);
        rows.Add(eggs > 0
            ? new(DailyStepKind.LuckyEgg, "Eat Lucky Surprise Egg", $"{eggs} in the inventory (item {LuckySurpriseEggItemId})", "open", "Run", true)
            : new(DailyStepKind.LuckyEgg, "Eat Lucky Surprise Egg", $"None in the inventory (item {LuckySurpriseEggItemId})", "done", "Run", false, "No Lucky Surprise Egg to eat."));

        var rests = (int)settings.Get("dailyRests");
        rows.Add(rests > 0
            ? new(DailyStepKind.FreeRests, "Free rests", $"Up to {rests} rests; stops at the first rest that costs adventures", "open", "Run", true)
            : new(DailyStepKind.FreeRests, "Free rests", "Rests per daily run is 0 (Settings)", "blocked", "Run", false, "Set rests per daily run above 0 in Settings."));

        rows.Add(ConsumableRow(DailyStepKind.Booze, "Booze", settings.BoozeItems, live.State.Status.Inebriety, "inebriety", "booze"));
        rows.Add(ConsumableRow(DailyStepKind.Spleen, "Spleen", settings.SpleenItems, live.State.Status.Spleen, "spleen", "spleen items"));

        var clovers = live.Clovers;
        rows.Add(clovers > 0
            ? new(DailyStepKind.CloverCheck, "Clover check", $"{clovers} 11-leaf clover(s) in the inventory", "done", "Check", true)
            : new(DailyStepKind.CloverCheck, "Clover check", "No 11-leaf clovers in the inventory", "open", "Check", true));

        // A skip (right-click) hides the action for today; done steps stay done.
        return rows.Select(r => !r.IsDone && settings.IsSkipped(role, r.Id, today)
            ? r with { Status = "skipped", CanRun = false, DisabledReason = "Skipped for today." }
            : r).ToArray();
    }

    private static DailyStepRow ConsumableRow(DailyStepKind kind, string title,
        IReadOnlyList<DailyConsumable> list, int? level, string levelName, string noun)
    {
        if (list.Count == 0)
            return new(kind, title, $"No {noun} chosen. Pick them in Settings.", "blocked", "Run", false,
                $"Choose {noun} in Settings first.");
        var names = string.Join(", ", list.Select(x => $"{x.Name} × {x.Quantity}"));
        var current = level is { } l ? $" · {levelName} {l}" : "";
        return new(kind, title, names + current, "open", "Run", true);
    }

    /// <summary>The explicit actions a Run on this row would send, in order. Empty when the row cannot run.</summary>
    public static IReadOnlyList<CharacterAction> ActionsFor(DailyStepRow row, DesktopSettings settings, int eggsOwned)
    {
        if (!row.CanRun) return Array.Empty<CharacterAction>();
        switch (row.Kind)
        {
            case DailyStepKind.LuckyEgg:
                return [new ConsumeCharacterItem(LuckySurpriseEggItemId, CharacterConsumableKind.Food,
                    Math.Clamp(eggsOwned, 1, new AdventurePlannerOptions().MaxLuckySurpriseEggsPerPlan))];
            case DailyStepKind.FreeRests:
                return Enumerable.Repeat<CharacterAction>(new RestAtCampground(), (int)settings.Get("dailyRests")).ToArray();
            case DailyStepKind.Booze:
                return settings.BoozeItems.Select(x => (CharacterAction)new ConsumeCharacterItem(x.ItemId, CharacterConsumableKind.Drink, x.Quantity)).ToArray();
            case DailyStepKind.Spleen:
                return settings.SpleenItems.Select(x => (CharacterAction)new ConsumeCharacterItem(x.ItemId, CharacterConsumableKind.Spleen, x.Quantity)).ToArray();
            default:
                return Array.Empty<CharacterAction>();
        }
    }

    /// <summary>The confirmation text for a row's actions (what will be sent).</summary>
    public static string ConfirmText(DailyStepRow row, DesktopSettings settings, int eggsOwned)
    {
        var actions = ActionsFor(row, settings, eggsOwned);
        var lines = actions.OfType<ConsumeCharacterItem>().Select(a =>
            $"{a.Kind switch { CharacterConsumableKind.Food => "Eat", CharacterConsumableKind.Drink => "Drink", _ => "Take" }} item {a.ItemId} × {a.Quantity}").ToList();
        if (actions.OfType<RestAtCampground>().Any())
            lines.Add($"Rest at the campground up to {actions.Count} time(s). Stops at the first rest that costs adventures.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Turns an exception or a KoL refusal into one readable sentence.</summary>
    public static string ReadableError(string step, Exception exception) => exception switch
    {
        KoLibri.Core.Basis.KoLHtmlResponseException { Kind: KoLibri.Core.Basis.KoLHtmlResponseKind.LoginPage }
            => $"{step}: the session expired. Log in again, then retry.",
        OperationCanceledException => $"{step}: cancelled.",
        HttpRequestException h => $"{step}: the request to Kingdom of Loathing failed ({h.Message}). If it was already sent the action may have happened: refresh before retrying.",
        InvalidOperationException i => $"{step}: {i.Message}",
        _ => $"{step}: {exception.Message}"
    };
}
