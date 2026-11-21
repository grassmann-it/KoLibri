using System.Globalization;
using System.Text.Json;

namespace KolArbitrage.Core.Markt;

/// <summary>
/// Provenance for a dynamically supplied coinmaster catalog.
///
/// The manifest describes where and when rows were obtained.  It deliberately
/// contains no executable KoL operation and does not imply that a catalog row
/// is safe to purchase.
/// </summary>
public sealed record CoinmasterCatalogManifest
{
    public const string SchemaV1 = "kol-arbitrage.coinmaster-catalog/v1";

    public string Schema { get; init; } = SchemaV1;
    public string SourceName { get; init; } = "Coinmaster";
    public Uri? SourceUri { get; init; }
    public string? Version { get; init; }
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? ETag { get; init; }
    public string? ContentHash { get; init; }
    public bool IsStale { get; init; }

    public DateTimeOffset RetrievedAt => FetchedAt;
    public Uri? CatalogUri => SourceUri;
    public string? CatalogVersion => Version;
    public bool IsDynamic => SourceUri is not null || !string.IsNullOrWhiteSpace(Version);

    public CoinmasterCatalogManifest()
    {
    }

    public CoinmasterCatalogManifest(
        Uri? sourceUri,
        string? version = null,
        DateTimeOffset? fetchedAt = null,
        DateTimeOffset? expiresAt = null,
        string? sourceName = null,
        bool isStale = false)
    {
        SourceUri = sourceUri;
        Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        FetchedAt = fetchedAt ?? DateTimeOffset.UtcNow;
        ExpiresAt = expiresAt;
        SourceName = string.IsNullOrWhiteSpace(sourceName) ? "Coinmaster" : sourceName.Trim();
        IsStale = isStale;
    }

    public CoinmasterCatalogManifest(
        Uri? sourceUri,
        DateTimeOffset fetchedAt,
        string? version = null,
        DateTimeOffset? expiresAt = null,
        string? sourceName = null,
        bool isStale = false)
        : this(sourceUri, version, fetchedAt, expiresAt, sourceName, isStale)
    {
    }

    public bool IsFresh(DateTimeOffset now, TimeSpan maxAge)
    {
        if (IsStale || maxAge < TimeSpan.Zero || now - FetchedAt > maxAge) return false;
        return ExpiresAt is null || now <= ExpiresAt.Value;
    }

    public bool IsValid(out string error)
    {
        if (string.IsNullOrWhiteSpace(Schema))
        {
            error = "Catalog manifest schema is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(SourceName))
        {
            error = "Catalog manifest source name is required.";
            return false;
        }

        if (ExpiresAt is not null && ExpiresAt < FetchedAt)
        {
            error = "Catalog manifest expiry cannot precede its fetch time.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static CoinmasterCatalogManifest From(CoinmasterCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new CoinmasterCatalogManifest(snapshot.SourceUri, snapshot.Version,
            snapshot.FetchedAt, isStale: snapshot.IsStale);
    }
}

/// <summary>One point-in-time, read-only coinmaster catalog payload.</summary>
public sealed record CoinmasterCatalogSnapshot
{
    public IReadOnlyList<CoinmasterCatalogEntry> Entries { get; init; } = Array.Empty<CoinmasterCatalogEntry>();
    public IReadOnlyDictionary<string, int> TicketCosts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public CoinmasterCatalogManifest Manifest { get; init; } = new();

    public IReadOnlyList<CoinmasterCatalogEntry> Rows => Entries;
    public IReadOnlyList<CoinmasterCatalogEntry> Items => Entries;
    public Uri? SourceUri => Manifest.SourceUri;
    public string? Version => Manifest.Version;
    public DateTimeOffset FetchedAt => Manifest.FetchedAt;
    public bool IsStale => Manifest.IsStale;
    public bool IsDynamic => Manifest.IsDynamic;

    public CoinmasterCatalogEntry? Find(string itemId) =>
        string.IsNullOrWhiteSpace(itemId)
            ? null
            : Entries.FirstOrDefault(x => string.Equals(x.ItemId.Trim(), itemId.Trim(), StringComparison.OrdinalIgnoreCase));

    public IEnumerable<CoinmasterCatalogEntry> ForCurrency(string currency) =>
        string.IsNullOrWhiteSpace(currency)
            ? []
            : Entries.Where(x => string.Equals(x.Currency.Trim(), currency.Trim(), StringComparison.OrdinalIgnoreCase));

    public CoinmasterCatalogSnapshot()
    {
    }

    public CoinmasterCatalogSnapshot(
        IEnumerable<CoinmasterCatalogEntry> entries,
        CoinmasterCatalogManifest manifest,
        IReadOnlyDictionary<string, int>? ticketCosts = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(manifest);
        Entries = entries.Select(row => new CoinmasterCatalogEntry(
            row.Currency.Trim(), row.ItemId.Trim(), row.ItemName.Trim(), row.CurrencyCost,
            row.IsTradeable, row.IsOneDayTicket, row.RequiresTicket,
            string.IsNullOrWhiteSpace(row.TicketCurrency) ? null : row.TicketCurrency.Trim(),
            row.TicketCost)).ToArray();
        Manifest = manifest;
        TicketCosts = NormalizeTicketCosts(ticketCosts);
    }

    public CoinmasterCatalogSnapshot(
        IEnumerable<CoinmasterCatalogEntry> entries,
        IReadOnlyDictionary<string, int>? ticketCosts,
        CoinmasterCatalogManifest manifest)
        : this(entries, manifest, ticketCosts)
    {
    }

    public CoinmasterCatalogSnapshot(
        IReadOnlyList<CoinmasterCatalogEntry> entries,
        DateTimeOffset fetchedAt,
        Uri? sourceUri = null,
        string? version = null,
        bool isStale = false,
        IReadOnlyDictionary<string, int>? ticketCosts = null)
        : this(entries, new CoinmasterCatalogManifest(sourceUri, version, fetchedAt,
            isStale: isStale), ticketCosts)
    {
    }

    public bool IsValid(out string error)
    {
        if (!Manifest.IsValid(out error)) return false;
        if (Entries.Any(x =>
                !TryValidate(x, out _)))
        {
            error = "Catalog contains an invalid coinmaster row.";
            return false;
        }

        foreach (var pair in TicketCosts)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0)
            {
                error = "Catalog contains an invalid ticket cost.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    public CoinmasterCatalog ToCatalog() => new(Entries, TicketCosts, Manifest);
    public CoinmasterCatalog ToCoinmasterCatalog() => ToCatalog();

    public static CoinmasterCatalogSnapshot From(
        CoinmasterCatalog catalog,
        CoinmasterCatalogManifest? manifest = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new CoinmasterCatalogSnapshot(catalog.Entries,
            manifest ?? catalog.Manifest ?? new CoinmasterCatalogManifest(), catalog.TicketCosts);
    }

    private static IReadOnlyDictionary<string, int> NormalizeTicketCosts(
        IReadOnlyDictionary<string, int>? costs)
    {
        if (costs is null || costs.Count == 0)
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in costs)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                throw new ArgumentException("Ticket currency is required.", nameof(costs));
            if (pair.Value <= 0)
                throw new ArgumentOutOfRangeException(nameof(costs), "Ticket costs must be positive.");
            result[pair.Key.Trim()] = pair.Value;
        }
        return result;
    }

    private static bool TryValidate(CoinmasterCatalogEntry row, out string error)
    {
        try
        {
            row.Validate();
            error = string.Empty;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

/// <summary>Read-only source contract for dynamic coinmaster data.</summary>
public interface ICoinmasterCatalogSource
{
    Task<CoinmasterCatalogSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Convenience source backed by a caller-provided reader.</summary>
public sealed class CoinmasterCatalogSource : ICoinmasterCatalogSource
{
    private readonly Func<CancellationToken, Task<CoinmasterCatalogSnapshot>> _reader;

    public CoinmasterCatalogSource(Func<CancellationToken, Task<CoinmasterCatalogSnapshot>> reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public CoinmasterCatalogSource(Func<CancellationToken, Task<CoinmasterCatalog>> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = async cancellationToken => CoinmasterCatalogSnapshot.From(
            await reader(cancellationToken).ConfigureAwait(false));
    }

    public CoinmasterCatalogSource(Func<Task<CoinmasterCatalogSnapshot>> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = _ => reader();
    }

    public CoinmasterCatalogSource(Func<Task<CoinmasterCatalog>> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = async _ => CoinmasterCatalogSnapshot.From(
            await reader().ConfigureAwait(false));
    }

    public async Task<CoinmasterCatalogSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await _reader(cancellationToken).ConfigureAwait(false);
        return snapshot ?? throw new InvalidOperationException("Coinmaster catalog source returned no snapshot.");
    }
}

/// <summary>Explicit adapter for callers that want a catalog instead of a snapshot.</summary>
public sealed class CoinmasterCatalogSourceAdapter
{
    private readonly ICoinmasterCatalogSource _source;

    public CoinmasterCatalogSourceAdapter(ICoinmasterCatalogSource source)
        => _source = source ?? throw new ArgumentNullException(nameof(source));

    public async Task<CoinmasterCatalog> ReadAsync(CancellationToken cancellationToken = default)
        => (await _source.ReadAsync(cancellationToken).ConfigureAwait(false)).ToCatalog();
}

/// <summary>Mutable in-memory snapshot source useful for refresh workflows.</summary>
public sealed class CoinmasterCatalogSnapshotStore : ICoinmasterCatalogSource
{
    private readonly object _gate = new();
    private CoinmasterCatalogSnapshot _current = new();

    public CoinmasterCatalogSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public void Replace(CoinmasterCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate) _current = snapshot;
    }

    public Task<CoinmasterCatalogSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Current);
    }

    public async Task<CoinmasterCatalogSnapshot> RefreshAsync(
        ICoinmasterCatalogSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var snapshot = await source.ReadAsync(cancellationToken).ConfigureAwait(false);
        Replace(snapshot);
        return snapshot;
    }
}

public static class CoinmasterCatalogSourceExtensions
{
    public static async Task<CoinmasterCatalog> ReadCatalogAsync(
        this ICoinmasterCatalogSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return (await source.ReadAsync(cancellationToken).ConfigureAwait(false)).ToCatalog();
    }

    public static async Task<CoinmasterCatalogSnapshot> ReadSnapshotAsync(
        this ICoinmasterCatalogSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return await source.ReadAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Importer boundary for maintained exports and fixtures.</summary>
public interface ICoinmasterCatalogImporter
{
    CoinmasterCatalogSnapshot Import(
        IEnumerable<CoinmasterCatalogEntry> entries,
        CoinmasterCatalogManifest manifest,
        IReadOnlyDictionary<string, int>? ticketCosts = null);
}

/// <summary>
/// Normalizes dynamic rows into a manifest-backed snapshot.  The importer is
/// intentionally pure; it never fetches a source or submits a purchase.
/// </summary>
public sealed class CoinmasterCatalogImporter : ICoinmasterCatalogImporter
{
    public CoinmasterCatalogSnapshot Import(
        IEnumerable<CoinmasterCatalogEntry> entries,
        CoinmasterCatalogManifest manifest,
        IReadOnlyDictionary<string, int>? ticketCosts = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(manifest);
        var snapshot = new CoinmasterCatalogSnapshot(entries, manifest, ticketCosts);
        if (!snapshot.IsValid(out var error)) throw new ArgumentException(error, nameof(entries));
        return snapshot;
    }

    public CoinmasterCatalogSnapshot Import(
        IEnumerable<CoinmasterCatalogRecord> entries,
        CoinmasterCatalogManifest manifest,
        IReadOnlyDictionary<string, int>? ticketCosts = null) =>
        Import(entries.Cast<CoinmasterCatalogEntry>(), manifest, ticketCosts);

    public CoinmasterCatalogSnapshot Import(
        IEnumerable<ForeignBribeCatalogEntry> entries,
        CoinmasterCatalogManifest manifest,
        IReadOnlyDictionary<string, int>? ticketCosts = null) =>
        Import(entries.Select(static x => (CoinmasterCatalogEntry)x), manifest, ticketCosts);

    public CoinmasterCatalogSnapshot ImportRows(
        IEnumerable<CoinmasterCatalogEntry> entries,
        CoinmasterCatalogManifest manifest,
        IReadOnlyDictionary<string, int>? ticketCosts = null) =>
        Import(entries, manifest, ticketCosts);

    public CoinmasterCatalogSnapshot ImportRows(
        IEnumerable<CoinmasterCatalogRecord> entries,
        CoinmasterCatalogManifest manifest,
        IReadOnlyDictionary<string, int>? ticketCosts = null) =>
        Import(entries, manifest, ticketCosts);

    public CoinmasterCatalogSnapshot Import(
        CoinmasterCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsValid(out var error)) throw new ArgumentException(error, nameof(snapshot));
        return snapshot;
    }

    /// <summary>Imports a JSON object containing entries, ticketCosts and manifest.</summary>
    public CoinmasterCatalogSnapshot ImportJson(
        string json,
        CoinmasterCatalogManifest? manifest = null)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("JSON is required.", nameof(json));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            return Import(ParseEntries(root), manifest ?? new CoinmasterCatalogManifest());
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("Coinmaster catalog JSON must be an object or array.");

        var entries = root.TryGetPropertyIgnoreCase("entries", out var entriesNode)
            ? ParseEntries(entriesNode)
            : root.TryGetPropertyIgnoreCase("rows", out var rowsNode)
                ? ParseEntries(rowsNode)
                : Array.Empty<CoinmasterCatalogEntry>();
        var parsedManifest = manifest ?? (root.TryGetPropertyIgnoreCase("manifest", out var manifestNode)
            ? ParseManifest(manifestNode)
            : new CoinmasterCatalogManifest());
        var ticketCosts = root.TryGetPropertyIgnoreCase("ticketCosts", out var costsNode)
            ? ParseTicketCosts(costsNode)
            : null;
        return Import(entries, parsedManifest, ticketCosts);
    }

    public static CoinmasterCatalogSnapshot FromJson(
        string json,
        CoinmasterCatalogManifest? manifest = null) =>
        new CoinmasterCatalogImporter().ImportJson(json, manifest);

    private static IReadOnlyList<CoinmasterCatalogEntry> ParseEntries(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Array)
            throw new FormatException("Coinmaster catalog entries must be an array.");
        var rows = new List<CoinmasterCatalogEntry>();
        foreach (var element in node.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new FormatException("Coinmaster catalog rows must be objects.");
            var currency = GetString(element, "currency") ?? throw new FormatException("Currency is required.");
            var itemId = GetString(element, "itemId", "itemID", "id") ?? throw new FormatException("Item id is required.");
            var itemName = GetString(element, "itemName", "name") ?? throw new FormatException("Item name is required.");
            var cost = GetInt(element, "currencyCost", "cost", "price") ?? throw new FormatException("Currency cost is required.");
            var tradeable = GetBool(element, true, "isTradeable", "tradeable");
            var ticket = GetBool(element, false, "isOneDayTicket", "oneDayTicket", "ticket");
            var requires = GetBool(element, false, "requiresTicket", "requiredTicket");
            var ticketCurrency = GetString(element, "ticketCurrency");
            var ticketCost = GetInt(element, "ticketCost", "ticketUnits");
            rows.Add(new CoinmasterCatalogEntry(currency, itemId, itemName, cost, tradeable,
                ticket, requires, ticketCurrency, ticketCost));
        }
        return rows;
    }

    private static CoinmasterCatalogManifest ParseManifest(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) throw new FormatException("Catalog manifest must be an object.");
        Uri? source = null;
        var sourceText = GetString(node, "sourceUri", "catalogUri");
        if (!string.IsNullOrWhiteSpace(sourceText) && Uri.TryCreate(sourceText, UriKind.Absolute, out var parsed))
            source = parsed;
        var fetched = GetDate(node, "fetchedAt", "retrievedAt") ?? DateTimeOffset.UtcNow;
        var expires = GetDate(node, "expiresAt");
        var stale = GetBool(node, false, "isStale", "stale");
        return new CoinmasterCatalogManifest(source, GetString(node, "version", "catalogVersion"),
            fetched, expires, GetString(node, "sourceName"), stale)
        {
            Schema = GetString(node, "schema") ?? CoinmasterCatalogManifest.SchemaV1,
            ETag = GetString(node, "eTag", "etag"),
            ContentHash = GetString(node, "contentHash")
        };
    }

    private static IReadOnlyDictionary<string, int> ParseTicketCosts(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
            throw new FormatException("Ticket costs must be an object.");
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in node.EnumerateObject())
        {
            if (!property.Value.TryGetInt32(out var units))
                throw new FormatException("Ticket costs must be integer values.");
            result[property.Name] = units;
        }
        return result;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetPropertyIgnoreCase(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static int? GetInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetPropertyIgnoreCase(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        }
        return null;
    }

    private static bool GetBool(JsonElement element, bool fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetPropertyIgnoreCase(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)) return parsed;
        }
        return fallback;
    }

    private static DateTimeOffset? GetDate(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetPropertyIgnoreCase(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var parsed)) return parsed;
        }
        return null;
    }
}

/// <summary>Small JSON helper kept local to the importer contract.</summary>
internal static class CoinmasterJsonExtensions
{
    public static bool TryGetPropertyIgnoreCase(this JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
