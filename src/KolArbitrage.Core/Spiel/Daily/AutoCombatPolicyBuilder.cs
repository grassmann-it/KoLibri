
namespace KolArbitrage.Core.Spiel;

/// <summary>
/// Builds a deterministic combat policy from the operator's choices:
/// the skills to use, in order, the items to spend and what to do when
/// the skills run out.
///
/// The rules are ordered, so the first matching rule wins — that is how
/// the operator says "use this skill first, then that one, and attack
/// only when nothing else is left".
/// </summary>
public static class AutoCombatPolicyBuilder
{
    /// <summary>
    /// Builds the policy. The skills are tried in the given order, the
    /// items follow, and attacking is the default when nothing matched.
    /// </summary>
    public static AutoCombatPolicy Build(
        IReadOnlyList<string> skillIds,
        IReadOnlyList<string> itemIds,
        int skillUses = 1,
        int itemUses = 1,
        AutoCombatAction? defaultAction = null)
    {
        ArgumentNullException.ThrowIfNull(skillIds);
        ArgumentNullException.ThrowIfNull(itemIds);
        var rules = new List<AutoCombatRule>();
        foreach (var skill in skillIds.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            rules.Add(new AutoCombatRule
            {
                // An empty predicate matches every round — the rules are
                // ordered, so the first skill that still has uses left wins.
                When = new AutoCombatPredicate(),
                Then = new AutoCombatAction
                {
                    Kind = AutoCombatActionKind.Skill,
                    Id = skill.Trim(),
                    Quantity = 1
                },
                MaxUses = Math.Max(1, skillUses)
            });
        }
        foreach (var item in itemIds.Where(i => !string.IsNullOrWhiteSpace(i)))
        {
            rules.Add(new AutoCombatRule
            {
                When = new AutoCombatPredicate(),
                Then = new AutoCombatAction
                {
                    Kind = AutoCombatActionKind.Item,
                    Id = item.Trim(),
                    Quantity = 1
                },
                MaxUses = Math.Max(1, itemUses)
            });
        }
        return new AutoCombatPolicy
        {
            Rules = rules.ToArray(),
            DefaultAction = defaultAction ?? new AutoCombatAction
            {
                Kind = AutoCombatActionKind.Attack,
                Quantity = 1
            }
        };
    }
}
