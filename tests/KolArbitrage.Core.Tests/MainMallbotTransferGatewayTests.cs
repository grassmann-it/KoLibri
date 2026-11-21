using Xunit;
using KolArbitrage.Core.Basis;
using KolArbitrage.Core.Markt;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

public sealed class MainMallbotTransferGatewayTests
{
    [Fact]
    public async Task PreviewAndApprovedFixtureCommitVerifyExpectedDeltas()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var initial = Observation(now, 3, 0, 10_000, 1_000);
        var transport = new FixtureMainMallbotTransferTransport(initial);
        var catalog = new ItemMaster([
            new ItemMasterRecord("123", "Giftable stock", access: "g")
        ]);
        var gateway = new MainMallbotTransferGateway(
            transport,
            new MainMallbotTransferAccountBinding(AccountRole.Main, "main-profile"),
            new MainMallbotTransferAccountBinding(AccountRole.Mallbot, "mallbot-profile"),
            catalog,
            new MainMallbotTransferPolicy { DryRun = false, RequireApproval = true },
            new TransferApprovalPolicy { DryRun = false, RequireApproval = true },
            clock: new FixedTimeProvider(now));

        var request = new TransferRequest
        {
            SourceAccount = "main-profile",
            DestinationAccount = "mallbot-profile",
            Items = new Dictionary<string, int> { ["123"] = 2 },
            Reason = "send sellable stock",
            IdempotencyKey = "fixture-transfer-1"
        };

        var preview = await gateway.PreviewAsync(request);
        Assert.True(preview.IsReady, string.Join(";", preview.AbortReasons));
        var approval = await gateway.ApproveAsync(preview, "test");
        var result = await gateway.CommitAsync(preview, approval);

        Assert.True(result.Verified, string.Join(";", result.Messages));
        Assert.Equal(2, Assert.Single(result.Lines).ConfirmedQuantity);
        Assert.Equal(1, transport.State.SourceQuantity("123"));
        Assert.Equal(2, transport.State.DestinationQuantity("123"));
        Assert.Equal(1, transport.CommitCount);
        Assert.Equal(0, transport.HttpRequestCount);
    }

    [Fact]
    public async Task NonGiftableItemIsRefusedBeforeCommit()
    {
        var now = DateTimeOffset.UtcNow;
        var transport = new FixtureMainMallbotTransferTransport(Observation(now, 3, 0, 100, 0));
        var catalog = new ItemMaster([
            new ItemMasterRecord("124", "Trade only", access: "t")
        ]);
        var gateway = new MainMallbotTransferGateway(
            transport,
            new MainMallbotTransferAccountBinding(AccountRole.Main, "main"),
            new MainMallbotTransferAccountBinding(AccountRole.Mallbot, "mallbot"),
            catalog,
            new MainMallbotTransferPolicy { DryRun = false },
            new TransferApprovalPolicy { DryRun = false, RequireApproval = false });
        var request = new TransferRequest
        {
            SourceAccount = "Main",
            DestinationAccount = "Mallbot",
            Items = new Dictionary<string, int> { ["124"] = 1 },
            Reason = "must be giftable",
            IdempotencyKey = "fixture-transfer-2"
        };

        var preview = await gateway.PreviewAsync(request);
        Assert.Contains(TransferAbortReason.InvalidItem, preview.AbortReasons);
        var result = await gateway.CommitAsync(preview);
        Assert.Contains(TransferAbortReason.InvalidItem, result.AbortReasons);
        Assert.Equal(0, transport.CommitCount);
    }

    [Fact]
    public async Task IdempotencyReplayDoesNotSendSecondFixtureCommit()
    {
        var now = DateTimeOffset.UtcNow;
        var transport = new FixtureMainMallbotTransferTransport(Observation(now, 2, 0, 0, 0));
        var gateway = new MainMallbotTransferGateway(
            transport,
            new MainMallbotTransferAccountBinding(AccountRole.Main, "main"),
            new MainMallbotTransferAccountBinding(AccountRole.Mallbot, "mallbot"),
            new ItemMaster([new ItemMasterRecord("123", "Giftable", access: "g")]),
            new MainMallbotTransferPolicy { DryRun = false, RequireApproval = false },
            new TransferApprovalPolicy { DryRun = false, RequireApproval = false },
            idempotency: new InMemoryTransferIdempotencyStore());
        var request = new TransferRequest
        {
            SourceAccount = "Main", DestinationAccount = "Mallbot",
            Items = new Dictionary<string, int> { ["123"] = 1 },
            Reason = "idempotency fixture", IdempotencyKey = "replay-1"
        };

        var preview = await gateway.PreviewAsync(request);
        var first = await gateway.CommitAsync(preview);
        var second = await gateway.CommitAsync(preview);

        Assert.True(first.Verified, string.Join(";", first.Messages));
        Assert.True(second.IdempotencyReplayed);
        Assert.Equal(1, transport.CommitCount);
    }

    [Fact]
    public async Task GuardedKoLTransportDoesNotPostByDefault()
    {
        var readers = new Dictionary<AccountRole, IAccountReadClient>
        {
            [AccountRole.Main] = new FixedAccountReadClient(Observation(DateTimeOffset.UtcNow, 1, 0, 10, 0), true),
            [AccountRole.Mallbot] = new FixedAccountReadClient(Observation(DateTimeOffset.UtcNow, 0, 0, 10, 0), false)
        };
        using var handler = new CountingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var transport = new KoLMainMallbotTransferTransport(readers,
            new Dictionary<AccountRole, HttpClient> { [AccountRole.Main] = client });
        var before = await transport.RefreshAsync(new MainMallbotTransferRefresh(
            new TransferRequest
            {
                SourceAccount = "Main", DestinationAccount = "Mallbot",
                Items = new Dictionary<string, int> { ["123"] = 1 }, Reason = "test", IdempotencyKey = "guarded"
            },
            new MainMallbotTransferAccountBinding(AccountRole.Main, "main"),
            new MainMallbotTransferAccountBinding(AccountRole.Mallbot, "mallbot")));
        var preview = TransferApprovalService.Preview(new TransferRequest
        {
            SourceAccount = "Main", DestinationAccount = "Mallbot",
            Items = new Dictionary<string, int> { ["123"] = 1 }, Reason = "test", IdempotencyKey = "guarded"
        }, before, new TransferApprovalPolicy { DryRun = false, RequireApproval = false });
        var receipt = await transport.CommitAsync(new MainMallbotTransferTransportCommit(
            preview, before, "approval", DateTimeOffset.UtcNow));

        Assert.False(receipt.Accepted);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(0, transport.HttpRequestCount);
    }

    private static TransferObservation Observation(DateTimeOffset at, int sourceQuantity,
        int destinationQuantity, long sourceMeat, long destinationMeat)
        => new()
        {
            SourceInventory = new KoLInventorySnapshot([new KoLInventoryItem("123", "Stock", sourceQuantity)], at),
            DestinationInventory = new KoLInventorySnapshot([new KoLInventoryItem("123", "Stock", destinationQuantity)], at),
            SourceStatus = new KoLStatusSnapshot("main", 10, sourceMeat, 13, at),
            DestinationStatus = new KoLStatusSnapshot("mallbot", 10, destinationMeat, 13, at),
            SourceAccount = "Main",
            DestinationAccount = "Mallbot",
            ObservedAt = at
        };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedAccountReadClient(TransferObservation observation, bool source)
        : IAccountReadClient
    {
        private readonly TransferObservation _observation = observation;
        private readonly bool _source = source;
        public Task<KoLStatusSnapshot> FetchStatusAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult((_source ? _observation.SourceStatus : _observation.DestinationStatus)!);
        public Task<KoLInventorySnapshot> FetchInventoryAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(_source ? _observation.SourceInventory : _observation.DestinationInventory);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
