// Read side of 0.4's BridgeAuditLog/AuditHashChain (KoLibri.Core), for the MCP tool kol_daily_audit.
// Same file (LocalApplicationData/KoLibri/bridge-audit.jsonl), same canonical form, same hash chain.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KoLibri.Extras;

public sealed record BridgeAuditRow(
    DateTimeOffset OccurredAt, string Phase, string? AccountId, string Action, string? Status, string RequestId, string? Message, string Hash);

public sealed record BridgeAuditBreak(int Index, BridgeAuditRow BrokenRow, string ExpectedHash);

public sealed class BridgeAuditReader
{
    public const string EmptyHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly string[] Phases = { "Preview", "Commit", "Verify" };

    private readonly string _path;

    public BridgeAuditReader(string? path = null) => _path = path ?? DefaultPath();

    public string FilePath => _path;

    public static string DefaultPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoLibri", "bridge-audit.jsonl");

    private sealed record Raw(BridgeAuditRow Row, string? PrevHash, string Canonical);

    public IReadOnlyList<BridgeAuditRow> Read() => ReadRaw().Select(r => r.Row).ToArray();

    /// <summary>The first row whose hash or previous hash does not fit the chain, or null.</summary>
    public BridgeAuditBreak? Verify()
    {
        string? prev = null;
        var index = 0;
        foreach (var raw in ReadRaw())
        {
            var expected = Hash(prev, raw.Canonical);
            if (!string.Equals(raw.Row.Hash, expected, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(raw.PrevHash ?? EmptyHash, prev ?? EmptyHash, StringComparison.OrdinalIgnoreCase))
                return new BridgeAuditBreak(index, raw.Row, expected);
            prev = raw.Row.Hash;
            index++;
        }
        return null;
    }

    private IEnumerable<Raw> ReadRaw()
    {
        if (!File.Exists(_path)) yield break;
        foreach (var line in File.ReadAllLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            Raw? raw;
            try { raw = Parse(line); }
            catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                raw = null; // unparseable rows are skipped, as in 0.4
            }
            if (raw is not null) yield return raw;
        }
    }

    private static Raw? Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var e = root.GetProperty("auditEvent");
        string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var phaseElement = e.GetProperty("phase");
        var phase = phaseElement.ValueKind == JsonValueKind.Number ? Phases[phaseElement.GetInt32()] : phaseElement.GetString()!;
        var occurredAt = e.GetProperty("occurredAt").GetDateTimeOffset();
        var data = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        if (e.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            foreach (var p in d.EnumerateObject()) data[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString();
        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["eventId"] = Str(e, "eventId"),
            ["occurredAt"] = occurredAt.ToString("O", CultureInfo.InvariantCulture),
            ["phase"] = phase,
            ["operationId"] = Str(e, "operationId"),
            ["sourceId"] = Str(e, "sourceId"),
            ["requestId"] = Str(e, "requestId"),
            ["action"] = Str(e, "action"),
            ["status"] = Str(e, "status"),
            ["message"] = Str(e, "message"),
            ["accountId"] = Str(e, "accountId"),
            ["isStale"] = e.TryGetProperty("isStale", out var stale) && stale.ValueKind == JsonValueKind.True,
            ["data"] = data
        };
        var hash = Str(root, "hash");
        if (string.IsNullOrWhiteSpace(hash)) return null;
        var row = new BridgeAuditRow(occurredAt, phase, Str(e, "accountId"), Str(e, "action") ?? "", Str(e, "status"),
            Str(e, "requestId") ?? "", Str(e, "message"), hash);
        return new Raw(row, Str(root, "prevHash"), JsonSerializer.Serialize(payload, Canonical));
    }

    private static string Hash(string? prev, string canonical)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((prev ?? EmptyHash) + "\n" + canonical))).ToLowerInvariant();

    private static readonly JsonSerializerOptions Canonical = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
