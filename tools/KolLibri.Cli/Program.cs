using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using KoLibri.Cli.Repl;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;

return await KolibriCli.RunAsync(args);

internal static partial class KolibriCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static KolibriCli() => JsonOptions.Converters.Add(new JsonStringEnumConverter());

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

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
                "mafia-script" => await MafiaScriptAsync(args[1..]),
                "shop" => await ShopAsync(args[1..]),
                "crm" => await CrmAsync(args[1..]),
                "consume" => await ConsumeAsync(args[1..]),
                "rest" => await RestAsync(args[1..]),
                "cast" => await CastAsync(args[1..]),
                "relay" => await RelayAsync(args[1..]),
                "crm-block" => await CrmBlockAsync(args[1..]),
                "mall" => await MallAsync(args[1..]),
                "arbitrage" => await ArbitrageAsync(args[1..]),
                "pricegun" => await PricegunAsync(args[1..]),
                "compare" or "history" => await CompareAsync(args[1..]),
                "bribe-rank" => await BribeRankAsync(args[1..]),
                "bribe-plan" => await BribePlanAsync(args[1..]),
                "workflow-plan" => await WorkflowPlanAsync(args[1..]),
                "adventure-plan" => await AdventurePlanAsync(args[1..]),
                "adventure" => await AdventureCommandAsync(args[1..]),
                "daily" => await DailyCommandAsync(args[1..]),
                "market-scan" or "market" => await MarketScanCommands.RunAsync(args[1..], JsonOptions),
                "daily-audit" => await DailyAuditCommandAsync(args[1..]),
                "combat" => await CombatCommandAsync(args[1..]),
                "autocombat" or "auto-combat" => await AutoCombatCommandAsync(args[1..]),
                "categorize" => await CategorizeAsync(args[1..]),
                "reprice" => await RepriceAsync(args[1..]),
                "batch" => await RepriceAsync(args[1..]),
                "snatch-plan" => await SnatchPlanAsync(args[1..]),
                "watchlist" or "scheduler" => await WatchlistSchedulerCommand.RunAsync(args[1..], ReadClientAsync, JsonOptions),
                "repl" => await ReplMainAsync(args[1..]),
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

    private static async Task<int> StatusAsync(string[] args)
    {
        var client = await ReadClientAsync(ParseRole(GetOption(args, "--role") ?? "main"));
        var status = await client.Api.FetchStatusAsync(forceRefresh: HasFlag(args, "--refresh"));
        WriteJson(status);
        return 0;
    }

    private static async Task<int> InventoryAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var client = await ReadClientAsync(role);
        if (HasFlag(args, "--probe"))
        {
            using var http = await ReadAuthenticatedHttpAsync(role);
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
                    var r = await http.GetAsync(path);
                    var body = await r.Content.ReadAsStringAsync();
                    Console.Error.WriteLine($"--- {path} [{r.StatusCode}] len={body.Length} ---");
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
        var inventory = await client.Api.FetchInventoryAsync(forceRefresh: HasFlag(args, "--refresh"));
        WriteJson(inventory);
        return 0;
    }

    private static async Task<(KoLAccountReadClient Api, KoLMallReadClient Mall)> ReadClientAsync(AccountRole role)
    {
        var http = await ReadAuthenticatedHttpAsync(role);
        // Build an item-id → display-name resolver from the bundled Mafia
        // vendor data so inventory snapshots show "seal tooth" instead of "44".
        var resolver = BuildItemNameResolver();
        return (new KoLAccountReadClient(http, itemNameResolver: resolver),
                new KoLMallReadClient(http, itemNameResolver: resolver));
    }

    private static Func<string, string?>? BuildItemNameResolver() => KoLibri.Core.Spiel.BundledItemNames.Resolver;

    private static async Task<HttpClient> ReadAuthenticatedHttpAsync(AccountRole role)
    {
        var accounts = AccountSessionManager.CreateDefault();
        var session = await accounts.GetSessionAsync(role)
            ?? throw new InvalidOperationException($"{role} is not logged in. Run 'login --role {role.ToString().ToLowerInvariant()}'.");
        if (string.IsNullOrWhiteSpace(session.Credentials.CookieHeader))
            throw new InvalidOperationException($"{role} has no valid KoL session. Log in again.");

        var http = KoLLoginClient.CreateAuthenticatedClient(
            session.Credentials,
            new Uri("https://www.kingdomofloathing.com/"));
        return http;
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
    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        PrintHelp();
        return 2;
    }

    private static void WriteJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static async Task<int> ReplMainAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var dataDir = GetOption(args, "--data")
            ?? Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data");
        if (!Directory.Exists(dataDir))
        {
            Console.Error.WriteLine($"Vendor directory '{dataDir}' is missing.");
            return 1;
        }
        var loader = new MafiaDataLoader();
        loader.Load(dataDir, out var diagnostics);
        if (diagnostics.WarningCount > 0)
            Console.Error.WriteLine($"Catalog loaded with {diagnostics.WarningCount} parse warnings.");

        var context = new KoLibriReplContext(role, loader.Load(dataDir),
            readClientFactory: r => new KoLAccountReadClient(BuildHttpForRole(r).GetAwaiter().GetResult()),
            mallClientFactory: r => new KoLMallReadClient(BuildHttpForRole(r).GetAwaiter().GetResult(), itemNameResolver: BuildItemNameResolver()));
        var session = new ReplSession(context);
        return await session.RunAsync(CancellationToken.None);
    }

    private static async Task<HttpClient> BuildHttpForRole(AccountRole role)
    {
        var accounts = AccountSessionManager.CreateDefault();
        var session = await accounts.GetSessionAsync(role)
            ?? throw new InvalidOperationException($"{role} is not logged in. Run 'login --role {role.ToString().ToLowerInvariant()}'.");
        if (string.IsNullOrWhiteSpace(session.Credentials.CookieHeader))
            throw new InvalidOperationException($"{role} has no valid KoL session. Log in again.");
        return KoLLoginClient.CreateAuthenticatedClient(
            session.Credentials,
            new Uri("https://www.kingdomofloathing.com/"));
    }

    private static void PrintHelp()
    {
        Console.WriteLine("KoLibri CLI (read-only plus explicitly approved shop price updates)");
        Console.WriteLine("  cast --role main|mallbot --skill ID [--times N] [--execute]   cast a skill (Soul Food = 7185)");
        Console.WriteLine("  relay --role main|mallbot [--port 8766] [--browser-ua]   local proxy: browse KoL in your own browser via the stored session, logs requests");
        Console.WriteLine("  login main|mallbot [--username NAME] [--no-remember-password]");
        Console.WriteLine("  login status [--role main|mallbot|all]       shows stored sessions + cookies");
        Console.WriteLine("  login clear --role main|mallbot|all          deletes stored credentials");
        Console.WriteLine("  login refresh --role main|mallbot [--username NAME --password PWD]   (without flags: stored password)");
        Console.WriteLine("  login refresh --role main|mallbot             (interactive input)");
        Console.WriteLine("  token mint [--label TXT]                       creates an approval token");
        Console.WriteLine("  token show                                      shows the current token");
        Console.WriteLine("  status --role main|mallbot [--refresh]");
        Console.WriteLine("  inventory --role main|mallbot [--refresh]");
        Console.WriteLine("  mafia-script FILE [--role main|mallbot]    runs supported KoLmafia read commands");
        Console.WriteLine("  shop --role mallbot [--refresh]");
        Console.WriteLine("  crm --role mallbot [--refresh]    own shop: buyer/sales analysis");
        Console.WriteLine("  consume --role R --kind food|drink|spleen --item ID [--qty N] [--execute]   eat/drink/spleen (preview without --execute)");
        Console.WriteLine("  rest --role R [--times N] [--execute]            rest at the campground (free rests are free; other rests can cost adventures)");
        Console.WriteLine("  crm-block --key BUYER_KEY [--blocked true|false] [--note TEXT]");
        Console.WriteLine("  mall --role mallbot --item ITEM_ID [--refresh]");
        Console.WriteLine("  pricegun --item ITEM_ID [--refresh]");
        Console.WriteLine("  compare --role mallbot --item ITEM_ID [--item ITEM_ID] [--refresh]");
        Console.WriteLine("  bribe-rank --role mallbot --item 8691 [--currency-value CURRENCY:MEAT] [--refresh]");
        Console.WriteLine("  bribe-plan --role mallbot [--quantity 40] [--reserve 1000000] [--refresh]");
        Console.WriteLine("  workflow-plan [--refresh] [--offline --main-ready --mallbot-ready]");
        Console.WriteLine("                [--meat N --bag-price N --bag-stock N --quantity N --reserve N]");
        Console.WriteLine("  adventure-plan --role main [--target 100] [--yield 0.5] [--skeleton-available] [--refresh]");
        Console.WriteLine("  adventure choice-chain --location ID --run-id RUN [--choice-action CHOICE:OPTION] [--execute --approval-token TOKEN]");
        Console.WriteLine("                 [--periodic --iterations N --interval SEC --workers 1..20]  (read-only)");
        Console.WriteLine("  adventure locations|start|inspect|step|stop ...                 (previews; only adventure run --execute and choice-chain --execute send requests)");
        Console.WriteLine("  daily campfire [--leaves N] [--reserve N] [--knucklebones N] [--shortner-price N] [--meat N] [--not-useful]");
        Console.WriteLine("  daily hermit   [--permits N] [--clovers N] [--adventure-value N] [--clover-floor N] [--reserve N]");
        Console.WriteLine("                 [--clover-sale N] [--wishes N] [--wish-buy N] [--wish-price N]");
        Console.WriteLine("  daily internet-shop [--already-claimed NAME] [--meat N]");
        Console.WriteLine("  daily routine [--leaves N] [--permits N] [--clovers N] [--main-meat N] [--mallbot-meat N]");
        Console.WriteLine("                 [--bag-price N] [--bag-stock N] [--already-claimed NAME]");
        Console.WriteLine("                 [--mall-price N] [--bacon-median N] [--owned-machines N]");
        Console.WriteLine("                 [--adventure-value N] [--clover-floor N] [--clover-sale N]");
        Console.WriteLine("                 [--output PATH] [--execute --approval-token TOKEN --role main|mallbot]");
        Console.WriteLine("  daily go [--role main|mallbot|all] [--main-leaves N] [--hermit-clovers N] [--mallbot-leaves N] [--main-meat N]");
        Console.WriteLine("           [--mallbot-meat N] [--main-permits N] [--mallbot-permits N]");
        Console.WriteLine("           [--bag-price N] [--bag-stock N] [--execute --approval-token TOKEN]");
        Console.WriteLine("           [--already-claimed NAME] [--output PATH]");
        Console.WriteLine("  combat preview|inspect|manual ...                              (preview-only)");
        Console.WriteLine("  autocombat --dry-run [--max-rounds N] [--skill ID] [--item ID]");
        Console.WriteLine("  categorize --role mallbot [--limit 20] [--refresh]");
        Console.WriteLine("  reprice --role mallbot [--filter tiny|high|tiny-and-high|all] [--refresh]");
        Console.WriteLine("  snatch-plan --role mallbot [--item ITEM_ID ...] [--limit 20] [--workers 8]");
        Console.WriteLine("              [--budget N --reserve N --min-margin 0.10 --min-profit N --min-spread N]");
        Console.WriteLine("  watchlist add|remove|list --store PATH --item ITEM_ID [--tradeable true --can-sell true]");
        Console.WriteLine("  watchlist scan --role mallbot [--once|--periodic --interval SEC --iterations N] [--workers 8]");
        Console.WriteLine("              [--store PATH --watchlist-id ID --budget N --reserve N --transfer-cost N]");
        Console.WriteLine("              [--min-sales N --min-liquidity 0.05 --max-age-hours 6]");
        Console.WriteLine("  apply-prices --role mallbot --set ITEM_ID:EXPECTED:NEW [--set ...] [--commit]");
        PrintArbitrageHelp();
        Console.WriteLine("  repl [--role main|mallbot] [--data PATH]      starts the interactive read-only REPL");
        Console.WriteLine();
        Console.WriteLine("apply-prices is preview-only by default; only --commit sends the price change.");
        Console.WriteLine("There are no purchase or transfer commands.");
        Console.WriteLine("Adventure and combat commands send no live POSTs; they only produce previews.");
    }
}
