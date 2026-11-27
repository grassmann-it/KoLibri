using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KoLibri;

// `kolibri adventure run`: the live adventure loop, on KoLibri's Adventurer (RunAsync / FightToEndAsync / ChooseAsync).
// The other adventure subcommands of 0.4 were previews on the core kernel; they come back in a follow-up PR.
internal static partial class KolibriCli
{
    internal static async Task<int> AdventureCommandAsync(string[] args)
    {
        if (args.Length == 0 || HasFlag(args, "--help") || IsHelp(args[0]))
        {
            PrintAdventureHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "run" or "execute" => await AdventureRunAsync(args[1..]),
            "start" or "begin" or "bounded-preview" or "run-preview" => await AdventureRunAsync(args[1..]),
            "locations" or "location" or "list" => await AdventureLocationsAsync(args[1..]),
            "inspect" or "status" or "resume" => await AdventureInspectAsync(args[1..]),
            "step" or "advance" => await AdventureStepAsync(args[1..]),
            "fight" => await AdventureFightAsync(args[1..]),
            "stop" or "abort" or "choice-chain" or "choicechain" => AdventureRetiredCommand(args[0]),
            _ => Fail($"Unknown adventure command: {args[0]}")
        };
    }

    internal const string DefaultFightAction = "attack";
    internal const int DefaultMaxFightRounds = 30;
    internal const int DefaultMinHpPercent = 20;

    private static readonly Regex SnarfblatInId = new(@"(?:^|[=:])(\d+)(?:[:]|$)", RegexOptions.Compiled);

    /// <summary>"adventure=543:Name" → 543, "113" → 113 (as AdventureLocationIds in 0.4).</summary>
    internal static bool TryGetSnarfblat(string? locationId, out int snarfblat)
    {
        snarfblat = 0;
        if (string.IsNullOrWhiteSpace(locationId)) return false;
        var match = SnarfblatInId.Match(locationId.Trim());
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out snarfblat) && snarfblat > 0;
    }

    /// <summary>--fight-action → combat action: attack, run, skill:ID, item:ID.</summary>
    internal static CombatAction ParseFightAction(string text)
    {
        var value = text.Trim().ToLowerInvariant();
        if (value is "attack") return CombatAction.Attack;
        if (value is "run" or "runaway") return CombatAction.RunAway;
        if (value.StartsWith("skill:", StringComparison.Ordinal) && int.TryParse(value[6..], out var skill) && skill > 0) return CombatAction.Skill(skill);
        if (value.StartsWith("item:", StringComparison.Ordinal) && int.TryParse(value[5..], out var item) && item > 0) return CombatAction.Item(item);
        throw new ArgumentException($"--fight-action '{text}' is not one of attack, run, skill:ID, item:ID. Nothing was sent.");
    }

    /// <summary>The fight rule of 0.4: the first configured action every round, and the fight is handed back below the HP floor.</summary>
    internal sealed class CliCombatStrategy(CombatAction? action, int minHpPercent) : ICombatStrategy
    {
        public string? StopReason { get; private set; }

        public CombatAction Next(CombatContext context)
        {
            if (minHpPercent > 0 && (context.MaxHp <= 0 || 100L * context.Hp < (long)minHpPercent * context.MaxHp))
            {
                StopReason = "HpBelowMinimum";
                return CombatAction.Stop;
            }
            if (action is null)
            {
                StopReason = "UnknownFight";
                return CombatAction.Stop;
            }
            return action;
        }
    }

    /// <summary>Records every automatic choice, like the 0.4 resolver log (autoChoices).</summary>
    internal sealed class LoggingChoiceResolver(IChoiceResolver inner, string location) : IChoiceResolver
    {
        public List<object> Log { get; } = new();

        public ChoiceDecision Resolve(ChoiceContext context)
        {
            var decision = inner.Resolve(context);
            if (decision.Option is { } option)
                Log.Add(new { adventureId = location, choiceId = context.ChoiceId.ToString(CultureInfo.InvariantCulture), title = context.Title, option,
                    source = decision.Source.ToString(), reason = decision.Reason });
            return decision;
        }
    }

    /// <summary>The 0.4 condition file (AdventureCondition JSON) as stop conditions; unsupported kinds stop before anything is sent.</summary>
    internal static IReadOnlyList<StopCondition> ReadStopConditions(JsonElement[] conditions)
    {
        var result = new List<StopCondition>();
        foreach (var c in conditions)
        {
            string Text(string name)
            {
                if (c.ValueKind != JsonValueKind.Object) return "";
                foreach (var p in c.EnumerateObject())
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                        return p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetRawText() : p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "";
                return "";
            }
            var kind = Text("kind").ToLowerInvariant();
            var op = Text("operator").ToLowerInvariant();
            var target = int.TryParse(Text("target"), out var t) ? Math.Max(0, t) : 0;
            var key = Text("key");
            if (kind is "itemcount" && op is "" or "atleast" && int.TryParse(key, out var item) && item > 0)
                result.Add(StopCondition.ItemCountAtLeast(item, target));
            else if (kind is "adventuresremaining" or "turnsremaining" && op is "atmost")
                result.Add(StopCondition.AdventuresAtMost(target));
            else
                throw new ArgumentException($"Condition {c.GetRawText()} is not supported in KoLibri 0.5-dev (supported: ItemCount AtLeast with an item id, AdventuresRemaining AtMost). Nothing was sent.");
        }
        return result;
    }

    private static async Task<int> AdventureRunAsync(string[] args)
    {
        var location = ReadAdventureId(args);
        var runId = GetOption(args, "--run-id") ?? $"cli-{Guid.NewGuid():N}";
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        // An explicit --snarfblat wins; otherwise a catalog id ("adventure=543:Name") supplies it.
        var snarfblat = ReadNullableInt(args, "--snarfblat")
            ?? (TryGetSnarfblat(location, out var catalogSnarfblat) ? catalogSnarfblat : (int?)null);
        var conditionsPath = GetOption(args, "--conditions-file");
        var conditions = conditionsPath is null ? Array.Empty<JsonElement>() : ReadJsonArray<JsonElement>(conditionsPath, "conditions");
        if (!HasFlag(args, "--execute"))
        {
            WriteJson(new
            {
                command = "adventure run",
                readOnly = true,
                previewOnly = true,
                executed = false,
                approvalRequired = true,
                runId,
                location,
                snarfblat,
                role = role.ToString(),
                conditions,
                note = "Preview only. Add --execute and --approval-token to submit allowlisted requests."
            });
            return 0;
        }

        var approvalToken = GetOption(args, "--approval-token");
        if (string.IsNullOrWhiteSpace(approvalToken))
            throw new ArgumentException("Live adventure execution requires --approval-token.");
        if (!await AgentGuard.ApprovalTokenValidAsync(approvalToken).ConfigureAwait(false))
            throw new ArgumentException("The supplied approval token does not match the stored approval token. Nothing was sent.");

        // Everything that can be refused is checked before a session is opened.
        var stopWhen = ReadStopConditions(conditions);
        var fightActions = GetOptions(args, "--fight-action").ToList();
        // Documented default: a fight that opens during the run is fought with "attack" until it ends,
        // bounded by --max-fight-rounds and --min-hp-percent. --no-auto-fight turns this off; the run then
        // stops at the fight and names the command to finish it.
        var autoFight = fightActions.Count == 0 && !HasFlag(args, "--no-auto-fight");
        if (autoFight) fightActions.Add(DefaultFightAction);
        var fightAction = fightActions.Count > 0 ? ParseFightAction(fightActions[0]) : null;
        foreach (var extra in fightActions.Skip(1)) ParseFightAction(extra);
        var choiceTable = new Dictionary<int, int>();
        foreach (var choice in GetOptions(args, "--choice-action"))
        {
            var separator = choice.IndexOf(':');
            if (separator <= 0 || separator == choice.Length - 1
                || !int.TryParse(choice[..separator], out var choiceId) || !int.TryParse(choice[(separator + 1)..], out var optionId))
                throw new ArgumentException("--choice-action expects CHOICE_ID:OPTION_ID.");
            choiceTable[choiceId] = optionId;
        }
        if (snarfblat is not > 0)
            throw new ArgumentException($"No location number (snarfblat) for '{location}'. Pass --snarfblat N. Nothing was sent.");

        var turnsWanted = Math.Max(1, ReadInt(args, "--max-turns", ReadInt(args, "--turns", 1)));
        var loopStarted = DateTimeOffset.UtcNow;
        var loopDeadline = TimeSpan.FromSeconds(Math.Max(1, ReadInt(args, "--max-duration-seconds", 300 * Math.Min(turnsWanted, 100))));
        var minHpPercent = Math.Clamp(ReadInt(args, "--min-hp-percent", DefaultMinHpPercent), 0, 100);
        var maxFightRounds = Math.Max(1, ReadInt(args, "--max-fight-rounds", DefaultMaxFightRounds));
        var resolver = new LoggingChoiceResolver(CreateChoiceResolver(LoadChoicePolicy(), choiceTable), location);

        var (client, session) = await OpenAsync(role);
        await using var _ = client;
        await using var __ = session;
        // Request trail and the pages seen, for the unknown-page archive (0.4 #72/#73).
        var trail = new List<string>();
        session.Events.Request += (_, e) =>
        {
            trail.Add($"{e.Method.ToString().ToUpperInvariant()} {e.Path} -> {e.StatusCode} {e.Kind}");
            if (trail.Count > 20) trail.RemoveAt(0);
        };
        var pages = new List<Page>();
        session.Events.Encounter += (_, e) => { pages.Add(e.Encounter.Page); if (pages.Count > 10) pages.RemoveAt(0); };
        string? unknownPagePath = null;
        var steps = new List<object>();
        var turns = new List<object>();
        string stopReason = "Complete";
        RunResult? result = null;
        string? loopStop = null;
        string? fightStop = null;
        var currentRunId = runId;
        var before = await session.Character.GetStatusAsync(refresh: true);
        for (var turn = 1; turn <= turnsWanted; turn++)
        {
            if (turn > 1) currentRunId = $"{runId}-{turn}";
            var strategy = new CliCombatStrategy(fightAction, minHpPercent);
            result = await session.Adventure.RunAsync(new RunRequest(snarfblat.Value, 1)
            {
                Combat = strategy,
                Choices = resolver,
                StopWhen = stopWhen,
                MaxRoundsPerFight = maxFightRounds
            });
            stopReason = StopReasonName(result, strategy, maxFightRounds);
            if (result.StopReason == RunStopReason.NeedsCombat) fightStop = stopReason;
            foreach (var encounter in result.Encounters)
            {
                string? archived = null;
                if (encounter is BlockedEncounter { Reason: BlockReason.UnknownPage } unknown)
                {
                    var index = pages.FindLastIndex(p => !ReferenceEquals(p, unknown.Page));
                    archived = UnknownPageArchive.TrySave(unknown.Title == "Unreadable fight page" ? "UnknownFight" : "UnknownEncounter",
                        unknown.Page.Html, DateTimeOffset.UtcNow, trail.ToArray(), index >= 0 ? pages[index].Html : null);
                    unknownPagePath = archived ?? unknownPagePath;
                }
                steps.Add(StepDocument(encounter, archived));
            }
            var after = await session.Character.GetStatusAsync(refresh: true);
            turns.Add(new
            {
                turn,
                runId = currentRunId,
                stopReason,
                monster = result.Encounters.OfType<FightEncounter>().Select(f => f.MonsterName).LastOrDefault(),
                adventuresBefore = before.Adventures,
                adventuresAfter = after.Adventures,
                hp = after.MaxHp > 0 ? $"{after.Hp}/{after.MaxHp}" : null
            });
            if (result.StopReason != RunStopReason.TurnsDone) break;
            if (turn == turnsWanted) break;
            if (after.Adventures == before.Adventures)
            { loopStop = "The adventure counter did not change after a completed adventure; stopping instead of guessing."; break; }
            if (after.Adventures <= 0) { loopStop = "No adventures left."; break; }
            if (minHpPercent > 0 && (after.MaxHp <= 0 || 100L * after.Hp < (long)minHpPercent * after.MaxHp))
            { loopStop = $"HP is below {minHpPercent}% (or unknown) before the next adventure."; break; }
            if (DateTimeOffset.UtcNow - loopStarted >= loopDeadline) { loopStop = "The run's time limit was reached."; break; }
            before = after;
        }

        var needs = result?.StopReason == RunStopReason.NeedsChoice && result.Open is ChoiceEncounter open ? open : null;
        object? leftover = null;
        var leftoverFight = false;
        if (needs is null && result?.StopReason is not (RunStopReason.TurnsDone or RunStopReason.NeedsCombat))
        {
            var state = await DetectOpenAsync(session);
            leftoverFight = state.Kind == "Fight";
            leftover = state.IsUnavailable ? new { open = (bool?)null, message = state.Message } : state.IsOpen ? OpenChoiceDocument(state, role) : null;
        }
        var continueCommand = BuildContinueCommand(args, location, role, snarfblat);
        var fightOpen = needs is null ? FightStopDocument(fightStop ?? stopReason, leftoverFight, args, location, role, snarfblat) : null;
        WriteJson(new
        {
            command = "adventure run",
            readOnly = false,
            previewOnly = false,
            executed = true,
            approvalRequired = true,
            runId,
            location,
            role = role.ToString(),
            turnsRequested = turnsWanted,
            turnsDone = turns.Count,
            loopStop,
            stopReason,
            needsDecision = needs is null ? null : NeedsDecisionDocument(needs, runId, location, role, result!.Detail, continueCommand),
            openChoice = leftover,
            openFight = fightOpen,
            autoChoices = resolver.Log,
            conditions = Array.Empty<object>(),
            turns,
            steps = steps.ToArray(),
            detail = result?.Detail,
            unknownPagePath,
            auditPath = (string?)null
        });
        if (needs is not null || leftover is not null || fightOpen is not null) return 4; // a choice or fight is open: needs the user, not an error
        if (loopStop is not null && loopStop.StartsWith("The adventure counter", StringComparison.Ordinal)) return 3;
        return stopReason is "GatewayStopped" ? 3 : 0;
    }

    /// <summary>The 0.4 stop reason names (BoundedAdventureRunStopReason) for a 0.5 run result.</summary>
    internal static string StopReasonName(RunResult result, CliCombatStrategy strategy, int maxFightRounds) => result.StopReason switch
    {
        RunStopReason.TurnsDone => "Complete",
        RunStopReason.Condition => "ConditionsSatisfied",
        RunStopReason.NeedsChoice => "NeedsDecision",
        RunStopReason.NeedsCombat when strategy.StopReason == "HpBelowMinimum" => "HpBelowMinimum",
        RunStopReason.NeedsCombat when strategy.StopReason == "UnknownFight" => "UnknownFight",
        RunStopReason.NeedsCombat => "FightRoundLimitReached",
        RunStopReason.Cancelled => "DeadlineReached",
        _ => "GatewayStopped"
    };

    private static object StepDocument(Encounter encounter, string? unknownPagePath)
    {
        var monster = (encounter as FightEncounter)?.MonsterName;
        var kind = encounter switch
        {
            FightEncounter => "Fight",
            ChoiceEncounter => "Choice",
            NonCombatEncounter => "NonCombat",
            BlockedEncounter b => "Blocked:" + b.Reason,
            _ => encounter.GetType().Name
        };
        return new
        {
            status = kind,
            requestId = (string?)null,
            responseHash = (string?)null,
            abortReason = encounter is BlockedEncounter blocked ? blocked.Reason.ToString() : null,
            messages = encounter.Title is { } title ? new[] { title } : Array.Empty<string>(),
            requiresRefresh = false,
            complete = encounter is NonCombatEncounter,
            monster,
            turtleMonster = IsTurtleMonster(monster) ? true : (bool?)null,
            unknownPagePath
        };
    }

    private static readonly Regex TurtleMonster = new(@"\b(turtles?|tortoises?|terrapins?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A fight against a turtle monster (only a marker; taming is never automated).</summary>
    internal static bool IsTurtleMonster(string? monsterName) => !string.IsNullOrWhiteSpace(monsterName) && TurtleMonster.IsMatch(monsterName);

    internal static object NeedsDecisionDocument(ChoiceEncounter choice, string runId, string location, AccountRole role, string? reason, string? continueCommand = null)
        => new
        {
            status = "NeedsDecision",
            runId,
            role = role.ToString(),
            adventureId = location,
            choiceId = choice.ChoiceId.ToString(CultureInfo.InvariantCulture),
            title = choice.Title,
            options = choice.Options.Select(o => new { number = o.Option, text = o.Text }).ToArray(),
            reason,
            instruction = "A choice adventure is open and needs the user's decision. Show the options to the user and ask which one to take; do not guess.",
            resumeCommand = $"kolibri choice submit --role {role.ToString().ToLowerInvariant()} --choice {choice.ChoiceId} --option <N> --execute --approval-token <token>",
            thenContinue = continueCommand
        };

    /// <summary>
    /// When a run stops while a fight is still open (no usable fight action, round limit, HP floor), say so
    /// and name the exact command that finishes it. Never a silent stop mid-fight.
    /// </summary>
    internal static object? FightStopDocument(string stopReason, bool leftoverFight, string[] args, string location, AccountRole role, int? snarfblat)
    {
        if (!(stopReason is "FightRoundLimitReached" or "HpBelowMinimum" or "UnknownFight" || leftoverFight))
            return null;

        var finish = string.Join(' ', new[]
        {
            "kolibri adventure run", "--location", Quote(location), "--role", role.ToString().ToLowerInvariant(),
            snarfblat is > 0 ? $"--snarfblat {snarfblat.Value}" : "",
            "--fight-action", DefaultFightAction, "--execute", "--approval-token", "<token>"
        }.Where(x => x.Length > 0));
        var why = stopReason switch
        {
            "FightRoundLimitReached" => "the fight round limit was reached",
            "HpBelowMinimum" => "HP is below the configured minimum (or unknown)",
            "UnknownFight" => "no fight action is configured for this fight (--no-auto-fight or a non-matching --fight-action)",
            _ => "the run stopped before the fight ended"
        };
        return new
        {
            open = true,
            message = $"A fight is still open on the account: {why}. It was not abandoned; finish it before anything else.",
            finishCommand = finish,
            note = "Add --max-fight-rounds N / --min-hp-percent N to change the bounds; the default fight action is 'attack'."
        };
    }

    private static readonly string[] ContinueValueOptions =
    {
        "--max-steps", "--max-fight-rounds", "--min-hp-percent", "--max-duration-seconds", "--max-turns", "--max-runs", "--conditions-file"
    };

    /// <summary>The command that continues this run later: same location, snarfblat, rules and limits.</summary>
    internal static string BuildContinueCommand(string[] args, string location, AccountRole role, int? snarfblat)
    {
        var parts = new List<string>
        {
            "kolibri adventure run", "--location", Quote(location), "--role", role.ToString().ToLowerInvariant()
        };
        if (snarfblat is > 0) parts.AddRange(new[] { "--snarfblat", snarfblat.Value.ToString(CultureInfo.InvariantCulture) });
        foreach (var action in GetOptions(args, "--fight-action")) parts.AddRange(new[] { "--fight-action", Quote(action) });
        foreach (var choice in GetOptions(args, "--choice-action")) parts.AddRange(new[] { "--choice-action", Quote(choice) });
        if (HasFlag(args, "--no-auto-fight")) parts.Add("--no-auto-fight");
        foreach (var name in ContinueValueOptions)
            if (GetOption(args, name) is { } value) parts.AddRange(new[] { name, Quote(value) });
        parts.AddRange(new[] { "--execute", "--approval-token", "<token>" });
        return string.Join(' ', parts);
    }

    private static string Quote(string value)
        => value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '=' or ',' or '/')
            ? value : "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string ReadAdventureId(string[] args)
        => GetOption(args, "--location")
            ?? GetOption(args, "--adventure")
            ?? Positionals(args).FirstOrDefault()
            ?? throw new ArgumentException("--location LOCATION is required.");

    private static T[] ReadJsonArray<T>(string path, string propertyName)
    {
        if (!File.Exists(path))
            throw new ArgumentException($"JSON file '{path}' was not found.");
        JsonDocument document;
        try { document = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (JsonException exception) { throw new ArgumentException($"JSON file '{path}' is invalid.", exception); }
        using (document)
        {
            var element = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement
                : document.RootElement.TryGetProperty(propertyName, out var nested) ? nested : default;
            if (element.ValueKind != JsonValueKind.Array)
                throw new ArgumentException($"{path} must be a JSON array or an object with '{propertyName}'.");
            return element.Deserialize<T[]>(JsonOptions) ?? Array.Empty<T>();
        }
    }

    private static int ReadInt(string[] args, string name, int fallback)
        => int.TryParse(GetOption(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static int? ReadNullableInt(string[] args, string name)
        => int.TryParse(GetOption(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static IReadOnlyList<string> Positionals(string[] args)
    {
        var values = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var value = args[index];
            if (value.StartsWith("-", StringComparison.Ordinal))
            {
                if (index + 1 < args.Length && !args[index + 1].StartsWith("-", StringComparison.Ordinal)) index++;
                continue;
            }
            values.Add(value);
        }
        return values;
    }

    private static void PrintAdventureHelp()
    {
        Console.WriteLine("  adventure run --location ID [--snarfblat N] [--role main] [--max-turns N] [--execute --approval-token TOKEN]");
        Console.WriteLine("      fights: default action 'attack' (override with --fight-action attack|run|skill:ID|item:ID, disable with --no-auto-fight),");
        Console.WriteLine("      bounded by --max-fight-rounds N (30) and --min-hp-percent N (20); a fight left open is reported as openFight with the command to finish it");
        Console.WriteLine("      choices: --choice-action CHOICE:OPTION plus the defaults of `choice policy`; an unknown choice stops with needsDecision (exit 4)");
        Console.WriteLine("  choice pending|submit|policy                         see `choice --help`");
        Console.WriteLine("      start|begin|bounded-preview|run-preview are aliases of `adventure run`");
        Console.WriteLine("  adventure inspect [--role main]                      the open fight/choice (read-only, exit 4 if one is open)");
        Console.WriteLine("  adventure step --location ID [--snarfblat N] --execute --approval-token TOKEN   exactly one adventure, nothing resolved (alias: advance)");
        Console.WriteLine("  adventure fight --action attack|run|skill:ID|item:ID[,ID2] [--until-end --max-rounds N --min-hp-percent N] --execute --approval-token TOKEN");
        Console.WriteLine("      one round, or with --until-end the same action until the fight ends or a bound is reached (exit 4 = fight still open)");
        Console.WriteLine("  adventure locations [--name TEXT] [--id N] [--limit N]   location catalog (no login, no request)");
        Console.WriteLine("  adventure stop|abort|choice-chain: gone (exit 2, the message names the replacement)");
    }
}
