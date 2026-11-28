using System.Globalization;
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
    public async Task Approval_for_zero_quantity_or_Meat_cannot_cover_positive_values()
    {
        var guard = new ApprovalGuard(new HashSet<GuardedActionKind> { GuardedActionKind.SendMail }, TimeSpan.FromMinutes(5));
        var approved = new GuardedAction(Main, GuardedActionKind.SendMail, "kmail to alice: items none; meat 0") { Path = "sendmessage.php" };
        guard.Present(guard.Issue(approved).Value);

        Assert.False((await guard.CheckAsync(approved with { Meat = 1 }, default)).Allowed);
        Assert.False((await guard.CheckAsync(approved with { Quantity = 1 }, default)).Allowed);
        Assert.True((await guard.CheckAsync(approved, default)).Allowed);
    }

    [Fact]
    public async Task Mall_approval_binds_item_and_unit_price_even_within_total_Meat()
    {
        var guard = new ApprovalGuard(new HashSet<GuardedActionKind> { GuardedActionKind.MallBuy }, TimeSpan.FromMinutes(5));
        var approved = new GuardedAction(Main, GuardedActionKind.MallBuy, "buy")
        {
            Item = 194, Quantity = 10, Meat = 1000, MaxUnitPrice = 100, Path = "mallstore.php"
        };
        guard.Present(guard.Issue(approved).Value);

        Assert.False((await guard.CheckAsync(approved with { Quantity = 1, MaxUnitPrice = 1000 }, default)).Allowed);
        Assert.False((await guard.CheckAsync(approved with { Item = null }, default)).Allowed);
        Assert.False((await guard.CheckAsync(approved with { Path = "other.php" }, default)).Allowed);
        Assert.False((await guard.CheckAsync(approved with { MaxUnitPrice = null }, default)).Allowed);
        Assert.True((await guard.CheckAsync(approved with { Quantity = 1, Meat = 90, MaxUnitPrice = 90 }, default)).Allowed);

        var unpriced = approved with { MaxUnitPrice = null };
        guard.Present(guard.Issue(unpriced).Value);
        Assert.False((await guard.CheckAsync(approved, default)).Allowed);
        Assert.True((await guard.CheckAsync(unpriced, default)).Allowed);
    }

    [Fact]
    public async Task Mail_approval_binds_recipient_and_attachments_in_a_stable_order()
    {
        var kol = new FakeKol().On("POST sendmessage.php", "<center>Message sent.");
        var guard = new ApprovalGuard(new HashSet<GuardedActionKind> { GuardedActionKind.SendMail }, TimeSpan.FromMinutes(5));
        await using var client = TestClient.Create(kol, guard: guard);
        await using var session = await client.OpenAsync("main");
        var message = new MailMessage(" Alice ", "hello") { Items = [new InventoryItem(456, "B", 2), new InventoryItem(123, "A", 1)] };
        var denied = await Assert.ThrowsAsync<ActionDeniedException>(() => session.Mail.SendAsync(message));
        Assert.Equal("kmail to alice: items 123x1,456x2; meat 0", denied.Action.Summary);
        guard.Present(guard.Issue(denied.Action).Value);

        await Assert.ThrowsAsync<ActionDeniedException>(() => session.Mail.SendAsync(message with { Recipient = "Bob" }));
        await Assert.ThrowsAsync<ActionDeniedException>(() => session.Mail.SendAsync(message with { Meat = 1 }));
        await Assert.ThrowsAsync<ActionDeniedException>(() => session.Mail.SendAsync(message with { Text = "other text" }));
        Assert.Empty(kol.Requests);
        var sent = await session.Mail.SendAsync(message with { Recipient = "ALICE", Items = message.Items.Reverse().ToArray() });
        Assert.Equal(MailSendStatus.Sent, sent.Status);
        Assert.Equal(1, kol.Count("POST", "sendmessage.php"));
    }

    [Fact]
    public async Task Deleting_mail_binds_the_folder_as_well_as_the_message_id()
    {
        var kol = new FakeKol();
        var guard = new ApprovalGuard(new HashSet<GuardedActionKind> { GuardedActionKind.Other }, TimeSpan.FromMinutes(5));
        await using var client = TestClient.Create(kol, guard: guard);
        await using var session = await client.OpenAsync("main");
        var inbox = await Assert.ThrowsAsync<ActionDeniedException>(() => session.Mail.DeleteAsync(MailFolder.Inbox, "123"));
        guard.Present(guard.Issue(inbox.Action).Value);

        await Assert.ThrowsAsync<ActionDeniedException>(() => session.Mail.DeleteAsync(MailFolder.Saved, "123"));
        await Assert.ThrowsAsync<ActionDeniedException>(() => session.Mail.DeleteAsync(MailFolder.Outbox, "123"));
        Assert.Empty(kol.Requests);
    }

    [Fact]
    public async Task RawPost_approval_binds_path_and_fields_and_ignores_pwd_and_field_order()
    {
        var kol = new FakeKol().On("POST campground.php", "<center>Done.");
        var guard = new ApprovalGuard(new HashSet<GuardedActionKind> { GuardedActionKind.RawPost }, TimeSpan.FromMinutes(5));
        await using var client = TestClient.Create(kol, guard: guard);
        await using var session = await client.OpenAsync("main");
        var form = new Dictionary<string, string> { ["b"] = "2", ["a[]"] = "3", ["a"] = "1", ["pwd"] = "old" };
        var denied = await Assert.ThrowsAsync<ActionDeniedException>(() => session.PostAsync("campground.php", form));
        Assert.Matches("^POST /campground\\.php #[0-9a-f]{8}$", denied.Action.Summary);
        guard.Present(guard.Issue(denied.Action).Value);

        await Assert.ThrowsAsync<ActionDeniedException>(() => session.PostAsync("backoffice.php", form));
        await Assert.ThrowsAsync<ActionDeniedException>(() => session.PostAsync("campground.php",
            new Dictionary<string, string>(form) { ["a"] = "different" }));
        Assert.Empty(kol.Requests);
        await session.PostAsync("campground.php", new Dictionary<string, string> { ["a"] = "1", ["pwd"] = "fresh", ["a[]"] = "3", ["b"] = "2" });
        guard.Present(guard.Issue(denied.Action).Value);
        await session.PostAsync("campground.php", new Dictionary<string, string>(form) { ["pwd"] = "fresh" });
        Assert.Equal(2, kol.Count("POST", "campground.php"));
        Assert.All(kol.Requests, request => Assert.Equal("a=1&a%5B%5D=3&b=2&pwd=fresh", request.Form));
    }

    [Theory]
    [InlineData("PWD")]
    [InlineData(" pwd ")]
    [InlineData("pwd[]")]
    public async Task RawPost_rejects_aliases_for_the_unbound_password_field(string alias)
    {
        var kol = new FakeKol();
        var guard = new RecordingGuard();
        await using var client = TestClient.Create(kol, guard: guard);
        await using var session = await client.OpenAsync("main");

        await Assert.ThrowsAsync<ArgumentException>(() => session.PostAsync("campground.php",
            new Dictionary<string, string> { ["pwd"] = "fresh", [alias] = "ambiguous" }));

        Assert.Empty(guard.Checked);
        Assert.Empty(kol.Requests);
    }

    [Theory]
    [InlineData(GuardedActionKind.RawPost)]
    [InlineData(GuardedActionKind.Other)]
    public async Task Request_approval_binds_the_port_and_redacted_query_values(GuardedActionKind kind)
    {
        var kol = new FakeKol();
        var guard = new ApprovalGuard(new HashSet<GuardedActionKind> { kind }, TimeSpan.FromMinutes(5));
        await using var client = TestClient.Create(kol, guard: guard);
        await using var session = await client.OpenAsync("main");
        Task<Page> Send(string path) => kind == GuardedActionKind.RawPost
            ? session.PostAsync(path, new Dictionary<string, string> { ["pwd"] = "fresh" })
            : session.GetPageAsync(path);
        const string approvedPath = "inv_use.php?token=approved&whichitem=1";
        var denied = await Assert.ThrowsAsync<ActionDeniedException>(() => Send(approvedPath));
        Assert.Equal("/inv_use.php?token=***&whichitem=1", denied.Action.Path);
        Assert.Matches("^[0-9A-F]{64}$", denied.Action.Binding!);
        guard.Present(guard.Issue(denied.Action).Value);

        var otherPort = await Assert.ThrowsAsync<ActionDeniedException>(() => Send("https://www.kingdomofloathing.com:8443/" + approvedPath));
        var otherValue = await Assert.ThrowsAsync<ActionDeniedException>(() => Send("inv_use.php?token=other&whichitem=1"));
        Assert.Equal(denied.Action.Summary, otherPort.Action.Summary);
        Assert.Equal(denied.Action.Summary, otherValue.Action.Summary);
        Assert.Empty(kol.Requests);
        await Send(approvedPath);
    }

    [Fact]
    public async Task RawPost_approval_checks_the_full_form_digest_when_the_display_prefix_matches()
    {
        var kol = new FakeKol();
        var guard = new ApprovalGuard(new HashSet<GuardedActionKind> { GuardedActionKind.RawPost }, TimeSpan.FromMinutes(5));
        await using var client = TestClient.Create(kol, guard: guard);
        await using var session = await client.OpenAsync("main");
        Task<Page> Send(string value) => session.PostAsync("campground.php",
            new Dictionary<string, string> { ["a"] = value, ["b"] = "2", ["pwd"] = "fresh" });
        var denied = await Assert.ThrowsAsync<ActionDeniedException>(() => Send("25336"));
        guard.Present(guard.Issue(denied.Action).Value);

        var changed = await Assert.ThrowsAsync<ActionDeniedException>(() => Send("93022"));
        Assert.Equal("POST /campground.php #148ee65f", changed.Action.Summary);
        Assert.Equal(denied.Action.Summary, changed.Action.Summary);
        Assert.Empty(kol.Requests);
        await Send("25336");
    }

    [Fact]
    public async Task Shop_reprice_approval_cannot_be_used_to_add_the_same_item()
    {
        var kol = new FakeKol();
        var guard = new ApprovalGuard(new HashSet<GuardedActionKind> { GuardedActionKind.ShopChange }, TimeSpan.FromMinutes(5));
        await using var client = TestClient.Create(kol, guard: guard);
        await using var session = await client.OpenAsync("main");

        var change = new ShopPriceChange(123, 700) { ExpectedPrice = 500, LimitPerDay = 2 };
        var denied = await Assert.ThrowsAsync<ActionDeniedException>(() => session.Shop.RepriceAsync([change]));
        Assert.Equal(new ItemRef(123), denied.Action.Item);
        Assert.Equal("reprice: 123@500->700", denied.Action.Summary);
        guard.Present(guard.Issue(denied.Action).Value);

        var add = await Assert.ThrowsAsync<ActionDeniedException>(() => session.Shop.AddAsync(123, 1, 700, limitPerDay: 2));
        Assert.Equal("list 1 x item 123 at 700, limit 2", add.Action.Summary);
        await Assert.ThrowsAsync<ActionDeniedException>(() => session.Shop.RepriceAsync([change with { LimitPerDay = null }]));
        await Assert.ThrowsAsync<ActionDeniedException>(() => session.Shop.RepriceAsync([change with { LimitPerDay = 3 }]));
        Assert.Empty(kol.Requests);
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("throw")]
    [InlineData("cancel")]
    public async Task Composite_guard_restores_approval_when_a_later_guard_stops_the_action(string outcome)
    {
        using var ct = new CancellationTokenSource();
        var approval = new ApprovalGuard(new HashSet<GuardedActionKind> { GuardedActionKind.Use }, TimeSpan.FromMinutes(5));
        var action = new GuardedAction(Main, GuardedActionKind.Use, "use") { Item = 194, Quantity = 1 };
        approval.Present(approval.Issue(action).Value);
        approval.Present(approval.Issue(action).Value);
        var later = new RecordingGuard
        {
            Decide = _ =>
            {
                if (outcome == "deny") return GuardDecision.Deny("blocked");
                if (outcome == "throw") throw new InvalidOperationException("guard failure");
                ct.Cancel();
                return GuardDecision.Allow;
            }
        };
        var guard = new CompositeGuard(new CompositeGuard(approval, new CompositeGuard(approval)), later);

        if (outcome == "deny")
            Assert.False((await guard.CheckAsync(action, ct.Token)).Allowed);
        else if (outcome == "throw")
            await Assert.ThrowsAsync<InvalidOperationException>(() => guard.CheckAsync(action, ct.Token).AsTask());
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guard.CheckAsync(action, ct.Token).AsTask());

        var retry = action with { };
        Assert.True((await approval.CheckAsync(retry, default)).Allowed);
        await approval.CompletedAsync(retry, succeeded: false, default);
        Assert.True((await approval.CheckAsync(retry, default)).Allowed);
        Assert.False((await approval.CheckAsync(action, default)).Allowed);
    }

    [Fact]
    public async Task Parallel_checks_of_the_same_action_restore_only_the_denied_calls_token()
    {
        var approval = new ApprovalGuard(new HashSet<GuardedActionKind> { GuardedActionKind.Use }, TimeSpan.FromMinutes(5));
        var action = new GuardedAction(Main, GuardedActionKind.Use, "use") { Item = 194, Quantity = 1 };
        var larger = action with { Quantity = 2 };
        approval.Present(approval.Issue(action).Value);
        approval.Present(approval.Issue(larger).Value);
        var later = new DenyFirstCheck();
        var guard = new CompositeGuard(approval, later);

        var denied = guard.CheckAsync(action, default).AsTask();
        try
        {
            Assert.False(denied.IsCompleted);
            Assert.True((await guard.CheckAsync(action, default)).Allowed);
            await guard.CompletedAsync(action, succeeded: true, default);
        }
        finally { later.Resume.TrySetResult(); }

        Assert.False((await denied).Allowed);
        Assert.False((await approval.CheckAsync(larger, default)).Allowed);
        Assert.True((await approval.CheckAsync(action, default)).Allowed);
        Assert.False((await approval.CheckAsync(action, default)).Allowed);
    }

    private sealed class DenyFirstCheck : IActionGuard
    {
        private int _checks;
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<GuardDecision> CheckAsync(GuardedAction action, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _checks) != 1) return GuardDecision.Allow;
            await Resume.Task;
            return GuardDecision.Deny("blocked");
        }

        public ValueTask CompletedAsync(GuardedAction action, bool succeeded, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("2026-11-22T03:30:00Z", "2026-11-21", "2026-11-22")]
    [InlineData("2026-07-01T03:30:00Z", "2026-06-30", "2026-07-01")]
    public void Usage_resets_at_0330_utc_year_round(string rolloverUtc, string previousDay, string nextDay)
    {
        var rollover = DateTimeOffset.Parse(rolloverUtc, CultureInfo.InvariantCulture);
        var now = rollover.AddSeconds(-1);
        var directory = Directory.CreateTempSubdirectory("kolibri-safety-");
        try
        {
            var ledger = new UsageLedger(Path.Combine(directory.FullName, "usage.json"), () => now);
            Assert.Equal(previousDay, UsageLedger.KolDay(now));
            ledger.Record(Main, 500, 3);
            Assert.Equal(500, ledger.MeatSpentToday(Main));
            Assert.Equal(3, ledger.TurnsSpentToday(Main));

            now = rollover;
            Assert.Equal(nextDay, UsageLedger.KolDay(now));
            Assert.Equal(0, ledger.MeatSpentToday(Main));
            Assert.Equal(0, ledger.TurnsSpentToday(Main));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task Daily_limits_deny_Meat_and_turns_that_would_overflow_used_amounts()
    {
        var ledger = new UsageLedger(Temp("usage.json"));
        ledger.Record(Main, 1, 1);
        var grants = new JsonGrantStore(Temp("grants.json"));
        await grants.SaveAsync(new Grant("mcp", Main, Rights.MallBuy | Rights.Adventure, null)
            { MeatPerDay = long.MaxValue, TurnsPerDay = int.MaxValue }, default);
        var budget = new BudgetGuard([new Budget(Main, long.MaxValue, MaxUnitPrice: long.MaxValue, TurnsPerDay: int.MaxValue)], ledger);
        var grant = new GrantGuard("mcp", grants, ledger);
        var buy = new GuardedAction(Main, GuardedActionKind.MallBuy, "buy") { Meat = long.MaxValue, Quantity = 2 };
        var run = new GuardedAction(Main, GuardedActionKind.Adventure, "run") { Quantity = int.MaxValue };
        foreach (var guard in new IActionGuard[] { budget, grant })
        {
            Assert.False((await guard.CheckAsync(buy, default)).Allowed);
            Assert.False((await guard.CheckAsync(run, default)).Allowed);
            Assert.True((await guard.CheckAsync(buy with { Meat = long.MaxValue - 1 }, default)).Allowed);
            Assert.True((await guard.CheckAsync(run with { Quantity = int.MaxValue - 1 }, default)).Allowed);
        }

        var unitPrice = new BudgetGuard([new Budget(Main, long.MaxValue, MaxUnitPrice: 100)], ledger);
        Assert.True((await unitPrice.CheckAsync(buy with { Meat = 200 }, default)).Allowed);
        Assert.False((await unitPrice.CheckAsync(buy with { Meat = 201 }, default)).Allowed);
        Assert.Throws<OverflowException>(() => ledger.Record(Main, long.MaxValue, 0));
        Assert.Throws<OverflowException>(() => ledger.Record(Main, 0, int.MaxValue));
        Assert.Equal(1, ledger.MeatSpentToday(Main));
        Assert.Equal(1, ledger.TurnsSpentToday(Main));
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
