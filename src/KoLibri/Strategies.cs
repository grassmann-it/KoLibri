using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
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

        /// <summary>Skills, die das Dropdown der Kampfseite gerade anbietet. Leer = die Seite hat keine Liste gezeigt (dann nichts ausschließen).</summary>
        public IReadOnlyList<CombatOption> AvailableSkills { get; init; } = Array.Empty<CombatOption>();

        /// <summary>Kampf-Items, die das Dropdown der Kampfseite gerade anbietet. Leer = keine Liste gezeigt.</summary>
        public IReadOnlyList<CombatOption> AvailableItems { get; init; } = Array.Empty<CombatOption>();

        /// <summary>KoL-Monster-Id der Kampfseite, falls sie eine nennt.</summary>
        public int? MonsterId => Fight.MonsterId;

        /// <summary>Turtle-Tamer-Monster (Name enthält turtle/tortoise/terrapin). Nur Markierung, gezähmt wird nie automatisch.</summary>
        public bool IsTurtleMonster => CoreSpiel.TurtleTamerEncounters.IsTurtleMonster(Fight.MonsterName);
    }

    /// <summary>Ein Eintrag aus dem Skill- oder Item-Dropdown der Kampfseite. MpCost ist null, wenn weder Seite noch Spieldaten ihn kennen.</summary>
    public sealed record CombatOption(int Id, string Name, int? MpCost);

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

        /// <summary>
        /// Regel-DSL von 0.4 (AutoCombatPolicy.Parse), z.B. "if hp &lt; 30% then run; skill 1005; attack".
        /// Skills und Items nur per Id; Namen lösen nur die Überladung mit <see cref="GameData"/> auf.
        /// </summary>
        public static ICombatStrategy FromRules(string rules) => Build(rules, null);

        /// <summary>
        /// Wie <see cref="FromRules(string)"/>, aber Skills und Items dürfen per Name stehen:
        /// <c>if monster "X" then skill "Name"; attack</c>. Ein unbekannter Name wirft eine ArgumentException,
        /// bevor ein Request geschickt wird. Fehlen die Spieldaten ganz, sagt die Meldung das.
        /// Zur Laufzeit wählt die Strategie nie einen Skill/ein Item, das die Kampfseite nicht anbietet
        /// oder dessen MP-Kosten die aktuellen MP übersteigen: dann gilt die nächste passende Regel, sonst Stop.
        /// </summary>
        public static ICombatStrategy FromRules(string rules, GameData data)
        {
            ArgumentNullException.ThrowIfNull(data);
            return Build(rules, data);
        }

        /// <summary>Gibt bei einem Turtle-Monster den Kampf an den Menschen zurück (Stop), sonst entscheidet <paramref name="inner"/>.</summary>
        public static ICombatStrategy StopOnTurtle(ICombatStrategy inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            return new DelegateStrategy(context => context.IsTurtleMonster ? CombatAction.Stop : inner.Next(context));
        }

        private static readonly Regex QuotedName = new(
            @"\b(?<kind>skill|cast|item|use)\s+(?<q>[""'])(?<name>.+?)\k<q>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // A bare action ("...; attack") reads as an unconditional rule: `true then attack`.
        private static readonly Regex BareAction = new(
            @"(?<lead>^|[;\n]\s*)(?<action>(?:attack|hit|skill|cast|item|use|run|flee)\b(?:(?!\bthen\b|->)[^;\n])*)(?=$|[;\n])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static RulesStrategy Build(string rules, GameData? data)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rules);
            CoreSpiel.AutoCombatPolicy policy;
            try
            {
                // `skill "Name With Spaces"` -> `skill(Name With Spaces)`: the 0.4 parser reads one bare word otherwise.
                var text = rules.TrimStart().StartsWith('{') || rules.TrimStart().StartsWith('[')
                    ? rules
                    : BareAction.Replace(QuotedName.Replace(rules, m => $"{m.Groups["kind"].Value}({m.Groups["name"].Value})"),
                        m => $"{m.Groups["lead"].Value}true then {m.Groups["action"].Value}");
                policy = CoreSpiel.AutoCombatPolicy.Parse(text);
                policy.Validate();
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
            {
                throw new ArgumentException($"Invalid combat rules: {exception.Message}", nameof(rules), exception);
            }
            if (data is not null) policy = ResolveNames(policy, data);
            return new RulesStrategy(policy, data);
        }

        private static CoreSpiel.AutoCombatPolicy ResolveNames(CoreSpiel.AutoCombatPolicy policy, GameData data)
        {
            var loaded = new Lazy<bool>(() => DataLoaded(data));
            CoreSpiel.AutoCombatAction Resolve(CoreSpiel.AutoCombatAction action)
            {
                if (action.Kind is not (CoreSpiel.AutoCombatActionKind.Skill or CoreSpiel.AutoCombatActionKind.Item)) return action;
                var name = action.Id!;
                if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return action;
                var isSkill = action.Kind == CoreSpiel.AutoCombatActionKind.Skill;
                if (!loaded.Value)
                    throw new ArgumentException(
                        $"Spieldaten fehlen: '{name}' kann nicht in eine Id aufgelöst werden, weil keine KoLmafia-Daten geladen sind "
                        + "(external/kolmafia-data nicht gefunden, siehe ClientOptions.DataDirectory). Mit Ids statt Namen geht es ohne Daten.", "rules");
                var id = isSkill ? data.FindSkill(name)?.Skill.Id : data.FindItem(name)?.Item.Id;
                if (id is null)
                    throw new ArgumentException($"Unbekannter {(isSkill ? "Skill" : "Item")} '{name}' in den Kampfregeln.", "rules");
                return action with { Id = id.Value.ToString(CultureInfo.InvariantCulture) };
            }
            return policy with
            {
                Rules = policy.Rules.Select(r => r with { Action = Resolve(r.Action) }).ToArray(),
                DefaultAction = policy.DefaultAction is { } d ? Resolve(d) : null
            };
        }

        // GameData loads silently empty when the data folder is missing; probe two names that exist in every dump.
        // (Replace with GameData.IsLoaded once the data package offers it.)
        private static bool DataLoaded(GameData data) => data.FindItem("seal tooth") is not null || data.FindSkill("Toss") is not null;

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
        /// A rule whose skill/item the fight page does not offer (or whose MP cost is too high) is skipped
        /// like a rule whose condition does not hold; if nothing else matches the answer is Stop, never a blind request.
        /// </summary>
        private sealed class RulesStrategy(CoreSpiel.AutoCombatPolicy policy, GameData? data) : ICombatStrategy
        {
            public CombatAction Next(CombatContext context)
            {
                var usage = new CoreSpiel.AutoCombatUsage();
                for (var i = 0; i < context.History.Count; i++)
                {
                    var past = Decide(context, Context(context, i + 1, i == 0 ? context.Hp : context.History[i - 1].Hp,
                        i == 0 ? context.Mp : context.History[i - 1].Mp), usage);
                    usage = usage.After(past);
                }
                var decision = Decide(context, Context(context, context.Round, context.Hp, context.Mp), usage);
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

            /// <summary>Evaluates the policy; rules that cannot be played right now are disabled and the evaluation repeated.</summary>
            private CoreSpiel.AutoCombatDecision Decide(CombatContext context, CoreSpiel.AutoCombatContext state, CoreSpiel.AutoCombatUsage usage)
            {
                var current = policy;
                for (var attempt = 0; attempt <= policy.Rules.Count; attempt++)
                {
                    var decision = current.Evaluate(state, usage);
                    if (decision.Action.Kind is not (CoreSpiel.AutoCombatActionKind.Skill or CoreSpiel.AutoCombatActionKind.Item)
                        || Playable(context, decision.Action, state.Mp))
                        return decision;
                    if (decision.RuleIndex is not { } index) return StopDecision(decision);
                    current = current with
                    {
                        Rules = current.Rules.Select((r, i) => i == index ? r with { Enabled = false } : r).ToArray()
                    };
                }
                return StopDecision(current.Evaluate(state, usage));
            }

            // A default action that cannot be played has no rule to disable: hand the fight back.
            private static CoreSpiel.AutoCombatDecision StopDecision(CoreSpiel.AutoCombatDecision decision) => decision with
            {
                Action = CoreSpiel.AutoCombatAction.Manual("Skill or item is not available on the fight page."),
                Status = CoreSpiel.AutoCombatDecisionStatus.Manual
            };

            private bool Playable(CombatContext context, CoreSpiel.AutoCombatAction action, int mp)
            {
                if (!int.TryParse(action.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return true;
                if (action.Kind == CoreSpiel.AutoCombatActionKind.Item)
                    return context.AvailableItems.Count == 0 || context.AvailableItems.Any(o => o.Id == id);
                var offered = context.AvailableSkills.Count == 0 ? null : context.AvailableSkills.FirstOrDefault(o => o.Id == id);
                if (context.AvailableSkills.Count > 0 && offered is null) return false;
                var cost = offered?.MpCost ?? data?.Skill(new SkillRef(id))?.MpCost;
                return cost is not { } needed || needed <= mp;
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
