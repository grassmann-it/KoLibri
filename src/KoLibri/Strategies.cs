using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
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
        /// <summary>
        /// Experimentell in 0.5: der Läufer schickt Makros nur mit <see cref="KolibriOptions.EnableExperimentalMacros"/> = true,
        /// sonst NotSupportedException (KOLIBRI0001), bis sie live geprüft sind.
        /// </summary>
        [Experimental(KolibriDiagnostics.CombatMacros)]
        Macro,
        Stop
    }

    /// <summary>Diagnose-Ids für [Experimental]-APIs. Nutzer schalten sie per NoWarn bewusst frei.</summary>
    public static class KolibriDiagnostics
    {
        /// <summary>KoL-Combat-Makros: Sendepfad vorhanden, läuft nur mit KolibriOptions.EnableExperimentalMacros (vor dem Live-Test).</summary>
        public const string CombatMacros = "KOLIBRI0001";
    }

    public sealed record CombatAction(CombatActionKind Kind, int? Id = null, int? SecondId = null, string? Macro = null)
    {
        // Keep the decision context on this action instance without changing record equality.
        private static readonly ConditionalWeakTable<CombatAction, CombatContext> DecisionContexts = new();
        internal CombatContext? Before => DecisionContexts.TryGetValue(this, out var context) ? context : null;

        internal CombatAction WithContext(CombatContext context)
        {
            var action = this with { };
            DecisionContexts.Add(action, context with { History = Array.Empty<FightRound>() });
            return action;
        }

        public static CombatAction Attack { get; } = new(CombatActionKind.Attack);
        public static CombatAction RunAway { get; } = new(CombatActionKind.RunAway);
        /// <summary>Kampf an den Menschen zurückgeben (Browser/Encounter-Ansicht).</summary>
        public static CombatAction Stop { get; } = new(CombatActionKind.Stop);
        public static CombatAction Skill(SkillRef skill) => new(CombatActionKind.Skill, skill.Id);
        public static CombatAction Item(ItemRef item, ItemRef? second = null) => new(CombatActionKind.Item, item.Id, second?.Id);
        /// <summary>
        /// KoL-Combat-Makro, wird über fight.php (action=macro, macrotext=…) geschickt. Experimentell in 0.5:
        /// läuft nur mit <see cref="KolibriOptions.EnableExperimentalMacros"/>. Leere oder zu lange Makros wirft schon hier eine ArgumentException.
        /// </summary>
        [Experimental(KolibriDiagnostics.CombatMacros)]
        public static CombatAction FromMacro(string macro) => new(CombatActionKind.Macro, Macro: CombatMacroText.Normalize(macro));
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

        /// <summary>
        /// Jede Runde dasselbe Makro (KoL wertet es serverseitig aus, eine Antwort kann mehrere Runden enthalten).
        /// Experimentell in 0.5: läuft nur mit <see cref="KolibriOptions.EnableExperimentalMacros"/>, siehe KolibriDiagnostics.CombatMacros.
        /// Bricht KoL das Makro ab (Kampf bleibt offen), schickt FightToEndAsync dasselbe Makro nicht noch einmal, sondern endet mit Stopped.
        /// </summary>
        [Experimental(KolibriDiagnostics.CombatMacros)]
        public static ICombatStrategy Macro(string macro)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(macro);
#pragma warning disable KOLIBRI0001
            var action = CombatAction.FromMacro(macro);
#pragma warning restore KOLIBRI0001
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
        /// Adapter over the 0.4 policy. The actions sent determine the budget. The recorded pre-round
        /// context identifies which rule selected each action.
        /// A rule whose skill/item the fight page does not offer (or whose MP cost is too high) is skipped
        /// like a rule whose condition does not hold; if nothing else matches the answer is Stop, never a blind request.
        /// </summary>
        private sealed class RulesStrategy(CoreSpiel.AutoCombatPolicy policy, GameData? data) : ICombatStrategy
        {
            public CombatAction Next(CombatContext context)
            {
                var usage = new CoreSpiel.AutoCombatUsage();
                foreach (var round in context.History)
                {
                    var ruleId = RuleFor(round, usage);
                    var kind = round.Action.Kind switch
                    {
                        CombatActionKind.Skill => CoreSpiel.AutoCombatActionKind.Skill,
                        CombatActionKind.Item => CoreSpiel.AutoCombatActionKind.Item,
                        CombatActionKind.RunAway => CoreSpiel.AutoCombatActionKind.Run,
                        _ => CoreSpiel.AutoCombatActionKind.Attack
                    };
                    usage = usage.After(new CoreSpiel.AutoCombatDecision { Action = new(kind), RuleId = ruleId });
                }
                return ToAction(Decide(context, Context(context), usage).Action).WithContext(context);
            }

            private string? RuleFor(FightRound round, CoreSpiel.AutoCombatUsage usage)
            {
                if (round.Before is { } before)
                {
                    var decision = Decide(before, Context(before), usage);
                    return ToAction(decision.Action) == round.Action ? decision.RuleId : null;
                }

                // Callers can also supply their own history. Attribute the action only when it identifies one rule.
                if (policy.DefaultAction is { } fallback && ToAction(fallback) == round.Action) return null;
                var matches = policy.Rules.Where(rule => rule.Enabled && ToAction(rule.Action) == round.Action).Take(2).ToArray();
                return matches.Length == 1 ? matches[0].Id?.Trim() : null;
            }

            private static CombatAction ToAction(CoreSpiel.AutoCombatAction action)
            {
                var id = int.TryParse(action.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;
                return action.Kind switch
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

            private static CoreSpiel.AutoCombatContext Context(CombatContext context) => new()
            {
                Round = context.Round,
                Hp = context.Hp,
                MaxHp = context.MaxHp,
                Mp = context.Mp,
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

    /// <summary>
    /// Makrotext wie KoLmafia ihn vor dem Senden verdichtet (FightRequest.handleMacroAction, MACRO_COMPACT_PATTERN):
    /// Kommentare (#…) bis zum nächsten Trenner weg, aufeinanderfolgende Trenner zusammengefasst, außen getrimmt.
    /// Ein leeres Makro würde KoL mit "Invalid macro" beantworten, darum wird es hier abgelehnt.
    /// </summary>
    internal static class CombatMacroText
    {
        /// <summary>KoLibri-eigene Obergrenze (KoLs genaue Grenze ist nicht belegt; KoLmafia prüft keine). Im Live-Test nachschärfen.</summary>
        internal const int MaxLength = 10_000;

        private static readonly Regex Compact = new(@"(?:#.*?)?([;\n])[\s;\n]*", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        internal static string Normalize(string macro)
        {
            ArgumentNullException.ThrowIfNull(macro);
            var text = Compact.Replace(macro.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'), "$1").Trim();
            if (text.Length == 0 || text.All(c => c is ';' or '\n'))
                throw new ArgumentException("The combat macro is empty (after removing comments); KoL would answer \"Invalid macro\".", nameof(macro));
            if (text.Length > MaxLength)
                throw new ArgumentException($"The combat macro has {text.Length} characters; KoLibri sends at most {MaxLength}.", nameof(macro));
            return text;
        }

        /// <summary>Kurzform für GuardedAction.Summary und Logs: Anfang des Makros plus Hash, nie der volle Text.</summary>
        internal static string Summary(string? macro)
        {
            var text = macro ?? "";
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();
            var head = text.Replace('\n', ' ');
            if (head.Length > 32) head = head[..32] + "…";
            return $"macro \"{head}\" #{hash} ({text.Length} chars)";
        }
    }
}
