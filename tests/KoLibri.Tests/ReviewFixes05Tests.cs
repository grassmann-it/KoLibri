using System.Globalization;
using KoLibri.Internal;
using KoLibri.Daily;
using KoLibri.Safety;
using KoLibri.Tests.Desktop;
using Xunit;

namespace KoLibri.Tests;

/// <summary>One test per finding of the independent 0.5 review (money, daily and adventure paths), against the fake KoL.</summary>
public sealed class ReviewFixes05Tests
{
    // 1. A forced Read lane does not bypass the guard.
    [Fact]
    public async Task A_state_changing_get_goes_through_the_guard_even_with_the_read_lane()
    {
        var kol = new FakeKol().On("GET inv_use.php", "<html><body>You acquire an item: <b>BACON</b></body></html>")
            .On("GET charpane.php", "<html><body>charpane</body></html>");
        var guard = new RecordingGuard();
        await using var client = TestClient.Create(kol, guard: guard);
        await using var session = await client.OpenAsync("main");

        await session.GetPageAsync("inv_use.php?which=3&whichitem=9025&pwd=abc", lane: RequestLane.Read);
        await session.GetPageAsync("inventory.php?which=3&pwd=abc", lane: RequestLane.Read);
        await session.GetPageAsync("charpane.php", lane: RequestLane.Read);

        Assert.Equal(2, guard.Checked.Count);
        Assert.Contains(guard.Checked, a => a.Path!.Contains("inv_use.php", StringComparison.Ordinal));
        Assert.DoesNotContain(guard.Checked, a => a.Path!.Contains("charpane.php", StringComparison.Ordinal));
    }

    // 2. A price that rose after the quote (but still below MaxUnitPrice) is not bought: the guard approved the quote.
    [Fact]
    public async Task A_price_rise_after_the_quote_buys_nothing_even_below_the_limit()
    {
        var kol = new FakeKol { Meat = 1_000_000 }
            .On("POST mall.php", MallKit.SearchPage(25_000))
            .On("POST mall.php", MallKit.SearchPage(26_000))
            .On("POST mallstore.php", "You acquire an item: <b>11-leaf clover</b>. You spent 26,000 Meat.");
        var guard = new RecordingGuard();
        await using var client = TestClient.Create(kol, guard: guard);
        await using var session = await client.OpenAsync("main");

        var quote = await session.Mall.QuoteAsync(new PurchaseOrder(new ItemRef(MallKit.Clover), 1, 30_000));
        Assert.Equal(25_000, quote.TotalCost);
        var result = await session.Mall.BuyAsync(quote, default);

        Assert.Equal(PurchaseStatus.PriceChanged, result.Status);
        Assert.Equal(0, result.QuantityBought);
        Assert.Equal(0, kol.Count("POST", "mallstore.php"));
        var approved = Assert.Single(guard.Checked);
        Assert.Equal(25_000, approved.Meat);
        Assert.Equal(25_000, approved.MaxUnitPrice);
    }

    [Theory]
    [InlineData(42, 100, 1, 99, 1)]
    [InlineData(42, 100, 1, 100, 0)]
    [InlineData(0, 100, 1, 100, 1)]
    [InlineData(-1, 100, 1, 100, 1)]
    [InlineData(42, 1_000_000_000, 1, 1_000_000_000, 1)]
    [InlineData(42, long.MaxValue, 2, -2, 2)]
    public async Task Mall_rejects_inconsistent_quote_totals_before_requests_or_guard_checks(
        int storeId, long price, int quantity, long total, int plannedQuantity)
    {
        var kol = new FakeKol();
        var guard = new RecordingGuard();
        await using var client = TestClient.Create(kol, guard: guard);
        await using var session = await client.OpenAsync("main");
        var order = new PurchaseOrder(MallKit.Clover, quantity, long.MaxValue);
        var quote = new PurchaseQuote(order, [new MallListing(storeId, "Shop", order.Item, price, quantity, null)],
            TotalCost: total, QuotedAt: DateTimeOffset.UtcNow) { PlannedQuantity = plannedQuantity };

        await Assert.ThrowsAsync<ArgumentException>(() => session.Mall.BuyAsync(quote));

        Assert.Empty(guard.Checked);
        Assert.Empty(kol.Requests);
    }

    [Theory]
    [InlineData(125, PurchaseStatus.Unclear, 125)]
    [InlineData(60, PurchaseStatus.Bought, 100)]
    [InlineData(null, PurchaseStatus.Unclear, 100)]
    public async Task Mall_verifies_Meat_asymmetrically(int? meatDelta, PurchaseStatus expected, long expectedSpent)
    {
        var kol = MallKit.Kol(100, "{}", """{"10881":1}""");
        kol.On("POST mallstore.php", (_, _) =>
        {
            if (meatDelta is { } delta)
                kol.Meat -= delta;
            else
                kol.On("GET api.php?what=status", (_, _) => FakeKol.Html("", System.Net.HttpStatusCode.BadGateway));
            return FakeKol.Html("You acquire an item: <b>11-leaf clover</b>. You spent 100 Meat.");
        });
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");
        var quote = await session.Mall.QuoteAsync(new PurchaseOrder(MallKit.Clover, 1, 100));

        var result = await session.Mall.BuyAsync(quote);

        Assert.Equal(expected, result.Status);
        Assert.Equal(1, result.QuantityBought);
        Assert.Equal(expectedSpent, result.MeatSpent);
        Assert.Single(MallKit.Purchases(kol));
    }

    [Fact]
    public async Task A_Mall_gate_refusal_has_zero_spend_even_when_the_balance_changes()
    {
        var kol = MallKit.Kol(100);
        var guard = new RecordingGuard();
        await using var client = TestClient.Create(kol, guard: guard);
        await using var session = await client.OpenAsync("main");
        var quote = await session.Mall.QuoteAsync(new PurchaseOrder(MallKit.Clover, 1, 100));
        using var cts = new CancellationTokenSource();
        var checks = 0;
        var requestsAtGate = 0;

        var result = await session.Mall.BuyAsync(quote, ct =>
        {
            checks++;
            Assert.True(ct.CanBeCanceled);
            Assert.Equal(2, kol.Count("POST", "mall.php"));
            Assert.Single(kol.Requests, r => r.Path.Contains("what=inventory", StringComparison.Ordinal));
            Assert.Empty(MallKit.Purchases(kol));
            requestsAtGate = kol.Requests.Count;
            kol.Meat -= 100;
            return ValueTask.FromResult(GuardDecision.Deny("Reservation no longer active."));
        }, cts.Token);

        Assert.Equal(1, checks);
        Assert.Single(guard.Checked);
        Assert.Equal(PurchaseStatus.Refused, result.Status);
        Assert.Equal(0, result.MeatSpent);
        Assert.Equal(0, result.QuantityBought);
        var line = Assert.Single(result.Lines);
        Assert.Equal(0, line.Bought);
        Assert.Equal("Reservation no longer active.", line.Message);
        Assert.Equal(requestsAtGate, kol.Requests.Count);
        Assert.False(Assert.Single(guard.Completed).Succeeded);
        Assert.Empty(MallKit.Purchases(kol));
    }

    [Fact]
    public async Task Mall_transport_gate_checks_after_waiting_for_the_action_lane()
    {
        var kol = new FakeKol();
        using var transport = new HeldActionLane(kol);
        await using var client = KolibriClient.Create(new KolibriOptions
        {
            HttpHandler = transport,
            AccountStore = MemoryAccountStore.WithMain(),
            StateDirectory = Directory.CreateTempSubdirectory("kolibri-lane-").FullName,
            DisableLimiterForTests = true
        });
        await using var session = await client.OpenAsync("main");
        var occupyingLane = session.Http.PostAsync("hold.php", null);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var gateRan = false;
        var laneReleased = false;
        var purchase = SessionPipeline.WithMallSendGateAsync(() => session.Http.PostAsync("mallstore.php", null), _ =>
        {
            gateRan = true;
            return ValueTask.FromResult(laneReleased ? GuardDecision.Deny("Budget changed while queued.") : GuardDecision.Allow);
        });
        try
        {
            Assert.False(gateRan);
            Assert.False(purchase.IsCompleted);
        }
        finally
        {
            laneReleased = true;
            transport.Release.TrySetResult();
        }

        using var held = await occupyingLane;
        await Assert.ThrowsAsync<PreSendBlockedException>(() => purchase);
        Assert.True(gateRan);
        Assert.Equal(0, kol.Count("POST", "mallstore.php"));
    }

    // 3. BudgetGuard.MaxUnitPrice holds per line, not on the average of a multi-store purchase.
    [Fact]
    public async Task The_unit_price_budget_checks_each_line_not_the_average()
    {
        var dir = Directory.CreateTempSubdirectory("kolibri-review-").FullName;
        var guard = new BudgetGuard(new[] { new Budget(new AccountId("main"), 1_000_000, MaxUnitPrice: 600) },
            new KoLibri.Safety.UsageLedger(Path.Combine(dir, "usage.json")));
        // Two stores: 100 + 1,000 Meat → average 550, but one line is above 600.
        var mixed = new GuardedAction(new AccountId("main"), GuardedActionKind.MallBuy, "buy 2") { Quantity = 2, Meat = 1_100, MaxUnitPrice = 1_000 };
        var cheap = mixed with { Meat = 1_000, MaxUnitPrice = 500 };

        Assert.False((await guard.CheckAsync(mixed, default)).Allowed);
        Assert.True((await guard.CheckAsync(cheap, default)).Allowed);
    }

    // 4. Repricing checks the caller's EXPECTED price, not a second read of the shop.
    [Fact]
    public async Task Reprice_with_an_expected_price_that_moved_changes_nothing()
    {
        var kol = new FakeKol().On("GET backoffice.php", ShopPage(600));
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var result = await session.Shop.RepriceAsync(new[] { new ShopPriceChange(new ItemRef(10881), 700) { ExpectedPrice = 500 } });

        Assert.Equal(ActionStatus.Rejected, result.Status);
        Assert.Contains("Conflict", result.Rejection);
        Assert.Equal(0, kol.Count("POST", "backoffice.php"));
    }

    [Fact]
    public async Task Shop_reprice_with_Hold_and_Ready_is_done_and_sends_only_the_changed_item()
    {
        static string Batch(long price) => "<input name=\"pwd\" value=\"fixture\">" + ShopPage(price)
            + ShopPage(300).Replace("10881", "123", StringComparison.Ordinal);
        var kol = new FakeKol().On("GET backoffice.php", Batch(500)).On("GET backoffice.php", Batch(500))
            .On("GET backoffice.php", Batch(700)).On("POST backoffice.php", "<!-- U:fixture -->");
        var guard = new RecordingGuard();
        await using var client = TestClient.Create(kol, guard: guard);
        await using var session = await client.OpenAsync("main");

        var result = await session.Shop.RepriceAsync([
            new ShopPriceChange(MallKit.Clover, 700) { ExpectedPrice = 500 },
            new ShopPriceChange(123, 300) { ExpectedPrice = 300 }
        ]);

        Assert.Equal(ActionStatus.Done, result.Status);
        Assert.Equal(1, result.Completed);
        Assert.Null(result.Rejection);
        Assert.True(Assert.Single(guard.Completed).Succeeded);
        Assert.Equal("reprice: 123@300->300, 10881@500->700", Assert.Single(guard.Checked).Summary);
        var form = Uri.UnescapeDataString(Assert.Single(kol.Requests, r => r.Method == "POST").Form);
        Assert.Contains("price[10881]=700", form);
        Assert.DoesNotContain("[123]", form);
    }

    // 6. The choice chain of one turn is bounded.
    [Fact]
    public async Task An_endless_choice_chain_stops_with_an_error()
    {
        // KoL answers every option with the next, different choice: no "same choice again" repeat to catch.
        var open = 1000;
        string Choice() => ActionTests.ChoicePage.Replace("1234", open.ToString(CultureInfo.InvariantCulture));
        var kol = new FakeKol()
            .On("POST adventure.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php"))
            .On("GET choice.php", (_, _) => FakeKol.Html(Choice()))
            .On("POST choice.php", (_, _) => { open++; return FakeKol.Html(Choice()); });
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var result = await session.Adventure.RunAsync(new RunRequest(113, 1)
        {
            Choices = ChoiceResolvers.FromTable(Enumerable.Range(1000, 100).ToDictionary(id => id, _ => 1))
        });

        Assert.Equal(RunStopReason.Error, result.StopReason);
        Assert.Contains("choices in one turn", result.Detail);
        Assert.Equal(Adventurer.MaxChoicesPerTurn, kol.Count("POST", "choice.php"));
    }

    // 7. Hermit steps only when asked for: --only campfire (hermit skipped) and the wanted count hold in every branch.
    [Fact]
    public void Hermit_steps_respect_skip_and_the_wanted_count_without_permits()
    {
        var live = new DailyRunLive { Permits = 0, Clovers = 3, WorthlessItems = 3 };
        var skipped = new DailyRunOptions { HermitCloversWanted = 3, Skip = new HashSet<string>(new[] { "hermit", "internet-shop" }) };
        var none = new DailyRunOptions();
        var one = new DailyRunOptions { HermitCloversWanted = 1 };

        Assert.DoesNotContain(DailyRun.Plan(live, skipped), s => s.Id is "choice-clover" or "worthless-item");
        Assert.DoesNotContain(DailyRun.Plan(live, none), s => s.Id is "choice-clover" or "worthless-item");
        var planned = DailyRun.Plan(live, one);
        Assert.Single(planned, s => s.Id == "choice-clover");
        Assert.Single(planned, s => s.Id == "worthless-item");
    }

    // 8. HTTP 200 alone is not a Hermit trade: no daily marker without the clover on the page.
    [Fact]
    public async Task A_hermit_answer_without_the_clover_sets_no_daily_marker()
    {
        var kol = new FakeKol().On("POST hermit.php", "<html><body>The Hermit is busy right now.</body></html>")
            .On("POST hermit.php", "<html><body>You acquire an item: <b>11-leaf clover</b></body></html>");
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");
        var step = new[] { new DailyStep("choice", "choice-clover", "1") };

        var unclear = await DailyRun.ExecuteAsync(session, step);
        Assert.False(session.Daily.IsDone(DailyRun.Markers.HermitClovers));
        Assert.Contains(unclear.FailedSteps, f => f.Contains("unklar", StringComparison.Ordinal));

        var done = await DailyRun.ExecuteAsync(session, step);
        Assert.Empty(done.FailedSteps);
        Assert.True(session.Daily.IsDone(DailyRun.Markers.HermitClovers));
    }

    private static string ShopPage(long price) => string.Create(CultureInfo.InvariantCulture, $$"""
        <table><tr class="deets"><td><input type="text" name="price[10881]" value="{{price}}"></td>
        <td><b>11-leaf clover</b></td><td>3</td>
        <td><span class="tohide">{{price:N0}}</span><input type="text" class="hideit price" value="{{price}}"></td>
        <td><input name="limit[10881]" value="0"></td></tr></table>
        """);
}
