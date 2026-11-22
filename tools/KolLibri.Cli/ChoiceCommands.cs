using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

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

    internal static object OpenChoiceDocument(OpenChoiceDetection detection, AccountRole role)
        => new
        {
            open = detection.IsOpen,
            kind = detection.Kind.ToString(),
            message = detection.Message,
            choiceId = detection.Choice?.ChoiceId,
            title = detection.Choice?.Title,
            options = detection.Choice?.Options.Select(o => new { number = o.Number, text = o.Text }).ToArray(),
            submitCommand = detection.Choice is { } c
                ? $"kolibri choice submit --role {role.ToString().ToLowerInvariant()} --choice {c.ChoiceId} --option <N> --execute --approval-token <token>"
                : null
        };

    internal static object NeedsDecisionDocument(NeedsDecisionInfo info, string? continueCommand = null)
        => new
        {
            status = "NeedsDecision",
            runId = info.RunId,
            role = info.AccountRole,
            adventureId = info.AdventureId,
            choiceId = info.ChoiceId,
            title = info.Title,
            options = info.Options.Select(o => new { number = o.Number, text = o.Text }).ToArray(),
            reason = info.Reason,
            instruction = info.Instruction,
            resumeCommand = info.ChoiceId is null ? null : info.ResumeCommand(),
            thenContinue = continueCommand
        };

    private static async Task<int> ChoicePendingAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        using var http = await ReadAuthenticatedHttpAsync(role);
        var detection = await new OpenChoiceClient(http).DetectAsync();
        WriteJson(new { command = "choice pending", readOnly = true, role = role.ToString(), result = OpenChoiceDocument(detection, role) });
        if (detection.IsUnavailable) return 5; // could not ask KoL: never report "free"
        return detection.IsOpen ? 4 : 0;
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

        using var http = await ReadAuthenticatedHttpAsync(role);
        var result = await new OpenChoiceClient(http).SubmitAsync(choice, option);
        WriteJson(new
        {
            command = "choice submit",
            executed = result.Submitted,
            role = role.ToString(),
            choiceId = choice,
            option,
            message = result.Message,
            next = OpenChoiceDocument(result.Next, role)
        });
        return result.Submitted ? 0 : 3;
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
            builtInChoices = ChoiceResolver.BuiltInTable.Count
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

    /// <summary>Strict default: per-choice defaults from the policy file, mode ask unless configured.</summary>
    internal static ChoiceResolver CreateChoiceResolver()
        => new(new ChoiceResolutionConfigStore(Environment.GetEnvironmentVariable("KOLIBRI_CHOICE_POLICY")).Load());

    /// <summary>After a live run: report whatever choice/fight is still open so it is never left silently.</summary>
    internal static async Task<object?> DetectLeftoverChoiceAsync(KoLAdventureSession session)
    {
        try
        {
            var detection = await session.DetectOpenChoiceAsync().ConfigureAwait(false);
            if (detection.IsUnavailable)
                return new { open = (bool?)null, message = detection.Message };
            return detection.IsOpen ? OpenChoiceDocument(detection, session.Role) : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new { open = (bool?)null, message = $"Could not check for an open choice: {e.Message}" };
        }
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
