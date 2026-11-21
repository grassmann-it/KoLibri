using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using KoLibri.Cli.Repl;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;

internal static partial class KolibriCli
{
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
            ?? throw new ArgumentException("--key BUYER_KEY is missing.");
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
        var itemId = GetOption(args, "--item") ?? throw new ArgumentException("--item ITEM_ID is missing.");
        var client = await ReadClientAsync(ParseRole(GetOption(args, "--role") ?? "mallbot"));
        var result = await client.Mall.SearchAsync(itemId, forceRefresh: HasFlag(args, "--refresh"));
        WriteJson(result);
        return 0;
    }

    private static async Task<int> PricegunAsync(string[] args)
    {
        var itemId = GetOption(args, "--item")
            ?? (args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null)
            ?? throw new ArgumentException("--item ITEM_ID is missing.");
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
        if (itemIds.Count == 0) throw new ArgumentException("At least one --item ITEM_ID is required.");
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
                throw new ArgumentException($"Invalid --currency-value '{spec}'. Expected CURRENCY:MEAT.");
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
            rationale = floor is null ? "No current mall price." : "Preview only; buying and opening remain separate approval steps."
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
            Console.Error.WriteLine($"Workflow snapshot unavailable; plan is blocked: {snapshotWarning}");
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
            accepted = "Suggestions only. Before any purchase, the offer, price and seller must be re-checked and explicitly approved."
        });
        return 0;
    }

    private static async Task<int> ApplyPricesAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "mallbot");
        var specifications = GetOptions(args, "--set");
        if (specifications.Count == 0)
            throw new ArgumentException("At least one --set ITEM_ID:EXPECTED:NEW is required.");

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
                $"Invalid --set '{specification}'. Expected ITEM_ID:EXPECTED_PRICE:NEW_PRICE.");
        return new ShopPriceUpdate(fields[0], expected, proposed);
    }
}
