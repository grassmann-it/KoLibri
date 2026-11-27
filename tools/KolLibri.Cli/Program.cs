using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using KoLibri;

return await KolibriCli.RunAsync(args);

internal static partial class KolibriCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static KolibriCli() => JsonOptions.Converters.Add(new JsonStringEnumConverter());

    /// <summary>Set per run: whether experimental commands are unlocked (also shown in the help).</summary>
    private static bool _experimental;

    public static Task<int> RunAsync(string[] args) => RunAsync(args, Environment.GetEnvironmentVariable);

    internal static async Task<int> RunAsync(string[] args, Func<string, string?> env)
    {
        _experimental = ExperimentalGate.Enabled(args, env);
        args = ExperimentalGate.Strip(args);
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        // Before the agent guard and before any session is read: a hidden command sends nothing.
        if (ExperimentalGate.Check(args[0], _experimental, Console.Error) is { } hidden) return hidden;

        if (args[0].Equals("agent-mode", StringComparison.OrdinalIgnoreCase))
            return AgentGuard.Command(args[1..]);
        var blocked = AgentGuard.Check(args, Environment.GetEnvironmentVariable,
            Console.IsInputRedirected, Console.IsOutputRedirected, AgentGuard.UnattendedAllowed(),
            AgentGuard.ConfirmOnTerminal, Console.Error);
        if (blocked is { } code) return code;

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "login" => await LoginDispatchAsync(args[1..]),
                "token" => await TokenDispatchAsync(args[1..]),
                "status" => await StatusAsync(args[1..]),
                "inventory" => await InventoryAsync(args[1..]),
                "mafia-script" => Removed(args[0], "status / inventory (JSON output)"),
                "shop" => await ShopAsync(args[1..]),
                "crm" => await CrmAsync(args[1..]),
                "consume" => await ConsumeAsync(args[1..]),
                "keys" => await KeysAsync(args[1..]),
                "rest" => await RestAsync(args[1..]),
                "cast" => await CastAsync(args[1..]),
                "relay" => await RelayAsync(args[1..]),
                "crm-block" => await CrmBlockAsync(args[1..]),
                "mall" => await MallAsync(args[1..]),
                "mall-reconcile" => await MallReconcileAsync(args[1..]),
                "arbitrage" => await ArbitrageAsync(args[1..]),
                "pricegun" => await PricegunAsync(args[1..]),
                "compare" => await RepriceCommandAsync(args[1..], compareMode: true),
                "history" => await PricegunAsync(args[1..]),
                "bribe-rank" or "bribe-plan" or "workflow-plan" => Removed(args[0], "no replacement (the foreign-bribe workflow is gone)"),
                "adventure-plan" => Removed(args[0], "'adventure run --max-turns N' (preview without --execute)"),
                "adventure" => await AdventureCommandAsync(args[1..]),
                "choice" => await ChoiceCommandAsync(args[1..]),
                "daily" => await DailyCommandAsync(args[1..]),
                "market-scan" or "market" => Removed(args[0], "'arbitrage plan|buy' or 'keys ...'"),
                "daily-audit" => await DailyCommandAsync(["audit", .. args[1..]]),
                "combat" => Removed(args[0], "'adventure inspect' or 'adventure run --fight-action ... --max-turns 1'"),
                "autocombat" or "auto-combat" => Removed(args[0], "'adventure run --fight-action ...'"),
                "categorize" => Removed(args[0], "'reprice' (its report carries an advice text per line)"),
                "reprice" or "batch" => await RepriceCommandAsync(args[1..], compareMode: false),
                "snatch-plan" => Removed(args[0], "'arbitrage plan'"),
                "watchlist" or "scheduler" => Removed(args[0], "cron / Task Scheduler running 'daily run'"),
                "repl" => Removed(args[0], "the one-shot CLI or MCP"),
                "apply-prices" => await ApplyPricesAsync(args[1..]),
                _ => Fail($"Unknown command: {args[0]}")
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Abgebrochen.");
            return 130;
        }
        catch (Exception exception)
        {
            // Exception messages from the core never include passwords or
            // cookie values. Keep CLI errors short and machine-readable.
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    /// <summary>Exit code for commands unavailable in KoLibri 0.5.</summary>
    internal const int NotYetExitCode = 3;
    internal const int RemovedExitCode = 2;

    internal static int Removed(string command, string replacement)
    {
        Console.Error.WriteLine($"'{command}' was removed in KoLibri 0.5: use {replacement}. Nothing was sent.");
        return RemovedExitCode;
    }

    internal static int NotYet(string command)
    {
        Console.Error.WriteLine($"'{command}' is not available in KoLibri 0.5; nothing was sent.");
        return NotYetExitCode;
    }

    private static async Task<int> StatusAsync(string[] args)
    {
        var (client, session) = await OpenAsync(ParseRole(GetOption(args, "--role") ?? "main"));
        await using var _ = client;
        await using var __ = session;
        var status = await session.Character.GetStatusAsync(refresh: HasFlag(args, "--refresh"));
        var equipment = await session.Inventory.GetEquipmentAsync();
        WriteJson(StatusDocument(status, equipment, session.Account.PlayerId));
        return 0;
    }

    /// <summary>The 0.4 status JSON (KoLStatusSnapshot field names), built from the 0.5 records.</summary>
    internal static object StatusDocument(Status s, Equipment? equipment, int? playerId) => new
    {
        playerName = s.PlayerName,
        adventures = s.Adventures,
        meat = s.Meat,
        level = s.Level,
        fetchedAt = s.FetchedAt,
        isStale = false,
        adventuresKnown = true,
        currentHp = s.Hp,
        maximumHp = s.MaxHp,
        currentMp = s.Mp,
        maximumMp = s.MaxMp,
        effects = s.Effects.Select(e => e.Name).ToArray(),
        fullness = s.Fullness.Current,
        inebriety = s.Inebriety.Current,
        spleen = s.Spleen.Current,
        storagePullsRemaining = s.StoragePullsRemaining,
        pathName = s.PathName,
        roninTurnsRemaining = s.RoninTurnsRemaining,
        timedEffects = s.Effects.Select(e => new { name = e.Name, turnsRemaining = e.Turns, id = e.Id }).ToArray(),
        playerId = playerId?.ToString(CultureInfo.InvariantCulture),
        familiarId = s.FamiliarId?.ToString(CultureInfo.InvariantCulture),
        fullnessLimit = s.Fullness.Limit,
        inebrietyLimit = s.Inebriety.Limit,
        spleenLimit = s.Spleen.Limit,
        equipment = equipment?.Slots.Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value!.Value.Id.ToString(CultureInfo.InvariantCulture))
    };

    internal static object InventoryDocument(Inventory inventory) => new
    {
        items = inventory.Items.Select(i => new { itemId = i.Item.Id.ToString(CultureInfo.InvariantCulture), name = i.Name, quantity = i.Quantity }).ToArray(),
        fetchedAt = inventory.FetchedAt,
        isStale = false
    };

    private static async Task<int> InventoryAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var (client, session) = await OpenAsync(role);
        await using var _ = client;
        await using var __ = session;
        if (HasFlag(args, "--probe"))
        {
            foreach (var path in new[] {
                "api.php?what=inventory",
                "api.php?what=inventory&for=KoLibri",
                "inventory.php",
                "inventory.php?which=1",
                "inventory.php?which=2",
                "inventory.php?which=3",
                "api.php?what=inv_easy",
                "api.php?what=item&itemid=10881&for=KoLibri",
                "api.php?what=item&itemid=42&for=KoLibri",
                "api.php?what=item&itemid=9025&for=KoLibri",
                "api.php?what=item&itemid=995&for=KoLibri",
                "campground.php",
                "campground.php?preaction=leaves",
                "campground.php?preaction=leaves&whichchoice=1510&option=1&leaves=1",
                "storage.php",
                "campaway.php",
                "place.php?whichplace=campaway",
                "hermit.php",
                "hermit.php?action=trade&whichitem=10881&quantity=1"
            })
            {
                try
                {
                    var page = await session.GetPageAsync(path);
                    var body = page.Html;
                    Console.Error.WriteLine($"--- {path} [{(System.Net.HttpStatusCode)page.StatusCode}] len={body.Length} ---");
                    if (body.Length > 0)
                        Console.Error.WriteLine(body.Substring(0, Math.Min(600, body.Length)));
                    if (HasFlag(args, "--full"))
                    {
                        var outPath = Path.Combine(
                            Path.GetTempPath(), $"kolprobe-{path.GetHashCode():X}.html");
                        await File.WriteAllTextAsync(outPath, body);
                        Console.Error.WriteLine($"[saved {outPath}]");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"{path}: {ex.Message}");
                }
            }
            return 0;
        }
        var inventory = await session.Inventory.GetAsync(refresh: HasFlag(args, "--refresh"));
        WriteJson(InventoryDocument(inventory));
        return 0;
    }

    private static async Task<string> ReadPasswordAsync()
    {
        if (Console.IsInputRedirected)
            return (await Console.In.ReadToEndAsync()).TrimEnd('\r', '\n');

        Console.Error.Write("Password (input is hidden): ");
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0) chars.RemoveAt(chars.Count - 1);
                continue;
            }
            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        Console.Error.WriteLine();
        return new string(chars.ToArray());
    }

    private static AccountRole ParseRole(string value) => value.ToLowerInvariant() switch
    {
        "main" => AccountRole.Main,
        "mallbot" => AccountRole.Mallbot,
        _ => throw new ArgumentException("Role must be main or mallbot.")
    };

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static IReadOnlyList<string> GetOptions(string[] args, string name)
    {
        var values = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
                throw new ArgumentException($"{name} requires a value.");
            values.Add(args[++index]);
        }
        return values;
    }

    private static bool HasFlag(string[] args, string name)
        => args.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help";
    private static void PrintHelp() => PrintHelp(Console.Out, _experimental);

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        PrintHelp();
        return 2;
    }

    private static void WriteJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    internal static void PrintHelp(TextWriter w, bool experimental)
    {
        var assembly = typeof(KolibriCli).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? assembly.GetName().Version?.ToString(3);
        w.WriteLine($"KoLibri CLI {version} (EARLY ALPHA) - previews by default; live actions need --execute and your approval.");
        w.WriteLine("  Live use needs your own KoL account (one session per account), requests are rate limited (default 2 s), AI agents must get your confirmation.");
        w.WriteLine("  cast --role main|mallbot --skill ID [--times N] [--execute]   cast a skill (Soul Food = 7185)");
        w.WriteLine("  relay --role main|mallbot [--port 8766] [--browser-ua]   local proxy: browse KoL in your own browser via the stored session, logs requests");
        w.WriteLine("  login main|mallbot [--username NAME] [--no-remember-password]");
        w.WriteLine("  login status [--role main|mallbot|all]       shows stored sessions + cookies");
        w.WriteLine("  login clear --role main|mallbot|all          deletes stored credentials");
        w.WriteLine("  login refresh --role main|mallbot [--username NAME --password PWD]   (without flags: stored password; asks if none is stored; --no-remember-password keeps none)");
        w.WriteLine("  login refresh --role main|mallbot             (asks for username/password when none is stored)");
        w.WriteLine("  token mint [--label TXT]                       creates an approval token");
        w.WriteLine("  token show                                      shows the current token");
        w.WriteLine("  status --role main|mallbot [--refresh]");
        w.WriteLine("  inventory --role main|mallbot [--refresh]");
        w.WriteLine("  shop --role mallbot [--refresh]");
        PrintKeysHelp(w);
        w.WriteLine("  consume --role R --kind food|drink|spleen --item ID [--qty N] [--execute]   eat/drink/spleen (preview without --execute)");
        w.WriteLine("  rest --role R [--times N] [--execute]            rest at the campground (free rests are free; other rests can cost adventures)");
        w.WriteLine("  mall --role mallbot --item ITEM_ID [--refresh]");
        w.WriteLine("  mall-reconcile --role mallbot [--settle|--release]   resolve unclear Mall purchase Meat (asks to confirm the amount)");
        w.WriteLine("  adventure run --location ID [--role main] [--max-turns N] [--fight-action attack|skill:ID|item:ID|run]");
        w.WriteLine("                [--choice-action CHOICE:OPTION] [--execute --approval-token TOKEN]");
        w.WriteLine("  choice pending|submit|policy ...                 open choices; submit previews unless --execute");
        w.WriteLine("  daily run [--role main|mallbot] [--hermit-clovers N] [--cargo-pocket auto|none|N[,M]] [--chess] [--skip campfire|hermit|internet-shop|toaster|vip|cargo|chess] [--execute --approval-token TOKEN]");
        if (experimental)
        {
            w.WriteLine();
            w.WriteLine($"Experimental (--experimental or KOLIBRI_EXPERIMENTAL=1; hidden by default in {version}):");
            w.WriteLine("  crm --role mallbot [--refresh]    own shop: buyer/sales analysis");
            w.WriteLine("  crm-block --key BUYER_KEY [--blocked true|false] [--note TEXT]");
            w.WriteLine("  pricegun --item ITEM_ID [--refresh]");
            w.WriteLine("  reprice --role mallbot [--item ID ...]   price report; compare = reprice for given items");
            w.WriteLine("  apply-prices --role mallbot --set ITEM_ID:EXPECTED:NEW [--set ...] [--commit]");
            PrintArbitrageHelp(w);
            w.WriteLine("  apply-prices is preview-only by default; only --commit sends the price change.");
        }
        else
        {
            w.WriteLine();
            w.WriteLine("Experimental market and shop commands are hidden; add --experimental to list them.");
        }
        w.WriteLine();
        w.WriteLine("Removed in 0.5 (exit 2, nothing is sent):");
        w.WriteLine("  mafia-script, repl            -> status / inventory (JSON), one-shot CLI or MCP");
        w.WriteLine("  adventure-plan                -> adventure run --max-turns N");
        w.WriteLine("  combat, autocombat            -> adventure inspect, adventure run --fight-action ...");
        w.WriteLine("  daily-audit                   -> daily audit");
        if (experimental)
            w.WriteLine("  experimental: categorize -> reprice; snatch-plan, market-scan -> arbitrage plan|buy; history -> pricegun; watchlist/scheduler -> cron + daily run; bribe-rank, bribe-plan, workflow-plan (gone)");
        w.WriteLine();
        w.WriteLine("There are no transfer commands. Adventure, consume and daily commands only send requests with --execute.");
    }
}
