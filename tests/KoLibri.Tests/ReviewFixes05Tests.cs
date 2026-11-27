using System.Globalization;
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
        var result = await session.Mall.BuyAsync(quote);

        Assert.Equal(PurchaseStatus.PriceChanged, result.Status);
        Assert.Equal(0, result.QuantityBought);
        Assert.Equal(0, kol.Count("POST", "mallstore.php"));
        var approved = Assert.Single(guard.Checked);
        Assert.Equal(25_000, approved.Meat);
        Assert.Equal(25_000, approved.MaxUnitPrice);
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
