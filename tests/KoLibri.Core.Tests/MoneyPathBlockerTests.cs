using System.Net;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;
using LibKolibri.Access;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>GELD-1..3: unreadable receipts, lost KMail replies and crash-safe, cross-process budget holds.</summary>
public sealed class MoneyPathBlockerTests
{
    private static readonly AccountCredentials Credentials = new() { CookieHeader = "PHPSESSID=x" };

    private static MallListing Listing(string store, long price)
        => new("8763", store, "Seller", price, 50, null, DateTimeOffset.UtcNow, new Uri("https://kol.test/mall.php"));

    private sealed class Mall(params MallListing[] listings) : IMallReadClient
    {
        public Task<MallSnapshot> SearchAsync(string itemId, bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new MallSnapshot(itemId, listings, DateTimeOffset.UtcNow, new Uri("https://kol.test/mall.php")));
        public Task<IReadOnlyList<OwnShopListing>> FetchOwnShopAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OwnShopListing>>(Array.Empty<OwnShopListing>());
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Posts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post) Posts++;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Page(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body) };

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>Loading...</body></html>")]
    public async Task Unreadable_200_receipt_is_indeterminate_and_stops_before_the_next_shop(string body)
    {
        var handler = new Handler(r => r.Method == HttpMethod.Get ? Page("<input type=hidden name=\"pwd\" value=\"h\">") : Page(body));
        var executor = new BaconArbitrageExecutor(
            new MallBuyClient(new HttpClient(handler), new Uri("https://kol.test/")),
            new Mall(Listing("111", 1_000), Listing("222", 1_100)));

        var outcome = await executor.TopUpItemAsync(Credentials, 8763, 5, 10_000_000, 1_500, 1_000, itemName: "BACON");

        Assert.True(outcome.Unclear);
        Assert.Equal(1, handler.Posts);                 // the second shop was never asked
        Assert.Equal(5_000, outcome.UnclearSpend);      // worst case stays reserved
    }

    [Fact]
    public async Task A_known_refusal_page_is_still_a_certain_rejection()
    {
        var handler = new Handler(r => r.Method == HttpMethod.Get
            ? Page("<input type=hidden name=\"pwd\" value=\"h\">")
            : Page("This store doesn't have that item at that price."));
        var result = await new MallBuyClient(new HttpClient(handler), new Uri("https://kol.test/"))
            .BuyAsync(Credentials, 111, 8763, 1_000, 1, "h");
        Assert.False(result.Accepted);
        Assert.False(result.Unverified);
    }

    [Fact]
    public void Receipt_outcome_is_typed_not_read_from_the_message()
    {
        var unknown = new MallPurchaseTransportReceipt { Accepted = false, Indeterminate = true, Message = "KoL rate limited the Mall request." };
        var refused = new MallPurchaseTransportReceipt { Accepted = false, Message = "indeterminate in a word only" };
        Assert.True(unknown.MaybeSent);
        Assert.False(refused.MaybeSent);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "<html>garbled</html>", KoLMailSendOutcome.Unconfirmed)]
    [InlineData(HttpStatusCode.OK, "<center>Message sent.", KoLMailSendOutcome.Sent)]
    [InlineData(HttpStatusCode.BadGateway, "", KoLMailSendOutcome.Unconfirmed)]
    [InlineData(HttpStatusCode.BadRequest, "", KoLMailSendOutcome.NotSent)]
    public async Task Mail_outcome_distinguishes_lost_replies_from_refusals(HttpStatusCode code, string body, KoLMailSendOutcome expected)
    {
        using var http = new HttpClient(new Handler(_ => Page(body, code)));
        var outcome = await KoLMailbox.SendWithOutcomeAsync(http, "hash", "Other", "hi", [new KoLMailAttachment(5, 1)], 0);
        Assert.Equal(expected, outcome);
    }

    [Fact]
    public async Task Mail_reply_lost_after_the_post_is_unconfirmed()
    {
        using var http = new HttpClient(new LostReply());
        Assert.Equal(KoLMailSendOutcome.Unconfirmed,
            await KoLMailbox.SendWithOutcomeAsync(http, "hash", "Other", "hi", [], 100));
    }

    private sealed class LostReply : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("connection reset");
    }

    // ---- GELD-3 ----

    private static string TempLedger() => Path.Combine(Directory.CreateTempSubdirectory("usage").FullName, "usage.jsonl");

    [Fact]
    public void A_second_ledger_instance_sees_the_open_hold_and_cannot_exhaust_the_same_budget()
    {
        var path = TempLedger();
        var a = new UsageLedger(path);
        var b = new UsageLedger(path);                      // stands in for a second MCP process
        Assert.NotNull(a.TryReserve("g", "mall.buy", "buy", "Main", 600, 1_000));
        Assert.Null(b.TryReserve("g", "mall.buy", "buy", "Main", 600, 1_000));
        Assert.Equal(600, b.UsedToday("g", "mall.buy"));
    }

    [Fact]
    public void An_open_hold_survives_a_crash_and_is_adopted_as_unreconciled()
    {
        var path = TempLedger();
        var crashed = new UsageLedger(path);
        Assert.NotNull(crashed.TryReserve("g", "mall.buy", "buy", "Main", 600, 1_000));
        // process ends without settling; the restarted ledger finds the owner dead
        var restarted = new UsageLedger(path, ownerAlive: _ => false);
        Assert.Equal(600, restarted.UsedToday("g", "mall.buy"));
        var open = Assert.Single(restarted.Unreconciled("g"));
        Assert.Equal(600, open.Amount);
        Assert.Null(restarted.TryReserve("g", "mall.buy", "buy", "Main", 600, 1_000));
        Assert.True(restarted.Reconcile(open.Seq, 0));
        Assert.Equal(0, restarted.UsedToday("g", "mall.buy"));
        Assert.True(restarted.VerifyChain());
    }

    [Fact]
    public void A_live_owners_hold_is_not_adopted_and_release_frees_it()
    {
        var path = TempLedger();
        var a = new UsageLedger(path);
        var hold = a.TryReserve("g", "mall.buy", "buy", "Main", 600, 1_000)!;
        var other = new UsageLedger(path);                  // real owner check: this process is alive
        Assert.Empty(other.Unreconciled("g"));
        hold.Release();
        Assert.Equal(0, other.UsedToday("g", "mall.buy"));
    }

    // ---- residuals ----

    [Fact]
    public void Receipt_truncated_after_you_acquire_is_unverified_for_a_multi_unit_order()
    {
        var truncated = MallBuyClient.ReadReceipt("<b>You acquire", 8763, 100, 5);
        Assert.True(truncated.Unverified);
        Assert.Equal(0, truncated.Quantity);
        Assert.False(MallBuyClient.ReadReceipt("<b>You acquire an item", 8763, 100, 1).Unverified);
        Assert.Equal(3, MallBuyClient.ReadReceipt("You spent 300 Meat. You acquire an item (3)", 8763, 100, 5).Quantity);
    }

    [Fact]
    public async Task Mail_cancelled_after_the_post_started_is_unconfirmed_but_before_it_is_a_cancellation()
    {
        using var cts = new CancellationTokenSource();
        using var http = new HttpClient(new CancelAfterPost(cts));
        Assert.Equal(KoLMailSendOutcome.Unconfirmed,
            await KoLMailbox.SendWithOutcomeAsync(http, "hash", "Other", "hi", [new KoLMailAttachment(5, 1)], 0, cts.Token));
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            KoLMailbox.SendWithOutcomeAsync(http, "hash", "Other", "hi", [], 100, pre.Token));
    }

    private sealed class CancelAfterPost(CancellationTokenSource cts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Page(""));
        }
    }

    [Fact]
    public void A_damaged_journal_refuses_booking_and_release_and_keeps_the_hold()
    {
        var path = TempLedger();
        var ledger = new UsageLedger(path);
        var hold = ledger.TryReserve("g", "mall.buy", "buy", "Main", 600, 1_000)!;
        File.AppendAllText(path, "{\"seq\":1,\"kolDay\":\"x"); // aborted write: no newline
        hold.Commit(600, "bought");
        Assert.Equal(File.ReadAllText(path), "{\"seq\":1,\"kolDay\":\"x");   // nothing glued onto the tail
        Assert.Null(ledger.TryReserve("g", "mall.buy", "buy", "Main", 1, 1_000));
        var restarted = new UsageLedger(path, ownerAlive: _ => false);
        Assert.Null(restarted.TryReserve("g", "mall.buy", "buy", "Main", 1, 1_000));
        Assert.Equal(File.ReadAllText(path), "{\"seq\":1,\"kolDay\":\"x");
        Assert.Equal(600, restarted.UsedToday("g", "mall.buy"));   // hold still counts
    }

    [Fact]
    public void A_booking_starts_on_a_fresh_line_after_an_unterminated_tail()
    {
        var path = TempLedger();
        var ledger = new UsageLedger(path);
        var a = ledger.TryReserve("g", "mall.buy", "buy", "Main", 100, 1_000)!;
        a.Commit(100, "one");
        File.WriteAllText(path, File.ReadAllText(path).TrimEnd('\n'));   // valid line, newline lost
        var fresh = new UsageLedger(path);
        fresh.TryReserve("g", "mall.buy", "buy", "Main", 50, 1_000)!.Commit(50, "two");
        Assert.Equal(2, fresh.Entries("g").Count);
        Assert.Equal(150, fresh.UsedToday("g", "mall.buy"));
        Assert.True(fresh.VerifyChain());
    }
}
