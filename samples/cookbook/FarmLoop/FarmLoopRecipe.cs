using KoLibri;

namespace Cookbook.FarmLoop;

public sealed class FarmLoopOptions
{
    /// <summary>Harte Obergrenze, egal was übergeben wird.</summary>
    public const int HardMaxTurns = 100;

    public int Snarfblat { get; init; } = 113;
    public int MaxTurns { get; init; } = 5;
    /// <summary>Skill-Id des Heilskills; 0 = nie heilen.</summary>
    public int HealSkillId { get; init; }
    public int HealBelowPercent { get; init; } = 40;
    /// <summary>Ohne Yes bleibt es beim Trockenlauf: nichts wird abenteuert.</summary>
    public bool Yes { get; init; }
}

/// <summary>Eigene Kampfstrategie: heilt unter X % HP, greift sonst an, bricht nach Runde 20 ab.</summary>
public sealed class CautiousStrategy : ICombatStrategy
{
    public const int LastRound = 20;
    private readonly int _healSkillId;
    private readonly int _healBelowPercent;

    public CautiousStrategy(int healSkillId, int healBelowPercent)
    {
        _healSkillId = healSkillId;
        _healBelowPercent = healBelowPercent;
    }

    public CombatAction Next(CombatContext context)
    {
        if (context.Round >= LastRound) return new CombatAction(CombatActionKind.Stop);
        var lowHp = context.MaxHp > 0 && context.Hp * 100 < context.MaxHp * _healBelowPercent;
        if (lowHp && _healSkillId > 0 && context.Mp > 0)
            return new CombatAction(CombatActionKind.Skill, _healSkillId);
        return new CombatAction(CombatActionKind.Attack);
    }
}

public static class Recipe
{
    public static async Task<int> RunAsync(KolibriSession session, FarmLoopOptions options, TextWriter output, CancellationToken ct = default)
    {
        var turns = Math.Clamp(options.MaxTurns, 1, FarmLoopOptions.HardMaxTurns);
        var request = new RunRequest(new Location(options.Snarfblat), turns)
        {
            Combat = new CautiousStrategy(options.HealSkillId, options.HealBelowPercent),
            Choices = ChoiceResolvers.Safe(),
            StopWhen = new[] { StopCondition.HpBelowPercent(25) },
        };

        output.WriteLine($"Plan: bis zu {turns} Turns in Snarfblat {options.Snarfblat}, Heilung unter {options.HealBelowPercent} % HP" +
                         (options.HealSkillId > 0 ? $" mit Skill {options.HealSkillId}" : " (kein Heilskill)") + ".");
        if (!options.Yes)
        {
            output.WriteLine("Trockenlauf: es wurde nichts abenteuert. Mit --yes wirklich starten.");
            return 0;
        }

        var run = await session.Adventure.RunAsync(request, ct);
        output.WriteLine($"{run.StopReason} nach {run.TurnsSpent} Turns, {run.MeatGained:N0} Meat" +
                         (run.Detail is { Length: > 0 } d ? $" ({d})" : ""));
        foreach (var item in run.Items) output.WriteLine($"  + {item.Quantity} x {item.Name}");
        if (run.Open is ChoiceEncounter choice)
            output.WriteLine($"Offene Choice {choice.ChoiceId}: " + string.Join(", ", choice.Options.Select(o => $"{o.Option}={o.Text}")));
        return run.StopReason is RunStopReason.TurnsDone or RunStopReason.Condition ? 0 : 1;
    }
}
