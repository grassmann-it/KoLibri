using System.Text;
using Xunit;
using KolArbitrage.Core.Basis;

namespace KolArbitrage.Core.Tests;

public sealed class AuditLedgerReplayTests
{
    [Fact]
    public void Hash_chain_roundtrip_detects_no_tampering()
    {
        var events = new[]
        {
            AuditEvent.Preview("op-1", "src", "req-1", "adventure.preview"),
            AuditEvent.Commit("op-1", "src", "req-1", "adventure.commit"),
            AuditEvent.Verify("op-1", "src", "req-1", "adventure.verify")
        };
        var sealedEvents = AuditHashChain.Seal(events);
        var break1 = AuditHashChain.Verify(sealedEvents);
        Assert.Null(break1);
        Assert.Equal(3, sealedEvents.Count);
        Assert.NotEqual(sealedEvents[0].Hash, sealedEvents[1].Hash);
        Assert.Equal(sealedEvents[0].Hash, sealedEvents[1].PrevHash);
        Assert.Equal(sealedEvents[1].Hash, sealedEvents[2].PrevHash);
    }

    [Fact]
    public void Hash_chain_detects_mutated_row()
    {
        var events = new[]
        {
            AuditEvent.Preview("op-1", "src", "req-1", "adventure.preview"),
            AuditEvent.Commit("op-1", "src", "req-1", "adventure.commit"),
            AuditEvent.Verify("op-1", "src", "req-1", "adventure.verify")
        };
        var sealedEvents = AuditHashChain.Seal(events).ToArray();
        var mutatedEvent = AuditEvent.Commit(
            "op-1", "src", "req-1", "adventure.commit",
            message: "tampered");
        var tampered = new SealedAuditEvent(mutatedEvent, sealedEvents[1].PrevHash, sealedEvents[1].Hash);
        var chain = new[] { sealedEvents[0], tampered, sealedEvents[2] };
        var break1 = AuditHashChain.Verify(chain);
        Assert.NotNull(break1);
        Assert.Equal(1, break1!.Index);
        Assert.Equal(tampered.Hash, break1.BrokenRow.Hash);
    }

    [Fact]
    public void Hash_chain_detects_swapped_row()
    {
        var a = AuditEvent.Preview("op-1", "src", "req-1", "adventure.preview");
        var b = AuditEvent.Commit("op-1", "src", "req-1", "adventure.commit");
        var c = AuditEvent.Verify("op-1", "src", "req-1", "adventure.verify");
        var sealedEvents = AuditHashChain.Seal(new[] { a, b, c }).ToArray();
        var swapped = new[] { sealedEvents[0], sealedEvents[2], sealedEvents[1] };
        var break1 = AuditHashChain.Verify(swapped);
        Assert.NotNull(break1);
        Assert.Equal(1, break1!.Index);
    }

    [Fact]
    public void Empty_chain_is_valid()
    {
        var break1 = AuditHashChain.Verify(Array.Empty<SealedAuditEvent>());
        Assert.Null(break1);
    }

    [Fact]
    public async Task Approval_token_store_roundtrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "kolibri-token-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new ApprovalTokenStore(root);
            Assert.True(store.TryValidatePath("sub/token.json", out var resolved));
            Assert.StartsWith(root, resolved);

            var token = new ApprovalToken("opaque-secret", DateTimeOffset.UtcNow, "test");
            await store.SaveAsync(token);
            var loaded = await store.LoadAsync();
            Assert.NotNull(loaded);
            Assert.Equal("opaque-secret", loaded!.Token);
            Assert.Equal("test", loaded.Label);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Approval_token_store_rejects_path_traversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "kolibri-token-traversal-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new ApprovalTokenStore(root);
            var r1 = store.TryValidatePath("../escape.json", out var p1);
            var r2 = store.TryValidatePath("/etc/passwd", out var p2);
            var r3 = store.TryValidatePath("..\\windows-escape.json", out var p3);
            Assert.False(r1, $"../escape.json => '{p1}'");
            Assert.False(r2, $"/etc/passwd => '{p2}'");
            Assert.False(r3, $"..\\windows-escape.json => '{p3}'");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Approval_token_store_constructor_rejects_traversal_filename()
    {
        var root = Path.Combine(Path.GetTempPath(), "kolibri-token-ctor-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Throws<ArgumentException>(() => new ApprovalTokenStore(root, "../escape.json"));
            Assert.Throws<ArgumentException>(() => new ApprovalTokenStore(root, "/etc/passwd"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
