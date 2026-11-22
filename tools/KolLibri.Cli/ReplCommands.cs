using System.Globalization;
using System.Text.Json;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Cli.Repl;

/// <summary>
/// Read/write surface exposed to the Kolibri REPL. Implementations are
/// kept separate from the one-shot CLI commands so the REPL can reuse
/// them while the old behaviour stays intact.
/// </summary>
public interface IReplContext
{
    /// <summary>Resolved account role for the next read/write operation.</summary>
    AccountRole Role { get; }

    /// <summary>Read the account status snapshot.</summary>
    Task<KoLStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Read the inventory snapshot.</summary>
    Task<KoLInventorySnapshot> GetInventoryAsync(CancellationToken cancellationToken);

    /// <summary>Look up a single item by id or name.</summary>
    Task<MafiaItemRecord?> LookupItemAsync(string query, CancellationToken cancellationToken);

    /// <summary>Look up a single monster by id or name.</summary>
    Task<MafiaMonsterRecord?> LookupMonsterAsync(string query, CancellationToken cancellationToken);

    /// <summary>Compute the daily routine from the supplied snapshot.</summary>
    DailyRoutineReport RunDailyRoutine(DailyRoutineSnapshot snapshot);

    /// <summary>Run a one-shot reprice preview.</summary>
    Task<IReadOnlyList<object>> RepricePreviewAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Optional getter for a single recipe/cooked item.</summary>
    MafiaConcoctionRecord? LookupConcoction(string query);

    /// <summary>List login state for every role. Read-only, never mutates disk.</summary>
    Task<IReadOnlyList<LoginStatusRow>> GetLoginStatusAsync(CancellationToken cancellationToken);

    /// <summary>Clear stored credentials for the requested role(s).</summary>
    Task<int> ClearLoginAsync(IEnumerable<AccountRole> roles, CancellationToken cancellationToken);

    /// <summary>Force a fresh login and store the resulting credentials.</summary>
    Task<LoginRefreshRow> RefreshLoginAsync(
        AccountRole role, string username, string password,
        CancellationToken cancellationToken);
}

/// <summary>One row in the `login status` REPL output.</summary>
public sealed record LoginStatusRow(
    AccountRole Role,
    string? ProfileId,
    string? DisplayName,
    bool Enabled,
    bool HasCookie,
    bool Expired,
    DateTimeOffset? ExpiresAt,
    string? Endpoint);

/// <summary>One row in the `login refresh` REPL output.</summary>
public sealed record LoginRefreshRow(
    AccountRole Role,
    string Username,
    DateTimeOffset LoggedInAt);

/// <summary>
/// Result of one REPL command: a human-readable text payload plus an
/// optional machine-readable structured payload that the JSON mode can
/// emit instead.
/// </summary>
public sealed record ReplResult(string Text, object? Structured = null)
{
    public static ReplResult Plain(string text) => new(text);
    public static ReplResult Tabular(string title, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
        => new(FormatTable(title, columns, rows), new { title, columns, rows });
    public static ReplResult Json(object payload) => new(JsonSerializer.Serialize(payload, ReplJsonOptions), payload);

    private static string FormatTable(string title, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(title).Append('\n');
        if (columns.Count > 0)
        {
            sb.Append(string.Join("  ", columns)).Append('\n');
            sb.Append(new string('-', columns.Sum(c => c.Length + 2))).Append('\n');
        }
        foreach (var row in rows)
            sb.Append(string.Join("  ", row)).Append('\n');
        return sb.ToString();
    }

    internal static readonly JsonSerializerOptions ReplJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

/// <summary>
/// Top-level dispatcher for the Kolibri REPL. Each command is a small
/// method that takes the trailing args and the context, and returns a
/// <see cref="ReplResult"/>.
/// </summary>
public sealed class ReplDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async ValueTask<ReplResult> DispatchAsync(
        string[] args,
        IReplContext context,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0) return ReplResult.Plain(Help());
        var head = args[0].ToLowerInvariant();
        return head switch
        {
            "help" or "?" => ReplResult.Plain(Help()),
            "exit" or "quit" or ":q" => ReplResult.Plain("(exiting)"),
            "role" => ReplResult.Plain($"role = {context.Role}"),
            "status" or "me" => await StatusAsync(context, cancellationToken),
            "inventory" or "inv" => await InventoryAsync(context, cancellationToken),
            "where" => await WhereAsync(context, cancellationToken),
            "look" or "item" => await ItemAsync(args, context, cancellationToken),
            "monster" => await MonsterAsync(args, context, cancellationToken),
            "routine" => await RoutineAsync(args, context, cancellationToken),
            "campfire" => await CampfireAsync(args, context, cancellationToken),
            "hermit" => await HermitAsync(args, context, cancellationToken),
            "internet-shop" or "internetshop" => await InternetShopAsync(args, context, cancellationToken),
            "reprice" => await RepriceAsync(args, context, cancellationToken),
            "tokens" => await TokensAsync(args, context, cancellationToken),
            "login" => await LoginAsync(args, context, cancellationToken),
            "json" => ReplResult.Plain("json <on|off> — toggles structured output (currently session-scoped)"),
            _ => ReplResult.Plain($"unknown command '{head}'. type 'help'.")
        };
    }

    public string Help() => string.Join("\n", new[]
    {
        "Kolibri REPL — read-only by default, live actions gated by an approval token.",
        "  help                                  show this help",
        "  role                                  show the active account role",
        "  status | me                           account status snapshot",
        "  inventory | inv                       inventory snapshot",
        "  where                                 the location currently read",
        "  look <query> | item <query>           look up an item by id or name",
        "  monster <query>                       look up a monster by id or name",
        "  routine [--leaves N ...]              build the daily routine from a snapshot",
        "  campfire [--leaves N ...]             plan campfire leaves / day shortner",
        "  hermit [--permits N ...]              plan hermit permits + clovers",
        "  internet-shop [--already-claimed X]  plan daily free pulls",
        "  reprice [--limit N]                   reprice the current mall store",
        "  tokens                                show stored approval-token files",
        "  login status | clear [--role ...] |    manage stored account sessions",
        "        refresh <role> <user> <password>",
        "  exit | quit | :q                      leave the REPL"
    });

    private static async Task<ReplResult> StatusAsync(IReplContext ctx, CancellationToken token)
    {
        var status = await ctx.GetStatusAsync(token);
        return ReplResult.Tabular(
            $"status — role={ctx.Role}",
            new[] { "field", "value" },
            new[]
            {
                new[] { "name", status.PlayerName ?? "?" },
                new[] { "level", status.Level.ToString(CultureInfo.InvariantCulture) },
                new[] { "hp", $"{status.CurrentHp}/{status.MaximumHp}" },
                new[] { "mp", $"{status.CurrentMp}/{status.MaximumMp}" },
                new[] { "meat", status.Meat.ToString("N0", CultureInfo.InvariantCulture) },
                new[] { "adventures", status.Adventures.ToString(CultureInfo.InvariantCulture) }
            });
    }

    private static async Task<ReplResult> InventoryAsync(IReplContext ctx, CancellationToken token)
    {
        var inv = await ctx.GetInventoryAsync(token);
        if (inv.Items.Count == 0)
            return ReplResult.Plain("(empty inventory)");
        var rows = inv.Items.Take(20).Select(item => new[]
        {
            item.ItemId, item.Quantity.ToString(CultureInfo.InvariantCulture), item.Name ?? "?"
        }).ToArray();
        return ReplResult.Tabular(
            $"inventory — {inv.Items.Count} item types (showing first 20)",
            new[] { "id", "qty", "name" }, rows);
    }

    private static Task<ReplResult> WhereAsync(IReplContext ctx, CancellationToken _)
        => Task.FromResult(ReplResult.Plain($"active role: {ctx.Role}"));

    private static async Task<ReplResult> ItemAsync(string[] args, IReplContext ctx, CancellationToken token)
    {
        if (args.Length < 2) return ReplResult.Plain("usage: look <id-or-name>");
        var query = string.Join(' ', args.Skip(1));
        var item = await ctx.LookupItemAsync(query, token);
        if (item is null) return ReplResult.Plain($"(no item matching '{query}')");
        return ReplResult.Tabular(
            $"item: {item.Name}",
            new[] { "field", "value" },
            new[]
            {
                new[] { "id", item.Id },
                new[] { "name", item.Name },
                new[] { "type", item.Type },
                new[] { "tradeable", item.Tradeable ? "yes" : "no" },
                new[] { "usable", item.Usable ? "yes" : "no" },
                new[] { "autosell", item.Autosell.ToString(CultureInfo.InvariantCulture) }
            });
    }

    private static async Task<ReplResult> MonsterAsync(string[] args, IReplContext ctx, CancellationToken token)
    {
        if (args.Length < 2) return ReplResult.Plain("usage: monster <id-or-name>");
        var query = string.Join(' ', args.Skip(1));
        var monster = await ctx.LookupMonsterAsync(query, token);
        if (monster is null) return ReplResult.Plain($"(no monster matching '{query}')");
        return ReplResult.Tabular(
            $"monster: {monster.Name}",
            new[] { "field", "value" },
            new[]
            {
                new[] { "id", monster.Id },
                new[] { "name", monster.Name },
                new[] { "phylum", monster.Phylum },
                new[] { "hp", monster.Hp.ToString(CultureInfo.InvariantCulture) },
                new[] { "attack", monster.Attack.ToString(CultureInfo.InvariantCulture) },
                new[] { "defense", monster.Defense.ToString(CultureInfo.InvariantCulture) }
            });
    }

    private static Task<ReplResult> RoutineAsync(string[] args, IReplContext ctx, CancellationToken _)
    {
        var snapshot = ParseRoutineSnapshot(args);
        var report = ctx.RunDailyRoutine(snapshot);
        var lines = report.ApprovableSteps.Count == 0
            ? new[] { "(nothing to do today)" }
            : report.ApprovableSteps.ToArray();
        return Task.FromResult(ReplResult.Tabular(
            $"daily routine — verdict={report.Verdict}, steps={report.ApprovableSteps.Count}",
            new[] { "#", "step" },
            lines.Select((s, i) => new[] { (i + 1).ToString(CultureInfo.InvariantCulture), s }).ToArray()));
    }

    private static Task<ReplResult> CampfireAsync(string[] args, IReplContext ctx, CancellationToken _)
    {
        var snapshot = ParseCampfireSnapshot(args);
        var plan = new CampfireLeavesPlanner().Plan(snapshot);
        return Task.FromResult(ReplResult.Plain(
            $"campfire — verdict={plan.Verdict}, burn={plan.ShouldBurn}, shortner={plan.ShouldBuyDayShortner}\n{plan.Rationale}"));
    }

    private static Task<ReplResult> HermitAsync(string[] args, IReplContext ctx, CancellationToken _)
    {
        var snapshot = ParseHermitSnapshot(args);
        var plan = new HermitPlanner().Plan(snapshot);
        return Task.FromResult(ReplResult.Plain(
            $"hermit — verdict={plan.Verdict}, clover={plan.ShouldUseClover}, wishes={plan.ShouldBuyWishes}\n{plan.Rationale}"));
    }

    private static Task<ReplResult> InternetShopAsync(string[] args, IReplContext ctx, CancellationToken _)
    {
        var snapshot = ParseInternetShopSnapshot(args);
        var plan = new DailyInternetShopPlanner().Plan(snapshot);
        var rows = plan.Steps.Select((s, i) => new[]
        {
            (i + 1).ToString(CultureInfo.InvariantCulture), s.Kind, s.Description
        }).ToArray();
        return Task.FromResult(ReplResult.Tabular(
            $"internet-shop — verdict={plan.Verdict}",
            new[] { "#", "kind", "step" }, rows));
    }

    private static async Task<ReplResult> RepriceAsync(string[] args, IReplContext ctx, CancellationToken token)
    {
        var limit = 10;
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--limit", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                limit = Math.Clamp(n, 1, 100);
            }
        }
        var results = await ctx.RepricePreviewAsync(limit, token);
        return ReplResult.Plain($"reprice — {results.Count} suggestions (preview only; apply via the dedicated reprice command).");
    }

    private static Task<ReplResult> TokensAsync(string[] args, IReplContext ctx, CancellationToken _)
    {
        var root = KoLibri.Core.Basis.ApprovalTokenStore.DefaultRoot();
        var manual = Path.Combine(root, "manual-approval-token.json");
        var app = Path.Combine(root, "approval-token.json");
        var rows = new List<IReadOnlyList<string>>();
        foreach (var (label, path) in new[] { ("manual-browser", manual), ("app-default", app) })
        {
            rows.Add(new[] { label, File.Exists(path) ? "present" : "missing", path });
        }
        return Task.FromResult(ReplResult.Tabular("approval tokens", new[] { "scope", "state", "path" }, rows));
    }

    private static async Task<ReplResult> LoginAsync(string[] args, IReplContext ctx, CancellationToken token)
    {
        if (args.Length < 2) return ReplResult.Plain("usage: login status | login clear [--role ...] | login refresh <role> <user> <password>");
        var sub = args[1].ToLowerInvariant();
        switch (sub)
        {
            case "status":
            {
                var rows = await ctx.GetLoginStatusAsync(token);
                var cells = rows.Select(r => new[]
                {
                    r.Role.ToString(),
                    r.ProfileId ?? "?",
                    r.DisplayName ?? "?",
                    r.Enabled ? "yes" : "no",
                    r.HasCookie ? "yes" : "no",
                    r.Expired ? "expired" : "fresh",
                    r.ExpiresAt?.ToString("u") ?? "-"
                }).ToArray();
                return ReplResult.Tabular(
                    "login status",
                    new[] { "role", "id", "name", "enabled", "cookie", "state", "expires" },
                    cells);
            }
            case "clear":
            {
                IReadOnlyList<AccountRole> role;
                try { role = StrictOptions.LoginRoles(args[2..]); }
                catch (ArgumentException exception) { return ReplResult.Plain($"login clear: {exception.Message} Nothing was cleared."); }
                var cleared = await ctx.ClearLoginAsync(role, token);
                return ReplResult.Plain($"login clear: {cleared} role(s) cleared.");
            }
            case "refresh":
            {
                if (args.Length < 5)
                    return ReplResult.Plain("usage: login refresh <role> <username> <password>");
                if (!Enum.TryParse<AccountRole>(args[2], true, out var role))
                    return ReplResult.Plain($"unknown role '{args[2]}'. use main or mallbot.");
                var username = args[3];
                var password = args[4];
                var result = await ctx.RefreshLoginAsync(role, username, password, token);
                return ReplResult.Plain(
                    $"login refresh: {result.Role}={result.Username} ok at {result.LoggedInAt:u}.");
            }
            default:
                return ReplResult.Plain($"unknown login subcommand '{sub}'. use: status, clear, refresh.");
        }
    }


    internal static DailyRoutineSnapshot ParseRoutineSnapshot(string[] args)
    {
        long L(string key, long fallback = 0) => long.TryParse(Arg(args, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Max(0, v) : fallback;
        int I(string key, int fallback = 0) => int.TryParse(Arg(args, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        var alreadyPulled = args.Skip(1).Where((_, i) => i % 2 == 0 && args[i + 1] == "--already-claimed")
            .ToArray();
        return new DailyRoutineSnapshot
        {
            CampfireLeaves = I("--leaves"),
            CampfireReserve = I("--campfire-reserve", 3),
            DayShortnerPrice = L("--shortner-price"),
            MainMeat = L("--main-meat"),
            HermitPermits = I("--permits"),
            Clovers = I("--clovers"),
            AdventureReserveValue = L("--adventure-value"),
            CloverFloorValue = L("--clover-floor"),
            ExpectedCloverSaleValue = L("--clover-sale"),
            WishBuyQuantity = I("--wish-buy"),
            WishUnitPrice = L("--wish-price"),
            AlreadyPulledItemIds = alreadyPulled,
            MallbotMeat = L("--mallbot-meat"),
            ForeignBribeBagPrice = L("--bag-price"),
            ForeignBribeBagStock = I("--bag-stock"),
            PilotBagQuantity = I("--pilot-bags", 40),
            MallbotMeatReserve = L("--mallbot-reserve", 1_000_000)
        };
    }

    internal static CampfireLeavesSnapshot ParseCampfireSnapshot(string[] args)
        => new()
        {
            CampfireLeaves = ParseInt(args, "--leaves"),
            MinimumReserve = ParseInt(args, "--reserve", 3),
            DayShortnerPrice = ParseLong(args, "--shortner-price"),
            AvailableMeat = ParseLong(args, "--meat"),
            Knucklebones = ParseInt(args, "--knucklebones"),
            IsUsefulDay = !args.Contains("--not-useful", StringComparer.OrdinalIgnoreCase)
        };

    internal static HermitSnapshot ParseHermitSnapshot(string[] args)
        => new()
        {
            Permits = ParseInt(args, "--permits"),
            Clovers = ParseInt(args, "--clovers"),
            ReserveAdventureValue = ParseLong(args, "--adventure-value"),
            MinimumCloverSaleValue = ParseLong(args, "--clover-floor"),
            ExpectedCloverSaleValue = ParseLong(args, "--clover-sale"),
            WishBuyQuantity = ParseInt(args, "--wish-buy"),
            WishUnitPrice = ParseLong(args, "--wish-price")
        };

    internal static InternetShopSnapshot ParseInternetShopSnapshot(string[] args)
        => new()
        {
            AlreadyPulledItemIds = FlagList(args, "--already-claimed"),
            AvailableMeat = ParseLong(args, "--meat"),
            Bacon = ParseInt(args, "--bacon"),
            BaconMachineOwned = args.Contains("--machine"),
            BaconMachineUsedToday = args.Contains("--machine-used")
        };

    private static string? Arg(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static int ParseInt(string[] args, string key, int fallback = 0)
        => int.TryParse(Arg(args, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static long ParseLong(string[] args, string key, long fallback = 0)
        => long.TryParse(Arg(args, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string[] FlagList(string[] args, string key)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                result.Add(args[i + 1]);
        return result.ToArray();
    }
}
