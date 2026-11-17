using System.Net;
using System.Text;
using KolArbitrage.Core.Integration;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class ShopRepriceApprovalServiceTests
{
    [Fact]
    public async Task PreviewApprovalCommitAndVerifyStayBoundToActionAndAccount()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var fixture = new ShopFixtureHandler(96_969);
        using var http = new HttpClient(fixture);
        using var journal = new JsonlAuditEventJournal(
            Path.Combine(Path.GetTempPath(), $"kol-shop-audit-{Guid.NewGuid():N}.jsonl"));
        try
        {
            var service = new ShopRepriceApprovalService(
                new KoLShopMutationClient(http, new Uri("https://fixture.invalid/")),
                new ShopRepricePolicy { DryRun = false, RequireApproval = true },
                new SerialAccountLane("mallbot"),
                new FixedTimeProvider(now),
                audit: journal);

            var preview = await service.PreviewAsync(new ShopRepriceRequest(
                [new ShopPriceUpdate("42", 96_969, 1_199)],
                "mallbot", "fixture reprice", "shop-op-1"));

            Assert.True(preview.IsReady);
            Assert.Equal(1, fixture.GetCount);
            Assert.Equal(0, fixture.PostCount);

            var approval = await service.ApproveAsync(preview, "tester", "approval-1");
            var committed = await service.CommitAsync(preview, approval);

            Assert.True(committed.Committed);
            Assert.True(committed.Verified);
            Assert.Equal(1_199, fixture.Price);
            Assert.Equal(1, fixture.PostCount);
            // Preview GET, service guard GET, client commit GET + verification GET.
            Assert.Equal(4, fixture.GetCount);

            var rows = await journal.ReadAsync(new AuditEventQuery { OperationId = preview.ActionId });
            Assert.Equal(3, rows.Count);
            Assert.Equal(new[] { AuditEventPhase.Preview, AuditEventPhase.Commit, AuditEventPhase.Verify },
                rows.Select(x => x.Phase));
            Assert.All(rows, row => Assert.Equal("mallbot", row.AccountId));
        }
        finally
        {
            var path = journal.Path;
            journal.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task MissingApprovalAndFreshConflictNeverPost()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var fixture = new ShopFixtureHandler(1000);
        using var http = new HttpClient(fixture);
        using var service = new ShopRepriceApprovalService(
            new KoLShopMutationClient(http, new Uri("https://fixture.invalid/")),
            new ShopRepricePolicy { DryRun = false, RequireApproval = true },
            new SerialAccountLane("mallbot"),
            new FixedTimeProvider(now));

        var preview = await service.PreviewAsync(new ShopRepriceRequest(
            [new ShopPriceUpdate("42", 1000, 900)], "mallbot", idempotencyKey: "shop-op-2"));
        var missing = await service.CommitAsync(preview, (string?)null);
        Assert.False(missing.Committed);
        Assert.Contains(ShopRepriceAbortReason.MissingApproval, missing.AbortReasons);
        Assert.Equal(0, fixture.PostCount);

        fixture.Price = 1100;
        var approval = await service.ApproveAsync(preview, approvalToken: "approval-2");
        var conflict = await service.CommitAsync(preview, approval);
        Assert.False(conflict.Committed);
        Assert.Contains(ShopRepriceAbortReason.Conflict, conflict.AbortReasons);
        Assert.Equal(0, fixture.PostCount);
    }

    [Fact]
    public async Task IdempotencyReplaysVerifiedResultWithoutSecondPost()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var fixture = new ShopFixtureHandler(1000);
        using var http = new HttpClient(fixture);
        using var service = new ShopRepriceApprovalService(
            new KoLShopMutationClient(http, new Uri("https://fixture.invalid/")),
            new ShopRepricePolicy { DryRun = false, RequireApproval = true },
            new SerialAccountLane("mallbot"),
            new FixedTimeProvider(now));
        var preview = await service.PreviewAsync(new ShopRepriceRequest(
            [new ShopPriceUpdate("42", 1000, 900)], "mallbot", idempotencyKey: "shop-op-3"));
        var approval = await service.ApproveAsync(preview, approvalToken: "approval-3");

        var first = await service.CommitAsync(preview, approval);
        var posts = fixture.PostCount;
        var replay = await service.CommitAsync(preview, approval);

        Assert.True(first.Verified);
        Assert.True(replay.Verified);
        Assert.True(replay.IdempotencyReplayed);
        Assert.Equal(posts, fixture.PostCount);
    }

    private sealed class ShopFixtureHandler(long initialPrice) : HttpMessageHandler
    {
        private readonly object _gate = new();

        public long Price { get; set; } = initialPrice;
        public int GetCount { get; private set; }
        public int PostCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (request.Method == HttpMethod.Get)
                {
                    GetCount++;
                    var body = $"<input type=\"hidden\" name=\"pwd\" value=\"fixture-token\" />" +
                               $"<input name=\"price[42]\" value=\"{Price}\" />" +
                               "<input name=\"limit[42]\" value=\"1\" />";
                    return Task.FromResult(Response(body));
                }

                PostCount++;
                var form = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty;
                var encoded = "price%5B42%5D=";
                var start = form.IndexOf(encoded, StringComparison.Ordinal);
                if (start >= 0)
                {
                    start += encoded.Length;
                    var end = form.IndexOf('&', start);
                    var value = end < 0 ? form[start..] : form[start..end];
                    Price = long.Parse(Uri.UnescapeDataString(value));
                }
                return Task.FromResult(Response("<!-- U:fixture -->"));
            }
        }

        private static HttpResponseMessage Response(string body)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html")
            };
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

