using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Analytics;
using KolArbitrage.Core.Data;
using KolArbitrage.Core.Integration;
using KolArbitrage.Core.Pricing;
using KolArbitrage.Core.Safety;
using KoLibri.Cli.Repl;

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

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "login" => await LoginDispatchAsync(args[1..]),
                "token" => await TokenDispatchAsync(args[1..]),
                "status" => await StatusAsync(args[1..]),
                "inventory" => await InventoryAsync(args[1..]),
                "shop" => await ShopAsync(args[1..]),
                "crm" => await CrmAsync(args[1..]),
                "crm-block" => await CrmBlockAsync(args[1..]),
                "mall" => await MallAsync(args[1..]),
                "pricegun" => await PricegunAsync(args[1..]),
                "compare" or "history" => await CompareAsync(args[1..]),
                "bribe-rank" => await BribeRankAsync(args[1..]),
                "bribe-plan" => await BribePlanAsync(args[1..]),
                "workflow-plan" => await WorkflowPlanAsync(args[1..]),
                "adventure-plan" => await AdventurePlanAsync(args[1..]),
                "adventure" => await AdventureCommandAsync(args[1..]),
                "daily" => await DailyCommandAsync(args[1..]),
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
                _ => Fail($"Unbekannter Befehl: {args[0]}")
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
            Console.Error.WriteLine($"Fehler: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> LoginDispatchAsync(string[] args)
    {
        if (args.Length > 0)
        {
            var head = args[0].ToLowerInvariant();
            if (head is "status" or "--status")
                return await LoginStatusAsync(args[1..]);
            if (head is "clear" or "--clear")
                return await LoginClearAsync(args[1..]);
            if (head is "refresh" or "--refresh")
                return await LoginRefreshAsync(args[1..]);
        }
        return await LoginAsync(args);
    }

    private static async Task<int> LoginAsync(string[] args)
    {
        var roleValue = GetOption(args, "--role")
            ?? (args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null)
            ?? throw new ArgumentException("login main oder login mallbot verwenden.");
        var role = ParseRole(roleValue);
        var username = GetOption(args, "--username");
        if (string.IsNullOrWhiteSpace(username) && !Console.IsInputRedirected)
        {
            Console.Error.Write("Benutzername: ");
            username = Console.ReadLine()?.Trim();
        }
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Benutzername fehlt.");
        var password = await ReadPasswordAsync();
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Passwort fehlt.");

        var rememberPassword = !HasFlag(args, "--no-remember-password");
        var login = await new KoLLoginClient().LoginAsync(username, password, rememberPassword);
        var accounts = AccountSessionManager.CreateDefault();
        await accounts.SetProfileAsync(new AccountProfile
        {
            Id = role == AccountRole.Main ? "main" : "mallbot",
            DisplayName = username,
            Role = role,
            Enabled = true
        });
        await accounts.SetCredentialsAsync(role, login.Credentials);
        WriteJson(new { ok = true, role = role.ToString(), username, rememberPassword, loggedInAt = login.LoggedInAt });
        return 0;
    }

    private static async Task<int> LoginStatusAsync(string[] args)
    {
        var accounts = AccountSessionManager.CreateDefault();
        await accounts.InitializeAsync();
        var profiles = await accounts.GetAccountsAsync();
        var rows = new List<object>();
        foreach (var role in Enum.GetValues<AccountRole>())
        {
            var profile = profiles.FirstOrDefault(p => p.Role == role);
            var session = await accounts.GetSessionAsync(role, includeExpired: true);
            var expired = session?.IsExpired() ?? false;
            var hasCookie = !string.IsNullOrWhiteSpace(session?.Credentials.CookieHeader);
            rows.Add(new
            {
                role = role.ToString(),
                profileId = profile?.Id,
                displayName = profile?.DisplayName,
                enabled = profile?.Enabled ?? false,
                hasCookie,
                expired,
                loggedInAt = session?.Credentials.ExpiresAt,
                endpoint = session?.Profile.Endpoint?.ToString()
            });
        }
        WriteJson(new
        {
            command = "login status",
            accounts = rows
        });
        return 0;
    }

    private static async Task<int> LoginClearAsync(string[] args)
    {
        var roleValue = GetOption(args, "--role");
        var roles = ResolveLoginRoles(roleValue);
        var accounts = AccountSessionManager.CreateDefault();
        await accounts.InitializeAsync();
        foreach (var role in roles)
        {
            var profile = await accounts.GetProfileAsync(role);
            if (profile is null)
            {
                Console.Error.WriteLine($"login clear: no profile for {role}.");
                continue;
            }
            // Removing credentials by overwriting with an empty password
            // clears the cookie, then we keep the profile so the operator
            // does not have to re-add it on next login.
            try
            {
                await accounts.SetCredentialsAsync(role, new AccountCredentials
                {
                    Password = string.Empty,
                    CookieHeader = string.Empty,
                    ExpiresAt = DateTimeOffset.MinValue
                });
            }
            catch (AccountConfigurationException)
            {
                // No profile configured: silently skip.
            }
            Console.Error.WriteLine($"login clear: cleared credentials for {role} (profile '{profile.Id}' retained).");
        }
        WriteJson(new { command = "login clear", roles = roles.Select(r => r.ToString()).ToArray() });
        return 0;
    }

    private static async Task<int> LoginRefreshAsync(string[] args)
    {
        var roleValue = GetOption(args, "--role") ?? "main";
        var role = ParseRole(roleValue);
        var username = GetOption(args, "--username");
        var password = GetOption(args, "--password");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            // Fall back to interactive prompt when neither flag was supplied.
            if (!Console.IsInputRedirected)
            {
                if (string.IsNullOrWhiteSpace(username))
                {
                    Console.Error.Write($"login refresh {role} Benutzername: ");
                    username = Console.ReadLine()?.Trim();
                }
                if (string.IsNullOrEmpty(password))
                {
                    password = await ReadPasswordAsync();
                }
            }
        }
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new ArgumentException("login refresh braucht --username und --password.");

        var login = await new KoLLoginClient().LoginAsync(username, password, rememberPassword: true);
        var accounts = AccountSessionManager.CreateDefault();
        await accounts.InitializeAsync();
        await accounts.SetCredentialsAsync(role, login.Credentials);
        WriteJson(new
        {
            command = "login refresh",
            role = role.ToString(),
            ok = true,
            loggedInAt = login.LoggedInAt
        });
        return 0;
    }

    private static async Task<int> DailyAuditCommandAsync(string[] args)
    {
        // Usage:
        //   kolibri daily-audit [path] [--tail N] [--verify] [--role X]
        var path = args.Length > 0 && !args[0].StartsWith('-')
            ? args[0]
            : KolArbitrage.Core.Daily.BridgeAuditLog.DefaultPath();
        var tail = int.TryParse(GetOption(args, "--tail"), out var t) ? t : 0;
        var verify = args.Any(a => string.Equals(a, "--verify", StringComparison.OrdinalIgnoreCase));
        var roleFilter = GetOption(args, "--role");
        var accountFilter = GetOption(args, "--account");

        await using var log = new KolArbitrage.Core.Daily.BridgeAuditLog(path);
        var rows = log.ReadSealedEvents();
        if (verify)
        {
            var breakInfo = log.VerifyChain();
            if (breakInfo is null)
            {
                WriteJson(new { command = "daily-audit", path, state = "verified", rowCount = rows.Count });
                return 0;
            }
            WriteJson(new
            {
                command = "daily-audit",
                path,
                state = "broken",
                breakIndex = breakInfo.Index,
                brokenAction = breakInfo.BrokenRow.AuditEvent.Action,
                expectedHash = breakInfo.ExpectedHash,
                actualHash = breakInfo.BrokenRow.Hash
            });
            return 1;
        }
        IEnumerable<KolArbitrage.Core.Integration.SealedAuditEvent> filtered = rows;
        if (!string.IsNullOrWhiteSpace(roleFilter))
        {
            // legacy `role` filter maps Main/Mallbot to accountId values; we
            // treat either as a substring match so the operator can grep.
            filtered = filtered.Where(r =>
                r.AuditEvent.AccountId?.Contains(roleFilter, StringComparison.OrdinalIgnoreCase) == true);
        }
        if (!string.IsNullOrWhiteSpace(accountFilter))
        {
            filtered = filtered.Where(r =>
                r.AuditEvent.AccountId?.Contains(accountFilter, StringComparison.OrdinalIgnoreCase) == true);
        }
        var ordered = filtered.ToArray();
        if (tail > 0) ordered = ordered.TakeLast(tail).ToArray();
        WriteJson(new
        {
            command = "daily-audit",
            path,
            rowCount = ordered.Length,
            rows = ordered.Select(r => new
            {
                occurredAt = r.AuditEvent.OccurredAt,
                phase = r.AuditEvent.Phase.ToString(),
                accountId = r.AuditEvent.AccountId,
                action = r.AuditEvent.Action,
                status = r.AuditEvent.Status,
                requestId = r.AuditEvent.RequestId,
                message = r.AuditEvent.Message,
                hash = r.Hash
            })
        });
        return 0;
    }

    private static async Task<int> TokenDispatchAsync(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "show";
        var store = new KolArbitrage.Core.Security.ApprovalTokenStore(
            KolArbitrage.Core.Security.ApprovalTokenStore.DefaultRoot());
        switch (sub)
        {
            case "mint":
            case "new":
            {
                var label = GetOption(args[1..], "--label");
                var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                var record = new KolArbitrage.Core.Security.ApprovalToken(
                    Token: token,
                    CreatedAt: DateTimeOffset.UtcNow,
                    Label: label ?? "kolibri-cli");
                await store.SaveAsync(record);
                WriteJson(new
                {
                    command = "token mint",
                    path = store.FilePath,
                    token,
                    record.CreatedAt,
                    record.Label
                });
                return 0;
            }
            case "show":
            {
                var token = await store.LoadAsync();
                if (token is null)
                {
                    WriteJson(new { command = "token show", state = "missing", path = store.FilePath });
                    return 1;
                }
                WriteJson(new
                {
                    command = "token show",
                    state = "present",
                    path = store.FilePath,
                    token = token.Token,
                    token.CreatedAt,
                    token.Label
                });
                return 0;
            }
            default:
                Console.Error.WriteLine("token: subcommand muss mint oder show sein.");
                return 2;
        }
    }

    private static List<AccountRole> ResolveLoginRoles(string? roleValue)
    {
        var value = (roleValue ?? "all").Trim().ToLowerInvariant();
        if (value is "all" or "both")
            return new() { AccountRole.Main, AccountRole.Mallbot };
        return new() { ParseRole(value) };
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

    private static async Task<int> ShopAsync(string[] args)
    {
        var client = await ReadClientAsync(ParseRole(GetOption(args, "--role") ?? "mallbot"));
        var shop = await client.Mall.FetchOwnShopAsync(forceRefresh: HasFlag(args, "--refresh"));
        WriteJson(new { fetchedAt = DateTimeOffset.UtcNow, count = shop.Count, listings = shop });
        return 0;
    }

    private static async Task<int> CrmAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "mallbot");
        using var http = await ReadAuthenticatedHttpAsync(role);
        var crmStore = new BuyerCrmStore();
        var buyerHasher = crmStore.CreateHasher();
        var accounts = AccountSessionManager.CreateDefault();
        var knownAccountKeys = (await accounts.GetAccountsAsync())
            .Select(profile => buyerHasher.Hash(profile.DisplayName))
            .ToHashSet(StringComparer.Ordinal);
        var salesClient = new KoLShopSalesReadClient(http, buyerHasher: buyerHasher);
        var snapshot = await salesClient.FetchSalesAsync(forceRefresh: HasFlag(args, "--refresh"));
        var database = await crmStore.LoadAsync();
        var sync = BuyerCrmService.ApplySales(database, snapshot.Sales);
        if (sync.NewSales > 0)
            await crmStore.SaveAsync(sync.Database);
        var report = ShopBuyerAnalytics.Analyze(snapshot.Sales, new ShopBuyerAnalyticsOptions
        {
            KnownAccountBuyerKeys = knownAccountKeys
        });
        WriteJson(new
        {
            role = role.ToString(),
            snapshot.FetchedAt,
            snapshot.IsStale,
            crmSync = new
            {
                sync.NewSales,
                sync.DuplicateSales,
                sync.PointsGranted,
                profileCount = sync.Database.Buyers.Count
            },
            report
        });
        return 0;
    }

    private static async Task<int> CrmBlockAsync(string[] args)
    {
        var buyerKey = GetOption(args, "--key")
            ?? (args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null)
            ?? throw new ArgumentException("--key BUYER_KEY fehlt.");
        var blocked = !bool.TryParse(GetOption(args, "--blocked"), out var parsed) || parsed;
        var store = new BuyerCrmStore();
        var database = await store.LoadAsync();
        var updated = BuyerCrmService.SetBlocked(database, buyerKey, blocked, GetOption(args, "--note"));
        await store.SaveAsync(updated);
        WriteJson(new { buyerKey, blocked, saved = true });
        return 0;
    }

    private static async Task<int> MallAsync(string[] args)
    {
        var itemId = GetOption(args, "--item") ?? throw new ArgumentException("--item ITEM_ID fehlt.");
        var client = await ReadClientAsync(ParseRole(GetOption(args, "--role") ?? "mallbot"));
        var result = await client.Mall.SearchAsync(itemId, forceRefresh: HasFlag(args, "--refresh"));
        WriteJson(result);
        return 0;
    }

    private static async Task<int> PricegunAsync(string[] args)
    {
        var itemId = GetOption(args, "--item")
            ?? (args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null)
            ?? throw new ArgumentException("--item ITEM_ID fehlt.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var client = new PricegunReadClient(http);
        var snapshot = await client.FetchAsync(itemId, forceRefresh: HasFlag(args, "--refresh"));
        var prices = snapshot.Sales.Select(x => (decimal)x.UnitPrice).OrderBy(x => x).ToArray();
        var totalQuantity = snapshot.Sales.Sum(x => (long)x.Quantity);
        var weightedAverage = totalQuantity == 0
            ? (decimal?)null
            : snapshot.Sales.Sum(x => (decimal)x.UnitPrice * x.Quantity) / totalQuantity;
        var median = prices.Length == 0
            ? (decimal?)null
            : prices.Length % 2 == 1
                ? prices[prices.Length / 2]
                : (prices[prices.Length / 2 - 1] + prices[prices.Length / 2]) / 2m;
        WriteJson(new
        {
            snapshot.ItemId,
            snapshot.DisplayName,
            snapshot.FetchedAt,
            snapshot.IsStale,
            saleCount = snapshot.Sales.Count,
            totalQuantity,
            minPrice = prices.Length == 0 ? (decimal?)null : prices[0],
            latest = snapshot.Sales.OrderByDescending(x => x.SoldAt).FirstOrDefault(),
            medianPrice = median,
            weightedAveragePrice = weightedAverage,
            maxPrice = prices.Length == 0 ? (decimal?)null : prices[^1],
            sales = snapshot.Sales
        });
        return 0;
    }

    private static async Task<int> CompareAsync(string[] args)
    {
        var itemIds = GetOptions(args, "--item");
        if (itemIds.Count == 0) throw new ArgumentException("Mindestens ein --item ITEM_ID ist erforderlich.");
        var role = ParseRole(GetOption(args, "--role") ?? "mallbot");
        var client = await ReadClientAsync(role);
        var shop = await client.Mall.FetchOwnShopAsync(forceRefresh: HasFlag(args, "--refresh"));
        var byId = shop.ToDictionary(x => x.ItemId, StringComparer.OrdinalIgnoreCase);
        using var pricegunHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var pricegun = new PricegunReadClient(pricegunHttp);
        var work = itemIds.Select(async itemId =>
        {
            byId.TryGetValue(itemId, out var own);
            var searchTerm = own?.DisplayName ?? itemId;
            var mall = await client.Mall.SearchAsync(searchTerm, forceRefresh: HasFlag(args, "--refresh"));
            var history = await pricegun.FetchAsync(itemId, forceRefresh: HasFlag(args, "--refresh"));
            var prices = history.Sales.Select(x => (decimal)x.UnitPrice).OrderBy(x => x).ToArray();
            var totalQuantity = history.Sales.Sum(x => (long)x.Quantity);
            var weightedAverage = totalQuantity == 0 ? (decimal?)null : history.Sales.Sum(x => (decimal)x.UnitPrice * x.Quantity) / totalQuantity;
            var median = prices.Length == 0 ? (decimal?)null : prices.Length % 2 == 1
                ? prices[prices.Length / 2]
                : (prices[prices.Length / 2 - 1] + prices[prices.Length / 2]) / 2m;
            var lowest = mall.Listings.Count == 0 ? (decimal?)null : mall.Listings.Min(x => (decimal)x.Price);
            var current = own is null ? (decimal?)null : own.Price;
            var historicalPremium = lowest is > 0 && median is not null
                ? (median.Value - lowest.Value) / lowest.Value
                : (decimal?)null;
            var currentVsHistory = current is > 0 && median is not null
                ? (median.Value - current.Value) / current.Value
                : (decimal?)null;
            return new
            {
                itemId,
                displayName = own?.DisplayName ?? history.DisplayName,
                currentPrice = current,
                mallLowest = lowest,
                pricegunMin = prices.Length == 0 ? (decimal?)null : prices[0],
                pricegunMedian = median,
                pricegunWeightedAverage = weightedAverage,
                pricegunMax = prices.Length == 0 ? (decimal?)null : prices[^1],
                pricegunLatest = history.Sales.OrderByDescending(x => x.SoldAt).FirstOrDefault(),
                saleCount = history.Sales.Count,
                historicalHigher = historicalPremium is > 0.10m,
                historicalPremiumPercent = historicalPremium,
                currentVsHistoryPercent = currentVsHistory,
                buyCandidate = lowest is not null && median is not null && lowest.Value <= median.Value * 0.80m && history.Sales.Count >= 3,
                mallIsStale = mall.IsStale,
                pricegunIsStale = history.IsStale
            };
        });
        var results = await Task.WhenAll(work);
        WriteJson(new { role = role.ToString(), count = results.Length, results });
        return 0;
    }

    private static async Task<int> BribeRankAsync(string[] args)
    {
        var item = GetOption(args, "--item");
        if (item is not null && !string.Equals(item, ForeignBribeCatalog.BagOfForeignBribesItemId, StringComparison.Ordinal))
            throw new ArgumentException($"--item muss {ForeignBribeCatalog.BagOfForeignBribesItemId} (bag of foreign bribes) sein.");
        var role = ParseRole(GetOption(args, "--role") ?? "mallbot");
        var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in GetOptions(args, "--currency-value"))
        {
            var separator = spec.LastIndexOf(':');
            if (separator <= 0 || separator == spec.Length - 1 ||
                !decimal.TryParse(spec[(separator + 1)..], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var meat))
                throw new ArgumentException($"Ungültiger --currency-value '{spec}'. Erwartet WÄHRUNG:MEAT.");
            values[spec[..separator].Trim()] = meat;
        }
        var client = await ReadClientAsync(role);
        using var pricegunHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var planner = new ForeignBribePlanner(client.Mall, new PricegunReadClient(pricegunHttp));
        var results = await planner.RankAsync(values, HasFlag(args, "--refresh"));
        WriteJson(new { role = role.ToString(), bagItemId = ForeignBribeCatalog.BagOfForeignBribesItemId, currencyValues = values, count = results.Count, results });
        return 0;
    }

    private static async Task<int> BribePlanAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "mallbot");
        var requested = int.TryParse(GetOption(args, "--quantity"), out var q) ? Math.Clamp(q, 1, 1000) : 40;
        var reserve = long.TryParse(GetOption(args, "--reserve"), out var r) ? Math.Max(0, r) : 1_000_000;
        var client = await ReadClientAsync(role);
        var status = await client.Api.FetchStatusAsync(forceRefresh: HasFlag(args, "--refresh"));
        var mall = await client.Mall.SearchAsync("bag of foreign bribes", forceRefresh: HasFlag(args, "--refresh"));
        var floor = mall.Listings.FirstOrDefault();
        var affordable = floor is null ? 0 : Math.Max(0, (int)Math.Min(requested, (status.Meat - reserve) / floor.Price));
        WriteJson(new
        {
            role = role.ToString(),
            itemId = ForeignBribeCatalog.BagOfForeignBribesItemId,
            itemName = "bag of foreign bribes",
            meat = status.Meat,
            reserve,
            requestedQuantity = requested,
            mallFloor = floor?.Price,
            floorQuantity = floor?.Quantity,
            plannedQuantity = affordable,
            plannedSpend = floor is null ? 0 : affordable * floor.Price,
            openOn = "Main",
            purchaseExecuted = false,
            rationale = floor is null ? "Kein aktueller Mallpreis." : "Nur Vorschau; Kauf und Öffnen bleiben getrennte Approval-Schritte."
        });
        return 0;
    }

    private static async Task<int> WorkflowPlanAsync(string[] args)
    {
        // A workflow plan is useful even when a previous session has expired.
        // `--offline` therefore skips all network calls and can be paired with
        // explicit fixture values for a deterministic checklist preview.
        var offline = HasFlag(args, "--offline") || HasFlag(args, "--no-network");
        var accounts = AccountSessionManager.CreateDefault();
        var main = await accounts.GetSessionAsync(AccountRole.Main);
        var mallbot = await accounts.GetSessionAsync(AccountRole.Mallbot);
        var mainValid = HasFlag(args, "--main-ready");
        var mallbotValid = HasFlag(args, "--mallbot-ready");
        if (!offline)
        {
            mainValid |= !string.IsNullOrWhiteSpace(main?.Credentials.CookieHeader);
            mallbotValid |= !string.IsNullOrWhiteSpace(mallbot?.Credentials.CookieHeader);
        }
        long meat = 0;
        long? bagPrice = null;
        var bagStock = 0;
        string? snapshotWarning = null;
        if (mainValid && !offline)
        {
            try
            {
                using var http = await ReadAuthenticatedHttpAsync(AccountRole.Main);
                _ = await new KoLAccountReadClient(http).FetchStatusAsync(forceRefresh: HasFlag(args, "--refresh"));
            }
            catch (Exception exception)
            {
                mainValid = false;
                snapshotWarning = $"Main: {exception.Message}";
            }
        }
        if (mallbotValid && !offline)
        {
            try
            {
                using var http = await ReadAuthenticatedHttpAsync(AccountRole.Mallbot);
                var api = new KoLAccountReadClient(http);
                var mall = new KoLMallReadClient(http);
                meat = (await api.FetchStatusAsync(forceRefresh: HasFlag(args, "--refresh"))).Meat;
                var bags = await mall.SearchAsync("bag of foreign bribes", forceRefresh: HasFlag(args, "--refresh"));
                if (bags.Listings.Count > 0)
                {
                    bagPrice = bags.Listings[0].Price;
                    bagStock = bags.Listings[0].Quantity;
                }
            }
            catch (Exception exception)
            {
                // Keep the command read-only and emit a blocked plan instead
                // of failing before a user can inspect the checklist.
                mallbotValid = false;
                snapshotWarning = snapshotWarning is null
                    ? $"Mallbot: {exception.Message}"
                    : $"{snapshotWarning}; Mallbot: {exception.Message}";
            }
        }
        if (long.TryParse(GetOption(args, "--meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var meatOverride))
            meat = Math.Max(0, meatOverride);
        if (long.TryParse(GetOption(args, "--bag-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bagPriceOverride))
            bagPrice = Math.Max(0, bagPriceOverride);
        if (int.TryParse(GetOption(args, "--bag-stock"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bagStockOverride))
            bagStock = Math.Max(0, bagStockOverride);
        var quantity = int.TryParse(GetOption(args, "--quantity"), out var parsedQuantity) ? Math.Clamp(parsedQuantity, 1, 1000) : 40;
        var reserve = long.TryParse(GetOption(args, "--reserve"), out var parsedReserve) ? Math.Max(0, parsedReserve) : 1_000_000;
        var snapshot = new KoLWorkflowSnapshot
        {
            MainSessionValid = mainValid,
            MallbotSessionValid = mallbotValid,
            MallbotMeat = meat,
            ForeignBribeBagPrice = bagPrice,
            ForeignBribeBagStock = bagStock,
            PilotBagQuantity = quantity,
            MallbotMeatReserve = reserve,
            MainCanOpenBags = mainValid,
            MainCanAdventure = mainValid,
            MainCanUseHermit = mainValid,
            PriceSourcesFresh = mallbotValid,
            AdventurePlanReady = false,
            OwnShopScanned = false,
            InventoryScanned = false
        };
        if (snapshotWarning is not null)
            Console.Error.WriteLine($"Workflow-Snapshot nicht verfügbar; Plan ist blockiert: {snapshotWarning}");
        WriteJson(new KoLWorkflowPlanner().Build(snapshot));
        return 0;
    }

    private static async Task<int> AdventurePlanAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var target = int.TryParse(GetOption(args, "--target"), out var parsedTarget)
            ? Math.Clamp(parsedTarget, 0, 10000) : 100;
        var expectedYield = decimal.TryParse(
            GetOption(args, "--yield"), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedYield)
            ? Math.Max(0m, parsedYield) : 0.5m;
        var client = await ReadClientAsync(role);
        var forceRefresh = HasFlag(args, "--refresh");
        async Task<AdventureState> ReadStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await client.Api.FetchStatusAsync(
                forceRefresh: forceRefresh || HasFlag(args, "--periodic"),
                cancellationToken: cancellationToken);
            var inventory = await client.Api.FetchInventoryAsync(
                forceRefresh: forceRefresh || HasFlag(args, "--periodic"),
                cancellationToken: cancellationToken);
            return new AdventureState
            {
                IsStale = status.IsStale || inventory.IsStale,
                Knucklebones = FindQuantity(inventory, "knucklebone"),
                AdventureTurnsRemaining = status.Adventures,
                LuckySurpriseEggs = FindQuantity(inventory, "lucky surprise egg"),
                ForeignBribeBags = FindQuantity(inventory, "foreign bribe"),
                PirateEncryptionKeys = FindQuantity(inventory, "pirate encryption key"),
                ElfGuardPresents = FindQuantity(inventory, "elf guard present"),
                CrimbucaneerBooty = FindQuantity(inventory, "crimbuccaneer booty", "crimbuccaneer booty"),
                SkeletonOfCrimbosPastAvailable = HasFlag(args, "--skeleton-available")
            };
        }
        var options = new AdventurePlannerOptions
        {
            DailyKnucklebonesTarget = target,
            ExpectedKnucklebonesPerAdventure = expectedYield,
            ConsumeLuckySurpriseEggs = !HasFlag(args, "--no-eggs")
        };
        var iterations = Math.Clamp(ReadInt(args, "--iterations", HasFlag(args, "--periodic") ? 1 : 1), 1, 1_000);
        var periodic = HasFlag(args, "--periodic") || iterations > 1;
        if (periodic)
        {
            var scheduler = new DailyAdventurePlanScheduler(
                new DailyAdventurePlanner(options));
            var ticks = new List<DailyAdventureScheduleTick>();
            await foreach (var tick in scheduler.RunAsync(
                ReadStateAsync,
                options,
                new DailyAdventureSchedulerOptions
                {
                    MaxIterations = iterations,
                    Interval = TimeSpan.FromSeconds(Math.Max(0, ReadInt(args, "--interval", 0))),
                    WorkerLimit = Math.Clamp(ReadInt(args, "--workers", 1), 1, 20)
                }))
            {
                ticks.Add(tick);
            }
            WriteJson(new
            {
                command = "adventure-plan",
                role = role.ToString(),
                readOnly = true,
                periodic = true,
                iterations = ticks.Count,
                ticks,
                execution = "preview-only; no adventure, fight, choice or item request was sent"
            });
            return 0;
        }

        var state = await ReadStateAsync(CancellationToken.None);
        var plan = new DailyAdventurePlanner(options).Plan(state, DateOnly.FromDateTime(DateTime.UtcNow));
        WriteJson(new
        {
            command = "adventure-plan",
            role = role.ToString(),
            readOnly = true,
            state,
            plan,
            execution = "preview-only; fights and choices require a future approved adapter"
        });
        return 0;
    }

    private static int FindQuantity(KoLInventorySnapshot inventory, params string[] fragments)
        => inventory.Items
            .Where(item => fragments.Any(fragment => item.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .Sum(item => item.Quantity);

    private static async Task<int> CategorizeAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "mallbot");
        var limit = int.TryParse(GetOption(args, "--limit"), out var parsed) ? Math.Clamp(parsed, 1, 100) : 20;
        var client = await ReadClientAsync(role);
        var shop = await client.Mall.FetchOwnShopAsync(forceRefresh: HasFlag(args, "--refresh"));
        var categorizer = new InventoryCategorizer();
        var results = new List<InventoryCategorization>();
        foreach (var listing in shop.Take(limit))
        {
            var market = await client.Mall.SearchAsync(listing.DisplayName ?? listing.ItemId);
            var lowest = market.Listings.Count == 0 ? (decimal?)null : market.Listings.Min(x => (decimal)x.Price);
            results.Add(categorizer.Analyze(new InventoryItemSnapshot
            {
                ItemId = listing.ItemId,
                DisplayName = listing.DisplayName ?? $"Item {listing.ItemId}",
                OwnedQuantity = listing.Quantity,
                CurrentListingPrice = listing.Price,
                LowestMarketPrice = lowest,
                MarketReferencePrice = lowest,
                ObservedQuantity = market.Listings.Sum(x => x.Quantity)
            }));
        }
        WriteJson(new { role = role.ToString(), count = results.Count, results });
        return 0;
    }

    private static async Task<int> RepriceAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "mallbot");
        var filter = (GetOption(args, "--filter") ?? "tiny-and-high").ToLowerInvariant();
        var client = await ReadClientAsync(role);
        var shop = await client.Mall.FetchOwnShopAsync(forceRefresh: HasFlag(args, "--refresh"));
        var candidates = shop.Where(x => filter switch
        {
            "tiny" => (x.DisplayName ?? string.Empty).Contains("tiny plastic", StringComparison.OrdinalIgnoreCase),
            "high" or "high-price" => x.Price >= 10_000,
            "all" => true,
            _ => (x.DisplayName ?? string.Empty).Contains("tiny plastic", StringComparison.OrdinalIgnoreCase) || x.Price >= 10_000
        }).ToArray();

        var categorizer = new InventoryCategorizer();
        var work = candidates.Select(async listing =>
        {
            var market = await client.Mall.SearchAsync(listing.DisplayName ?? listing.ItemId);
            var lowest = market.Listings.Count == 0 ? (decimal?)null : market.Listings.Min(x => (decimal)x.Price);
            // If our own ask is already at or below the observed market floor,
            // keep it unchanged. This avoids repeatedly undercutting our own
            // listing when KoL includes our store in the mall search results.
            var suggested = lowest is null
                ? (decimal?)null
                : listing.Price <= lowest.Value
                    ? listing.Price
                    : Math.Max(100m, Math.Min(lowest.Value - 1m, Math.Floor(lowest.Value * 0.995m)));
            var analysis = categorizer.Analyze(new InventoryItemSnapshot
            {
                ItemId = listing.ItemId,
                DisplayName = listing.DisplayName ?? $"Item {listing.ItemId}",
                OwnedQuantity = listing.Quantity,
                CurrentListingPrice = listing.Price,
                MarketReferencePrice = lowest,
                LowestMarketPrice = lowest,
                ObservedQuantity = market.Listings.Sum(x => x.Quantity)
            });
            return new
            {
                listing.ItemId,
                listing.DisplayName,
                listing.Price,
                listing.Quantity,
                marketLowest = lowest,
                suggestedPrice = suggested,
                delta = suggested is null ? (decimal?)null : suggested.Value - listing.Price,
                analysis.Categories,
                analysis.SuggestedAction,
                analysis.Rationale,
                market.IsStale
            };
        });
        var results = await Task.WhenAll(work);
        WriteJson(new { role = role.ToString(), filter, candidateCount = candidates.Length, results = results.OrderByDescending(x => Math.Abs(x.delta ?? 0m)) });
        return 0;
    }

    private static async Task<int> SnatchPlanAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "mallbot");
        var requestedItems = GetOptions(args, "--item");
        var limit = int.TryParse(GetOption(args, "--limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLimit)
            ? Math.Clamp(parsedLimit, 1, 500) : 20;
        var workers = int.TryParse(GetOption(args, "--workers"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWorkers)
            ? Math.Clamp(parsedWorkers, 1, 20) : 8;
        var refresh = HasFlag(args, "--refresh");
        var accounts = await ReadClientAsync(role);
        var status = await accounts.Api.FetchStatusAsync(forceRefresh: refresh);
        var ownShop = await accounts.Mall.FetchOwnShopAsync(forceRefresh: refresh);

        var budget = decimal.TryParse(GetOption(args, "--budget"), NumberStyles.Number, CultureInfo.InvariantCulture, out var budgetOverride)
            ? Math.Max(0m, budgetOverride) : Math.Max(0m, status.Meat);
        var reserve = decimal.TryParse(GetOption(args, "--reserve"), NumberStyles.Number, CultureInfo.InvariantCulture, out var reserveOverride)
            ? Math.Max(0m, reserveOverride) : 1_000_000m;
        reserve = Math.Min(reserve, budget);
        var minimumMargin = decimal.TryParse(GetOption(args, "--min-margin"), NumberStyles.Number, CultureInfo.InvariantCulture, out var marginOverride)
            ? Math.Max(0m, marginOverride) : 0.10m;
        var minimumProfit = decimal.TryParse(GetOption(args, "--min-profit"), NumberStyles.Number, CultureInfo.InvariantCulture, out var profitOverride)
            ? Math.Max(0m, profitOverride) : 1_000m;
        var minimumSpread = decimal.TryParse(GetOption(args, "--min-spread"), NumberStyles.Number, CultureInfo.InvariantCulture, out var spreadOverride)
            ? Math.Max(0m, spreadOverride) : 100m;
        var maxPerItem = int.TryParse(GetOption(args, "--max-item"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxItemOverride)
            ? Math.Clamp(maxItemOverride, 1, 1_000_000) : 100;
        var maxProposals = int.TryParse(GetOption(args, "--max-proposals"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxProposalOverride)
            ? Math.Clamp(maxProposalOverride, 1, 1_000) : 50;

        var items = requestedItems.Count > 0
            ? requestedItems.Select(x => x.Trim()).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToArray()
            : ownShop.Select(x => x.ItemId).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToArray();

        using var pricegunHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var pricegun = new PricegunReadClient(pricegunHttp);
        var blacklist = await new JsonFileSellerBlacklistStore().LoadAsync();
        var ownPriceByItem = ownShop.GroupBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Price).ToHashSet(), StringComparer.OrdinalIgnoreCase);
        using var gate = new SemaphoreSlim(workers, workers);
        var tasks = items.Select(async itemId =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                try
                {
                    var own = ownShop.FirstOrDefault(x => string.Equals(x.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
                    var mall = await accounts.Mall.SearchAsync(own?.DisplayName ?? itemId, forceRefresh: refresh).ConfigureAwait(false);
                    var history = await pricegun.FetchAsync(itemId, forceRefresh: refresh).ConfigureAwait(false);
                    var independent = mall.Listings
                        // Own-shop parsing has no store id; matching our exact ask
                        // prices prevents the snatcher buying our own listing.
                        .Where(x => !ownPriceByItem.TryGetValue(itemId, out var ownPrices) || !ownPrices.Contains(x.Price))
                        .Where(x => !SellerBlacklistMatcher.IsBlocked(blacklist, x)).ToArray();
                    var snapshot = new PriceSnapshot(
                        itemId,
                        independent,
                        history.Sales,
                        ownShop.Where(x => string.Equals(x.ItemId, itemId, StringComparison.OrdinalIgnoreCase)),
                        mall.FetchedAt,
                        history.FetchedAt,
                        mall.IsStale,
                        history.IsStale,
                        mall.IsStale || history.IsStale,
                        mall.SourceUri,
                        history.SourceUri);
                    var estimate = new MarketPriceEstimator(new MarketPriceEstimatorOptions { TopN = 10 }).Estimate(snapshot);
                    var listings = independent.Select(x => new UnderpricedPurchaseListing(
                        x.ItemId, x.StoreId, x.Price, x.Quantity, x.ObservedAt, x.PurchaseLimit, x.StoreName)).ToArray();
                    var policy = new UnderpricedPurchaseLimits
                    {
                        Budget = budget,
                        Reserve = reserve,
                        MaxQuantityPerItem = maxPerItem,
                        MaxProposals = maxProposals,
                        MinimumMarginPercent = minimumMargin,
                        MinimumAbsoluteProfit = minimumProfit,
                        MinimumAbsoluteSpreadPerUnit = minimumSpread,
                        MaximumReferenceAge = TimeSpan.FromHours(6)
                    };
                    var plan = new UnderpricedPurchasePlanner(policy).Plan(listings, estimate);
                    return (object)new
                    {
                        itemId,
                        displayName = own?.DisplayName ?? history.DisplayName,
                        mallFetchedAt = mall.FetchedAt,
                        pricegunFetchedAt = history.FetchedAt,
                        mallIsStale = mall.IsStale,
                        pricegunIsStale = history.IsStale,
                        estimate,
                        plan
                    };
                }
                catch (Exception exception)
                {
                    return (object)new { itemId, error = exception.Message };
                }
            }
            finally
            {
                gate.Release();
            }
        });
        var results = await Task.WhenAll(tasks);
        WriteJson(new
        {
            role = role.ToString(),
            fetchedAt = DateTimeOffset.UtcNow,
            readOnly = true,
            purchaseExecuted = false,
            workerLimit = workers,
            itemCount = items.Length,
            budget,
            reserve,
            policy = new { minimumMargin, minimumProfit, minimumSpread, maxPerItem, maxProposals },
            results,
            accepted = "Nur Vorschläge. Vor einem Kauf müssen Angebot, Preis und Seller erneut geprüft und explizit freigegeben werden."
        });
        return 0;
    }

    private static async Task<int> ApplyPricesAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "mallbot");
        var specifications = GetOptions(args, "--set");
        if (specifications.Count == 0)
            throw new ArgumentException("Mindestens ein --set ITEM_ID:ERWARTET:NEU ist erforderlich.");

        var updates = specifications.Select(ParsePriceUpdate).ToArray();
        using var http = await ReadAuthenticatedHttpAsync(role);
        var client = new KoLShopMutationClient(http);
        var result = await client.ApplyPriceUpdatesAsync(updates, HasFlag(args, "--commit"));
        WriteJson(new
        {
            role = role.ToString(),
            committed = result.Committed,
            succeeded = result.Succeeded,
            result.ObservedAt,
            result.Results
        });
        return result.Succeeded ? 0 : 3;
    }

    private static ShopPriceUpdate ParsePriceUpdate(string specification)
    {
        var fields = specification.Split(':', StringSplitOptions.TrimEntries);
        if (fields.Length != 3
            || !long.TryParse(fields[0], out _)
            || !long.TryParse(fields[1], out var expected)
            || !long.TryParse(fields[2], out var proposed))
            throw new ArgumentException(
                $"Ungültiges --set '{specification}'. Erwartet ITEM_ID:ERWARTETER_PREIS:NEUER_PREIS.");
        return new ShopPriceUpdate(fields[0], expected, proposed);
    }

    private static async Task<(KoLAccountReadClient Api, KoLMallReadClient Mall)> ReadClientAsync(AccountRole role)
    {
        var http = await ReadAuthenticatedHttpAsync(role);
        // Build an item-id → display-name resolver from the bundled Mafia
        // vendor data so inventory snapshots show "seal tooth" instead of "44".
        var resolver = BuildItemNameResolver();
        return (new KoLAccountReadClient(http, itemNameResolver: resolver),
                new KoLMallReadClient(http));
    }

    private static Func<string, string?>? BuildItemNameResolver()
    {
        try
        {
            var loader = new KolArbitrage.Core.Data.MafiaDataLoader();
            var path = Path.Combine(
                AppContext.BaseDirectory, "kolmafia-data");
            if (!Directory.Exists(path))
                path = Path.Combine(Directory.GetCurrentDirectory(), "external", "kolmafia-data");
            if (!Directory.Exists(path)) return null;
            var catalog = loader.Load(path);
            var dict = catalog.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.Id) && !string.IsNullOrWhiteSpace(i.Name))
                .GroupBy(i => i.Id!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name!, StringComparer.OrdinalIgnoreCase);
            return id => dict.TryGetValue(id, out var n) ? n : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<HttpClient> ReadAuthenticatedHttpAsync(AccountRole role)
    {
        var accounts = AccountSessionManager.CreateDefault();
        var session = await accounts.GetSessionAsync(role)
            ?? throw new InvalidOperationException($"{role} ist nicht angemeldet. Erst 'login --role {role.ToString().ToLowerInvariant()}'.");
        if (string.IsNullOrWhiteSpace(session.Credentials.CookieHeader))
            throw new InvalidOperationException($"{role} hat keine gültige KoL-Session. Erneut einloggen.");

        var http = KoLLoginClient.CreateAuthenticatedClient(
            session.Credentials,
            new Uri("https://www.kingdomofloathing.com/"));
        return http;
    }

    private static async Task<string> ReadPasswordAsync()
    {
        if (Console.IsInputRedirected)
            return (await Console.In.ReadToEndAsync()).TrimEnd('\r', '\n');

        Console.Error.Write("Passwort (Eingabe bleibt unsichtbar): ");
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
        _ => throw new ArgumentException("Role muss main oder mallbot sein.")
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
                throw new ArgumentException($"{name} benötigt einen Wert.");
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
            mallClientFactory: r => new KoLMallReadClient(BuildHttpForRole(r).GetAwaiter().GetResult()));
        var session = new ReplSession(context);
        return await session.RunAsync(CancellationToken.None);
    }

    private static async Task<HttpClient> BuildHttpForRole(AccountRole role)
    {
        var accounts = AccountSessionManager.CreateDefault();
        var session = await accounts.GetSessionAsync(role)
            ?? throw new InvalidOperationException($"{role} ist nicht angemeldet. Erst 'login --role {role.ToString().ToLowerInvariant()}'.");
        if (string.IsNullOrWhiteSpace(session.Credentials.CookieHeader))
            throw new InvalidOperationException($"{role} hat keine gültige KoL-Session. Erneut einloggen.");
        return KoLLoginClient.CreateAuthenticatedClient(
            session.Credentials,
            new Uri("https://www.kingdomofloathing.com/"));
    }

    private static void PrintHelp()
    {
        Console.WriteLine("KoLibri CLI (read-only plus explicitly approved shop price updates)");
        Console.WriteLine("  login main|mallbot [--username NAME] [--no-remember-password]");
        Console.WriteLine("  login status [--role main|mallbot|all]       zeigt gespeicherte Sessions + Cookies");
        Console.WriteLine("  login clear --role main|mallbot|all          löscht gespeicherte Credentials");
        Console.WriteLine("  login refresh --role main|mallbot --username NAME --password PWD");
        Console.WriteLine("  login refresh --role main|mallbot             (interaktive Eingabe)");
        Console.WriteLine("  token mint [--label TXT]                       erzeugt einen Approval-Token");
        Console.WriteLine("  token show                                      zeigt den aktuellen Token");
        Console.WriteLine("  status --role main|mallbot [--refresh]");
        Console.WriteLine("  inventory --role main|mallbot [--refresh]");
        Console.WriteLine("  shop --role mallbot [--refresh]");
        Console.WriteLine("  crm --role mallbot [--refresh]    eigener Shop: Käufer-/Sales-Analyse");
        Console.WriteLine("  crm-block --key BUYER_KEY [--blocked true|false] [--note TEXT]");
        Console.WriteLine("  mall --role mallbot --item ITEM_ID [--refresh]");
        Console.WriteLine("  pricegun --item ITEM_ID [--refresh]");
        Console.WriteLine("  compare --role mallbot --item ITEM_ID [--item ITEM_ID] [--refresh]");
        Console.WriteLine("  bribe-rank --role mallbot --item 8691 [--currency-value WÄHRUNG:MEAT] [--refresh]");
        Console.WriteLine("  bribe-plan --role mallbot [--quantity 40] [--reserve 1000000] [--refresh]");
        Console.WriteLine("  workflow-plan [--refresh] [--offline --main-ready --mallbot-ready]");
        Console.WriteLine("                [--meat N --bag-price N --bag-stock N --quantity N --reserve N]");
        Console.WriteLine("  adventure-plan --role main [--target 100] [--yield 0.5] [--skeleton-available] [--refresh]");
        Console.WriteLine("  adventure choice-chain --location ID --run-id RUN [--choice-action CHOICE:OPTION] [--execute --approval-token TOKEN]");
        Console.WriteLine("                 [--periodic --iterations N --interval SEC --workers 1..20]  (read-only)");
        Console.WriteLine("  adventure locations|start|inspect|step|stop ...                 (preview-only)");
        Console.WriteLine("  daily campfire [--leaves N] [--reserve N] [--knucklebones N] [--shortner-price N] [--meat N] [--not-useful]");
        Console.WriteLine("  daily hermit   [--permits N] [--clovers N] [--adventure-value N] [--clover-floor N] [--reserve N]");
        Console.WriteLine("                 [--clover-sale N] [--wishes N] [--wish-buy N] [--wish-price N]");
        Console.WriteLine("  daily internet-shop [--already-claimed NAME] [--meat N]");
        Console.WriteLine("  daily bacon [--owned N] [--mall-price N] [--median N] [--meat N]");
        Console.WriteLine("               [--min-profit N] [--max-qty N]");
        Console.WriteLine("  daily routine [--leaves N] [--permits N] [--clovers N] [--main-meat N] [--mallbot-meat N]");
        Console.WriteLine("                 [--bag-price N] [--bag-stock N] [--already-claimed NAME]");
        Console.WriteLine("                 [--mall-price N] [--bacon-median N] [--owned-machines N]");
        Console.WriteLine("                 [--adventure-value N] [--clover-floor N] [--clover-sale N]");
        Console.WriteLine("                 [--output PATH] [--execute --approval-token TOKEN --role main|mallbot]");
        Console.WriteLine("  daily-go [--role main|mallbot|all] [--main-leaves N] [--mallbot-leaves N] [--main-meat N]");
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
        Console.WriteLine("  apply-prices --role mallbot --set ITEM_ID:ERWARTET:NEU [--set ...] [--commit]");
        Console.WriteLine("  repl [--role main|mallbot] [--data PATH]      startet das interaktive Read-Only-REPL");
        Console.WriteLine();
        Console.WriteLine("apply-prices ist standardmäßig nur Vorschau; erst --commit sendet die Preisänderung.");
        Console.WriteLine("Es gibt keine Kauf- oder Transfer-Befehle.");
        Console.WriteLine("Adventure- und Combat-Befehle senden keine Live-POSTs; sie erzeugen nur Vorschauen.");
    }
}
