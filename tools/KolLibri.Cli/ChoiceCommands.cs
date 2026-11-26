using KoLibri;

// `kolibri choice ...`: inspect and answer an open choice adventure, and configure automatic
// handling. pending/policy list are read-only; submit previews unless --execute is given.
internal static partial class KolibriCli
{
    internal static async Task<int> ChoiceCommandAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintChoiceHelp();
            return 0;
        }
        return args[0].ToLowerInvariant() switch
        {
            "pending" or "open" => await ChoicePendingAsync(args[1..]),
            "submit" => await ChoiceSubmitAsync(args[1..]),
            "policy" => ChoicePolicyCommand(args[1..]),
            _ => Fail($"Unknown choice command: {args[0]}")
        };
    }

    /// <summary>What is open on the account: a choice, a fight, nothing, or unknown (<paramref name="unavailable"/>).</summary>
    internal sealed record OpenState(string Kind, ChoiceEncounter? Choice, string? Reason = null)
    {
        public static OpenState From(Encounter? encounter) => encounter switch
        {
            ChoiceEncounter c => new("Choice", c),
            FightEncounter => new("Fight", null),
            _ => new("None", null)
        };

        public bool IsOpen => Kind is "Choice" or "Fight";
        public bool IsUnavailable => Kind == "Unavailable";
        public string Message => Kind switch
        {
            "Unavailable" => $"The open choice could not be read: {Reason ?? "unknown error"}. Nothing is assumed to be free.",
            "Choice" => "A choice adventure is open. Continue there (or decide with `kolibri choice submit`).",
            "Fight" => "A fight is open. Continue there.",
            _ => "No choice or fight is open."
        };
    }

    internal static async Task<OpenState> DetectOpenAsync(KolibriSession session)
    {
        try { return OpenState.From(await session.Adventure.GetOpenEncounterAsync().ConfigureAwait(false)); }
        catch (SessionExpiredException) { return new OpenState("Unavailable", null, "the session expired (login page)"); }
        catch (Exception e) when (e is KolibriException or HttpRequestException or TaskCanceledException)
        {
            return new OpenState("Unavailable", null, e.Message);
        }
    }

    internal static object OpenChoiceDocument(OpenState state, AccountRole role)
        => new
        {
            open = state.IsOpen,
            kind = state.Kind,
            message = state.Message,
            choiceId = state.Choice?.ChoiceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            title = state.Choice?.Title,
            options = state.Choice?.Options.Select(o => new { number = o.Option, text = o.Text }).ToArray(),
            submitCommand = state.Choice is { } c
                ? $"kolibri choice submit --role {role.ToString().ToLowerInvariant()} --choice {c.ChoiceId} --option <N> --execute --approval-token <token>"
                : null
        };

    private static async Task<int> ChoicePendingAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var (client, session) = await OpenAsync(role);
        await using var _ = client;
        await using var __ = session;
        var state = await DetectOpenAsync(session);
        WriteJson(new { command = "choice pending", readOnly = true, role = role.ToString(), result = OpenChoiceDocument(state, role) });
        if (state.IsUnavailable) return 5; // could not ask KoL: never report "free"
        return state.IsOpen ? 4 : 0;
    }

    private static async Task<int> ChoiceSubmitAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var choice = GetOption(args, "--choice");
        if (string.IsNullOrWhiteSpace(choice) || !int.TryParse(GetOption(args, "--option"), out var option) || option <= 0)
            throw new ArgumentException("choice submit requires --choice ID and --option N.");
        if (!HasFlag(args, "--execute"))
        {
            WriteJson(new
            {
                command = "choice submit",
                readOnly = true,
                previewOnly = true,
                executed = false,
                approvalRequired = true,
                role = role.ToString(),
                choiceId = choice,
                option,
                note = "Preview only; nothing was sent. Add --execute and --approval-token to submit this option to the open choice."
            });
            return 0;
        }
        var token = GetOption(args, "--approval-token");
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Live choice submission requires --approval-token.");
        if (!await AgentGuard.ApprovalTokenValidAsync(token).ConfigureAwait(false))
            throw new ArgumentException("The supplied approval token does not match the stored approval token. Nothing was sent.");
        if (!int.TryParse(choice.Trim(), out var choiceId))
            throw new ArgumentException("--choice must be a numeric choice id.");
        var (client, session) = await OpenAsync(role);
        await using var _ = client;
        await using var __ = session;
        bool submitted;
        string message;
        OpenState next;
        try
        {
            var after = await session.Adventure.ChooseAsync(choiceId, option);
            next = OpenState.From(after is FightEncounter or ChoiceEncounter ? after : null);
            submitted = true;
            message = next.IsOpen ? "Submitted. Another choice or fight is now open." : "Submitted. No choice is open any more.";
        }
        catch (KolibriException exception) when (exception is not SessionExpiredException)
        {
            submitted = false;
            message = exception.Message;
            next = await DetectOpenAsync(session);
        }
        WriteJson(new
        {
            command = "choice submit",
            executed = submitted,
            role = role.ToString(),
            choiceId = choice,
            option,
            message,
            next = OpenChoiceDocument(next, role)
        });
        return submitted ? 0 : 3;
    }

    private static int ChoicePolicyCommand(string[] args)
    {
        var store = new ChoiceResolutionConfigStore(Environment.GetEnvironmentVariable("KOLIBRI_CHOICE_POLICY"));
        var action = args.Length == 0 ? "list" : args[0].ToLowerInvariant();
        var rest = args.Length == 0 ? args : args[1..];
        var config = store.Load();
        switch (action)
        {
            case "list":
                break;
            case "set":
                config = ApplyChoicePolicySet(config, rest);
                store.Save(config);
                break;
            case "clear":
                if (HasFlag(rest, "--all")) config = new ChoiceResolutionConfig();
                else if (GetOption(rest, "--choice") is { } id) config.Choices.Remove(id.Trim());
                else throw new ArgumentException("choice policy clear requires --choice ID or --all.");
                store.Save(config);
                break;
            default:
                return Fail($"Unknown choice policy command: {action}");
        }
        WriteJson(new
        {
            command = $"choice policy {action}",
            path = store.Path,
            mode = config.Mode == ChoiceResolutionMode.AutoSafe ? "auto-safe" : "ask",
            limits = new
            {
                maxMeatCost = new { value = config.MaxMeatCost, @default = ChoiceResolutionConfig.MaxMeatCostDefault, min = ChoiceResolutionConfig.MaxMeatCostMin, max = ChoiceResolutionConfig.MaxMeatCostMax },
                maxHpPercent = new { value = config.MaxHpPercent, @default = ChoiceResolutionConfig.MaxHpPercentDefault, min = ChoiceResolutionConfig.MaxHpPercentMin, max = ChoiceResolutionConfig.MaxHpPercentMax },
                maxAdventureCost = new { value = config.MaxAdventureCost, @default = ChoiceResolutionConfig.MaxAdventureCostDefault, min = ChoiceResolutionConfig.MaxAdventureCostMin, max = ChoiceResolutionConfig.MaxAdventureCostMax }
            },
            choices = config.Choices.Select(x => new { choiceId = x.Key, option = x.Value.Option, title = x.Value.Title }).ToArray(),
            builtInChoices = 0
        });
        return 0;
    }

    internal static ChoiceResolutionConfig ApplyChoicePolicySet(ChoiceResolutionConfig config, string[] args)
    {
        if (GetOption(args, "--mode") is { } mode)
        {
            config = config with
            {
                Mode = mode.ToLowerInvariant() switch
                {
                    "ask" => ChoiceResolutionMode.Ask,
                    "auto-safe" or "autosafe" => ChoiceResolutionMode.AutoSafe,
                    _ => throw new ArgumentException("--mode must be ask or auto-safe.")
                }
            };
        }
        config = config with
        {
            MaxMeatCost = RangedInt(args, "--max-meat", config.MaxMeatCost, ChoiceResolutionConfig.MaxMeatCostMin, ChoiceResolutionConfig.MaxMeatCostMax),
            MaxHpPercent = RangedInt(args, "--max-hp-percent", config.MaxHpPercent, ChoiceResolutionConfig.MaxHpPercentMin, ChoiceResolutionConfig.MaxHpPercentMax),
            MaxAdventureCost = RangedInt(args, "--max-adventures", config.MaxAdventureCost, ChoiceResolutionConfig.MaxAdventureCostMin, ChoiceResolutionConfig.MaxAdventureCostMax)
        };
        if (GetOption(args, "--choice") is { } id)
        {
            if (!int.TryParse(GetOption(args, "--option"), out var option) || option <= 0)
                throw new ArgumentException("--choice needs --option N (a positive number).");
            config.Choices[id.Trim()] = new ChoiceDefaultEntry { Option = option, Title = GetOption(args, "--title") };
        }
        return config;
    }

    private static int RangedInt(string[] args, string name, int current, int min, int max)
    {
        var text = GetOption(args, name);
        if (text is null) return current;
        if (!int.TryParse(text, out var value) || value < min || value > max)
            throw new ArgumentException($"{name} must be a number from {min} to {max}.");
        return value;
    }

    /// <summary>The policy file of 0.4 (per-choice defaults, mode ask|auto-safe) as a KoLibri resolver.</summary>
    internal static ChoiceResolutionConfig LoadChoicePolicy()
        => new ChoiceResolutionConfigStore(Environment.GetEnvironmentVariable("KOLIBRI_CHOICE_POLICY")).Load();

    /// <summary>Strict default: per-choice defaults from the policy file (plus <paramref name="extra"/>), mode ask unless configured.</summary>
    internal static IChoiceResolver CreateChoiceResolver(ChoiceResolutionConfig config, IReadOnlyDictionary<int, int>? extra = null)
    {
        var table = new Dictionary<int, int>();
        foreach (var (id, entry) in config.Choices)
            if (int.TryParse(id, out var choiceId)) table[choiceId] = entry.Option;
        if (extra is not null)
            foreach (var (id, option) in extra) table[id] = option;
        return config.Mode == ChoiceResolutionMode.AutoSafe ? ChoiceResolvers.Safe(table) : ChoiceResolvers.FromTable(table);
    }

    private static void PrintChoiceHelp()
    {
        Console.WriteLine("  choice pending --role R                              read-only: is a choice/fight open? shows id, title, options");
        Console.WriteLine("  choice submit --role R --choice ID --option N [--execute --approval-token TOKEN]   preview unless --execute");
        Console.WriteLine("  choice policy list|set|clear                         automatic choice handling (default mode: ask)");
        Console.WriteLine("    set [--mode ask|auto-safe] [--max-meat N] [--max-hp-percent N] [--max-adventures N] [--choice ID --option N [--title T]]");
        Console.WriteLine("    clear --choice ID | --all");
    }
}
