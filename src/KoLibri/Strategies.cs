using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CoreSpiel = KoLibri.Core.Spiel;

namespace KoLibri
{
    public enum CombatActionKind
    {
        Attack,
        Skill,
        Item,
        RunAway,
        /// <summary>Experimentell in 0.5: der Läufer lehnt Makros mit NotSupportedException ab, bis sie live geprüft sind.</summary>
        [Experimental(KolibriDiagnostics.CombatMacros)]
        Macro,
        Stop
    }

    /// <summary>Diagnose-Ids für [Experimental]-APIs. Nutzer schalten sie per NoWarn bewusst frei.</summary>
    public static class KolibriDiagnostics
    {
        /// <summary>KoL-Combat-Makros: angelegt, aber vor dem Live-Test nicht freigeschaltet.</summary>
        public const string CombatMacros = "KOLIBRI0001";
    }

    public sealed record CombatAction(CombatActionKind Kind, int? Id = null, int? SecondId = null, string? Macro = null)
    {
        public static CombatAction Attack { get; } = new(CombatActionKind.Attack);
        public static CombatAction RunAway { get; } = new(CombatActionKind.RunAway);
        /// <summary>Kampf an den Menschen zurückgeben (Browser/Encounter-Ansicht).</summary>
        public static CombatAction Stop { get; } = new(CombatActionKind.Stop);
        public static CombatAction Skill(SkillRef skill) => new(CombatActionKind.Skill, skill.Id);
        public static CombatAction Item(ItemRef item, ItemRef? second = null) => new(CombatActionKind.Item, item.Id, second?.Id);
        /// <summary>KoL-Combat-Makro, wird über fight.php?action=macro geschickt. Experimentell in 0.5.</summary>
        [Experimental(KolibriDiagnostics.CombatMacros)]
        public static CombatAction FromMacro(string macro) => new(CombatActionKind.Macro, Macro: macro);
    }

    /// <summary>Was eine Strategie pro Runde sieht. Reiner Schnappschuss, keine Requests.</summary>
    public sealed record CombatContext(FightEncounter Fight, int Round, int Hp, int MaxHp, int Mp, int MaxMp, IReadOnlyList<FightRound> History)
    {
        public IReadOnlyList<ActiveEffect> Effects { get; init; } = Array.Empty<ActiveEffect>();
        public Inventory? Inventory { get; init; }
    }

    /// <summary>Wählt die nächste Kampfaktion. Analog zu IChoiceResolver: synchron, rein, ohne Requests.</summary>
    public interface ICombatStrategy
    {
        CombatAction Next(CombatContext context);
    }

    public static class CombatStrategies
    {
        public static ICombatStrategy AttackOnly { get; } = new DelegateStrategy(_ => CombatAction.Attack);

        /// <summary>Jede Runde dasselbe Makro (KoL wertet es serverseitig aus). Experimentell in 0.5, siehe KolibriDiagnostics.CombatMacros.</summary>
        [Experimental(KolibriDiagnostics.CombatMacros)]
        public static ICombatStrategy Macro(string macro)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(macro);
            var action = CombatAction.FromMacro(macro);
            return new DelegateStrategy(_ => action);
        }

        /// <summary>Regel-DSL von 0.4 (AutoCombatPolicy.Parse), z.B. "if hp &lt; 30% then run; skill 1005 x3; attack".</summary>
        public static ICombatStrategy FromRules(string rules)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rules);
            CoreSpiel.AutoCombatPolicy policy;
            try
            {
                policy = CoreSpiel.AutoCombatPolicy.Parse(rules);
                policy.Validate();
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
            {
                throw new ArgumentException($"Invalid combat rules: {exception.Message}", nameof(rules), exception);
            }
            return new RulesStrategy(policy);
        }

        public static ICombatStrategy FromDelegate(Func<CombatContext, CombatAction> next)
        {
            ArgumentNullException.ThrowIfNull(next);
            return new DelegateStrategy(next);
        }

        private sealed class DelegateStrategy(Func<CombatContext, CombatAction> next) : ICombatStrategy
        {
            public CombatAction Next(CombatContext context) => next(context);
        }

        /// <summary>
        /// Adapter over the 0.4 policy. It stays pure: rule usage ("skill 1005 x3") is rebuilt from the round
        /// history on every call by replaying the policy, nothing is stored between calls.
        /// </summary>
        private sealed class RulesStrategy(CoreSpiel.AutoCombatPolicy policy) : ICombatStrategy
        {
            public CombatAction Next(CombatContext context)
            {
                var usage = new CoreSpiel.AutoCombatUsage();
                for (var i = 0; i < context.History.Count; i++)
                {
                    var past = policy.Evaluate(Context(context, i + 1, i == 0 ? context.Hp : context.History[i - 1].Hp,
                        i == 0 ? context.Mp : context.History[i - 1].Mp), usage);
                    usage = usage.After(past);
                }
                var decision = policy.Evaluate(Context(context, context.Round, context.Hp, context.Mp), usage);
                var id = int.TryParse(decision.Action.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;
                return decision.Action.Kind switch
                {
                    CoreSpiel.AutoCombatActionKind.Attack => CombatAction.Attack,
                    CoreSpiel.AutoCombatActionKind.Skill when id is { } skill => CombatAction.Skill(skill),
                    CoreSpiel.AutoCombatActionKind.Item when id is { } item => CombatAction.Item(item),
                    CoreSpiel.AutoCombatActionKind.Run => CombatAction.RunAway,
                    _ => CombatAction.Stop
                };
            }

            private static CoreSpiel.AutoCombatContext Context(CombatContext context, int round, int hp, int mp) => new()
            {
                Round = round,
                Hp = hp,
                MaxHp = context.MaxHp,
                Mp = mp,
                MaxMp = context.MaxMp,
                Monster = context.Fight.MonsterName,
                Effects = context.Effects.Select(e => e.Name).ToArray()
            };
        }
    }

    public sealed record ChoiceContext(Location? Location, int ChoiceId, string? Title, IReadOnlyList<ChoiceOption> Options);

    public enum ChoiceDecisionSource { Configured, BuiltIn, Strategy, None }

    public sealed record ChoiceDecision(int? Option, ChoiceDecisionSource Source, string Reason)
    {
        /// <summary>Keine Entscheidung: der Lauf stoppt mit RunStopReason.NeedsChoice.</summary>
        public static ChoiceDecision Ask(string reason) => new(null, ChoiceDecisionSource.None, reason);
    }

    /// <summary>Wählt eine Option für eine Choice. Synchron, rein, ohne Requests.</summary>
    public interface IChoiceResolver
    {
        ChoiceDecision Resolve(ChoiceContext context);
    }

    public static class ChoiceResolvers
    {
        /// <summary>Löst nie auf; jeder Choice stoppt den Lauf.</summary>
        public static IChoiceResolver AlwaysAsk { get; } = new CoreResolver(new CoreSpiel.ChoiceResolutionConfig());

        /// <summary>Feste Optionen je Choice-Id; unbekannte Choices stoppen.</summary>
        public static IChoiceResolver FromTable(IReadOnlyDictionary<int, int> optionByChoiceId)
        {
            ArgumentNullException.ThrowIfNull(optionByChoiceId);
            return new CoreResolver(Config(CoreSpiel.ChoiceResolutionMode.Ask, optionByChoiceId));
        }

        /// <summary>Tabelle plus eingebaute kostenfreie Optionen (heute ChoiceResolutionMode.AutoSafe).</summary>
        public static IChoiceResolver Safe(IReadOnlyDictionary<int, int>? optionByChoiceId = null)
            => new CoreResolver(Config(CoreSpiel.ChoiceResolutionMode.AutoSafe, optionByChoiceId ?? new Dictionary<int, int>()));

        private static CoreSpiel.ChoiceResolutionConfig Config(CoreSpiel.ChoiceResolutionMode mode, IReadOnlyDictionary<int, int> table) => new()
        {
            Mode = mode,
            Choices = table.ToDictionary(
                kv => kv.Key.ToString(CultureInfo.InvariantCulture),
                kv => new CoreSpiel.ChoiceDefaultEntry { Option = kv.Value },
                StringComparer.Ordinal)
        };

        /// <summary>Adapter over the 0.4 resolver; a fresh one per call keeps it pure.</summary>
        private sealed class CoreResolver(CoreSpiel.ChoiceResolutionConfig config) : IChoiceResolver
        {
            public ChoiceDecision Resolve(ChoiceContext context)
            {
                ArgumentNullException.ThrowIfNull(context);
                var request = new CoreSpiel.ChoiceResolutionRequest(
                    context.Location?.Snarfblat.ToString(CultureInfo.InvariantCulture) ?? "choice",
                    context.ChoiceId.ToString(CultureInfo.InvariantCulture),
                    context.Title,
                    context.Options.Select(o => new CoreSpiel.ChoiceOptionInfo(o.Option, o.Text)).ToArray());
                var result = new CoreSpiel.ChoiceResolver(config).Resolve(request);
                return result.Option is { } option
                    ? new ChoiceDecision(option, result.Source == CoreSpiel.ChoiceResolutionSource.Configured
                        ? ChoiceDecisionSource.Configured : ChoiceDecisionSource.BuiltIn, result.Reason)
                    : ChoiceDecision.Ask(result.Reason);
            }
        }
    }
}
