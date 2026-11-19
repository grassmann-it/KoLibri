using KolArbitrage.Core.Adventure;

namespace KolArbitrage.Core.Data;

/// <summary>
/// Typed action the operator (or future AutoCombat) picks for the next
/// bounded adventure step. Designed so manual and auto paths share one
/// contract; <see cref="AutoCombatStepSpecMapper"/> translates an
/// <see cref="AutoCombatAction"/> into the same shape.
/// </summary>
public abstract record AdventureStepSpec
{
    public abstract AdventureKernelAction ToKernelAction(string adventureId);
}

public sealed record AttackStepSpec : AdventureStepSpec
{
    public override AdventureKernelAction ToKernelAction(string adventureId)
        => new AdventureFightAction(adventureId, "attack", 0);
}

public sealed record SkillStepSpec(string SkillId, string? MonsterId = null, int? Round = null) : AdventureStepSpec
{
    public override AdventureKernelAction ToKernelAction(string adventureId)
        => new AdventureFightAction(adventureId, $"skill:{SkillId}", Round ?? 0, null, MonsterId);
}

public sealed record ItemStepSpec(string ItemId, int Quantity = 1, string? MonsterId = null, int? Round = null) : AdventureStepSpec
{
    public override AdventureKernelAction ToKernelAction(string adventureId)
    {
        // AdventureFightAction does not accept an extra quantity field directly;
        // the kernel encodes the quantity in the item prefix.
        var quantity = Math.Max(1, Quantity);
        var prefix = quantity > 1 ? $"item:{ItemId}:{quantity}" : $"item:{ItemId}";
        return new AdventureFightAction(adventureId, prefix, Round ?? 0, null, MonsterId);
    }
}

public sealed record RunStepSpec : AdventureStepSpec
{
    public override AdventureKernelAction ToKernelAction(string adventureId)
        => new AdventureFightAction(adventureId, "run", 0);
}

public sealed record ChoiceStepSpec(string ChoiceId, string OptionId, IReadOnlyDictionary<string, string>? ExtraFields = null) : AdventureStepSpec
{
    public override AdventureKernelAction ToKernelAction(string adventureId)
        => new AdventureChoiceAction(adventureId, ChoiceId, OptionId, null, ExtraFields);
}

/// <summary>
/// Maps an <see cref="AutoCombatAction"/> into an
/// <see cref="AdventureStepSpec"/> so a future AutoCombat runner can
/// drive the same manual-session state machine.
/// </summary>
public static class AutoCombatStepSpecMapper
{
    public static AdventureStepSpec? TryMap(string adventureId, AutoCombatAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Kind switch
        {
            AutoCombatActionKind.Attack => new AttackStepSpec(),
            AutoCombatActionKind.Skill when !string.IsNullOrWhiteSpace(action.Id)
                => new SkillStepSpec(action.Id!),
            AutoCombatActionKind.Item when !string.IsNullOrWhiteSpace(action.Id)
                => new ItemStepSpec(action.Id!, Quantity: action.Quantity > 0 ? action.Quantity : 1),
            AutoCombatActionKind.Run => new RunStepSpec(),
            _ => null
        };
    }
}
