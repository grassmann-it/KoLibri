namespace KoLibri
{
    public enum RecoveryStepKind { None, Rest, UseItem, Cast, Stop, NeedInventory }

    /// <summary>Ein Schritt der Erholung zwischen zwei Zügen. Werte entstehen über die statischen Fabriken.</summary>
    public sealed record RecoveryStep(RecoveryStepKind Kind, int Id = 0, int Times = 1, string? Reason = null)
    {
        /// <summary>Nichts (mehr) zu tun.</summary>
        public static RecoveryStep None { get; } = new(RecoveryStepKind.None);
        /// <summary>Einmal im Campground ausruhen.</summary>
        public static RecoveryStep Rest { get; } = new(RecoveryStepKind.Rest);
        /// <summary>Die Policy braucht das Inventar: der Läufer lädt es einmal und fragt erneut. Wird nur mit Inventar im Kontext nicht mehr gebraucht.</summary>
        public static RecoveryStep NeedsInventory { get; } = new(RecoveryStepKind.NeedInventory);
        public static RecoveryStep UseItem(ItemRef item) => new(RecoveryStepKind.UseItem, item.Id);
        public static RecoveryStep Cast(SkillRef skill, int times = 1)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(times, 1);
            return new RecoveryStep(RecoveryStepKind.Cast, skill.Id, times);
        }
        /// <summary>Der Lauf endet mit <see cref="RunStopReason.Recovery"/> und diesem Grund.</summary>
        public static RecoveryStep Stop(string reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            return new RecoveryStep(RecoveryStepKind.Stop, 0, 1, reason);
        }
    }

    /// <summary>Was die Policy sieht: aktueller Status, Inventar (nur nach <see cref="RecoveryStep.NeedsInventory"/>) und die Schritte dieses Zwischen-Zugs.</summary>
    public sealed record RecoveryContext(Status Status, Inventory? Inventory, IReadOnlyList<RecoveryStep> DoneThisTurn);

    /// <summary>Synchron und rein: entscheidet vor jedem neuen Zug den nächsten Erholungsschritt.</summary>
    public interface IRecoveryPolicy
    {
        RecoveryStep Next(RecoveryContext context);
    }

    public static class RecoveryPolicies
    {
        /// <summary>Keine Erholung (Standard).</summary>
        public static IRecoveryPolicy None { get; } = new NonePolicy();

        /// <summary>
        /// Erholt, solange HP (MP) unter dem Prozentwert liegen. Die Schritte sind nach Priorität geordnet;
        /// ein Item-Schritt zählt nur, wenn das Item im Inventar liegt. Ist kein Schritt nutzbar, endet der Lauf mit <see cref="RunStopReason.Recovery"/>.
        /// Ein Prozentwert von 0 schaltet die jeweilige Erholung ab.
        /// </summary>
        public static IRecoveryPolicy Simple(int hpBelowPercent, int mpBelowPercent, IReadOnlyList<RecoveryStep> hpSteps, IReadOnlyList<RecoveryStep> mpSteps)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(hpBelowPercent, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(hpBelowPercent, 100);
            ArgumentOutOfRangeException.ThrowIfLessThan(mpBelowPercent, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(mpBelowPercent, 100);
            ArgumentNullException.ThrowIfNull(hpSteps);
            ArgumentNullException.ThrowIfNull(mpSteps);
            return new SimplePolicy(hpBelowPercent, mpBelowPercent, hpSteps.ToArray(), mpSteps.ToArray());
        }

        /// <summary>Hält den Effekt: liegt er unter <paramref name="minTurns"/> Zügen (oder fehlt), wird <paramref name="how"/> einmal pro Zwischen-Zug ausgeführt.</summary>
        public static IRecoveryPolicy Upkeep(int effectId, int minTurns, RecoveryStep how)
        {
            ArgumentNullException.ThrowIfNull(how);
            ArgumentOutOfRangeException.ThrowIfLessThan(minTurns, 1);
            if (how.Kind is not (RecoveryStepKind.Rest or RecoveryStepKind.UseItem or RecoveryStepKind.Cast))
                throw new ArgumentException("An upkeep step must be Rest, UseItem or Cast.", nameof(how));
            return new UpkeepPolicy(effectId, minTurns, how);
        }

        /// <summary>Fragt die Policies der Reihe nach; der erste Schritt, der nicht <see cref="RecoveryStepKind.None"/> ist, gilt.</summary>
        public static IRecoveryPolicy Combine(params IRecoveryPolicy[] policies)
        {
            ArgumentNullException.ThrowIfNull(policies);
            return new CombinedPolicy(policies.ToArray());
        }

        private sealed class NonePolicy : IRecoveryPolicy
        {
            public RecoveryStep Next(RecoveryContext context) => RecoveryStep.None;
        }

        private sealed class SimplePolicy : IRecoveryPolicy
        {
            private readonly int _hp, _mp;
            private readonly RecoveryStep[] _hpSteps, _mpSteps;

            public SimplePolicy(int hp, int mp, RecoveryStep[] hpSteps, RecoveryStep[] mpSteps)
            {
                _hp = hp; _mp = mp; _hpSteps = hpSteps; _mpSteps = mpSteps;
            }

            public RecoveryStep Next(RecoveryContext context)
            {
                var s = context.Status;
                if (_hp > 0 && s.MaxHp > 0 && 100L * s.Hp < (long)_hp * s.MaxHp)
                    return Pick(_hpSteps, context, $"HP is below {_hp}% and no recovery step is usable.");
                if (_mp > 0 && s.MaxMp > 0 && 100L * s.Mp < (long)_mp * s.MaxMp)
                    return Pick(_mpSteps, context, $"MP is below {_mp}% and no recovery step is usable.");
                return RecoveryStep.None;
            }

            private static RecoveryStep Pick(RecoveryStep[] steps, RecoveryContext context, string none)
            {
                foreach (var step in steps)
                {
                    switch (step.Kind)
                    {
                        case RecoveryStepKind.UseItem:
                            if (context.Inventory is null) return RecoveryStep.NeedsInventory;
                            if (context.Inventory.Count(new ItemRef(step.Id)) > 0) return step;
                            break;
                        case RecoveryStepKind.Rest:
                        case RecoveryStepKind.Cast:
                        case RecoveryStepKind.Stop:
                            return step;
                    }
                }
                return RecoveryStep.Stop(none);
            }
        }

        private sealed class UpkeepPolicy : IRecoveryPolicy
        {
            private readonly int _effectId, _minTurns;
            private readonly RecoveryStep _how;

            public UpkeepPolicy(int effectId, int minTurns, RecoveryStep how)
            {
                _effectId = effectId; _minTurns = minTurns; _how = how;
            }

            public RecoveryStep Next(RecoveryContext context)
            {
                if (context.DoneThisTurn.Contains(_how)) return RecoveryStep.None;
                var turns = context.Status.Effects.Where(e => e.Id == _effectId).Select(e => e.Turns).DefaultIfEmpty(0).Max();
                if (turns >= _minTurns) return RecoveryStep.None;
                if (_how.Kind == RecoveryStepKind.UseItem)
                {
                    if (context.Inventory is null) return RecoveryStep.NeedsInventory;
                    if (context.Inventory.Count(new ItemRef(_how.Id)) <= 0)
                        return RecoveryStep.Stop($"Effect {_effectId} has {turns} turns left (minimum {_minTurns}) and item {_how.Id} is not in the inventory.");
                }
                return _how;
            }
        }

        private sealed class CombinedPolicy : IRecoveryPolicy
        {
            private readonly IRecoveryPolicy[] _policies;
            public CombinedPolicy(IRecoveryPolicy[] policies) => _policies = policies;

            public RecoveryStep Next(RecoveryContext context)
            {
                foreach (var policy in _policies)
                {
                    var step = policy.Next(context);
                    if (step.Kind != RecoveryStepKind.None) return step;
                }
                return RecoveryStep.None;
            }
        }
    }
}
