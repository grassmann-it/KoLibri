using KoLibri;
using KoLibri.Daily;

internal static partial class KolibriCli
{
    internal static async Task<int> DailyCommandAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintDailyHelp();
            return 0;
        }

        var sub = args[0].ToLowerInvariant();
        var rest = args[1..];
        switch (sub)
        {
            case "run": return await DailyRunLiveAsync(rest);
            case "audit": return DailyAudit(rest);
            case "campfire": return await DailyAliasAsync(sub, "campfire", rest);
            case "hermit": return await DailyAliasAsync(sub, "hermit", rest);
            case "toaster" or "vip" or "cargo" or "chess":
                return await DailyAliasAsync(sub, sub, rest);
            case "internet-shop" or "internetshop" or "meme-shop":
                return await DailyAliasAsync(sub, "internet-shop", rest);
            case "meme-shop-read":
                if (HasFlag(rest, "--execute"))
                    return Fail("'daily meme-shop-read' is a preview; use 'daily run --only internet-shop --execute --approval-token TOKEN' to send.");
                return await DailyAliasAsync(sub, "internet-shop", rest);
            case "routine" or "go" or "daily-go" or "bridge" or "daily-bridge" or "breakfast":
                return await DailyAliasAsync(sub, null, rest);
            case "bribes-sample":
                return DailyRemoved(sub, "The foreign-bribe path is gone in 0.5.");
            case "send-items":
                return DailyRemoved(sub, "There are no transfer commands in the 0.5 CLI.");
            case "item-arbitrage":
                return DailyRemoved(sub, "Use 'arbitrage plan' instead.");
            default: return Fail($"Unknown daily command: {args[0]}");
        }
    }

    private static int DailyRemoved(string sub, string hint)
    {
        Console.Error.WriteLine($"'daily {sub}' was removed in 0.5. {hint} Nothing was sent.");
        return 2;
    }

    private static async Task<int> DailyAliasAsync(string sub, string? only, string[] rest)
    {
        var target = "daily run" + (only is null ? "" : " --only " + only);
        Console.Error.WriteLine($"'daily {sub}' is now '{target}' (daily run reads the live state; nothing is sent without --execute).");
        var forwarded = only is null ? rest : rest.Concat(new[] { "--only", only }).ToArray();
        return await DailyRunLiveAsync(forwarded);
    }

    private static readonly string[] DailyKnownSteps = { "campfire", "hermit", "internet-shop", "toaster", "vip", "cargo", "chess" };
    private static readonly string[] DailyValueFlags = { "--role", "--hermit-clovers", "--skip", "--only", "--approval-token", "--cargo-pocket", "--cargo-price-lookups" };

    /// <summary>The first flag `daily run` does not know (0.4 snapshot flags like --leaves/--meat), or null.</summary>
    private static string? UnknownDailyRunFlag(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--")) continue;
            if (DailyValueFlags.Contains(a, StringComparer.OrdinalIgnoreCase)) { i++; continue; }
            if (string.Equals(a, "--execute", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(a, "--chess", StringComparison.OrdinalIgnoreCase)) continue;
            return a;
        }
        return null;
    }

    private static object LiveDocument(DailyRunLive live) => new
    {
        leaves = live.Leaves, clovers = live.Clovers, permits = live.Permits, worthlessItems = live.WorthlessItems,
        bacon = live.Bacon, baconMachines = live.BaconMachines, meat = live.Meat,
        dayShortenerDoneToday = live.DayShortenerDoneToday,
        baconMachineUsedToday = live.BaconMachineUsedToday,
        memeItemsDoneToday = live.MemeItemsDoneToday,
        adventures = live.Adventures,
        items = ItemsDocument(live)
    };

    /// <summary>Owned daily items and what is done today (only items the account owns).</summary>
    private static object ItemsDocument(DailyRunLive live) => new
    {
        toaster = live.HasToaster ? new { doneToday = live.ToasterDoneToday } : null,
        vipKlaw = live.HasVipKey ? new { pullsDoneToday = live.KlawPullsDoneToday.Count, pullsPerDay = DailyItems.VipKlawDailyPulls } : null,
        cargoShorts = live.HasCargoShorts ? new { doneToday = live.CargoPocketDoneToday } : null,
        fancyChessSet = live.HasFancyChessSet ? new { doneToday = live.FancyChessDoneToday } : null
    };

    /// <summary>daily run [--role main|mallbot|all] [--hermit-clovers N] [--cargo-pocket N[,M…]] [--chess] [--skip|--only STEP] [--execute --approval-token T]</summary>
    private static async Task<int> DailyRunLiveAsync(string[] args)
    {
        var unknown = UnknownDailyRunFlag(args);
        if (unknown is not null)
            return Fail($"'daily run' does not know {unknown}: daily run reads the live state (leaves, clovers, meat) itself; there are no snapshot flags.");
        var roleText = (GetOption(args, "--role") ?? "main").ToLowerInvariant();
        var roles = roleText == "all" ? new[] { AccountRole.Main, AccountRole.Mallbot } : new[] { ParseRole(roleText) };
        var execute = HasFlag(args, "--execute");
        var token = GetOption(args, "--approval-token");
        var skip = GetOptions(args, "--skip").Select(s => s.Trim().ToLowerInvariant()).ToList();
        var only = GetOptions(args, "--only").Select(s => s.Trim().ToLowerInvariant()).ToList();
        if (only.Count > 0 && skip.Count > 0)
            return Fail("Use either --only or --skip, not both.");
        var badOnly = only.FirstOrDefault(o => !DailyKnownSteps.Contains(o));
        if (badOnly is not null)
            return Fail($"--only takes {string.Join(", ", DailyKnownSteps)} (got '{badOnly}').");
        var badSkip = skip.FirstOrDefault(o => !DailyKnownSteps.Contains(o));
        if (badSkip is not null)
            return Fail($"--skip takes {string.Join(", ", DailyKnownSteps)} (got '{badSkip}').");
        IReadOnlyList<int> cargoPockets;
        var cargoValues = GetOptions(args, "--cargo-pocket").Select(v => v.Trim()).ToArray();
        var cargoMode = CargoPocketMode.Auto;
        if (cargoValues.Any(v => v.Equals("none", StringComparison.OrdinalIgnoreCase) || v.Equals("off", StringComparison.OrdinalIgnoreCase)))
            cargoMode = CargoPocketMode.Off;
        var cargoList = cargoValues.Where(v => !v.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && !v.Equals("none", StringComparison.OrdinalIgnoreCase) && !v.Equals("off", StringComparison.OrdinalIgnoreCase));
        try { cargoPockets = cargoList.SelectMany(CargoPockets.Parse).ToArray(); }
        catch (FormatException exception) { return Fail("--cargo-pocket takes auto, none or pocket numbers: " + exception.Message); }
        var fightPocket = cargoPockets.FirstOrDefault(CargoPockets.StartsFight);
        if (fightPocket != 0)
            return Fail($"--cargo-pocket {CargoPockets.Describe(fightPocket)} starts a fight; daily run only picks pockets without a fight.");
        if (execute && !await AgentGuard.ApprovalTokenValidAsync(token).ConfigureAwait(false))
            throw new ArgumentException("The supplied approval token is missing or does not match the stored approval token. Nothing was sent.");
        var skipSet = only.Count > 0
            ? DailyKnownSteps.Where(k => !only.Contains(k)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : skip.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = new DailyRunOptions
        {
            HermitCloversWanted = Math.Max(0, ReadInt(args, "--hermit-clovers", 0)),
            Skip = skipSet,
            CargoPockets = cargoPockets,
            CargoMode = cargoMode,
            CargoPriceLookups = Math.Max(0, ReadInt(args, "--cargo-price-lookups", CargoPocketValue.DefaultPriceLookups)),
            PlayChess = HasFlag(args, "--chess")
        };

        if (roles.Length == 1)
        {
            var (doc, code) = await DailyRunRoleAsync(roles[0], options, execute);
            WriteJson(doc);
            return code;
        }

        var docs = new List<object>();
        var worst = 0;
        foreach (var r in roles)
        {
            var (doc, code) = await DailyRunRoleAsync(r, options, execute);
            docs.Add(doc);
            if (ExitRank(code) > ExitRank(worst)) worst = code;
        }
        WriteJson(new { command = "daily run", role = "all", readOnly = !execute, roles = docs.ToArray() });
        return worst;
    }

    /// <summary>The value ranking of the Cargo pockets for the preview (top 5 and the chosen one), when the "auto" step is planned.</summary>
    private static async Task<object?> CargoPreviewAsync(KolibriSession session, DailyRunLive live, DailyRunOptions options, IReadOnlyList<DailyStep> steps)
    {
        if (!live.HasCargoShorts) return null;
        var mode = DailyItems.CargoModeOf(options);
        if (!steps.Any(s => s.Id == DailyItems.StepIds.CargoPocket && s.Option == DailyItems.CargoAuto))
            return new { mode = mode.ToString().ToLowerInvariant(), planned = steps.Any(s => s.Id == DailyItems.StepIds.CargoPocket) };
        var ranking = await DailyItems.RankCargoAsync(session, options);
        return new
        {
            mode = "auto",
            planned = true,
            pocketsKnown = ranking.PocketsKnown,
            priceLookups = ranking.PriceLookups,
            chosen = ranking.Chosen is { } c ? CandidateDocument(c) : null,
            top = ranking.Top.Select(CandidateDocument).ToArray(),
            note = ranking.Note
        };
    }

    private static object CandidateDocument(CargoCandidate c) => new
    {
        pocket = c.Pocket, content = c.Content, quantity = c.Quantity, unitPrice = c.UnitPrice, value = c.Value, source = c.Source
    };

    private static int ExitRank(int code) => code switch { 0 => 0, 4 => 1, 3 => 2, _ => 3 };

    private static async Task<(object Document, int Code)> DailyRunRoleAsync(AccountRole role, DailyRunOptions options, bool execute)
    {
        var (client, session) = await OpenAsync(role);
        await using var _ = client;
        await using var __ = session;
        options = options with { StateDirectory = client.Options.StateDirectory };
        var live = await DailyRun.ReadAsync(session);
        var steps = DailyRun.Plan(live, options);
        object Step(DailyStep s) => new { kind = s.Kind, id = s.Id, option = s.Option };
        var liveDoc = LiveDocument(live);
        if (!execute)
        {
            return (new
            {
                command = "daily run", readOnly = true, previewOnly = true, role = role.ToString(), live = liveDoc,
                steps = steps.Select(Step).ToArray(),
                campfireSkipped = DailyRun.CampfireSkipReason(live, options),
                itemNotes = DailyItems.Notes(live, options).Select(n => new { step = n.Step, note = n.Text }).ToArray(),
                cargo = await CargoPreviewAsync(session, live, options, steps),
                note = "Preview: only status and inventory were read. Add --execute --approval-token to send these steps."
            }, 0);
        }

        var result = await DailyRun.ExecuteAsync(session, steps, options);
        var after = await DailyRun.ReadAsync(session);
        return (new
        {
            command = "daily run", readOnly = false, executed = result.Executed, role = role.ToString(),
            completeness = result.Completeness.ToString(),
            attempted = result.AttemptedSteps, submitted = result.SubmittedSteps, failed = result.FailedSteps,
            results = result.Steps.Select(r => new { step = r.Step, option = r.Option, state = r.State, message = r.Message, gains = r.Gains }).ToArray(),
            steps = steps.Select(Step).ToArray(),
            before = liveDoc,
            after = new
            {
                leaves = after.Leaves, clovers = after.Clovers, permits = after.Permits, worthlessItems = after.WorthlessItems,
                bacon = after.Bacon, meat = after.Meat,
                dayShortenerDoneToday = after.DayShortenerDoneToday,
                baconMachineUsedToday = after.BaconMachineUsedToday,
                memeItemsDoneToday = after.MemeItemsDoneToday,
                adventures = after.Adventures,
                items = ItemsDocument(after)
            },
            auditPath = (string?)null
        }, result.Completeness is DailyRunCompleteness.Failed ? 3
            : result.Completeness is DailyRunCompleteness.Partial ? 4 : 0);
    }

    /// <summary>Test seam: audit log location (the real one is fixed, like in the MCP tool).</summary>
    internal static string? AuditPathOverride { get; set; }

    /// <summary>daily audit [--tail N] [--verify] [--role main|mallbot|all] [--account A]; read-only.</summary>
    private static int DailyAudit(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--path", StringComparison.OrdinalIgnoreCase) || !a.StartsWith("--") && !IsValueOf(args, a)))
            return Fail("'daily audit' reads the fixed bridge audit store; a path cannot be chosen.");
        var roleText = (GetOption(args, "--role") ?? "main").ToLowerInvariant();
        var roles = roleText == "all" ? new[] { AccountRole.Main, AccountRole.Mallbot } : new[] { ParseRole(roleText) };
        var tail = ReadInt(args, "--tail", 0);
        var account = GetOption(args, "--account");
        var log = new KoLibri.Extras.BridgeAuditReader(AuditPathOverride);
        var rows = log.Read();
        if (HasFlag(args, "--verify"))
        {
            var broken = log.Verify();
            WriteJson(new
            {
                command = "daily-audit", state = broken is null ? "verified" : "broken", rowCount = rows.Count,
                breakIndex = broken?.Index, brokenAction = broken?.BrokenRow.Action
            });
            return 0;
        }

        var ofRoles = rows.Where(r => roles.Any(role => string.Equals(r.AccountId, role.ToString(), StringComparison.OrdinalIgnoreCase)));
        var filtered = string.IsNullOrWhiteSpace(account)
            ? ofRoles
            : ofRoles.Where(r => r.AccountId?.Contains(account, StringComparison.OrdinalIgnoreCase) == true);
        var ordered = tail > 0 ? filtered.TakeLast(tail).ToArray() : filtered.ToArray();
        WriteJson(new
        {
            command = "daily-audit", rowCount = ordered.Length,
            rows = ordered.Select(r => new
            {
                occurredAt = r.OccurredAt, phase = r.Phase, accountId = r.AccountId, action = r.Action,
                status = r.Status, requestId = r.RequestId, message = r.Message, hash = r.Hash
            })
        });
        return 0;
    }

    private static bool IsValueOf(string[] args, string value)
    {
        var i = Array.IndexOf(args, value);
        return i > 0 && args[i - 1] is "--role" or "--tail" or "--account";
    }

    private static void PrintDailyHelp()
    {
        Console.WriteLine("  daily run [--role main|mallbot|all] [--hermit-clovers N] [--skip STEP]... | [--only STEP]...");
        Console.WriteLine("            (STEP = campfire|hermit|internet-shop|toaster|vip|cargo|chess)");
        Console.WriteLine("            [--cargo-pocket auto|none|N[,M...]] [--cargo-price-lookups N] [--chess] [--execute --approval-token TOKEN]");
        Console.WriteLine("            Day Shortener, Hermit clovers, Internet Meme Shop, cheap toaster, VIP Klaw (3x), owned items only;");
        Console.WriteLine("            --cargo-pocket: auto (default) = most valuable full pocket without a fight (items x market price, Meat);");
        Console.WriteLine("                            N,M = pockets by preference; none = no pick; fight pockets are always refused;");
        Console.WriteLine("            --chess: play the fancy chess set (opt-in: a lost game costs 10 adventures);");
        Console.WriteLine("            preview reads status and inventory only; each step runs at most once per KoL day; --role all = main, then mallbot");
        Console.WriteLine("  daily audit [--tail N] [--verify] [--role main|mallbot|all] [--account A]   read-only bridge audit log (fixed location)");
        Console.WriteLine("  Aliases: daily campfire|hermit|internet-shop|meme-shop|toaster|vip|cargo|chess = daily run --only STEP; daily routine|go|bridge|breakfast = daily run.");
        Console.WriteLine("  Removed: daily bribes-sample, send-items (no transfer commands), item-arbitrage (use 'arbitrage plan').");
    }
}
