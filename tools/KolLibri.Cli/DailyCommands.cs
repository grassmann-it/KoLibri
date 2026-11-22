using System.Globalization;
using System.Text.Json;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;

internal static partial class KolibriCli
{
    private static async Task<int> DailyCommandAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintDailyHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "campfire" => DailyCampfireAsync(args[1..]),
            "hermit" => DailyHermitAsync(args[1..]),
            "internet-shop" or "internetshop" => DailyInternetShopAsync(args[1..]),
            "meme-shop-read" => DailyMemeShopReadAsync(args[1..]),
            "meme-shop" => DailyMemeShopRunAsync(args[1..]),
            "bribes-sample" => DailyBribesSampleAsync(args[1..]),
            "send-items" => DailySendItemsAsync(args[1..]),
            "item-arbitrage" => DailyItemArbitrageAsync(args[1..]),
            "routine" => DailyRoutineAsync(args[1..]),
            "daily-go" or "go" => await DailyGoAsync(args[1..]),
            "daily-bridge" or "bridge" => await DailyBridgeAsync(args[1..]),
            "breakfast" => await DailyBreakfastAsync(args[1..]),
            _ => Fail($"Unknown daily command: {args[0]}")
        };
    }

    private static int DailyCampfireAsync(string[] args)
    {
        var snapshot = new CampfireLeavesSnapshot
        {
            CampfireLeaves = ReadInt(args, "--leaves", 0),
            MinimumReserve = ReadInt(args, "--reserve", 3),
            DayShortnerPrice = long.TryParse(GetOption(args, "--shortner-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var price) ? Math.Max(0, price) : long.MaxValue,
            AvailableMeat = long.TryParse(GetOption(args, "--meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var meat) ? Math.Max(0, meat) : 0,
            IsUsefulDay = !HasFlag(args, "--not-useful"),
            Knucklebones = ReadInt(args, "--knucklebones", 0)
        };
        var planner = new CampfireLeavesPlanner();
        var plan = planner.Plan(snapshot);
        WriteJson(new
        {
            command = "daily campfire",
            readOnly = true,
            previewOnly = true,
            plan.ShouldBurn,
            plan.Verdict,
            plan.ShouldBuyDayShortner,
            plan.Rationale,
            plan.Steps,
            plan.Snapshot,
            note = "Preview only; no choice.php POST is sent. Re-run with --execute --approval-token via the bounded-runner bridge."
        });
        return 0;
    }

    private static int DailyHermitAsync(string[] args)
    {
        var permits = Math.Max(0, ReadInt(args, "--permits", 3));
        var clovers = Math.Max(0, ReadInt(args, "--clovers", 0));
        var wishes = Math.Max(0, ReadInt(args, "--wishes", 0));
        var reserveAdventureValue = long.TryParse(GetOption(args, "--adventure-value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var adv) ? Math.Max(0, adv) : 0;
        var cloverFloor = long.TryParse(GetOption(args, "--clover-floor"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var floor) ? Math.Max(0, floor) : 0;
        var expectedClover = long.TryParse(GetOption(args, "--clover-sale"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sale) ? Math.Max(0, sale) : 0;
        var wishBuy = Math.Max(0, ReadInt(args, "--wish-buy", 0));
        var wishPrice = long.TryParse(GetOption(args, "--wish-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var price) ? Math.Max(0, price) : 0;

        var planner = new HermitPlanner();
        var plan = planner.Plan(new HermitSnapshot
        {
            Permits = permits,
            Clovers = clovers,
            WishesAvailable = wishes,
            ReserveAdventureValue = reserveAdventureValue,
            MinimumCloverSaleValue = cloverFloor,
            ExpectedCloverSaleValue = expectedClover,
            WishBuyQuantity = wishBuy,
            WishUnitPrice = wishPrice
        });

        WriteJson(new
        {
            command = "daily hermit",
            readOnly = true,
            previewOnly = true,
            plan.Verdict,
            plan.ShouldUseClover,
            plan.ShouldBuyWishes,
            plan.Rationale,
            plan.Steps,
            plan.Snapshot,
            note = "Preview only; no choice.php POST is sent."
        });
        return 0;
    }

    /// <summary>Read-only: one GET of the Internet Meme Shop page, parsed. Sends nothing else.</summary>
    private static int DailyMemeShopReadAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var dispatcher = CreateFreePullDispatcher(role);
        var credentials = TryGetCredentials(role);
        if (dispatcher is null || credentials is null)
        {
            WriteJson(new { command = "daily meme-shop-read", error = "No stored login for this role." });
            return 1;
        }
        var html = dispatcher.ReadMemeShopHtmlAsync(credentials).GetAwaiter().GetResult();
        if (HasFlag(args, "--raw"))
        {
            // Diagnostic: the first item row, with every hidden value redacted.
            var i = html.IndexOf("viral video", StringComparison.OrdinalIgnoreCase);
            var from = html.LastIndexOf("<tr", Math.Max(0, i), StringComparison.OrdinalIgnoreCase);
            var to = html.IndexOf("</tr>", Math.Max(0, i), StringComparison.OrdinalIgnoreCase);
            var row = from >= 0 && to > from ? html[from..(to + 5)] : html[..Math.Min(1500, html.Length)];
            Console.WriteLine(System.Text.RegularExpressions.Regex.Replace(
                row, "([?&]pwd=|value\\s*=\\s*[\"']?)[0-9a-fA-F]{16,}", "$1<redacted>"));
            return 0;
        }
        var page = MemeShopPage.Parse(html);
        WriteJson(new
        {
            command = "daily meme-shop-read",
            readOnly = true,
            bacon = page.Bacon,
            rowsOnOffer = page.Rows.Select(r => new { r.ItemName, r.RowId }).ToArray(),
            dailyItemsMissingFromShop = MemeShopCatalog.DailyItems
                .Where(i => page.Find(i.Name) is null).Select(i => i.Name).ToArray()
        });
        return 0;
    }

    /// <summary>
    /// Today's Internet Meme Shop visit for one role: push the BACON machine if it
    /// was not used today, then buy the daily items BACON allows. Touches nothing
    /// else. Without --execute it only prints the plan.
    /// </summary>
    private static int DailyMemeShopRunAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var execute = HasFlag(args, "--execute");
        var dispatcher = CreateFreePullDispatcher(role);
        var credentials = TryGetCredentials(role);
        if (dispatcher is null || credentials is null)
        {
            WriteJson(new { command = "daily meme-shop", error = "No stored login for this role." });
            return 1;
        }
        var client = ReadClientAsync(role).GetAwaiter().GetResult();
        var doneStore = new DailyDoneStore();
        var live = new LiveDailySnapshotBuilder(client.Api, doneStore)
            .BuildAsync(new DailyRoutineSnapshot(), role: role).GetAwaiter().GetResult();
        var bacon = live.Snapshot.Bacon;
        object? topUp = null;
        if (execute && !HasFlag(args, "--no-top-up") && client.Mall is not null)
        {
            // Fill BACON up to today's remaining set of daily items (machine yield counted),
            // paying at most --max-bacon-price per BACON and keeping a meat reserve.
            var needed = DailyInternetShopPlanner.BaconShortfall(
                live.Snapshot.AlreadyPulledItemIds, bacon,
                live.Snapshot.BaconMachineOwned, live.Snapshot.BaconMachineUsedToday);
            if (needed > 0)
            {
                var maxPrice = long.TryParse(GetOption(args, "--max-bacon-price"), out var mp) ? mp : 1_500;
                var reserve = long.TryParse(GetOption(args, "--reserve"), out var rv) ? rv
                    : role == AccountRole.Mallbot ? 1_000_000 : 200_000;
                var outcome = new BaconArbitrageExecutor(CreateMallBuyClient(role), client.Mall)
                    .TopUpBaconAsync(credentials, needed, live.Meat, maxPrice, reserve)
                    .GetAwaiter().GetResult();
                bacon += outcome.Quantity;
                topUp = new { needed, outcome.Verdict, outcome.Quantity, outcome.Spent, outcome.Rationale, maxPrice };
            }
        }
        var plan = new DailyInternetShopPlanner().Plan(new InternetShopSnapshot
        {
            AlreadyPulledItemIds = live.Snapshot.AlreadyPulledItemIds,
            Bacon = bacon,
            BaconMachineOwned = live.Snapshot.BaconMachineOwned,
            BaconMachineUsedToday = live.Snapshot.BaconMachineUsedToday
        });
        var results = new List<object>();
        if (execute)
        {
            foreach (var step in plan.Steps)
            {
                var outcome = AutoCombatDailyBridge.ExecuteMemeStepAsync(
                    new ChoiceStepSpec(step.ChoiceId, step.OptionId), dispatcher, credentials,
                    doneStore, role.ToString(), CancellationToken.None).GetAwaiter().GetResult();
                results.Add(new { step = step.Description, outcome.Accepted, outcome.Message });
            }
        }
        WriteJson(new
        {
            command = "daily meme-shop",
            readOnly = !execute,
            executed = execute,
            plan.Verdict,
            plan.Rationale,
            plan.Steps,
            plan.Skipped,
            topUp,
            results
        });
        return 0;
    }

    /// <summary>
    /// Measuring step for the Foreign Bribes arbitrage. Bags are bought on the Mallbot, sent to Main
    /// by KMail and opened ONLY on Main (never on the Mallbot). Each bag's yield is logged as an
    /// inventory diff on Main. Without --execute it only prints the plan.
    /// </summary>
    private static int DailyBribesSampleAsync(string[] args)
    {
        var bags = Math.Clamp(ReadInt(args, "--bags", 5), 1, 40);
        var maxPrice = long.TryParse(GetOption(args, "--max-price"), out var mp) ? mp : 40_000;
        var reserve = long.TryParse(GetOption(args, "--reserve"), out var rv) ? rv : 1_000_000;
        var execute = HasFlag(args, "--execute");
        var mallbotCredentials = TryGetCredentials(AccountRole.Mallbot);
        var mainCredentials = TryGetCredentials(AccountRole.Main);
        if (mallbotCredentials is null || mainCredentials is null)
        {
            WriteJson(new { command = "daily bribes-sample", error = "Both Mallbot and Main need a stored login." });
            return 1;
        }
        string mainName;
        try
        {
            mainName = AccountIdentityVerifier.VerifyRoleAsync(AccountSessionManager.CreateDefault(), AccountRole.Main)
                .GetAwaiter().GetResult().Name;
        }
        catch (Exception exception) when (exception is AccountIdentityMismatchException or InvalidOperationException)
        {
            WriteJson(new { command = "daily bribes-sample", error = exception.Message });
            return 1;
        }
        var mallbot = ReadClientAsync(AccountRole.Mallbot).GetAwaiter().GetResult();
        var main = ReadClientAsync(AccountRole.Main).GetAwaiter().GetResult();
        var status = mallbot.Api.FetchStatusAsync(true).GetAwaiter().GetResult();
        var floor = mallbot.Mall.SearchAsync("bag of foreign bribes").GetAwaiter().GetResult()
            .Listings.Where(l => l.Price > 0).OrderBy(l => l.Price).FirstOrDefault();
        if (!execute)
        {
            WriteJson(new
            {
                command = "daily bribes-sample", readOnly = true, buyOn = "Mallbot", sendTo = mainName, openOn = "Main",
                bags, maxPrice, reserve, mallbotMeat = status.Meat, mallFloor = floor?.Price,
                estimatedSpend = floor is null ? (long?)null : floor.Price * bags,
                note = "Add --execute to buy on the Mallbot, send to Main and open on Main."
            });
            return 0;
        }

        var buyer = new BaconArbitrageExecutor(CreateMallBuyClient(AccountRole.Mallbot), mallbot.Mall);
        var buy = buyer.TopUpItemAsync(mallbotCredentials, BaconArbitrageExecutor.Ids.ForeignBribeBag, bags,
            status.Meat, maxPrice, reserve).GetAwaiter().GetResult();
        if (buy.Quantity <= 0)
        {
            WriteJson(new { command = "daily bribes-sample", executed = true, bought = 0, buy.Verdict, buy.Rationale });
            return 0;
        }

        // Send exactly the bought bags to Main by KMail.
        var mallbotHash = CreateFreePullDispatcher(AccountRole.Mallbot)!
            .ReadSessionHashAsync(mallbotCredentials).GetAwaiter().GetResult();
        var mallbotHttp = ReadAuthenticatedHttpAsync(AccountRole.Mallbot).GetAwaiter().GetResult();
        var mainBefore = main.Api.FetchInventoryAsync(true).GetAwaiter().GetResult();
        var sent = !string.IsNullOrWhiteSpace(mallbotHash) && KoLMailbox.SendAsync(mallbotHttp, mallbotHash, mainName,
            "Foreign bribe bags for opening.",
            new[] { new KoLMailAttachment(BaconArbitrageExecutor.Ids.ForeignBribeBag, buy.Quantity) }, 0)
            .GetAwaiter().GetResult();
        if (!sent)
        {
            WriteJson(new { command = "daily bribes-sample", executed = true, bagsBought = buy.Quantity, buy.Spent,
                error = "Bags were bought on the Mallbot but the KMail transfer to Main was not confirmed. Nothing was opened." });
            return 1;
        }

        var arrived = CountItem(main.Api.FetchInventoryAsync(true).GetAwaiter().GetResult(),
            BaconArbitrageExecutor.Ids.ForeignBribeBag) - CountItem(mainBefore, BaconArbitrageExecutor.Ids.ForeignBribeBag);
        if (arrived < buy.Quantity)
        {
            WriteJson(new { command = "daily bribes-sample", executed = true, bagsBought = buy.Quantity, buy.Spent, arrivedOnMain = arrived,
                error = "Not all bags arrived on Main yet. Nothing was opened." });
            return 1;
        }

        // Opening happens on Main only.
        var mainHash = CreateFreePullDispatcher(AccountRole.Main)!.ReadSessionHashAsync(mainCredentials).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(mainHash))
        {
            WriteJson(new { command = "daily bribes-sample", executed = true, bagsBought = buy.Quantity, error = "No request hash for Main; bags are on Main, not opened." });
            return 1;
        }
        var actions = new CharacterActionClient(ReadAuthenticatedHttpAsync(AccountRole.Main).GetAwaiter().GetResult(), mainHash);
        var before = main.Api.FetchInventoryAsync(true).GetAwaiter().GetResult();
        var perBag = new List<object>();
        var previous = before;
        var opened = 0;
        for (var i = 0; i < buy.Quantity; i++)
        {
            var result = actions.ExecuteAsync(new UseInventoryItem(BaconArbitrageExecutor.Ids.ForeignBribeBag))
                .GetAwaiter().GetResult();
            perBag.Add(new { bag = i + 1, rejected = result.RejectionReason, gains = InventoryDiff(previous, result.Inventory) });
            previous = result.Inventory;
            if (result.RejectionReason is not null) break;
            opened++;
        }
        var total = InventoryDiff(before, previous);
        var record = new
        {
            at = DateTimeOffset.UtcNow, boughtOn = "Mallbot", openedOn = "Main", bagsBought = buy.Quantity, spent = buy.Spent,
            bagsOpened = opened, perBag, total
        };
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KoLibri", "bribe-samples.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, System.Text.Json.JsonSerializer.Serialize(record) + Environment.NewLine);
        WriteJson(new { command = "daily bribes-sample", executed = true, record.bagsBought, record.spent, record.bagsOpened, record.total, record.perBag, logPath = path });
        return 0;
    }

    private static int CountItem(KoLInventorySnapshot inventory, int itemId)
        => inventory.Items.Where(i => i.ItemId == itemId.ToString(CultureInfo.InvariantCulture)).Sum(i => i.Quantity);

    private static object[] InventoryDiff(KoLInventorySnapshot before, KoLInventorySnapshot after)
    {
        static Dictionary<string, (string Name, int Qty)> Map(KoLInventorySnapshot snap)
            => snap.Items.GroupBy(i => i.ItemId).ToDictionary(g => g.Key,
                g => (g.First().Name, g.Sum(i => i.Quantity)));
        var a = Map(before);
        var b = Map(after);
        return b.Keys.Union(a.Keys)
            .Select(id => new
            {
                itemId = id,
                name = b.TryGetValue(id, out var n) ? n.Name : a[id].Name,
                delta = (b.TryGetValue(id, out var x) ? x.Qty : 0) - (a.TryGetValue(id, out var y) ? y.Qty : 0)
            })
            .Where(d => d.delta != 0 && d.itemId != BaconArbitrageExecutor.Ids.ForeignBribeBag.ToString())
            .OrderByDescending(d => Math.Abs(d.delta))
            .Cast<object>().ToArray();
    }

    /// <summary>
    /// Sends items from one account to the other by KMail attachment and checks that they arrived.
    /// Without --execute it only prints what would be sent. Meat is never sent.
    /// </summary>
    private static int DailySendItemsAsync(string[] args) => DailySendItemsCore(args, WriteJson);

    /// <summary>The transfer itself; the JSON result goes to <paramref name="emit"/> so a caller can embed it.</summary>
    private static int DailySendItemsCore(string[] args, Action<object> emit)
    {
        var from = ParseRole(GetOption(args, "--from") ?? "main");
        var to = ParseRole(GetOption(args, "--to") ?? (from == AccountRole.Main ? "mallbot" : "main"));
        var itemId = ReadInt(args, "--item", 0);
        var quantity = ReadInt(args, "--qty", 1);
        var execute = HasFlag(args, "--execute");
        if (from == to || itemId <= 0 || quantity <= 0)
        {
            emit(new { command = "daily send-items", error = "Need two different roles, --item ID and --qty N." });
            return 1;
        }
        var fromCredentials = TryGetCredentials(from);
        if (fromCredentials is null)
        {
            emit(new { command = "daily send-items", error = "Missing stored login or recipient account." });
            return 1;
        }
        string recipient;
        try
        {
            // The target is the player KoL reports for the target role's live session, not a stored name.
            recipient = AccountIdentityVerifier.VerifyRoleAsync(AccountSessionManager.CreateDefault(), to)
                .GetAwaiter().GetResult().Name;
        }
        catch (Exception exception) when (exception is AccountIdentityMismatchException or InvalidOperationException)
        {
            WriteJson(new { command = "daily send-items", error = exception.Message });
            return 1;
        }
        var sender = ReadClientAsync(from).GetAwaiter().GetResult();
        var receiver = ReadClientAsync(to).GetAwaiter().GetResult();
        var senderHas = CountItem(sender.Api.FetchInventoryAsync(true).GetAwaiter().GetResult(), itemId);
        var receiverBefore = CountItem(receiver.Api.FetchInventoryAsync(true).GetAwaiter().GetResult(), itemId);
        if (!execute || senderHas < quantity)
        {
            emit(new
            {
                command = "daily send-items", readOnly = !execute, from = from.ToString(), to = to.ToString(), recipient,
                itemId, quantity, senderHas, receiverBefore,
                error = senderHas < quantity ? "Sender holds fewer than requested; nothing sent." : null,
                note = "Add --execute to send."
            });
            return senderHas < quantity ? 1 : 0;
        }
        var hash = CreateFreePullDispatcher(from)!.ReadSessionHashAsync(fromCredentials).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(hash))
        {
            emit(new { command = "daily send-items", error = "No request hash for the sender; nothing sent." });
            return 1;
        }
        var http = ReadAuthenticatedHttpAsync(from).GetAwaiter().GetResult();
        var confirmed = KoLMailbox.SendAsync(http, hash, recipient, "Items from KoLibri.",
            new[] { new KoLMailAttachment(itemId, quantity) }, 0).GetAwaiter().GetResult();
        var senderAfter = CountItem(sender.Api.FetchInventoryAsync(true).GetAwaiter().GetResult(), itemId);
        var receiverAfter = CountItem(receiver.Api.FetchInventoryAsync(true).GetAwaiter().GetResult(), itemId);
        emit(new
        {
            command = "daily send-items", executed = true, confirmed, from = from.ToString(), to = to.ToString(),
            itemId, quantity, senderBefore = senderHas, senderAfter, receiverBefore, receiverAfter,
            arrived = receiverAfter - receiverBefore >= quantity
        });
        return confirmed ? 0 : 1;
    }

    /// <summary>
    /// Buys a tradeable item when the cheapest mall listing is at least --min-margin below the recent
    /// Pricegun median, optionally on either account, and sends the purchase to the other account
    /// (normally the Mallbot, which owns the store). Without --execute it only prints the decision.
    /// </summary>
    private static int DailyItemArbitrageAsync(string[] args)
    {
        var itemId = ReadInt(args, "--item", 10881);
        var itemName = GetOption(args, "--name") ?? (itemId == 10881 ? "11-leaf clover" : null);
        var buyOn = ParseRole(GetOption(args, "--buy-on") ?? "main");
        var sendTo = GetOption(args, "--send-to") is { } st ? ParseRole(st) : (AccountRole?)null;
        var qty = Math.Clamp(ReadInt(args, "--qty", 10), 1, 200);
        var margin = double.TryParse(GetOption(args, "--min-margin"), NumberStyles.Float, CultureInfo.InvariantCulture, out var mg) ? mg : 0.10;
        var execute = HasFlag(args, "--execute");
        var credentials = TryGetCredentials(buyOn);
        if (string.IsNullOrWhiteSpace(itemName) || credentials is null || margin is < 0 or >= 1)
        {
            WriteJson(new { command = "daily item-arbitrage", error = "Need --item with --name, a stored login and 0 <= --min-margin < 1." });
            return 1;
        }
        var client = ReadClientAsync(buyOn).GetAwaiter().GetResult();
        using var pricegunHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var history = new PricegunReadClient(pricegunHttp).FetchAsync(itemId.ToString(CultureInfo.InvariantCulture), forceRefresh: true)
            .GetAwaiter().GetResult();
        var prices = history.Sales.Select(x => (long)x.UnitPrice).ToArray();
        var median = ArbitrageRule.Median(prices);
        var maxPriceOrNull = ArbitrageRule.MaxBuyPrice(prices, margin);
        if (median is null || maxPriceOrNull is null)
        {
            WriteJson(new { command = "daily item-arbitrage", error = $"Only {prices.Length} recent sales; not enough to judge a price." });
            return 1;
        }
        var maxPrice = maxPriceOrNull.Value;
        // A partial name can match other items: only offers of the requested item id count.
        var cheapest = client.Mall.SearchAsync(itemName).GetAwaiter().GetResult()
            .Listings.Where(l => l.Price > 0 && BaconArbitrageExecutor.ListingMatchesItem(l, itemId))
            .OrderBy(l => l.Price).FirstOrDefault();
        var meat = client.Api.FetchStatusAsync(true).GetAwaiter().GetResult().Meat;
        var reserve = long.TryParse(GetOption(args, "--reserve"), out var rv) ? rv : buyOn == AccountRole.Mallbot ? 1_000_000 : 200_000;
        var worthBuying = cheapest is not null && cheapest.Price <= maxPrice;
        object decision = new
        {
            command = "daily item-arbitrage", readOnly = !execute, item = itemName, itemId, buyOn = buyOn.ToString(),
            sendTo = sendTo?.ToString(), sales = prices.Length, median, minMargin = margin, maxPrice,
            cheapestListing = cheapest?.Price, worthBuying, meat, reserve, qty
        };
        if (!execute || !worthBuying)
        {
            WriteJson(decision);
            return 0;
        }
        var buy = new BaconArbitrageExecutor(CreateMallBuyClient(buyOn), client.Mall)
            .TopUpItemAsync(credentials, itemId, qty, meat, maxPrice, reserve, itemName: itemName).GetAwaiter().GetResult();
        string? transfer = null;
        object? transferResult = null;
        if (buy.Quantity > 0 && sendTo is { } target && target != buyOn)
        {
            var code = DailySendItemsCore(new[]
            {
                "--from", buyOn.ToString().ToLowerInvariant(), "--to", target.ToString().ToLowerInvariant(),
                "--item", itemId.ToString(CultureInfo.InvariantCulture), "--qty", buy.Quantity.ToString(CultureInfo.InvariantCulture), "--execute"
            }, doc => transferResult = doc);
            transfer = code == 0 ? "sent" : "send-failed";
        }
        // One JSON document; a failed transfer or an unclear purchase is a non-zero exit.
        WriteJson(new { decision, bought = buy.Quantity, spent = buy.Spent, buy.Verdict, buy.Rationale, unclear = buy.Unclear, transfer, transferResult });
        return transfer == "send-failed" || buy.Unclear ? 1 : 0;
    }

    private static int DailyInternetShopAsync(string[] args)
    {
        var pulled = GetOptions(args, "--already-claimed").ToArray();
        var meat = long.TryParse(GetOption(args, "--meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var meatOverride) ? Math.Max(0, meatOverride) : 0;
        var plan = new DailyInternetShopPlanner().Plan(new InternetShopSnapshot
        {
            AlreadyPulledItemIds = pulled,
            AvailableMeat = meat,
            Bacon = ReadInt(args, "--bacon", 0),
            BaconMachineOwned = HasFlag(args, "--machine"),
            BaconMachineUsedToday = HasFlag(args, "--machine-used")
        });
        WriteJson(new
        {
            command = "daily internet-shop",
            readOnly = true,
            previewOnly = true,
            plan.Verdict,
            plan.Rationale,
            plan.Steps,
            plan.Skipped,
            note = "Preview only; no choice POST is sent. The bounded runner can replay each step after --execute --approval-token."
        });
        return 0;
    }

    private static void PrintDailyHelp()
    {
        Console.WriteLine("  daily campfire [--leaves N] [--reserve N] [--knucklebones N]");
        Console.WriteLine("                 [--shortner-price N] [--meat N] [--not-useful]");
        Console.WriteLine("  daily hermit [--permits N] [--clovers N] [--adventure-value N]");
        Console.WriteLine("              [--clover-floor N] [--reserve N]");
        Console.WriteLine("  daily internet-shop [--already-claimed NAME] [--bacon N] [--machine] [--machine-used]");
        Console.WriteLine("  daily routine [--leaves N] [--permits N] [--clovers N] [--main-meat N] [--mallbot-meat N]");
        Console.WriteLine("                 [--bag-price N] [--bag-stock N] [--already-claimed NAME]");
        Console.WriteLine("                 [--mall-price N] [--bacon-median N] [--owned-machines N]");
        Console.WriteLine("                 [--adventure-value N] [--clover-floor N] [--clover-sale N]");
        Console.WriteLine("                 [--output PATH] [--execute --approval-token TOKEN --role main|mallbot]");
        Console.WriteLine("  daily go [--role main|mallbot|all] [--main-leaves N] [--mallbot-leaves N] [--main-meat N]");
        Console.WriteLine("           [--mallbot-meat N] [--main-permits N] [--mallbot-permits N]");
        Console.WriteLine("           [--bag-price N] [--bag-stock N] [--execute --approval-token TOKEN]");
        Console.WriteLine("           [--already-claimed NAME] [--output PATH]");
    }

    private static AdventureExecutionGateway CreateAdventureGateway(AccountRole role, string? approvalToken)
    {
        var token = approvalToken ?? throw new ArgumentException(
            "Live daily execution requires --approval-token.");
        var approval = new DelegateAdventureApprovalHook((_, _) =>
            ValueTask.FromResult(AdventureApprovalDecision.Allow(token)));
        var verification = new DelegateAdventureVerificationHook((context, _) =>
            ValueTask.FromResult(context.Response.Accepted != false
                ? AdventureVerificationResult.Success()
                : AdventureVerificationResult.Failed("The live response was rejected by KoL.")));
        var session = KoLAdventureSessionFactory
            .CreateAsync(role, approval, verification, cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return session.Gateway;
    }

    private static async Task<int> DailyGoAsync(string[] args)
    {
        var roles = ResolveRoles(args);
        if (roles.Count == 0)
            throw new ArgumentException("daily-go requires at least one role (main, mallbot, all).");

        var defaultSnapshot = BuildDailySnapshot(args);

        var overrides = roles.Select(role =>
        {
            var snapshot = defaultSnapshot;
            var prefix = role == AccountRole.Main ? "main" : "mallbot";
            var leavesOverride = ReadInt(args, $"--{prefix}-leaves", -1);
            var meatOverride = long.TryParse(GetOption(args, $"--{prefix}-meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : -1L;
            var permitsOverride = ReadInt(args, $"--{prefix}-permits", -1);
            var bagPriceOverride = long.TryParse(GetOption(args, $"--{prefix}-bag-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bp) ? bp : -1L;
            if (leavesOverride >= 0) snapshot = snapshot with { CampfireLeaves = leavesOverride };
            if (meatOverride >= 0) snapshot = role == AccountRole.Main
                ? snapshot with { MainMeat = meatOverride }
                : snapshot with { MallbotMeat = meatOverride };
            if (permitsOverride >= 0) snapshot = snapshot with { HermitPermits = permitsOverride };
            if (bagPriceOverride >= 0) snapshot = snapshot with { ForeignBribeBagPrice = bagPriceOverride };
            return new RoleOverride(role, snapshot);
        }).ToArray();

        var token = GetOption(args, "--approval-token");
        if (HasFlag(args, "--execute") && !await AgentGuard.ApprovalTokenValidAsync(token).ConfigureAwait(false))
            throw new ArgumentException("The supplied approval token is missing or does not match the stored approval token. Nothing was sent.");
        var useBridge = true; // the legacy choice-chain executor is gone; --use-bridge is accepted and ignored
        BridgeAuditLog? bridgeAudit = useBridge ? new BridgeAuditLog(BridgeAuditLog.DefaultPath()) : null;
        var orchestrator = new DailyOrchestrator(
            gatewayFactory: role => CreateAdventureGateway(role, token),
            approvalToken: token,
            stateReaderFactory: _ => new OfflineAccountDailyStateReader(),
            freePullFactory: role => CreateFreePullDispatcher(role),
            credentialsFactory: role => TryGetCredentials(role));
        var plan = orchestrator.BuildPlan(overrides, defaultSnapshot);

        DailyOrchestrationResult? execution = null;
        if (HasFlag(args, "--execute"))
        {
            execution = orchestrator.ExecuteBridgeAsync(plan, audit: bridgeAudit).GetAwaiter().GetResult();
        }
        bridgeAudit?.Dispose();

        WriteJson(new
        {
            command = "daily-go",
            readOnly = execution is null,
            previewOnly = execution is null,
            executed = execution is not null,
            plan.GeneratedAt,
            plan.TotalApprovableSteps,
            roles = plan.Plans.Select(p => new
            {
                role = p.Role.ToString(),
                approvableSteps = p.Preview.ApprovableSteps,
                verdict = p.Preview.Verdict,
                advisories = p.Preview.Advisories
            }).ToArray(),
            execution = execution is null ? null : new
            {
                succeededRoleCount = execution.SucceededRoleCount,
                failedRoleCount = execution.FailedRoleCount,
                perRole = execution.Results.Select(r => new
                {
                    role = r.Plan.Role.ToString(),
                    executed = r.Execution.Executed,
                    steps = r.Execution.Steps.Select(s => new
                    {
                        s.StepKind, s.Location, status = s.Status.ToString(),
                        s.StopReason, s.RequestId, s.Message
                    }).ToArray()
                }).ToArray()
            }
        });

        var outputPath = GetOption(args, "--output");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var full = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, JsonSerializer.Serialize(new
            {
                generatedAt = plan.GeneratedAt,
                totalApprovableSteps = plan.TotalApprovableSteps,
                roles = plan.Plans.Select(p => new
                {
                    role = p.Role.ToString(),
                    verdict = p.Preview.Verdict,
                    approvableSteps = p.Preview.ApprovableSteps
                }).ToArray(),
                execution
            }, JsonOptions));
            Console.Error.WriteLine($"daily-go report written to {full}");
        }
        return 0;
    }

    private static List<AccountRole> ResolveRoles(string[] args)
    {
        var requested = (GetOption(args, "--role") ?? "main").Trim().ToLowerInvariant();
        if (requested is "all" or "both")
            return new() { AccountRole.Main, AccountRole.Mallbot };
        return new() { ParseRole(requested) };
    }

    private static int DailyRoutineAsync(string[] args)
    {
        var snapshot = new DailyRoutineSnapshot
        {
            CampfireLeaves = ReadInt(args, "--leaves", 0),
            CampfireReserve = ReadInt(args, "--campfire-reserve", 3),
            DayShortnerPrice = long.TryParse(GetOption(args, "--shortner-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sp) ? Math.Max(0, sp) : long.MaxValue,
            MainMeat = long.TryParse(GetOption(args, "--main-meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm) ? Math.Max(0, mm) : 0,
            HermitPermits = ReadInt(args, "--permits", 0),
            Clovers = ReadInt(args, "--clovers", 0),
            AdventureReserveValue = long.TryParse(GetOption(args, "--adventure-value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var adv) ? Math.Max(0, adv) : 0,
            CloverFloorValue = long.TryParse(GetOption(args, "--clover-floor"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cf) ? Math.Max(0, cf) : 0,
            ExpectedCloverSaleValue = long.TryParse(GetOption(args, "--clover-sale"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cs) ? Math.Max(0, cs) : 0,
            WishBuyQuantity = ReadInt(args, "--wish-buy", 0),
            WishUnitPrice = long.TryParse(GetOption(args, "--wish-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wp) ? Math.Max(0, wp) : 0,
            UselessItems = ReadInt(args, "--useless-items", 0),
            AlreadyPulledItemIds = GetOptions(args, "--already-claimed").ToArray(),
            MallbotMeat = long.TryParse(GetOption(args, "--mallbot-meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) ? Math.Max(0, mb) : 0,
            ForeignBribeBagPrice = long.TryParse(GetOption(args, "--bag-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bp) ? Math.Max(0, bp) : long.MaxValue,
            ForeignBribeBagStock = ReadInt(args, "--bag-stock", 0),
            PilotBagQuantity = ReadInt(args, "--pilot-bags", 40),
            MallbotMeatReserve = long.TryParse(GetOption(args, "--mallbot-reserve"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mr) ? Math.Max(0, mr) : 1_000_000
        };

        var report = new DailyRoutineRunner().Run(snapshot);

        if (HasFlag(args, "--execute"))
        {
            Console.Error.WriteLine("daily routine is preview-only. Use 'daily go --execute' (live state, confirmed steps).");
            return 2;
        }
        DailyRoutineExecutionResult? executionResult = null;

        WriteJson(new
        {
            command = "daily routine",
            readOnly = executionResult is null,
            previewOnly = executionResult is null,
            executed = executionResult?.Executed ?? false,
            report.GeneratedAt,
            report.Verdict,
            report.Counts,
            report.ApprovableSteps,
            report.Advisories,
            subPlans = new
            {
                campfire = new
                {
                    report.Campfire.Verdict,
                    report.Campfire.ShouldBurn,
                    report.Campfire.ShouldBuyDayShortner,
                    report.Campfire.Rationale,
                    report.Campfire.Steps
                },
                hermit = new
                {
                    report.Hermit.Verdict,
                    report.Hermit.ShouldUseClover,
                    report.Hermit.ShouldBuyWishes,
                    report.Hermit.Rationale,
                    report.Hermit.Steps
                },
                internetShop = new
                {
                    report.InternetShop.Verdict,
                    report.InternetShop.Rationale,
                    report.InternetShop.Steps,
                    report.InternetShop.Skipped
                },
                foreignBribes = report.ForeignBribes
            },
            execution = executionResult is null
                ? null
                : new
                {
                    role = executionResult.Role.ToString(),
                    auditPath = executionResult.AuditPath,
                    steps = executionResult.Steps.Select(s => new
                    {
                        s.StepKind, s.Location, s.RunId, status = s.Status.ToString(),
                        s.RequestId, s.StopReason, s.Message
                    }).ToArray()
                },
            note = executionResult is null
                ? "Preview only; nothing was sent. Use the bounded-runner bridge to commit each step after --execute --approval-token."
                : $"Live execution {executionResult.Steps.Count} step(s); see auditPath + steps for details."
        });

        var outputPath = GetOption(args, "--output");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var full = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, JsonSerializer.Serialize(new
            {
                generatedAt = report.GeneratedAt,
                verdict = report.Verdict,
                counts = report.Counts,
                approvableSteps = report.ApprovableSteps,
                advisories = report.Advisories,
                snapshot,
                execution = executionResult is null ? null : new
                {
                    executionResult.Role,
                    executionResult.AuditPath,
                    executionResult.Steps
                }
            }, JsonOptions));
            Console.Error.WriteLine($"daily routine report written to {full}");
        }
        return 0;
    }

    private static async Task<int> DailyBreakfastAsync(string[] args)
    {
        // "Breakfast" reads the live accounts, derives the daily numbers
        // itself and runs the routine. The operator passes no inventory
        // flags — everything comes from KoL.
        var roles = ResolveRoles(args);
        if (roles.Count == 0)
            throw new ArgumentException(
                "breakfast requires at least one role (main, mallbot, all).");
        var token = GetOption(args, "--approval-token");
        var execute = HasFlag(args, "--execute");
        var baseline = BuildDailySnapshot(args);
        if (execute && !await AgentGuard.ApprovalTokenValidAsync(token).ConfigureAwait(false))
            throw new ArgumentException("The supplied approval token is missing or does not match the stored approval token. Nothing was sent.");
        var mayPurchase = AgentGuard.MayPurchase(execute, !string.IsNullOrWhiteSpace(token));

        var perRole = new List<object>();
        var bridge = new AutoCombatDailyBridge();
        BridgeAuditLog? audit = execute && !string.IsNullOrWhiteSpace(token)
            ? new BridgeAuditLog(BridgeAuditLog.DefaultPath())
            : null;
        try
        {
            foreach (var role in roles)
            {
                var client = await ReadClientAsync(role);
                var doneStore = new DailyDoneStore();
                var builder = new LiveDailySnapshotBuilder(client.Api, doneStore);
                var live = await builder.BuildAsync(baseline, role: role);
                var plan = bridge.Plan(live.State, live.Snapshot);
                AutoCombatDailyBridge.BridgeExecutionResult? execution = null;
                if (execute && !string.IsNullOrWhiteSpace(token))
                {
                    var gateway = CreateAdventureGateway(role, token);
                    execution = await bridge.ExecuteAsync(
                        plan.Steps, gateway,
                        freePullDispatcher: CreateFreePullDispatcher(role),
                        freePullCredentials: TryGetCredentials(role),
                        audit: audit, accountId: role.ToString(),
                        doneStore: doneStore, doneAccountKey: role.ToString());
                }

                // Restock + arbitrage: the mall side of the daily routine.
                // Both accounts may buy, so each role runs its own plan with
                // its own meat pool and its own daily shop limit.
                var arbitrage = new List<object>();
                if (client.Mall is not null)
                {
                    var buyClient = CreateMallBuyClient(role);
                    var credentials = TryGetCredentials(role);
                    var restock = new CloverRestockExecutor(buyClient, client.Mall);
                    var cloverTarget = ReadInt(args, "--clover-target", 3);
                    var cloverCeiling = long.TryParse(GetOption(args, "--clover-max-price"), out var cp) ? cp : 0;
                    var restockPlan = await restock.PlanAsync(
                        live.Clovers, cloverTarget, live.Meat, cloverCeiling)
                        .ConfigureAwait(false);
                    var restockVerdict = restockPlan.Verdict;
                    var restockBought = false;
                    var restockQuantity = 0;
                    var restockSpent = 0L;
                    var restockRationale = restockPlan.Rationale;
                    if (execute && !string.IsNullOrWhiteSpace(token) && credentials is not null)
                    {
                        var restockOutcome = await restock.RunAsync(
                            credentials, live.Clovers, cloverTarget,
                            live.Meat, cloverCeiling).ConfigureAwait(false);
                        restockVerdict = restockOutcome.Verdict;
                        restockBought = restockOutcome.Bought;
                        restockQuantity = restockOutcome.Quantity;
                        restockSpent = restockOutcome.Spent;
                        restockRationale = restockOutcome.Rationale;
                    }
                    arbitrage.Add(new
                    {
                        kind = "clover-restock",
                        verdict = restockVerdict,
                        bought = restockBought,
                        quantity = restockQuantity,
                        spent = restockSpent,
                        rationale = restockRationale
                    });

                    var baconMedian = long.TryParse(GetOption(args, "--bacon-median"), out var bm) ? bm : 0;
                    if (baconMedian > 0 && mayPurchase)
                    {
                        var bacon = new BaconArbitrageExecutor(buyClient, client.Mall);
                        var baconOutcome = await bacon.RunAsync(
                            credentials ?? throw new InvalidOperationException("no credentials"),
                            live.BaconMachines, live.Meat, baconMedian,
                            minimumProfitPerUnit: long.TryParse(GetOption(args, "--bacon-min-profit"), out var bp) ? bp : 50_000,
                            maximumQuantity: ReadInt(args, "--bacon-max-qty", 10))
                            .ConfigureAwait(false);
                        arbitrage.Add(new
                        {
                            kind = "bacon-arbitrage",
                            verdict = baconOutcome.Verdict,
                            bought = baconOutcome.Bought,
                            quantity = baconOutcome.Quantity,
                            spent = baconOutcome.Spent,
                            rationale = baconOutcome.Rationale
                        });
                    }
                }

                perRole.Add(new
                {
                    role = role.ToString(),
                    live = new
                    {
                        live.Meat,
                        live.Adventures,
                        live.Level,
                        live.ClassId,
                        live.Leaves,
                        live.Clovers,
                        live.Permits,
                        live.WorthlessItems,
                        live.Bacon,
                        live.BaconMachines
                    },
                    steps = plan.Steps.Select(s => new
                    {
                        kind = s.GetType().Name,
                        choiceId = s is ChoiceStepSpec cs ? cs.ChoiceId : null,
                        optionId = s is ChoiceStepSpec cs2 ? cs2.OptionId : null,
                        itemId = s is ItemStepSpec is2 ? is2.ItemId : null,
                        quantity = s is ItemStepSpec is3 ? is3.Quantity : (int?)null
                    }),
                    allowlist = plan.AllowlistedChoiceIds,
                    executed = execution?.Executed ?? false,
                    arbitrage,
                    auditPath = execution?.AuditPath
                });
            }
        }
        finally
        {
            audit?.Dispose();
        }

        WriteJson(new
        {
            command = "daily breakfast",
            readOnly = !execute,
            executed = execute && !string.IsNullOrWhiteSpace(token),
            roles = perRole
        });
        return 0;
    }

    private static async Task<int> DailyBridgeAsync(string[] args)
    {
        // The bridge path runs AutoCombatDailyBridge directly with a
        // snapshot-derived AccountDailyState. Read-only is fully self-
        // contained; with --execute + --approval-token we route each
        // choice step through the bounded runner and write a hash-chained
        // audit row per submission.
        var roles = ResolveRoles(args);
        var token = GetOption(args, "--approval-token");
        var execute = HasFlag(args, "--execute");
        if (execute && !await AgentGuard.ApprovalTokenValidAsync(token).ConfigureAwait(false))
            throw new ArgumentException("The supplied approval token is missing or does not match the stored approval token. Nothing was sent.");
        var snapshot = BuildDailySnapshot(args);
        var bridge = new AutoCombatDailyBridge();
        var emptyState = new AccountDailyState
        {
            Role = AccountRole.Main,
            Status = new KoLStatusSnapshot("offline", 0, 0, 0, DateTimeOffset.UtcNow)
                with { ClassId = "" },
            Inventory = new KoLInventorySnapshot(Array.Empty<KoLInventoryItem>(), DateTimeOffset.UtcNow),
            ObservedAt = DateTimeOffset.UtcNow
        };
        var plan = bridge.Plan(emptyState, snapshot);
        AutoCombatDailyBridge.BridgeExecutionResult? bridgeExecution = null;
        if (execute && !string.IsNullOrWhiteSpace(token))
        {
            using var audit = new BridgeAuditLog(BridgeAuditLog.DefaultPath());
            // One gateway per role; the bridge posts each step in turn.
            var tasks = roles.Select(async role =>
            {
                var gateway = CreateAdventureGateway(role, token);
                var exec = await bridge.ExecuteAsync(
                    plan.Steps, gateway, audit: audit,
                    accountId: role.ToString()).ConfigureAwait(false);
                return (role, exec);
            }).ToArray();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            bridgeExecution = results[0].exec;
        }
        var result = new
        {
            command = "daily-bridge",
            readOnly = !execute,
            executed = bridgeExecution?.Executed ?? (execute && !string.IsNullOrWhiteSpace(token)),
            roles = roles.Select(r => r.ToString()).ToArray(),
            steps = plan.Steps.Select(s => new
            {
                kind = s.GetType().Name,
                @switch = s
            }),
            allowlist = plan.AllowlistedChoiceIds,
            auditPath = bridgeExecution?.AuditPath,
            executedCount = bridgeExecution is null ? 0 : plan.Steps.OfType<ChoiceStepSpec>().Count()
        };
        WriteJson(result);
        return 0;
    }

    /// <summary>Snapshot-derived <see cref="IAccountDailyStateReader"/> that
    /// returns an empty state, so the bridge predicate layer fires only on
    /// snapshot-driven decisions (campfire leaves, internet-shop pulls).
    /// Item-driven decisions (archaeologist + spade) stay quiet.</summary>
    private sealed class OfflineAccountDailyStateReader : IAccountDailyStateReader
    {
        public Task<AccountDailyState> ReadAsync(AccountRole role, CancellationToken cancellationToken)
        {
            return Task.FromResult(new AccountDailyState
            {
                Role = role,
                Status = new KoLStatusSnapshot("offline", 0, 0, 0, DateTimeOffset.UtcNow) with { ClassId = "" },
                Inventory = new KoLInventorySnapshot(Array.Empty<KoLInventoryItem>(), DateTimeOffset.UtcNow),
                ObservedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private static MallBuyClient CreateMallBuyClient(AccountRole role)
    {
        var http = ReadAuthenticatedHttpAsync(role).GetAwaiter().GetResult();
        return new MallBuyClient(http, MallBuyClient.DefaultBaseUri);
    }

    private static FreePullDispatcher? CreateFreePullDispatcher(AccountRole role)
    {
        // The free-pull dispatcher needs a logged-in HttpClient. We reuse
        // the same authenticated client as the adventure gateway so the
        // cookie travels with both request paths.
        var http = ReadAuthenticatedHttpAsync(role).GetAwaiter().GetResult();
        return new FreePullDispatcher(http, FreePullDispatcher.DefaultBaseUri);
    }

    private static AccountCredentials? TryGetCredentials(AccountRole role)
    {
        try
        {
            var manager = AccountSessionManager.CreateDefault();
            manager.InitializeAsync().GetAwaiter().GetResult();
            return manager.GetSessionAsync(role).GetAwaiter().GetResult()?.Credentials;
        }
        catch
        {
            return null;
        }
    }

    private static DailyRoutineSnapshot BuildDailySnapshot(string[] args)
    {
        return new DailyRoutineSnapshot
        {
            CampfireLeaves = ReadInt(args, "--leaves", 0),
            CampfireReserve = ReadInt(args, "--campfire-reserve", 3),
            DayShortnerPrice = long.TryParse(GetOption(args, "--shortner-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sp) ? Math.Max(0, sp) : long.MaxValue,
            MainMeat = long.TryParse(GetOption(args, "--main-meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm) ? Math.Max(0, mm) : 0,
            HermitPermits = ReadInt(args, "--permits", 0),
            Clovers = ReadInt(args, "--clovers", 0),
            AdventureReserveValue = long.TryParse(GetOption(args, "--adventure-value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var adv) ? Math.Max(0, adv) : 0,
            CloverFloorValue = long.TryParse(GetOption(args, "--clover-floor"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cf) ? Math.Max(0, cf) : 0,
            ExpectedCloverSaleValue = long.TryParse(GetOption(args, "--clover-sale"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cs) ? Math.Max(0, cs) : 0,
            WishBuyQuantity = ReadInt(args, "--wish-buy", 0),
            WishUnitPrice = long.TryParse(GetOption(args, "--wish-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wp) ? Math.Max(0, wp) : 0,
            HermitCloversWanted = ReadInt(args, "--hermit-clovers", 0),
            UselessItems = ReadInt(args, "--useless-items", 0),
            AlreadyPulledItemIds = GetOptions(args, "--already-claimed").ToArray(),
            MallbotMeat = long.TryParse(GetOption(args, "--mallbot-meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) ? Math.Max(0, mb) : 0,
            ForeignBribeBagPrice = long.TryParse(GetOption(args, "--bag-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bp) ? Math.Max(0, bp) : long.MaxValue,
            ForeignBribeBagStock = ReadInt(args, "--bag-stock", 0),
            PilotBagQuantity = ReadInt(args, "--pilot-bags", 40),
            MallbotMeatReserve = long.TryParse(GetOption(args, "--mallbot-reserve"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mr) ? Math.Max(0, mr) : 1_000_000
        };
    }
}
