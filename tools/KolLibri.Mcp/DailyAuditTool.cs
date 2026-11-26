using System.Text.Json;
using KoLibri.Extras;

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
            "Read the bridge audit log rows of the requested role(s); the store location is fixed.",
            DailyAuditSchema(),
            DailyAuditAsync);
        Alias("daily-audit", "kol_daily_audit");
    }

    private async Task<object?> DailyAuditAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (arguments.TryGetProperty("path", out _))
            throw new McpInvalidParamsException("kol_daily_audit reads the fixed bridge audit store; a path cannot be chosen.");
        var roles = McpArgs.Roles(arguments, _options.DefaultRole);
        var tail = OptionalInt(arguments, "tail") ?? 0;
        var verify = OptionalBool(arguments, "verify") == true;
        var account = OptionalString(arguments, "account");
        var log = new BridgeAuditReader();
        var rows = log.Read();
        if (verify)
        {
            var breakInfo = log.Verify();
            return new
            {
                command = "daily-audit",
                state = breakInfo is null ? "verified" : "broken",
                rowCount = rows.Count,
                breakIndex = breakInfo?.Index,
                brokenAction = breakInfo?.BrokenRow.Action
            };
        }
        // Only rows of the requested (and, over HTTP, granted) roles: the store is shared, rows carry the account id.
        var ofRoles = rows.Where(r => roles.Any(role => string.Equals(r.AccountId, role.ToString(), StringComparison.OrdinalIgnoreCase)));
        var filtered = string.IsNullOrWhiteSpace(account)
            ? ofRoles
            : ofRoles.Where(r => r.AccountId?.Contains(account, StringComparison.OrdinalIgnoreCase) == true);
        var ordered = tail > 0 ? filtered.TakeLast(tail).ToArray() : filtered.ToArray();
        await Task.CompletedTask.ConfigureAwait(false);
        return new
        {
            command = "daily-audit",
            rowCount = ordered.Length,
            rows = ordered.Select(r => new
            {
                occurredAt = r.OccurredAt,
                phase = r.Phase,
                accountId = r.AccountId,
                action = r.Action,
                status = r.Status,
                requestId = r.RequestId,
                message = r.Message,
                hash = r.Hash
            })
        };
    }

    private static JsonElement DailyAuditSchema()
        => Schema("{\"type\":\"object\",\"properties\":{"
            + "\"role\":{\"enum\":[\"Main\",\"Mallbot\",\"all\"]},"
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
