using System.Net;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;

// Dependency-free smoke harness: no real account, credentials, or network.
await OfflineChecks.RunAsync();

static class OfflineChecks
{
    public static async Task RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("login challenge response", LoginChallengeAsync),
            ("encrypted account session round-trip", AccountRoundTripAsync),
            ("mall HTML parsing and cache", MallParsingAsync),
            ("shop price mutation preview and verification", ShopMutationAsync),
            ("Pricegun history parsing and cache", PricegunParsingAsync),
            ("foreign bribe catalog", ForeignBribeCatalogAsync),
            ("buyer CRM ledger and contact intent", BuyerCrmAsync),
            ("workflow plan dry run", WorkflowPlanAsync),
            ("adventure plan preview", AdventurePlannerAsync),
            ("pricing recommendation", PricingAsync),
            ("inventory categorization", CategorizationAsync)
        };

        foreach (var (name, run) in checks)
        {
            await run();
            Console.WriteLine($"PASS  {name}");
        }
        Console.WriteLine($"{checks.Length} offline checks passed.");
    }

    private static Task LoginChallengeAsync()
    {
        // Known KoL challenge-response vector: md5(md5(password) + ':' + challenge).
        var response = KoLLoginClient.ComputeChallengeResponse("secret", "abc123");
        Assert(response == "fa90712a698d2eb1f6624086a9676792", $"Unexpected challenge response: {response}");
        AssertThrows<ArgumentException>(() => KoLLoginClient.ComputeChallengeResponse("secret", ""));
        return Task.CompletedTask;
    }

    private static async Task AccountRoundTripAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kolibri-offline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var accounts = new JsonFileAccountStore(Path.Combine(root, "accounts.json"));
            var secrets = new FileSecretStore(root, Path.Combine(root, ".key"));
            var clock = new FixedClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var manager = new AccountSessionManager(accounts, secrets, clock);
            await manager.SetProfileAsync(new AccountProfile
            {
                Id = "fixture-main",
                DisplayName = "Fixture Main",
                Role = AccountRole.Main
            });
            await manager.SetCredentialsAsync(AccountRole.Main, new AccountCredentials
            {
                AccessToken = "offline-token",
                RefreshToken = "offline-refresh",
                CookieHeader = "charpwd=fixture-cookie",
                Username = "Fixture Main",
                ExpiresAt = clock.UtcNow.AddHours(1)
            });

            var session = await manager.GetRequiredSessionAsync(AccountRole.Main);
            Assert(session.Credentials.AccessToken == "offline-token", "Access token did not round-trip");
            Assert(session.Credentials.CookieHeader == "charpwd=fixture-cookie", "Cookie did not round-trip");
            Assert(await manager.GetSessionAsync(AccountRole.Mallbot) is null, "Missing role should have no session");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task MallParsingAsync()
    {
        var handler = new FixtureHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("mall.php", StringComparison.Ordinal) == true)
            {
                var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                var itemId = request.RequestUri?.Query.Contains("456", StringComparison.Ordinal) == true
                    || body.Contains("456", StringComparison.Ordinal) ? "456" : "123";
                return HtmlResponse($"<table><tr class=\"graybelow\" id=\"stock_7_{itemId}\"><td class=\"small store\"><b>Fixture Shop</b></td><td class=\"small stock\">12</td><td class=\"small price\"><a>1,234&nbsp;Meat</a></td></tr>" +
                    $"<tr class=\"grayabove\" id=\"stock_8_{itemId}\"><td class=\"small store\"><b>Second Shop</b></td><td class=\"small stock\">3</td><td class=\"small price\"><a>1,100&nbsp;Meat</a></td></tr></table>");
            }
            return HtmlResponse("<tr class=\"deets\" rel=\"fixture\"><td><b>Fixture Item</b></td><td>4</td><td><span class=\"tohide\">1,500</span><input class=\"hideit price\" name=\"price[123]\" /></td><td><input name=\"limit[123]\" value=\"2\" /></td></tr>");
        });
        using var http = new HttpClient(handler);
        var mall = new KoLMallReadClient(http, new Uri("https://fixture.invalid/"), new MallReadPolicy
        {
            MinimumRequestInterval = TimeSpan.Zero,
            Security = new KoLSecurityPolicy
            {
                AllowedHosts = new[] { "fixture.invalid" }
            }
        });

        var first = await mall.SearchAsync(" 123 ");
        Assert(first.Listings.Count == 2, "Expected two parsed mall listings");
        Assert(first.Listings[0].Price == 1100 && first.Listings[0].Quantity == 3,
            $"Listings were not sorted/parsed: {string.Join(",", first.Listings.Select(x => $"{x.Price}/{x.Quantity}"))}");
        _ = await mall.SearchAsync("123");
        Assert(handler.RequestCount == 1, "Fresh mall result should be served from cache");

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => mall.SearchAsync("456")));
        Assert(concurrent.All(x => x.Listings.Count == 2), "Concurrent mall reads returned inconsistent snapshots");
        Assert(handler.RequestCount == 2, "Concurrent reads should share one in-flight request");

        var own = await mall.FetchOwnShopAsync();
        Assert(own.Count == 1 && own[0].Price == 1500 && own[0].Quantity == 4 && own[0].PurchaseLimit == 2,
            "Own shop fixture was not parsed");
        Assert(handler.RequestCount == 3, "Expected two mall and one own-shop request");
        Assert(handler.Methods.All(x => x == HttpMethod.Get || x == HttpMethod.Post), "Read adapter issued an unexpected HTTP method");
    }

    private static async Task ShopMutationAsync()
    {
        var currentPrice = 96_969L;
        var handler = new FixtureHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                Assert(body.Contains("action=updateinv", StringComparison.Ordinal), "Mutation action missing");
                Assert(body.Contains("pwd=fixture-token", StringComparison.Ordinal), "Form token missing");
                Assert(body.Contains("price%5B2880%5D=1199", StringComparison.Ordinal), "Price field missing");
                currentPrice = 1_199;
                return HtmlResponse("<!-- U:fixture -->");
            }
            return HtmlResponse($"<input type=\"hidden\" name=\"pwd\" value=\"fixture-token\" />" +
                $"<input name=\"price[2880]\" value=\"{currentPrice:N0}\" />" +
                "<input name=\"limit[2880]\" value=\"1\" />");
        });
        using var http = new HttpClient(handler);
        var client = new KoLShopMutationClient(http, new Uri("https://fixture.invalid/"));
        var preview = await client.ApplyPriceUpdatesAsync(
            [new ShopPriceUpdate("2880", 96_969, 1_199)], commit: false);
        Assert(!preview.Committed && preview.Succeeded && preview.Results[0].Status == ShopPriceUpdateStatus.Ready,
            "Mutation preview should be ready without committing");
        Assert(handler.RequestCount == 1, "Preview should only fetch the shop once");

        var committed = await client.ApplyPriceUpdatesAsync(
            [new ShopPriceUpdate("2880", 96_969, 1_199)], commit: true);
        Assert(committed.Committed && committed.Succeeded && committed.Results[0].Status == ShopPriceUpdateStatus.Updated,
            "Mutation should be acknowledged and verified");
        Assert(handler.RequestCount == 4, "Commit should perform fresh GET, POST, and verification GET");

        var conflict = await client.ApplyPriceUpdatesAsync(
            [new ShopPriceUpdate("2880", 96_969, 999)], commit: true);
        Assert(!conflict.Committed && !conflict.Succeeded && conflict.Results[0].Status == ShopPriceUpdateStatus.Conflict,
            "Stale expected price must block a commit");
        Assert(handler.RequestCount == 5, "Conflict should not POST");
    }

    private static async Task PricingAsync()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var provider = new InMemoryPriceProvider(new[]
        {
            new PriceObservation("123", PriceSource.Mallbot, QuoteSide.Buy, 100, now, confidence: 1m),
            new PriceObservation("123", PriceSource.Shop, QuoteSide.Sell, 180, now, confidence: 1m),
            new PriceObservation("123", PriceSource.Pricegun, QuoteSide.Sell, 220, now, confidence: 1m)
        });
        var engine = new PricingEngine(provider, policy: new PricePolicy
        {
            MinimumMarginPercent = 0.10m,
            UnderpricedPercent = 0.20m,
            MinimumConfidence = 0.35m
        });
        var summary = await engine.SummarizeAsync("123");
        Assert(summary.LowestBuyPrice == 100 && summary.LowestSellPrice == 180, "Price summary minima changed");
        var recommendation = await engine.RecommendAsync("123");
        Assert(recommendation.IsUnderpriced && recommendation.RecommendedBuyPrice == 100, "Buy candidate was not detected");
        Assert(recommendation.IsProfitable && recommendation.RecommendedSellPrice >= 110, "Margin guard failed");
    }

    private static async Task PricegunParsingAsync()
    {
        var handler = new FixtureHandler((request, _) => HtmlResponse(
            "<html><head><title>tiny plastic fixture - Pricegun 🏷️🔫</title></head><body>" +
            "<h3>Recent Sales</h3><ol>" +
            "<li><span>8/28/26, 4:40 PM</span>1<!-- --> @ <!-- -->99,000,000</li>" +
            "<li><span>8/27/26, 3:02 AM</span>2 @ 40,000,000</li>" +
            "</ol></body></html>"));
        using var http = new HttpClient(handler);
        var client = new PricegunReadClient(http, new Uri("https://fixture.invalid/"), new PricegunReadPolicy
        {
            MinimumRequestInterval = TimeSpan.Zero
        });
        var first = await client.FetchAsync("123");
        Assert(first.DisplayName == "tiny plastic fixture", "Pricegun item title was not parsed");
        Assert(first.Sales.Count == 2 && first.Sales[0].UnitPrice == 99_000_000 && first.Sales[1].Quantity == 2,
            "Pricegun recent sales were not parsed");
        _ = await client.FetchAsync("123");
        Assert(handler.RequestCount == 1, "Fresh Pricegun result should be served from cache");
        var provider = new PricegunPriceProvider(client);
        var quotes = await provider.FetchAsync("123");
        Assert(quotes.Count == 2 && quotes.All(x => x.Source == PriceSource.Pricegun),
            "Pricegun observations were not exposed to the pricing engine");
    }

    private static Task CategorizationAsync()
    {
        var categorizer = new InventoryCategorizer();
        var result = categorizer.Analyze(new InventoryItemSnapshot
        {
            ItemId = "123",
            DisplayName = "Fixture Item",
            OwnedQuantity = 10,
            CostBasis = 60,
            CurrentListingPrice = 80,
            MarketReferencePrice = 100,
            LowestMarketPrice = 95,
            SoldQuantityLast7Days = 5,
            ObservedQuantity = 10
        });
        Assert(result.Has(InventoryCategory.Underpriced), "Underpriced category missing");
        Assert(result.Has(InventoryCategory.SellsWell), "Sells-well category missing");
        Assert(result.SuggestedAction == PricingAction.BuyCandidate, "Unexpected categorization action");
        return Task.CompletedTask;
    }

    private static Task ForeignBribeCatalogAsync()
    {
        Assert(ForeignBribeCatalog.BagOfForeignBribesItemId == "8691", "Bag item id changed");
        Assert(ForeignBribeCatalog.Entries.Count >= 20, "Foreign bribe catalog is unexpectedly small");
        Assert(ForeignBribeCatalog.Entries.Any(x => x.ItemId == "7458" && x.CurrencyCost == 300),
            "Spring Break book catalog entry missing");
        Assert(ForeignBribeCatalog.OneDayTicketCosts["FunFunds"] == 20,
            "Dinsey ticket cost missing");
        return Task.CompletedTask;
    }

    private static async Task BuyerCrmAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kolibri-crm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new BuyerCrmStore(root);
            var hasher = store.CreateHasher();
            var sale = new ShopSale(
                hasher.Hash("Loyal Buyer"),
                "fixture item",
                2,
                12_500,
                6_250,
                new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 16, 12, 1, 0, TimeSpan.Zero),
                "Loyal Buyer");
            var first = BuyerCrmService.ApplySales(new BuyerCrmDatabase(), [sale]);
            Assert(first.NewSales == 1 && first.PointsGranted == 13, "CRM points were not calculated");
            await store.SaveAsync(first.Database);
            var loaded = await store.LoadAsync();
            Assert(loaded.Buyers.Count == 1 && loaded.Buyers[0].CharacterName == "Loyal Buyer",
                "CRM vault did not preserve the local buyer profile");
            var duplicate = BuyerCrmService.ApplySales(loaded, [sale]);
            Assert(duplicate.NewSales == 0 && duplicate.DuplicateSales == 1,
                "CRM sale fingerprint was not idempotent");
            var intent = BuyerCrmService.BuildRewardIntent(
                loaded.Buyers[0], 10, BuyerContactAction.Gift, "loyalty reward", itemId: "123", quantity: 1);
            Assert(intent is { RequiresApproval: true, CharacterName: "Loyal Buyer" },
                "CRM reward intent was not approval-gated");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task WorkflowPlanAsync()
    {
        var planner = new KoLWorkflowPlanner();
        var ready = planner.Build(new KoLWorkflowSnapshot
        {
            MainSessionValid = true,
            MallbotSessionValid = true,
            MallbotMeat = 2_409_081,
            ForeignBribeBagPrice = 27_000,
            ForeignBribeBagStock = 463,
            PilotBagQuantity = 40,
            MallbotMeatReserve = 1_000_000,
            MainCanOpenBags = true,
            MainCanAdventure = true,
            MainCanUseHermit = true,
            AdventurePlanReady = true,
            PriceSourcesFresh = true,
            OwnShopScanned = true,
            InventoryScanned = true
        }, new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero));
        Assert(!ready.HasBlockingStep, "Ready workflow unexpectedly contains a blocked step");
        Assert(ready.Steps.Any(x => x.Action == KoLWorkflowAction.PlanForeignBribeBagPurchase &&
                                   x.Status == KoLWorkflowStepStatus.Ready),
            "Bag pilot was not marked ready");
        Assert(ready.Steps.Where(x => x.Action is KoLWorkflowAction.BuyForeignBribeBags or
                                      KoLWorkflowAction.TransferUnopenedBagsToMain or
                                      KoLWorkflowAction.OpenForeignBribeBagsOnMain)
                           .All(x => x.Status == KoLWorkflowStepStatus.ApprovalRequired),
            "Mutating bag actions must require approval");

        var blocked = planner.Build(new KoLWorkflowSnapshot());
        Assert(blocked.HasBlockingStep,
            "Empty workflow snapshot should be blocked");
        return Task.CompletedTask;
    }

    private static Task AdventurePlannerAsync()
    {
        var plan = new DailyAdventurePlanner().Plan(new AdventureState
        {
            Knucklebones = 10,
            AdventureTurnsRemaining = 200,
            LuckySurpriseEggs = 2,
            SkeletonOfCrimbosPastAvailable = true,
            PirateEncryptionKeys = 3,
            ForeignBribeBags = 1
        }, new DateOnly(2026, 11, 16));
        Assert(plan.Steps.Any(x => x.Action == AdventureActionKind.RunSkeletonOfCrimbosPast),
            "Skeleton adventure was not selected");
        Assert(plan.ProjectedKnucklebones >= 100, "Adventure plan did not reach target");
        Assert(plan.Advisories.Any(x => x.Contains("pirate encryption", StringComparison.OrdinalIgnoreCase)),
            "Adventure resource advisory missing");

        var encounter = new AdventureEncounterState(
            "skeleton-of-crimbos-past",
            AdventureEncounterKind.Fight,
            "crimbo",
            null,
            Array.Empty<string>(),
            CanContinue: true,
            Advisories: Array.Empty<string>(),
            FightRound: 3,
            ResponseHash: "fixture-response",
            RetryCount: 1);
        Assert(encounter.Kind == AdventureEncounterKind.Fight && encounter.FightRound == 3,
            "Fight encounter state did not preserve round");

        var checkpoint = new AdventureExecutionState
        {
            Role = AccountRole.Main,
            Phase = AdventureExecutionPhase.Blocked,
            FightRound = -1,
            RetryCount = -2,
            NeedsManual = true,
            BlockReason = "unknown choice"
        }.Normalize();
        Assert(checkpoint.NeedsManual && checkpoint.FightRound == 0 && checkpoint.RetryCount == 0,
            "Adventure checkpoint normalization failed");

        var parser = new KoLAdventureEncounterParser();
        var fight = parser.Parse(
            "skeleton-of-crimbos-past",
            new Uri("https://fixture.invalid/fight.php"),
            "<input name=\"onturn\" value=\"2\" />");
        Assert(fight.Kind == AdventureEncounterKind.Fight && fight.FightRound == 2,
            "Fight fixture was not classified");
        var choice = parser.Parse(
            "skeleton-of-crimbos-past",
            new Uri("https://fixture.invalid/choice.php"),
            "<input name=\"whichchoice\" value=\"123\" /><input name=\"option\" value=\"1\" />");
        Assert(choice.Kind == AdventureEncounterKind.Choice && choice.ChoiceId == "123" && choice.AvailableChoices.Contains("1"),
            "Choice fixture was not classified");
        var expired = parser.Parse(
            "skeleton-of-crimbos-past",
            new Uri("https://fixture.invalid/login.php"),
            "<form class=\"login\">Please log in</form>");
        Assert(expired.Kind == AdventureEncounterKind.Unknown && !expired.CanContinue,
            "Expired-session fixture should block adventure continuation");
        return Task.CompletedTask;
    }

    private static HttpResponseMessage HtmlResponse(string html) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try
        {
            action();
            throw new InvalidOperationException($"Expected {typeof(T).Name}");
        }
        catch (T)
        {
        }
    }

    private sealed class FixedClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow { get; } = value;
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<HttpMethod> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Methods.Add(request.Method);
            return Task.FromResult(responder(request, cancellationToken));
        }
    }
}
