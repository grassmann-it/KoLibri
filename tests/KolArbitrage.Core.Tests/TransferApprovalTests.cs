using Xunit;
using KolArbitrage.Core.Basis;
using KolArbitrage.Core.Markt;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

public sealed class TransferApprovalTests
{
    [Fact]
    public void PreviewAndVerificationBindBothAccountsAndExactDeltas()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var request = new TransferRequest
        {
            SourceAccount = "Main",
            DestinationAccount = "Mallbot",
            Items = new Dictionary<string, int> { ["bag-1"] = 2 },
            Reason = "send unopened arbitrage stock",
            IdempotencyKey = "transfer-1",
            Meat = 1_000
        };
        var before = Observation(now, sourceQuantity: 5, destinationQuantity: 0, sourceMeat: 10_000, destinationMeat: 2_000);
        var preview = TransferApprovalService.Preview(request, before,
            new TransferApprovalPolicy { DryRun = false, RequireApproval = true }, now);

        Assert.True(preview.IsReady);
        var after = Observation(now.AddSeconds(1), sourceQuantity: 3, destinationQuantity: 2, sourceMeat: 9_000, destinationMeat: 3_000);
        var result = TransferApprovalService.VerifyCommit(new TransferCommitRequest
        {
            Preview = preview,
            ActionId = preview.ActionId,
            IdempotencyKey = preview.Request.IdempotencyKey,
            Before = before,
            After = after,
            ApprovalToken = "approval",
            CommitAcknowledged = true,
            CommittedAt = now.AddSeconds(1)
        }, reservations: null);

        Assert.True(result.Verified);
        Assert.Equal(2, Assert.Single(result.Lines).ConfirmedQuantity);
        Assert.False(result.ReconciliationRequired);
    }

    [Fact]
    public void ChangedExpectedStateIsRefusedBeforeTransferVerification()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var request = new TransferRequest
        {
            SourceAccount = AccountRole.Main.ToString(),
            DestinationAccount = AccountRole.Mallbot.ToString(),
            Items = new Dictionary<string, int> { ["bag-1"] = 1 },
            Reason = "send sellable stock",
            IdempotencyKey = "transfer-2"
        };
        var before = Observation(now, sourceQuantity: 2, destinationQuantity: 0, sourceMeat: 0, destinationMeat: 0);
        var preview = TransferApprovalService.Preview(request, before,
            new TransferApprovalPolicy { DryRun = false, RequireApproval = false }, now);
        var changed = Observation(now.AddSeconds(1), sourceQuantity: 1, destinationQuantity: 0, sourceMeat: 0, destinationMeat: 0);
        var result = TransferApprovalService.VerifyCommit(new TransferCommitRequest
        {
            Preview = preview,
            ActionId = preview.ActionId,
            IdempotencyKey = preview.Request.IdempotencyKey,
            Before = changed,
            After = changed,
            CommitAcknowledged = true,
            CommittedAt = now.AddSeconds(1)
        });

        Assert.False(result.Verified);
        Assert.Contains(TransferAbortReason.ExpectedStateChanged, result.AbortReasons);
    }

    private static TransferObservation Observation(
        DateTimeOffset fetchedAt,
        int sourceQuantity,
        int destinationQuantity,
        long sourceMeat,
        long destinationMeat)
        => new()
        {
            SourceInventory = new KoLInventorySnapshot([new KoLInventoryItem("bag-1", "Bag", sourceQuantity)], fetchedAt),
            DestinationInventory = new KoLInventorySnapshot([new KoLInventoryItem("bag-1", "Bag", destinationQuantity)], fetchedAt),
            SourceStatus = new KoLStatusSnapshot("main", 10, sourceMeat, 13, fetchedAt),
            DestinationStatus = new KoLStatusSnapshot("mallbot", 10, destinationMeat, 13, fetchedAt),
            ObservedAt = fetchedAt,
            SourceAccount = "Main",
            DestinationAccount = "Mallbot"
        };
}
