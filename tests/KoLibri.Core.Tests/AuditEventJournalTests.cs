using Xunit;
using KoLibri.Core.Basis;

namespace KoLibri.Core.Tests;

public sealed class AuditEventJournalTests
{
    [Fact]
    public async Task LifecycleRowsRoundTripAndQueryByOperation()
    {
        var directory = CreateDirectory();
        try
        {
            var journal = new JsonlAuditEventJournal(Path.Combine(directory, "audit.jsonl"));
            var at = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
            await journal.AppendPreviewAsync("op-1", "mall", "req-1", "purchase", occurredAt: at,
                accountId: "mallbot", isStale: true);
            await journal.AppendCommitAsync("op-1", "mall", "req-2", "purchase", occurredAt: at.AddSeconds(1));
            await journal.AppendVerifyAsync("op-1", "mall", "req-3", "purchase", occurredAt: at.AddSeconds(2));

            var rows = await journal.QueryAsync(new AuditEventQuery { OperationId = "op-1" });

            Assert.Equal(3, rows.Count);
            Assert.Equal(
                new[] { AuditEventPhase.Preview, AuditEventPhase.Commit, AuditEventPhase.Verify },
                rows.Select(x => x.Phase));
            Assert.All(rows, row => Assert.Equal("op-1", row.OperationId));
            Assert.Equal("created", rows[0].Status);
            Assert.Equal(at, rows[0].OccurredAt);
            Assert.Equal("mallbot", rows[0].AccountId);
            Assert.True(rows[0].IsStale);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ConcurrentAppendsRemainOneCompleteJsonObjectPerLine()
    {
        var directory = CreateDirectory();
        try
        {
            var path = Path.Combine(directory, "audit.jsonl");
            var journal = new JsonlAuditEventJournal(path);
            var writes = Enumerable.Range(0, 64)
                .Select(i => journal.AppendAsync(new AuditEvent(
                    AuditEventPhase.Preview,
                    $"op-{i}",
                    "test",
                    $"req-{i}",
                    "scan")))
                .ToArray();
            await Task.WhenAll(writes);

            var rows = await journal.ReadAsync();
            Assert.Equal(64, rows.Count);
            Assert.Equal(64, await File.ReadAllLinesAsync(path).ContinueWith(task => task.Result.Length));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task DataIsCopiedAndReadOnly()
    {
        var details = new Dictionary<string, string?> { ["itemId"] = "42" };
        var auditEvent = AuditEvent.Preview("op", "source", "request", "scan", data: details);
        details["itemId"] = "changed";

        Assert.Equal("42", auditEvent.Data["itemId"]);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string?>)auditEvent.Data)["other"] = "value");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task StrictAndTolerantReadsHandleMalformedRows()
    {
        var directory = CreateDirectory();
        try
        {
            var path = Path.Combine(directory, "audit.jsonl");
            await File.WriteAllTextAsync(path, "{ definitely-not-json }\n");
            await Assert.ThrowsAsync<AuditJournalFormatException>(() =>
                new JsonlAuditEventJournal(path).ReadAsync());

            var tolerant = new JsonlAuditEventJournal(path, new AuditJournalOptions { IgnoreMalformedLines = true });
            Assert.Empty(await tolerant.ReadAsync());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kol-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
