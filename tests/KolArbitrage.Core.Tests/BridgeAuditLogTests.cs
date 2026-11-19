using KolArbitrage.Core.Daily;
using KolArbitrage.Core.Integration;
using Xunit;

namespace KolArbitrage.Core.Tests;

/// <summary>
/// Tests for the hash-chained bridge audit log. Verifies that a run
/// produces append-only rows, that the chain survives a write/flush,
/// and that a manual tamper is detected on verification.
/// </summary>
public sealed class BridgeAuditLogTests : IDisposable
{
    private readonly string _tempPath;

    public BridgeAuditLogTests()
    {
        _tempPath = Path.Combine(
            Path.GetTempPath(),
            $"kolibri-bridge-audit-{Guid.NewGuid():N}.jsonl");
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    [Fact]
    public async Task AppendAsync_writes_chainable_rows()
    {
        await using var log = new BridgeAuditLog(_tempPath);
        var opId = Guid.NewGuid().ToString("N");
        await log.AppendAsync(new AuditEvent(
            AuditEventPhase.Preview, opId, "test",
            requestId: "r1", action: "choice-campfire:1",
            status: "preview"));
        await log.AppendAsync(new AuditEvent(
            AuditEventPhase.Commit, opId, "test",
            requestId: "r2", action: "choice-clover:1",
            status: "submitted"));

        var rows = log.ReadSealedEvents();
        Assert.Equal(2, rows.Count);
        Assert.Null(rows[0].PrevHash);
        Assert.Equal(rows[0].Hash, rows[1].PrevHash);
        Assert.Null(log.VerifyChain());
    }

    [Fact]
    public async Task VerifyChain_returns_break_when_row_is_tampered_with()
    {
        await using var log = new BridgeAuditLog(_tempPath);
        await log.AppendAsync(new AuditEvent(
            AuditEventPhase.Preview, "op1", "test",
            requestId: "r1", action: "x", status: "preview"));
        await log.AppendAsync(new AuditEvent(
            AuditEventPhase.Commit, "op1", "test",
            requestId: "r2", action: "x", status: "submitted"));

        // Tamper: change one row's payload. Because the JSONL is line-based
        // we replace the action in-place and rewrite the hash to a dummy so
        // the row length stays the same. VerifyChain should catch both the
        // recomputed-hash mismatch and the prev-hash mismatch on the row
        // that follows.
        var lines = File.ReadAllLines(_tempPath);
        Assert.Equal(2, lines.Length);
        var tampered = lines[0].Replace("\"x\"", "\"y\"");
        File.WriteAllLines(_tempPath, new[] { tampered, lines[1] });
        var breakInfo = log.VerifyChain();
        Assert.NotNull(breakInfo);
        Assert.Equal(0, breakInfo!.Index);
    }

    [Fact]
    public async Task DefaultPath_uses_local_appdata_KoLibri()
    {
        var path = BridgeAuditLog.DefaultPath();
        Assert.EndsWith("bridge-audit.jsonl", path);
        Assert.Contains("KoLibri", path);
    }
}
