using KolArbitrage.Core.Integration;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class OperationLedgerTests
{
    [Fact]
    public void TypedPreviewApprovalCommitAndReplayAreOperationBound()
    {
        var at = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var service = new OperationLedgerService(
            options: new OperationLedgerOptions
            {
                PreviewLifetime = TimeSpan.FromMinutes(10),
                ApprovalLifetime = TimeSpan.FromMinutes(5)
            });

        var preview = service.RegisterPreview(
            action: "mall.buy",
            fingerprint: "price:42:100:2",
            accountId: "Mallbot",
            preview: new BuyPreview("42", 100, 2),
            idempotencyKey: "buy-1",
            operationId: "op-1",
            createdAt: at,
            actor: "reviewer");

        Assert.Equal("42", preview.Preview.ItemId);
        var approval = service.Approve("op-1", "mall.buy", "price:42:100:2", "mallbot",
            actor: "reviewer", approvedAt: at.AddSeconds(1));
        Assert.True(approval.Approved);
        Assert.NotNull(approval.ApprovalToken);

        var commit = service.Commit("op-1", "mall.buy", "price:42:100:2", "MALLBOT",
            approval.ApprovalToken, "buy-1", receipt: "receipt-1", committedAt: at.AddSeconds(2));
        Assert.True(commit.Committed);
        Assert.False(commit.Replayed);

        var replay = service.Commit("op-1", "mall.buy", "price:42:100:2", "mallbot",
            approval.ApprovalToken, "buy-1", committedAt: at.AddSeconds(3));
        Assert.True(replay.Replayed);
        Assert.Equal("receipt-1", replay.Receipt);

        var entry = service.GetEntry("op-1");
        Assert.NotNull(entry);
        Assert.NotEqual(approval.ApprovalToken, entry!.ApprovalTokenHash);
        Assert.True(entry.ApprovalTokenConsumed);
    }

    [Fact]
    public void WrongBindingCannotUseApprovalToken()
    {
        var at = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var service = new OperationLedgerService();
        service.RegisterPreview("mall.buy", "f-1", "main", new { Item = "42" },
            operationId: "op-2", createdAt: at);
        var approval = service.Approve("op-2", "mall.buy", "f-1", "main", approvedAt: at.AddSeconds(1));

        var result = service.Commit("op-2", "mall.sell", "f-1", "main", approval.ApprovalToken,
            committedAt: at.AddSeconds(2));

        Assert.True(result.Rejected);
        Assert.False(result.Committed);
        Assert.Equal(OperationLedgerState.Approved, service.GetEntry("op-2")!.State);
    }

    [Fact]
    public void ExpiredApprovalCannotCommitAndMissingOperationIsUnknown()
    {
        var at = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var service = new OperationLedgerService();
        service.RegisterPreview("transfer", "f-2", "main", new { Item = "42" },
            operationId: "op-3", createdAt: at, expiresAt: at.AddSeconds(2));
        var approval = service.Approve("op-3", "transfer", "f-2", "main", approvedAt: at.AddSeconds(1));

        var expired = service.Commit("op-3", "transfer", "f-2", "main", approval.ApprovalToken,
            committedAt: at.AddSeconds(2));
        Assert.Equal(OperationCommitState.Expired, expired.State);

        var unknown = service.Commit("missing", "transfer", "f-2", "main", "never", committedAt: at);
        Assert.True(unknown.Unknown);
    }

    [Fact]
    public void IdempotencyConflictAndRedactedAuditDoNotLeakToken()
    {
        var at = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var service = new OperationLedgerService();
        service.RegisterPreview("mall.buy", "f-3", "mallbot", new { Item = "42" },
            operationId: "op-4", idempotencyKey: "same-key", createdAt: at);

        var conflict = service.TryRegisterPreview(new OperationPreviewRequest<object>(
            "mall.sell", "f-4", "mallbot", new { Item = "99" },
            idempotencyKey: "same-key", operationId: "op-5", createdAt: at));
        Assert.True(conflict.Conflict);

        var approval = service.Approve("op-4", "mall.buy", "f-3", "mallbot", approvedAt: at.AddSeconds(1));
        var audit = service.ReadAuditEvents("op-4");
        Assert.NotEmpty(audit);
        Assert.DoesNotContain(audit, row => row.Message?.Contains(approval.ApprovalToken!, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(audit, row => row.Data.Values.Any(value => value?.Contains(approval.ApprovalToken!, StringComparison.Ordinal) == true));
    }

    [Fact]
    public void JsonStoreSurvivesServiceRecreation()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kol-op-ledger-{Guid.NewGuid():N}.json");
        try
        {
            var at = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
            var first = new OperationLedgerService(new JsonOperationLedgerStore(path));
            first.RegisterPreview("mall.buy", "f-5", "mallbot", new BuyPreview("42", 100, 1),
                operationId: "op-5", idempotencyKey: "buy-5", createdAt: at);
            var second = new OperationLedgerService(new JsonOperationLedgerStore(path));
            var approval = second.Approve("op-5", "mall.buy", "f-5", "mallbot", approvedAt: at.AddSeconds(1));
            Assert.True(approval.Approved);
            Assert.True(File.Exists(path));
            Assert.DoesNotContain(approval.ApprovalToken!, File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed record BuyPreview(string ItemId, long UnitPrice, int Quantity);
}
