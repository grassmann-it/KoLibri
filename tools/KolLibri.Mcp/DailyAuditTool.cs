using System.Text.Json;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KolLibri.Mcp;

/// <summary>
/// `kol_daily_audit` tool — read-only access to the bridge audit log so
/// MCP hosts can inspect what the daily bridge actually posted without
/// reaching into the operator's filesystem.
/// </summary>
public partial class KolibriMcpServer
{
    private void RegisterDailyAuditTool()
    {
        Register("kol_daily_audit",
            "Read the bridge audit log (default: ~/.local/share/KoLibri/bridge-audit.jsonl).",
            DailyAuditSchema(),
            DailyAuditAsync);
        Alias("daily-audit", "kol_daily_audit");
    }

    private async Task<object?> DailyAuditAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = OptionalString(arguments, "path") ?? BridgeAuditLog.DefaultPath();
        var tail = OptionalInt(arguments, "tail") ?? 0;
        var verify = OptionalBool(arguments, "verify") == true;
        var account = OptionalString(arguments, "account");

        await using var log = new BridgeAuditLog(path);
        var rows = log.ReadSealedEvents();
        if (verify)
        {
            var breakInfo = log.VerifyChain();
            return new
            {
                command = "daily-audit",
                path,
                state = breakInfo is null ? "verified" : "broken",
                rowCount = rows.Count,
                breakIndex = breakInfo?.Index,
                brokenAction = breakInfo?.BrokenRow.AuditEvent.Action
            };
        }
        var filtered = string.IsNullOrWhiteSpace(account)
            ? rows
            : rows.Where(r =>
                r.AuditEvent.AccountId?.Contains(account, StringComparison.OrdinalIgnoreCase) == true);
        var ordered = tail > 0 ? filtered.TakeLast(tail).ToArray() : filtered.ToArray();
        return new
        {
            command = "daily-audit",
            path,
            rowCount = ordered.Length,
            rows = ordered.Select(r => new
            {
                occurredAt = r.AuditEvent.OccurredAt,
                phase = r.AuditEvent.Phase.ToString(),
                accountId = r.AuditEvent.AccountId,
                action = r.AuditEvent.Action,
                status = r.AuditEvent.Status,
                requestId = r.AuditEvent.RequestId,
                message = r.AuditEvent.Message,
                hash = r.Hash
            })
        };
    }

    private static JsonElement DailyAuditSchema()
        => Schema("{\"type\":\"object\",\"properties\":{"
            + "\"path\":{\"type\":\"string\"},"
            + "\"tail\":{\"type\":\"integer\"},"
            + "\"verify\":{\"type\":\"boolean\"},"
            + "\"account\":{\"type\":\"string\"}"
            + "},\"additionalProperties\":false}");

    private static int? OptionalInt(JsonElement args, string name)
        => args.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;
    private static bool? OptionalBool(JsonElement args, string name)
        => args.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True
            || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
}
