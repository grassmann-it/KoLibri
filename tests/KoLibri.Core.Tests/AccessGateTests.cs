using LibKolibri.Access;
using Xunit;

namespace KoLibri.Core.Tests;

internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public sealed class AccessGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kolibri-access-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 21, 18, 0, 0, TimeSpan.Zero));
    private readonly GrantStore _grants;
    private readonly UsageLedger _ledger;
    private readonly AccessGate _gate;

    public AccessGateTests()
    {
        _grants = new GrantStore(Path.Combine(_dir, "grants.json"), _clock);
        _ledger = new UsageLedger(Path.Combine(_dir, "usage.jsonl"), _clock);
        _gate = new AccessGate(_grants, _ledger, _clock);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Token(long meatBudget = 5_000_000, TimeSpan? life = null, long? maxPrice = null, long? reserve = null)
        => _grants.Create("test-ai", ["Mallbot"],
            new Dictionary<string, RightLimit>
            {
                [Rights.AccountRead] = new(),
                [Rights.MallBuy] = new() { DailyBudget = meatBudget, MaxUnitPrice = maxPrice, MeatReserve = reserve },
            }, life ?? TimeSpan.FromDays(7)).Token;

    private static AccessRequest Buy(long amount, long? price = null, long? after = null)
        => new("mall.buy", "Mallbot", amount, price, after);

    [Fact]
    public void Unknown_token_is_refused()
        => Assert.Equal(AccessCode.UnknownToken, _gate.Authorize("kol_nope", Buy(1)).Code);

    [Fact]
    public void Token_is_not_stored_in_clear()
    {
        var token = Token();
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(_dir, "grants.json")));
    }

    [Fact]
    public void Read_needs_only_its_right_and_costs_nothing()
    {
        var token = Token();
        Assert.True(_gate.Authorize(token, new AccessRequest("account.status", "Mallbot")).Allowed);
        Assert.Equal(AccessCode.RightNotGranted, _gate.Authorize(token, new AccessRequest("market.search")).Code);
    }

    [Fact]
    public void Budget_is_exhausted_after_confirmed_spending()
    {
        var token = Token(meatBudget: 1_000);
        var first = _gate.Authorize(token, Buy(600));
        Assert.True(first.Allowed);
        first.Reservation!.Commit(600);
        Assert.Equal(600, _ledger.UsedToday(_grants.FindByToken(token)!.Id, Rights.MallBuy));

        var second = _gate.Authorize(token, Buy(600));
        Assert.Equal(AccessCode.BudgetExhausted, second.Code);
        Assert.True(_gate.Authorize(token, Buy(400)).Allowed);
    }

    [Fact]
    public void Open_reservations_count_so_parallel_calls_cannot_overrun()
    {
        var token = Token(meatBudget: 1_000);
        var a = _gate.Authorize(token, Buy(700));
        Assert.True(a.Allowed);
        Assert.Equal(AccessCode.BudgetExhausted, _gate.Authorize(token, Buy(700)).Code);
        a.Reservation!.Release();
        Assert.True(_gate.Authorize(token, Buy(700)).Allowed);
    }

    [Fact]
    public void Only_confirmed_amount_is_booked_and_settling_twice_does_nothing()
    {
        var token = Token(meatBudget: 1_000);
        var r = _gate.Authorize(token, Buy(900)).Reservation!;
        r.Commit(200);
        r.Commit(900);
        Assert.Equal(200, _ledger.UsedToday(_grants.FindByToken(token)!.Id, Rights.MallBuy));
    }

    [Fact]
    public void Budget_resets_at_the_kol_rollover()
    {
        var token = Token(meatBudget: 1_000);
        _gate.Authorize(token, Buy(1_000)).Reservation!.Commit(1_000);
        Assert.Equal(AccessCode.BudgetExhausted, _gate.Authorize(token, Buy(1)).Code);
        _clock.Advance(TimeSpan.FromHours(16));
        Assert.True(_gate.Authorize(token, Buy(1_000)).Allowed);
    }

    [Fact]
    public void Expired_and_revoked_grants_are_refused()
    {
        var shortLived = Token(life: TimeSpan.FromHours(1));
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(AccessCode.Expired, _gate.Authorize(shortLived, Buy(1)).Code);

        var token = Token();
        Assert.True(_gate.Authorize(token, Buy(1)).Allowed);
        Assert.True(_grants.Revoke(_grants.FindByToken(token)!.Id));
        Assert.Equal(AccessCode.Revoked, _gate.Authorize(token, Buy(1)).Code);
    }

    [Fact]
    public void Account_price_and_reserve_limits_are_enforced()
    {
        var token = Token(maxPrice: 500, reserve: 10_000);
        Assert.Equal(AccessCode.AccountNotGranted,
            _gate.Authorize(token, new AccessRequest("mall.buy", "Main", 1)).Code);
        Assert.Equal(AccessCode.PriceAboveLimit, _gate.Authorize(token, Buy(1_000, price: 501)).Code);
        Assert.Equal(AccessCode.BelowMeatReserve, _gate.Authorize(token, Buy(1_000, price: 100, after: 9_999)).Code);
        Assert.True(_gate.Authorize(token, Buy(1_000, price: 500, after: 10_000)).Allowed);
    }

    [Fact]
    public void A_write_right_without_budget_cannot_be_granted()
        => Assert.Throws<ArgumentException>(() => _grants.Create("x", ["Main"],
            new Dictionary<string, RightLimit> { [Rights.MallBuy] = new() }, TimeSpan.FromDays(1)));

    [Fact]
    public void A_plan_runs_once_and_only_for_the_same_arguments()
    {
        var token = Token();
        var plan = _gate.IssuePlan(token, "mall.buy", "{\"item\":1}");
        Assert.Equal(AccessCode.PlanInvalid, _gate.ConsumePlan(token, plan.PlanId, "mall.buy", "{\"item\":2}").Code);

        var plan2 = _gate.IssuePlan(token, "mall.buy", "{\"item\":1}");
        Assert.True(_gate.ConsumePlan(token, plan2.PlanId, "mall.buy", "{\"item\":1}").Allowed);
        Assert.Equal(AccessCode.PlanInvalid, _gate.ConsumePlan(token, plan2.PlanId, "mall.buy", "{\"item\":1}").Code);
    }

    [Fact]
    public void A_plan_expires_and_does_not_work_for_another_token()
    {
        var token = Token();
        var other = Token();
        var plan = _gate.IssuePlan(token, "mall.buy", "{}");
        Assert.Equal(AccessCode.PlanInvalid, _gate.ConsumePlan(other, plan.PlanId, "mall.buy", "{}").Code);

        var late = _gate.IssuePlan(token, "mall.buy", "{}");
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(AccessCode.PlanInvalid, _gate.ConsumePlan(token, late.PlanId, "mall.buy", "{}").Code);
    }

    [Fact]
    public void Usage_survives_a_restart_and_tampering_breaks_the_chain()
    {
        var token = Token(meatBudget: 1_000);
        _gate.Authorize(token, Buy(300)).Reservation!.Commit(300, "item 1");
        _gate.Authorize(token, Buy(200)).Reservation!.Commit(200, "item 2");
        Assert.True(_ledger.VerifyChain());

        var restarted = new UsageLedger(Path.Combine(_dir, "usage.jsonl"), _clock);
        Assert.Equal(500, restarted.UsedToday(_grants.FindByToken(token)!.Id, Rights.MallBuy));

        var file = Path.Combine(_dir, "usage.jsonl");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"amount\":300", "\"amount\":3"));
        Assert.False(new UsageLedger(file, _clock).VerifyChain());
    }
}
