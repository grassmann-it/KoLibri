using KoLibri.Daily;
using KoLibri.Safety;
using Xunit;

namespace KoLibri.Tests;

public sealed class SafetyAndDailyTests
{
    private static readonly AccountId Main = new("main");
    private static string Temp(string name) => Path.Combine(Directory.CreateTempSubdirectory("kolibri-safety-").FullName, name);

    [Fact]
    public async Task Composite_guard_stops_at_the_first_deny()
    {
        var allow = new RecordingGuard();
        var deny = new RecordingGuard { Decide = _ => GuardDecision.Deny("no") };
        var after = new RecordingGuard();
        var guard = new CompositeGuard(allow, deny, after);

        var decision = await guard.CheckAsync(new GuardedAction(Main, GuardedActionKind.Use, "use"), default);

        Assert.False(decision.Allowed);
        Assert.Single(allow.Checked);
        Assert.Empty(after.Checked);
    }

    [Fact]
    public async Task Approval_tokens_are_single_use_and_bound_to_the_action()
    {
        var guard = new ApprovalGuard(new HashSet<GuardedActionKind> { GuardedActionKind.MallBuy }, TimeSpan.FromMinutes(5));
        var buy = new GuardedAction(Main, GuardedActionKind.MallBuy, "buy 3 clovers") { Item = 194, Quantity = 3, Meat = 60_000 };

        Assert.False((await guard.CheckAsync(buy, default)).Allowed);
        guard.Present(guard.Issue(buy).Value);
        Assert.False((await guard.CheckAsync(buy with { Quantity = 4 }, default)).Allowed);
        Assert.True((await guard.CheckAsync(buy, default)).Allowed);
        Assert.False((await guard.CheckAsync(buy, default)).Allowed);
        Assert.True((await guard.CheckAsync(new GuardedAction(Main, GuardedActionKind.Rest, "rest"), default)).Allowed);
    }

    [Fact]
    public async Task Budget_and_grant_share_one_ledger_without_double_counting()
    {
        var ledger = new UsageLedger(Temp("usage.json"));
        var grants = new JsonGrantStore(Temp("grants.json"));
        await grants.SaveAsync(new Grant("mcp", Main, Rights.Read | Rights.MallBuy, null) { MeatPerDay = 100_000 }, default);
        var guard = new CompositeGuard(new GrantGuard("mcp", grants, ledger), new BudgetGuard(new[] { new Budget(Main, 50_000) }, ledger));
        var buy = new GuardedAction(Main, GuardedActionKind.MallBuy, "buy") { Quantity = 1, Meat = 30_000 };

        Assert.True((await guard.CheckAsync(buy, default)).Allowed);
        await guard.CompletedAsync(buy, succeeded: true, default);
        Assert.Equal(30_000, ledger.MeatSpentToday(Main));
        Assert.False((await guard.CheckAsync(buy with { Summary = "again" }, default)).Allowed);
        Assert.False((await guard.CheckAsync(new GuardedAction(Main, GuardedActionKind.Adventure, "adv"), default)).Allowed);
    }

    [Fact]
    public async Task Audit_journal_chains_entries_and_finds_tampering()
    {
        var path = Temp("audit.jsonl");
        using (var journal = new AuditJournal(path))
        {
            var action = new GuardedAction(Main, GuardedActionKind.Consume, "eat 1 x 61");
            await journal.CheckAsync(action, default);
            await journal.CompletedAsync(action, true, default);
            Assert.Null(journal.Verify());
            Assert.Equal(2, journal.Read().Count);
        }
        var lines = File.ReadAllLines(path);
        lines[0] = lines[0].Replace("eat 1 x 61", "eat 9 x 61");
        File.WriteAllLines(path, lines);
        using var reopened = new AuditJournal(path);
        Assert.Equal(1, reopened.Verify());
    }

    [Fact]
    public async Task Daily_runner_previews_runs_once_per_day_and_stops_after_unclear()
    {
        await using var client = TestClient.Create(new FakeKol());
        await using var session = await client.OpenAsync("main");
        var runs = 0;
        var tasks = new IDailyTask[]
        {
            new DelegateDailyTask("a", "Task A", (_, _) => { runs++; return Task.FromResult(new DailyTaskResult("a", DailyTaskState.Done, "ok")); }),
            new DelegateDailyTask("b", "Task B", (_, _) => Task.FromResult(new DailyTaskResult("b", DailyTaskState.Unclear, "lost answer"))),
            new DelegateDailyTask("c", "Task C", (_, _) => throw new InvalidOperationException("must not run")),
        };

        var preview = await DailyRunner.RunAsync(session, tasks, execute: false);
        Assert.All(preview, r => Assert.Equal(DailyTaskState.Planned, r.State));
        var first = await DailyRunner.RunAsync(session, tasks, execute: true);
        Assert.Equal(new[] { DailyTaskState.Done, DailyTaskState.Unclear }, first.Select(r => r.State));
        var second = await DailyRunner.RunAsync(session, tasks.Take(2), execute: true);
        Assert.All(second, r => Assert.Equal(DailyTaskState.AlreadyDone, r.State));
        Assert.Equal(1, runs);
    }
}
