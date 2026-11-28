using System.Globalization;
using System.Text.RegularExpressions;
using KoLibri;

// `kolibri adventure inspect|step|fight|locations`: one turn by hand (step -> fight ... -> choice submit),
// on the library's single-step API (GetOpenEncounterAsync / AdventureAsync / FightAsync / FightToEndAsync).
// Nothing is resolved automatically here; every document names the next command.
internal static partial class KolibriCli
{
    private const int MaxSnarfblatScan = 2000;

    /// <summary>`adventure stop|abort|choice-chain`: no running state in a one-shot CLI. Exit 2 with the replacement.</summary>
    private static int AdventureRetiredCommand(string name)
    {
        var replacement = name.ToLowerInvariant() switch
        {
            "choice-chain" or "choicechain" => "`kolibri adventure run --choice-action CHOICE:OPTION ...` for known choices and `kolibri choice submit` for a single one",
            _ => "Ctrl+C (exit 130) ends a running `adventure run`; there is no stored run to stop"
        };
        Console.Error.WriteLine($"'adventure {name}' does not exist in KoLibri 0.5. Use {replacement}. Nothing was sent.");
        return 2;
    }

    private static async Task<int> AdventureLocationsAsync(string[] args)
    {
        var name = GetOption(args, "--name");
        var idText = GetOption(args, "--id");
        var limit = Math.Max(1, ReadInt(args, "--limit", 100));
        int? id = null;
        if (idText is not null)
        {
            if (!int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                throw new ArgumentException("--id must be a positive location number (snarfblat).");
            id = parsed;
        }

        await using var client = CreateClient();
        var found = new List<LocationInfo>();
        if (id is { } wanted)
        {
            if (client.Data.Location(wanted) is { } byId) found.Add(byId);
        }
        else
        {
            // The library has lookups by id and exact name only; a text search walks the id range.
            for (var n = 1; n <= MaxSnarfblatScan; n++)
                if (client.Data.Location(n) is { } info
                    && (string.IsNullOrWhiteSpace(name) || info.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase)))
                    found.Add(info);
        }
        if (id is not null && !string.IsNullOrWhiteSpace(name))
            found = found.Where(l => l.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

        var shown = found.Take(limit).ToList();
        WriteJson(new
        {
            command = "adventure locations",
            readOnly = true,
            sentRequests = false,
            count = found.Count,
            truncated = found.Count > shown.Count,
            locations = shown.Select(l => new { snarfblat = l.Location.Snarfblat, name = l.Name, zone = l.Zone }).ToArray(),
            note = found.Count == 0
                ? "No location matched (or the KoLmafia data folder was not found). Pass --snarfblat N to adventure step/run directly."
                : "Use the snarfblat as --location (or --snarfblat) of adventure step/run."
        });
        return 0;
    }

    private static async Task<int> AdventureInspectAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var (client, session) = await OpenAsync(role);
        await using var _ = client;
        await using var __ = session;
        Encounter? open;
        try { open = await session.Adventure.GetOpenEncounterAsync(); }
        catch (Exception e) when (e is KolibriException or HttpRequestException or TaskCanceledException)
        {
            WriteJson(new { command = "adventure inspect", readOnly = true, role = role.ToString(), open = (bool?)null,
                message = $"The open encounter could not be read: {e.Message}. Nothing is assumed to be free." });
            return 5;
        }
        if (open is null)
        {
            WriteJson(new { command = "adventure inspect", readOnly = true, role = role.ToString(), open = false, message = "No fight or choice is open." });
            return 0;
        }
        var status = await session.Character.GetStatusAsync(refresh: false);
        WriteJson(new
        {
            command = "adventure inspect",
            readOnly = true,
            role = role.ToString(),
            open = open is FightEncounter or ChoiceEncounter,
            encounter = EncounterDocument(open),
            hp = status.MaxHp > 0 ? $"{status.Hp}/{status.MaxHp}" : null,
            nextCommand = NextCommandFor(open, role)
        });
        return open is FightEncounter or ChoiceEncounter ? 4 : 0;
    }

    private static async Task<int> AdventureStepAsync(string[] args)
    {
        var location = ReadAdventureId(args);
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var snarfblat = ReadNullableInt(args, "--snarfblat")
            ?? (TryGetSnarfblat(location, out var fromId) ? fromId : (int?)null);
        if (snarfblat is not > 0)
            throw new ArgumentException($"No location number (snarfblat) for '{location}'. Pass --snarfblat N. Nothing was sent.");
        if (!HasFlag(args, "--execute"))
        {
            WriteJson(new
            {
                command = "adventure step", readOnly = true, previewOnly = true, executed = false, approvalRequired = true,
                location, snarfblat, role = role.ToString(),
                note = "Preview only; nothing was sent. Add --execute and --approval-token to spend exactly one adventure (nothing is resolved)."
            });
            return 0;
        }
        await RequireManualApprovalAsync(args, "Live adventure execution");

        var (client, session) = await OpenAsync(role);
        await using var _ = client;
        await using var __ = session;
        var encounter = await session.Adventure.AdventureAsync(snarfblat.Value);
        WriteJson(new
        {
            command = "adventure step", readOnly = false, previewOnly = false, executed = true, approvalRequired = true,
            location, snarfblat, role = role.ToString(),
            encounter = EncounterDocument(encounter),
            nextCommand = NextCommandFor(encounter, role) ?? StepCommand(location, snarfblat.Value, role)
        });
        return encounter switch { FightEncounter or ChoiceEncounter => 4, BlockedEncounter => 3, _ => 0 };
    }

    private static async Task<int> AdventureFightAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var actionText = GetOption(args, "--action") ?? throw new ArgumentException("adventure fight requires --action attack|run|skill:ID|item:ID[,ID2].");
        var action = ParseManualFightAction(actionText);
        var untilEnd = HasFlag(args, "--until-end");
        var maxRounds = Math.Max(1, ReadInt(args, "--max-rounds", DefaultMaxFightRounds));
        var minHp = Math.Clamp(ReadInt(args, "--min-hp-percent", DefaultMinHpPercent), 0, 100);
        if (!HasFlag(args, "--execute"))
        {
            WriteJson(new
            {
                command = "adventure fight", readOnly = true, previewOnly = true, executed = false, approvalRequired = true,
                role = role.ToString(), action = actionText, untilEnd, maxRounds = untilEnd ? maxRounds : (int?)null, minHpPercent = untilEnd ? minHp : (int?)null,
                note = untilEnd ? "Preview only; nothing was sent. Add --execute and --approval-token to fight with this action until the fight ends or a bound is reached."
                    : "Preview only; nothing was sent. Add --execute and --approval-token to send exactly one round."
            });
            return 0;
        }
        await RequireManualApprovalAsync(args, "Live fighting");

        var (client, session) = await OpenAsync(role);
        await using var _ = client;
        await using var __ = session;
        var again = FightCommand(role, actionText, untilEnd, maxRounds, minHp);
        if (!untilEnd)
        {
            var round = await session.Adventure.FightAsync(action);
            if (round.Outcome == FightOutcome.Unknown)
            {
                WriteJson(new { command = "adventure fight", executed = true, role = role.ToString(), action = actionText, outcome = "Unknown",
                    message = "KoL did not answer with a fight page (no fight open, or an unreadable page). Check with `adventure inspect`.",
                    nextCommand = $"kolibri adventure inspect --role {RoleName(role)}" });
                return 3;
            }
            var tail = round.Outcome == FightOutcome.Ongoing ? null : await OpenAfterFightAsync(session);
            WriteJson(new
            {
                command = "adventure fight", readOnly = false, executed = true, role = role.ToString(), action = actionText, untilEnd = false,
                outcome = round.Outcome.ToString(), rounds = new[] { RoundDocument(round) }, openFight = round.Outcome == FightOutcome.Ongoing,
                openChoice = tail is null ? null : OpenChoiceDocument(tail, role), items = ItemsDocument(round.Items), meat = round.Meat,
                nextCommand = round.Outcome == FightOutcome.Ongoing ? again : SubmitCommandOf(tail, role)
            });
            return round.Outcome == FightOutcome.Ongoing || tail is not null ? 4 : 0;
        }

        var open = await DetectOpenAsync(session);
        var strategy = new CliCombatStrategy(action, minHp);
        var result = open.Kind == "Fight"
            ? await session.Adventure.FightToEndAsync(strategy, maxRounds, CancellationToken.None)
            : new FightResult(FightOutcome.Unknown, "", Array.Empty<FightRound>());
        var stopped = result.Outcome == FightOutcome.Stopped;
        var unknown = result.Outcome == FightOutcome.Unknown;
        var stopReason = stopped ? strategy.StopReason ?? "FightRoundLimitReached" : null;
        var choice = stopped || unknown ? null : await OpenAfterFightAsync(session);
        WriteJson(new
        {
            command = "adventure fight", readOnly = false, executed = result.Rounds.Count > 0, role = role.ToString(), action = actionText, untilEnd = true,
            monster = result.MonsterName, outcome = result.Outcome.ToString(), stopReason, rounds = result.Rounds.Select(RoundDocument).ToArray(),
            openFight = unknown ? (bool?)null : stopped,
            openChoice = choice is null ? null : OpenChoiceDocument(choice, role), items = ItemsDocument(result.Items), meat = result.Meat,
            nextCommand = unknown ? $"kolibri adventure inspect --role {RoleName(role)}" : stopped ? again : SubmitCommandOf(choice, role)
        });
        return unknown ? 3 : stopped || choice is not null ? 4 : 0;
    }

    /// <summary>skill:ID, item:ID or item:ID,ID2 (second item), attack, run.</summary>
    internal static CombatAction ParseManualFightAction(string text)
    {
        var value = text.Trim().ToLowerInvariant();
        if (value.StartsWith("item:", StringComparison.Ordinal))
        {
            var ids = value[5..].Split(',');
            if (ids.Length is 1 or 2 && ids.All(i => int.TryParse(i, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0))
            {
                var first = int.Parse(ids[0], CultureInfo.InvariantCulture);
                return ids.Length == 1 ? CombatAction.Item(first) : CombatAction.Item(first, int.Parse(ids[1], CultureInfo.InvariantCulture));
            }
            throw new ArgumentException($"--action '{text}' is not item:ID or item:ID,ID2. Nothing was sent.");
        }
        try { return ParseFightAction(text); }
        catch (ArgumentException) { throw new ArgumentException($"--action '{text}' is not one of attack, run, skill:ID, item:ID[,ID2]. Nothing was sent."); }
    }

    private static async Task RequireManualApprovalAsync(string[] args, string what)
    {
        var token = GetOption(args, "--approval-token");
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException($"{what} requires --approval-token.");
        if (!await AgentGuard.ApprovalTokenValidAsync(token).ConfigureAwait(false))
            throw new ArgumentException("The supplied approval token does not match the stored approval token. Nothing was sent.");
    }

    /// <summary>After a fight ended: is a choice open (one GET)? A document for it, else null.</summary>
    private static async Task<OpenState?> OpenAfterFightAsync(KolibriSession session)
    {
        var state = await DetectOpenAsync(session);
        return state.IsOpen || state.IsUnavailable ? state : null;
    }

    private static string? SubmitCommandOf(OpenState? state, AccountRole role)
        => state?.Choice is { } c ? NextCommandFor(c, role) : state?.Kind == "Fight" ? FightCommand(role, DefaultFightAction, false, DefaultMaxFightRounds, DefaultMinHpPercent) : null;

    private static string RoleName(AccountRole role) => role.ToString().ToLowerInvariant();

    private static string StepCommand(string location, int snarfblat, AccountRole role)
        => $"kolibri adventure step --location {Quote(location)} --snarfblat {snarfblat} --role {RoleName(role)} --execute --approval-token <token>";

    private static string FightCommand(AccountRole role, string action, bool untilEnd, int maxRounds, int minHp)
        => $"kolibri adventure fight --role {RoleName(role)} --action {Quote(action)}"
            + (untilEnd ? $" --until-end --max-rounds {maxRounds} --min-hp-percent {minHp}" : "")
            + " --execute --approval-token <token>";

    private static string? NextCommandFor(Encounter encounter, AccountRole role) => encounter switch
    {
        FightEncounter => FightCommand(role, DefaultFightAction, false, DefaultMaxFightRounds, DefaultMinHpPercent),
        ChoiceEncounter c => $"kolibri choice submit --role {RoleName(role)} --choice {c.ChoiceId} --option <N> --execute --approval-token <token>",
        _ => null
    };

    private static readonly Regex SelectBlock = new(@"<select[^>]*\bname=[""']?(whichskill|whichitem)[""']?[^>]*>(.*?)</select>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex OptionTag = new(@"<option[^>]*\bvalue=[""']?(\d+)[""']?[^>]*>(.*?)(?=<option|$)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>Skills and items the fight page offers (select lists), as far as the page shows them.</summary>
    private static (object[] Skills, object[] Items) FightChoices(string html)
    {
        var skills = new List<object>();
        var items = new List<object>();
        foreach (Match select in SelectBlock.Matches(html))
            foreach (Match option in OptionTag.Matches(select.Groups[2].Value))
            {
                var entry = new { id = int.Parse(option.Groups[1].Value, CultureInfo.InvariantCulture), text = Regex.Replace(option.Groups[2].Value, "<[^>]+>", "").Trim() };
                (select.Groups[1].Value.Equals("whichskill", StringComparison.OrdinalIgnoreCase) ? skills : items).Add(entry);
            }
        return (skills.ToArray(), items.ToArray());
    }

    private static object EncounterDocument(Encounter encounter)
    {
        switch (encounter)
        {
            case FightEncounter f:
                var (skills, items) = FightChoices(f.Page.Html);
                return new { kind = "Fight", monster = f.MonsterName, monsterId = f.MonsterId, round = f.Round, title = f.Title, skills, items };
            case FinishedFightEncounter d:
                // KoL ended the fight on the page that started it (auto-attack): nothing is open.
                return new { kind = "FightFinished", monster = d.MonsterName, monsterId = d.MonsterId, title = d.Title,
                    outcome = d.Result.Outcome.ToString(), meat = d.Result.Meat,
                    items = d.Result.Items.Select(i => new { id = i.Item.Id, name = i.Name, quantity = i.Quantity }).ToArray() };
            case ChoiceEncounter c:
                return new { kind = "Choice", choiceId = c.ChoiceId.ToString(CultureInfo.InvariantCulture), title = c.Title,
                    options = c.Options.Select(o => new { number = o.Option, text = o.Text }).ToArray() };
            case BlockedEncounter b:
                return new { kind = "Blocked", reason = b.Reason.ToString(), title = b.Title };
            default:
                return new { kind = "NonCombat", title = encounter.Title };
        }
    }

    private static object RoundDocument(FightRound r) => new
    {
        round = r.Round, outcome = r.Outcome.ToString(), hp = r.Hp, mp = r.Mp, damageDealt = r.DamageDealt, damageTaken = r.DamageTaken
    };

    private static object[] ItemsDocument(IReadOnlyList<InventoryItem> items)
        => items.Select(i => (object)new { itemId = i.Item.Id.ToString(CultureInfo.InvariantCulture), name = i.Name, quantity = i.Quantity }).ToArray();
}
