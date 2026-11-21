using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KolArbitrage.Core.Basis;

/// <summary>
/// Append-only audit row with its predecessor hash. The base <see cref="AuditEvent"/>
/// stays free of crypto fields so legacy readers keep working; the chain lives in
/// this layer.
/// </summary>
public sealed record SealedAuditEvent
{
    [JsonConstructor]
    public SealedAuditEvent(AuditEvent auditEvent, string? prevHash, string hash)
    {
        AuditEvent = auditEvent ?? throw new ArgumentNullException(nameof(auditEvent));
        PrevHash = string.IsNullOrWhiteSpace(prevHash) ? null : prevHash;
        Hash = RequireHash(hash, nameof(hash));
    }

    public AuditEvent AuditEvent { get; }
    public string? PrevHash { get; }
    public string Hash { get; }

    private static string RequireHash(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Hash must not be empty.", parameterName);
        return value;
    }
}

/// <summary>
/// SHA-256 hash chain over an ordered list of <see cref="AuditEvent"/> rows.
/// Rows are serialized in a canonical, key-stable form so the same event always
/// produces the same digest. A first row has <c>prevHash = null</c>.
/// </summary>
public static class AuditHashChain
{
    public const string EmptyHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string ComputeHash(string? prevHash, AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        var previous = prevHash ?? EmptyHash;
        var canonical = SerializeCanonical(auditEvent);
        var bytes = Encoding.UTF8.GetBytes(previous + "\n" + canonical);
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static IReadOnlyList<SealedAuditEvent> Seal(IEnumerable<AuditEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var result = new List<SealedAuditEvent>();
        string? prev = null;
        foreach (var auditEvent in events)
        {
            var hash = ComputeHash(prev, auditEvent);
            result.Add(new SealedAuditEvent(auditEvent, prev, hash));
            prev = hash;
        }
        return result;
    }

    /// <summary>
    /// Walks the chain and returns the first row whose stored hash does not
    /// match the recomputed hash. Returns <c>null</c> when the chain is intact.
    /// </summary>
    public static AuditChainBreak? Verify(IEnumerable<SealedAuditEvent> sealedEvents)
    {
        ArgumentNullException.ThrowIfNull(sealedEvents);
        var index = 0;
        string? prev = null;
        foreach (var sealedEvent in sealedEvents)
        {
            var expected = ComputeHash(prev, sealedEvent.AuditEvent);
            var expectedPrev = prev ?? EmptyHash;
            if (!string.Equals(sealedEvent.Hash, expected, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(sealedEvent.PrevHash ?? EmptyHash, expectedPrev, StringComparison.OrdinalIgnoreCase))
            {
                return new AuditChainBreak(index, sealedEvent, expected);
            }
            prev = sealedEvent.Hash;
            index++;
        }
        return null;
    }

    private static string SerializeCanonical(AuditEvent auditEvent)
    {
        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["eventId"] = auditEvent.EventId,
            ["occurredAt"] = auditEvent.OccurredAt.ToString("O", CultureInfo.InvariantCulture),
            ["phase"] = auditEvent.Phase.ToString(),
            ["operationId"] = auditEvent.OperationId,
            ["sourceId"] = auditEvent.SourceId,
            ["requestId"] = auditEvent.RequestId,
            ["action"] = auditEvent.Action,
            ["status"] = auditEvent.Status,
            ["message"] = auditEvent.Message,
            ["accountId"] = auditEvent.AccountId,
            ["isStale"] = auditEvent.IsStale,
            ["data"] = SerializeData(auditEvent.Data)
        };
        return JsonSerializer.Serialize(payload, CanonicalOptions);
    }

    private static SortedDictionary<string, object?> SerializeData(
        IReadOnlyDictionary<string, string?> data)
    {
        var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in data)
            sorted[pair.Key] = pair.Value;
        return sorted;
    }

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

/// <summary>Describes where an audit chain broke during verification.</summary>
public sealed record AuditChainBreak(int Index, SealedAuditEvent BrokenRow, string ExpectedHash);
