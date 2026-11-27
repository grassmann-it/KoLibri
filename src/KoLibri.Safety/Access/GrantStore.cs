// Moved from LibKolibri.Access (0.5 step 3, MCP). Internal transition layer: same file formats as 0.4
// (grants, usage ledger, lock); unified with the draft guards of this package in a later PR.
using System.Security.Cryptography;
using System.Text.Json;

namespace KoLibri.Safety.Access;

/// <summary>
/// Persists grants as a small JSON file. Tokens are random and stored only as a
/// SHA-256 hash, so a leaked file does not reveal a usable token.
/// </summary>
internal sealed class GrantStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    public GrantStore(string? path = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoLibri", "grants.json");
    }

    public static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Creates a grant and returns it together with the one-time secret token.</summary>
    public (Grant Grant, string Token) Create(
        string clientName,
        IEnumerable<string> accounts,
        IReadOnlyDictionary<string, RightLimit> rights,
        TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(clientName)) throw new ArgumentException("Client name is required.", nameof(clientName));
        var accountList = accounts.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        Validate(rights);

        var token = "kol_" + Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = _clock.GetUtcNow();
        var grant = new Grant
        {
            Id = Base64Url(RandomNumberGenerator.GetBytes(9)),
            ClientName = clientName.Trim(),
            TokenHash = HashToken(token),
            Accounts = accountList,
            Rights = new Dictionary<string, RightLimit>(rights),
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };
        lock (_gate)
        {
            var all = Load();
            all.Add(grant);
            Save(all);
        }
        return (grant, token);
    }

    /// <summary>Finds a grant by token; revoked and expired grants are still returned so callers can explain.</summary>
    public Grant? FindByToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = HashToken(token);
        lock (_gate)
            return Load().FirstOrDefault(g => CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(g.TokenHash), System.Text.Encoding.ASCII.GetBytes(hash)));
    }

    public Grant? Get(string id)
    {
        lock (_gate) return Load().FirstOrDefault(g => g.Id == id);
    }

    public IReadOnlyList<Grant> List()
    {
        lock (_gate) return Load();
    }

    public bool Revoke(string id)
    {
        lock (_gate)
        {
            var all = Load();
            var index = all.FindIndex(g => g.Id == id && g.RevokedAt is null);
            if (index < 0) return false;
            all[index] = all[index] with { RevokedAt = _clock.GetUtcNow() };
            Save(all);
            return true;
        }
    }

    private static void Validate(IReadOnlyDictionary<string, RightLimit> rights)
    {
        foreach (var (name, limit) in rights)
        {
            if (!Rights.All.Contains(name)) throw new ArgumentException($"Unknown right '{name}'.", nameof(rights));
            var writes = OperationCatalog.All.Any(o => o.Right == name && o.Kind == OperationKind.Write);
            if (writes && (limit.DailyBudget is null || limit.DailyBudget < 0))
                throw new ArgumentException($"Write right '{name}' needs a daily budget.", nameof(rights));
        }
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private List<Grant> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<Grant>>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }

    private void Save(List<Grant> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, Json));
        File.Move(temp, _path, overwrite: true);
    }
}
