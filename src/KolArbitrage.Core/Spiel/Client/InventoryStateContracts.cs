using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace KolArbitrage.Core.Spiel;

/// <summary>
/// Physical or derived locations used by an account inventory read.  Usable is
/// a projection over the physical locations: an item can therefore be both in
/// <see cref="Inventory"/> and <see cref="Usable"/> without being duplicated in
/// the account's physical stock.
/// </summary>
public enum InventoryBucket
{
    Unknown = 0,
    Inventory = 1,
    Closet = 2,
    Storage = 3,
    Equipment = 4,
    Familiar = 5,
    Usable = 6
}

/// <summary>Compatibility enum for callers that use KoL-prefixed names.</summary>
public enum KoLInventoryBucket
{
    Unknown = InventoryBucket.Unknown,
    Inventory = InventoryBucket.Inventory,
    Closet = InventoryBucket.Closet,
    Storage = InventoryBucket.Storage,
    Equipment = InventoryBucket.Equipment,
    Familiar = InventoryBucket.Familiar,
    Usable = InventoryBucket.Usable
}

/// <summary>One read-only item line in an account inventory state.</summary>
public record KoLInventoryStateItem
{
    public KoLInventoryStateItem()
        : this("unknown", "Unknown item", 0)
    {
    }

    public KoLInventoryStateItem(
        string itemId,
        string name,
        int quantity,
        InventoryBucket bucket = InventoryBucket.Inventory,
        decimal? costBasis = null,
        DateTimeOffset? firstSeenAt = null,
        bool isUsable = false,
        ItemMasterRecord? item = null)
    {
        ItemId = NormalizeItemId(itemId);
        Name = string.IsNullOrWhiteSpace(name) ? ItemId : name.Trim();
        Quantity = Math.Max(0, quantity);
        Bucket = bucket;
        CostBasis = NormalizeCost(costBasis);
        FirstSeenAt = firstSeenAt;
        IsUsable = isUsable || bucket == InventoryBucket.Usable;
        Item = item;
    }

    /// <summary>Overload for code using the KoL-prefixed bucket enum.</summary>
    public KoLInventoryStateItem(
        string itemId,
        string name,
        int quantity,
        KoLInventoryBucket bucket,
        decimal? costBasis = null,
        DateTimeOffset? firstSeenAt = null,
        bool isUsable = false,
        ItemMasterRecord? item = null)
        : this(itemId, name, quantity, (InventoryBucket)bucket, costBasis, firstSeenAt, isUsable, item)
    {
    }

    public string ItemId { get; init; }
    public string Name { get; init; }
    public int Quantity { get; init; }
    public InventoryBucket Bucket { get; init; }
    public InventoryBucket Location => Bucket;
    public InventoryBucket BucketKind => Bucket;
    public KoLInventoryBucket KoLBucket => (KoLInventoryBucket)Bucket;
    public decimal? CostBasis { get; init; }
    public decimal? UnitCost => CostBasis;
    public decimal? UnitCostBasis => CostBasis;
    public decimal? AcquisitionCost => CostBasis;
    public decimal? TotalCostBasis => CostBasis is { } value ? value * Quantity : null;
    public DateTimeOffset? FirstSeenAt { get; init; }
    public DateTimeOffset? FirstSeen => FirstSeenAt;
    public DateTimeOffset? AcquiredAt => FirstSeenAt;
    public bool IsUsable { get; init; }
    public bool Usable => IsUsable;
    public ItemMasterRecord? Item { get; init; }
    public ItemMasterRecord? Metadata => Item;
    public ItemMasterRecord? ItemMaster => Item;

    public bool IsTradeable => Item?.IsTradeable == true;
    public bool IsGiftable => Item?.IsGiftable == true;
    public bool IsAccountBound => Item?.IsAccountBound == true;
    public bool IsNpcSellable => Item?.IsNpcSellable == true;
    public IReadOnlySet<string> UseTags => Item?.UseTags ?? EmptyTags;

    private static readonly IReadOnlySet<string> EmptyTags =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeItemId(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            throw new ArgumentException("Item id is required.", nameof(itemId));
        return itemId.Trim();
    }

    private static decimal? NormalizeCost(decimal? value)
        => value is { } cost && cost >= 0m ? cost : null;
}

/// <summary>
/// A unit cost and the first observation date retained by a read-side ledger.
/// The type is deliberately a value object; adapters never update a ledger in
/// place while a request is being served.
/// </summary>
public record InventoryCostBasis
{
    public InventoryCostBasis(decimal? costBasis = null, DateTimeOffset? firstSeenAt = null)
    {
        CostBasis = costBasis is >= 0m ? costBasis : null;
        FirstSeenAt = firstSeenAt;
    }

    public decimal? CostBasis { get; init; }
    public decimal? UnitCost => CostBasis;
    public decimal? UnitCostBasis => CostBasis;
    public DateTimeOffset? FirstSeenAt { get; init; }
    public DateTimeOffset? FirstSeen => FirstSeenAt;
    public DateTimeOffset? AcquiredAt => FirstSeenAt;
}

/// <summary>Descriptive alias for callers that name the value an entry.</summary>
public record InventoryCostBasisEntry(decimal? CostBasis = null, DateTimeOffset? FirstSeenAt = null)
    : InventoryCostBasis(CostBasis, FirstSeenAt);

/// <summary>
/// Immutable item-id keyed cost/first-seen index.  <see cref="With"/> returns a
/// new index, which keeps historical read state separate from a live account.
/// </summary>
public sealed class InventoryCostBasisLedger : IReadOnlyDictionary<string, InventoryCostBasis>
{
    private readonly ImmutableDictionary<string, InventoryCostBasis> _entries;

    public InventoryCostBasisLedger(
        IEnumerable<KeyValuePair<string, InventoryCostBasis>>? entries = null)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, InventoryCostBasis>(StringComparer.OrdinalIgnoreCase);
        if (entries is not null)
        {
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null) continue;
                builder[entry.Key.Trim()] = entry.Value;
            }
        }
        _entries = builder.ToImmutable();
    }

    public static InventoryCostBasisLedger Empty { get; } = new();
    public int Count => _entries.Count;
    public IEnumerable<string> Keys => _entries.Keys;
    public IEnumerable<InventoryCostBasis> Values => _entries.Values;
    public InventoryCostBasis this[string key] => _entries[key.Trim()];

    public bool ContainsKey(string key) => key is not null && _entries.ContainsKey(key.Trim());
    public bool TryGetValue(string key, out InventoryCostBasis value)
    {
        if (key is not null && _entries.TryGetValue(key.Trim(), out value!)) return true;
        value = null!;
        return false;
    }

    public InventoryCostBasisLedger With(
        string itemId,
        decimal? costBasis = null,
        DateTimeOffset? firstSeenAt = null)
    {
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("Item id is required.", nameof(itemId));
        var value = new InventoryCostBasis(costBasis, firstSeenAt);
        return new InventoryCostBasisLedger(_entries.SetItem(itemId.Trim(), value));
    }

    public InventoryCostBasisLedger With(string itemId, InventoryCostBasis value)
    {
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("Item id is required.", nameof(itemId));
        ArgumentNullException.ThrowIfNull(value);
        return new InventoryCostBasisLedger(_entries.SetItem(itemId.Trim(), value));
    }

    public IEnumerator<KeyValuePair<string, InventoryCostBasis>> GetEnumerator() => _entries.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Read-only options used when projecting an account snapshot.</summary>
public sealed record InventoryStateAdapterOptions
{
    public ItemMaster? ItemMaster { get; init; }
    public InventoryCostBasisLedger? CostBasisLedger { get; init; }
    public IReadOnlyDictionary<string, InventoryCostBasis>? CostBasisByItem { get; init; }
    public IReadOnlyDictionary<string, decimal?>? ItemCosts { get; init; }
    public IReadOnlyDictionary<string, DateTimeOffset>? FirstSeenByItem { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }
    public bool IsStale { get; init; }
    public bool InferUsableFromItemMaster { get; init; } = true;
    public bool UseFetchedAtAsFirstSeenFallback { get; init; }

    internal InventoryCostBasis? Lookup(string itemId)
    {
        if (CostBasisLedger?.TryGetValue(itemId, out var ledgerValue) == true) return ledgerValue;
        if (CostBasisByItem is not null && TryGet(CostBasisByItem, itemId, out var value)) return value;
        decimal? cost = ItemCosts is not null && TryGet(ItemCosts, itemId, out var itemCost) ? itemCost : null;
        DateTimeOffset? firstSeen = FirstSeenByItem is not null && TryGet(FirstSeenByItem, itemId, out var seen)
            ? seen
            : null;
        return cost is not null || firstSeen is not null ? new InventoryCostBasis(cost, firstSeen) : null;
    }

    private static bool TryGet<T>(IReadOnlyDictionary<string, T> values, string itemId, out T value)
    {
        if (values.TryGetValue(itemId, out value!)) return true;
        var match = values.FirstOrDefault(x => string.Equals(x.Key, itemId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(match.Key))
        {
            value = match.Value;
            return true;
        }
        value = default!;
        return false;
    }
}

/// <summary>
/// Source-neutral bucket input for adapters that already have separate
/// account reads.  It keeps the legacy <see cref="KoLInventoryItem"/> shape at
/// the transport boundary while the state adapter adds metadata and derived
/// projections.
/// </summary>
public record KoLInventoryBuckets
{
    public KoLInventoryBuckets(
        IEnumerable<KoLInventoryItem>? inventory = null,
        IEnumerable<KoLInventoryItem>? closet = null,
        IEnumerable<KoLInventoryItem>? storage = null,
        IEnumerable<KoLInventoryItem>? equipment = null,
        IEnumerable<KoLInventoryItem>? familiar = null,
        IEnumerable<KoLInventoryItem>? usable = null)
    {
        Inventory = Copy(inventory);
        Closet = Copy(closet);
        Storage = Copy(storage);
        Equipment = Copy(equipment);
        Familiar = Copy(familiar);
        Usable = Copy(usable);
    }

    public IReadOnlyList<KoLInventoryItem> Inventory { get; init; }
    public IReadOnlyList<KoLInventoryItem> Closet { get; init; }
    public IReadOnlyList<KoLInventoryItem> Storage { get; init; }
    public IReadOnlyList<KoLInventoryItem> Equipment { get; init; }
    public IReadOnlyList<KoLInventoryItem> Familiar { get; init; }
    public IReadOnlyList<KoLInventoryItem> Usable { get; init; }

    private static IReadOnlyList<KoLInventoryItem> Copy(IEnumerable<KoLInventoryItem>? items)
        => items?.Where(x => x is not null).ToArray() ?? Array.Empty<KoLInventoryItem>();
}

/// <summary>Friendly alias for hosts that call the value an inventory state.</summary>
public sealed record InventoryBuckets : KoLInventoryBuckets
{
    public InventoryBuckets(
        IEnumerable<KoLInventoryItem>? inventory = null,
        IEnumerable<KoLInventoryItem>? closet = null,
        IEnumerable<KoLInventoryItem>? storage = null,
        IEnumerable<KoLInventoryItem>? equipment = null,
        IEnumerable<KoLInventoryItem>? familiar = null,
        IEnumerable<KoLInventoryItem>? usable = null)
        : base(inventory, closet, storage, equipment, familiar, usable)
    {
    }
}

/// <summary>
/// Immutable account inventory state with physical and usable projections.
/// </summary>
public sealed record KoLInventoryStateSnapshot
{
    private readonly ImmutableArray<KoLInventoryStateItem> _items;
    private readonly ImmutableDictionary<InventoryBucket, ImmutableArray<KoLInventoryStateItem>> _buckets;

    public KoLInventoryStateSnapshot(
        IEnumerable<KoLInventoryStateItem> items,
        DateTimeOffset fetchedAt,
        bool isStale = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items
            .Where(x => x is not null && x.Quantity > 0)
            .Select(x => x with { ItemId = x.ItemId.Trim(), Name = x.Name.Trim() })
            .ToImmutableArray();
        FetchedAt = fetchedAt;
        IsStale = isStale;
        _buckets = Enum.GetValues<InventoryBucket>()
            .Where(x => x != InventoryBucket.Unknown && x != InventoryBucket.Usable)
            .ToImmutableDictionary(
                x => x,
                x => _items.Where(item => item.Bucket == x).ToImmutableArray());
    }

    public IReadOnlyList<KoLInventoryStateItem> Items => _items;
    public DateTimeOffset FetchedAt { get; init; }
    public DateTimeOffset ObservedAt => FetchedAt;
    public bool IsStale { get; init; }
    public bool Stale => IsStale;

    public IReadOnlyList<KoLInventoryStateItem> Inventory => GetBucket(InventoryBucket.Inventory);
    public IReadOnlyList<KoLInventoryStateItem> Closet => GetBucket(InventoryBucket.Closet);
    public IReadOnlyList<KoLInventoryStateItem> Storage => GetBucket(InventoryBucket.Storage);
    public IReadOnlyList<KoLInventoryStateItem> Equipment => GetBucket(InventoryBucket.Equipment);
    public IReadOnlyList<KoLInventoryStateItem> Familiar => GetBucket(InventoryBucket.Familiar);
    public IReadOnlyList<KoLInventoryStateItem> InventoryItems => Inventory;
    public IReadOnlyList<KoLInventoryStateItem> ClosetItems => Closet;
    public IReadOnlyList<KoLInventoryStateItem> StorageItems => Storage;
    public IReadOnlyList<KoLInventoryStateItem> EquipmentItems => Equipment;
    public IReadOnlyList<KoLInventoryStateItem> FamiliarItems => Familiar;

    /// <summary>
    /// Items whose current item-master/use metadata says they can be used, or
    /// that were explicitly marked usable by the source.
    /// </summary>
    public IReadOnlyList<KoLInventoryStateItem> Usable
        => _items.Where(x => x.IsUsable || x.Bucket == InventoryBucket.Usable).ToArray();

    public IReadOnlyList<KoLInventoryStateItem> UsableItems => Usable;
    public IReadOnlyDictionary<InventoryBucket, IReadOnlyList<KoLInventoryStateItem>> Buckets
    {
        get
        {
            var result = _buckets.ToDictionary(x => x.Key, x => (IReadOnlyList<KoLInventoryStateItem>)x.Value);
            result[InventoryBucket.Usable] = Usable;
            return result;
        }
    }
    public IReadOnlyDictionary<InventoryBucket, IReadOnlyList<KoLInventoryStateItem>> ByBucket => Buckets;

    public IReadOnlyDictionary<string, int> Quantities
        => _items.GroupBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Sum(item => item.Quantity), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<KoLInventoryStateItem> GetBucket(InventoryBucket bucket)
        => bucket == InventoryBucket.Usable
            ? Usable
            : _buckets.TryGetValue(bucket, out var items) ? items : Array.Empty<KoLInventoryStateItem>();

    public IReadOnlyList<KoLInventoryStateItem> GetBucket(KoLInventoryBucket bucket)
        => GetBucket((InventoryBucket)bucket);

    public int Quantity(string itemId, InventoryBucket? bucket = null)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return 0;
        var source = bucket is { } selected ? GetBucket(selected) : _items;
        return source.Where(x => string.Equals(x.ItemId, itemId.Trim(), StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Quantity);
    }

    public int InventoryQuantity(string itemId) => Quantity(itemId, InventoryBucket.Inventory);
    public int ClosetQuantity(string itemId) => Quantity(itemId, InventoryBucket.Closet);
    public int StorageQuantity(string itemId) => Quantity(itemId, InventoryBucket.Storage);
    public int EquipmentQuantity(string itemId) => Quantity(itemId, InventoryBucket.Equipment);
    public int FamiliarQuantity(string itemId) => Quantity(itemId, InventoryBucket.Familiar);
    public int UsableQuantity(string itemId) => Quantity(itemId, InventoryBucket.Usable);

    public bool TryGet(string itemId, out KoLInventoryStateItem item)
    {
        item = _items.FirstOrDefault(x => string.Equals(x.ItemId, itemId?.Trim(), StringComparison.OrdinalIgnoreCase))!;
        return item is not null;
    }

    public KoLInventorySnapshot ToLegacySnapshot()
        => new(
            Inventory
                .Select(x => new KoLInventoryItem(x.ItemId, x.Name, x.Quantity))
                .ToArray(),
            FetchedAt,
            IsStale);

    public static KoLInventoryStateSnapshot Empty(DateTimeOffset fetchedAt, bool isStale = false)
        => new(Array.Empty<KoLInventoryStateItem>(), fetchedAt, isStale);
}

/// <summary>Read-only account client projection for the richer inventory state.</summary>
public interface IInventoryStateReadClient
{
    Task<KoLInventoryStateSnapshot> FetchInventoryStateAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}

/// <summary>Alias used by hosts that call the read side an inventory reader.</summary>
public interface IInventoryStateReader : IInventoryStateReadClient
{
}

/// <summary>
/// Read-only adapter over the existing <see cref="IAccountReadClient"/>.  It
/// does not issue a second request and has no operation that can mutate KoL.
/// </summary>
public sealed class KoLInventoryStateReadClient : IInventoryStateReadClient, IInventoryStateReader
{
    private readonly IAccountReadClient _account;
    private readonly ItemMaster? _itemMaster;
    private readonly InventoryStateAdapterOptions _options;

    public KoLInventoryStateReadClient(
        IAccountReadClient account,
        ItemMaster? itemMaster = null,
        InventoryStateAdapterOptions? options = null)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _itemMaster = itemMaster;
        _options = options ?? new InventoryStateAdapterOptions();
    }

    public async Task<KoLInventoryStateSnapshot> FetchInventoryStateAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _account.FetchInventoryAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        return KoLInventoryStateAdapter.FromSnapshot(
            snapshot,
            _itemMaster ?? _options.ItemMaster,
            _options);
    }
}

/// <summary>Pure adapters from legacy account snapshots or offline JSON.</summary>
public static class KoLInventoryStateAdapter
{
    public static KoLInventoryStateSnapshot FromSnapshot(
        KoLInventorySnapshot snapshot,
        ItemMaster? itemMaster = null,
        InventoryStateAdapterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var effectiveOptions = (options ?? new InventoryStateAdapterOptions()) with
        {
            ItemMaster = itemMaster ?? options?.ItemMaster,
            FetchedAt = options?.FetchedAt ?? snapshot.FetchedAt,
            IsStale = snapshot.IsStale || options?.IsStale == true
        };
        var items = snapshot.Items.Select(item => CreateItem(
            item.ItemId,
            item.Name,
            item.Quantity,
            InventoryBucket.Inventory,
            null,
            null,
            null,
            effectiveOptions));
        return new KoLInventoryStateSnapshot(items, effectiveOptions.FetchedAt!.Value, effectiveOptions.IsStale);
    }

    public static KoLInventoryStateSnapshot Adapt(
        KoLInventorySnapshot snapshot,
        ItemMaster? itemMaster = null,
        InventoryStateAdapterOptions? options = null)
        => FromSnapshot(snapshot, itemMaster, options);

    public static KoLInventoryStateSnapshot FromBuckets(
        KoLInventoryBuckets buckets,
        DateTimeOffset fetchedAt,
        ItemMaster? itemMaster = null,
        InventoryStateAdapterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        var effectiveOptions = (options ?? new InventoryStateAdapterOptions()) with
        {
            ItemMaster = itemMaster ?? options?.ItemMaster,
            FetchedAt = options?.FetchedAt ?? fetchedAt
        };
        var rows = new[]
        {
            (InventoryBucket.Inventory, buckets.Inventory),
            (InventoryBucket.Closet, buckets.Closet),
            (InventoryBucket.Storage, buckets.Storage),
            (InventoryBucket.Equipment, buckets.Equipment),
            (InventoryBucket.Familiar, buckets.Familiar),
            (InventoryBucket.Usable, buckets.Usable)
        };
        var stateItems = rows.SelectMany(row => row.Item2.Select(item => CreateItem(
            item.ItemId,
            item.Name,
            item.Quantity,
            row.Item1,
            null,
            null,
            row.Item1 == InventoryBucket.Usable,
            effectiveOptions)));
        return MergeBucketStateItems(stateItems, effectiveOptions.FetchedAt!.Value, effectiveOptions.IsStale);
    }

    public static KoLInventoryStateSnapshot Adapt(
        KoLInventoryBuckets buckets,
        DateTimeOffset fetchedAt,
        ItemMaster? itemMaster = null,
        InventoryStateAdapterOptions? options = null)
        => FromBuckets(buckets, fetchedAt, itemMaster, options);

    public static KoLInventoryStateSnapshot FromJson(
        string json,
        ItemMaster? itemMaster = null,
        InventoryStateAdapterOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("JSON is required.", nameof(json));
        using var document = JsonDocument.Parse(json);
        return FromJson(document.RootElement, itemMaster, options);
    }

    public static KoLInventoryStateSnapshot Parse(
        string json,
        ItemMaster? itemMaster = null,
        InventoryStateAdapterOptions? options = null)
        => FromJson(json, itemMaster, options);

    public static KoLInventoryStateSnapshot FromJson(
        JsonDocument document,
        ItemMaster? itemMaster = null,
        InventoryStateAdapterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return FromJson(document.RootElement, itemMaster, options);
    }

    public static KoLInventoryStateSnapshot FromJson(
        JsonElement root,
        ItemMaster? itemMaster = null,
        InventoryStateAdapterOptions? options = null)
    {
        var effectiveOptions = options ?? new InventoryStateAdapterOptions();
        itemMaster ??= effectiveOptions.ItemMaster;
        var fetchedAt = effectiveOptions.FetchedAt ??
            GetDateTime(root, "fetchedAt", "observedAt", "timestamp") ?? DateTimeOffset.UtcNow;
        var isStale = effectiveOptions.IsStale || GetBool(root, "stale", "isStale") == true;
        effectiveOptions = effectiveOptions with { FetchedAt = fetchedAt, IsStale = isStale };
        effectiveOptions = WithRootMetadata(root, effectiveOptions);
        var physical = new List<KoLInventoryStateItem>();
        var usableLines = new List<KoLInventoryStateItem>();
        var explicitBuckets = new HashSet<InventoryBucket>();

        foreach (var (name, bucket) in KnownBuckets)
        {
            if (!TryGetProperty(root, name, out var value)) continue;
            explicitBuckets.Add(bucket);
            var parsed = ParseBucket(value, bucket, effectiveOptions, itemMaster, explicitUsable: bucket == InventoryBucket.Usable);
            if (bucket == InventoryBucket.Usable) usableLines.AddRange(parsed);
            else physical.AddRange(parsed);
        }

        // Some exports put all rows under "items" and carry the location on
        // each row.  This is also useful for durable, source-neutral fixtures.
        if (TryGetProperty(root, "items", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var bucket = ParseBucketName(GetString(row, "bucket", "location", "section", "container"));
                if (bucket == InventoryBucket.Unknown) bucket = InventoryBucket.Inventory;
                var parsed = ParseEntry(row, null, bucket, effectiveOptions, itemMaster, explicitUsable: bucket == InventoryBucket.Usable);
                if (bucket == InventoryBucket.Usable) usableLines.Add(parsed);
                else physical.Add(parsed);
            }
        }

        // A flat legacy payload is accepted as inventory even when no named
        // bucket was included.
        if (physical.Count == 0 && usableLines.Count == 0 && root.ValueKind == JsonValueKind.Array)
        {
            physical.AddRange(ParseBucket(root, InventoryBucket.Inventory, effectiveOptions, itemMaster, false));
        }

        var merged = MergePhysical(physical);
        var byKey = merged.ToDictionary(
            x => (x.Bucket, x.ItemId),
            x => x);
        foreach (var usable in MergePhysical(usableLines))
        {
            var matchingKeys = byKey.Keys
                .Where(key => string.Equals(key.ItemId, usable.ItemId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matchingKeys.Length > 0)
            {
                foreach (var key in matchingKeys)
                {
                    var existing = byKey[key];
                    byKey[key] = existing with
                    {
                        IsUsable = true,
                        CostBasis = existing.CostBasis ?? usable.CostBasis,
                        FirstSeenAt = existing.FirstSeenAt ?? usable.FirstSeenAt
                    };
                }
            }
            else
            {
                byKey[(usable.Bucket, usable.ItemId)] = usable with { IsUsable = true };
            }
        }

        // Usability can be determined from the item master even when the API
        // does not send a separate usable collection.
        if (effectiveOptions.InferUsableFromItemMaster)
        {
            foreach (var item in byKey.Values.ToArray())
            {
                if (IsUsable(item.Item)) byKey[(item.Bucket, item.ItemId)] = item with { IsUsable = true };
            }
        }

        if (byKey.Count == 0 && explicitBuckets.Count == 0)
            return KoLInventoryStateSnapshot.Empty(fetchedAt, effectiveOptions.IsStale);
        return new KoLInventoryStateSnapshot(
            byKey.Values.OrderBy(x => x.Bucket).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            fetchedAt,
            effectiveOptions.IsStale);
    }

    public static KoLInventoryStateSnapshot Adapt(
        string json,
        ItemMaster? itemMaster = null,
        InventoryStateAdapterOptions? options = null)
        => FromJson(json, itemMaster, options);

    private static KoLInventoryStateSnapshot MergeBucketStateItems(
        IEnumerable<KoLInventoryStateItem> items,
        DateTimeOffset fetchedAt,
        bool isStale)
    {
        var physical = MergePhysical(items.Where(x => x.Bucket != InventoryBucket.Usable));
        var usable = MergePhysical(items.Where(x => x.Bucket == InventoryBucket.Usable));
        var byKey = physical.ToDictionary(x => (x.Bucket, x.ItemId), x => x);
        foreach (var line in usable)
        {
            var matches = byKey.Keys.Where(x => string.Equals(x.ItemId, line.ItemId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0)
            {
                byKey[(line.Bucket, line.ItemId)] = line;
                continue;
            }
            foreach (var key in matches)
            {
                var existing = byKey[key];
                byKey[key] = existing with
                {
                    IsUsable = true,
                    CostBasis = existing.CostBasis ?? line.CostBasis,
                    FirstSeenAt = existing.FirstSeenAt ?? line.FirstSeenAt
                };
            }
        }
        return new KoLInventoryStateSnapshot(byKey.Values, fetchedAt, isStale);
    }

    public static InventoryCostBasisLedger BuildCostBasisLedger(
        IEnumerable<KoLInventoryStateSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var values = new Dictionary<string, InventoryCostBasis>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            if (snapshot is null) continue;
            foreach (var item in snapshot.Items)
            {
                values.TryGetValue(item.ItemId, out var previous);
                var cost = previous?.CostBasis ?? item.CostBasis;
                var firstSeen = Min(previous?.FirstSeenAt, item.FirstSeenAt ?? snapshot.FetchedAt);
                values[item.ItemId] = new InventoryCostBasis(cost, firstSeen);
            }
        }
        return new InventoryCostBasisLedger(values);
    }

    public static InventoryCostBasisLedger BuildCostBasisLedger(
        IEnumerable<KoLInventorySnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var values = new Dictionary<string, InventoryCostBasis>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            if (snapshot is null) continue;
            foreach (var item in snapshot.Items)
            {
                if (string.IsNullOrWhiteSpace(item.ItemId)) continue;
                values.TryGetValue(item.ItemId, out var previous);
                var firstSeen = Min(previous?.FirstSeenAt, snapshot.FetchedAt);
                values[item.ItemId.Trim()] = new InventoryCostBasis(previous?.CostBasis, firstSeen);
            }
        }
        return new InventoryCostBasisLedger(values);
    }

    public static IReadOnlyDictionary<string, DateTimeOffset> BuildFirstSeenIndex(
        IEnumerable<KoLInventorySnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var values = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            if (snapshot is null) continue;
            foreach (var item in snapshot.Items)
            {
                if (string.IsNullOrWhiteSpace(item.ItemId)) continue;
                if (!values.TryGetValue(item.ItemId, out var first) || snapshot.FetchedAt < first)
                    values[item.ItemId.Trim()] = snapshot.FetchedAt;
            }
        }
        return values;
    }

    public static IReadOnlyDictionary<string, DateTimeOffset> BuildFirstSeenIndex(
        IEnumerable<KoLInventoryStateSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var values = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            if (snapshot is null) continue;
            foreach (var item in snapshot.Items)
            {
                if (string.IsNullOrWhiteSpace(item.ItemId)) continue;
                var observed = item.FirstSeenAt ?? snapshot.FetchedAt;
                if (!values.TryGetValue(item.ItemId, out var first) || observed < first)
                    values[item.ItemId.Trim()] = observed;
            }
        }
        return values;
    }

    private static readonly IReadOnlyDictionary<string, InventoryBucket> KnownBuckets =
        new Dictionary<string, InventoryBucket>(StringComparer.OrdinalIgnoreCase)
        {
            ["inventory"] = InventoryBucket.Inventory,
            ["closet"] = InventoryBucket.Closet,
            ["storage"] = InventoryBucket.Storage,
            ["equipment"] = InventoryBucket.Equipment,
            ["familiar"] = InventoryBucket.Familiar,
            ["familiaritems"] = InventoryBucket.Familiar,
            ["usable"] = InventoryBucket.Usable,
            ["usableitems"] = InventoryBucket.Usable
        };

    private static IEnumerable<KoLInventoryStateItem> ParseBucket(
        JsonElement value,
        InventoryBucket bucket,
        InventoryStateAdapterOptions options,
        ItemMaster? itemMaster,
        bool explicitUsable)
    {
        if (value.ValueKind == JsonValueKind.Object &&
            TryGetProperty(value, "items", out var nestedItems))
            value = nestedItems;
        if (value.ValueKind == JsonValueKind.Object &&
            TryGetProperty(value, "data", out var nestedData) &&
            (nestedData.ValueKind == JsonValueKind.Object || nestedData.ValueKind == JsonValueKind.Array))
            value = nestedData;

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in value.EnumerateArray())
            {
                if (entry.ValueKind is JsonValueKind.Object or JsonValueKind.String or JsonValueKind.Number)
                    yield return ParseEntry(entry, null, bucket, options, itemMaster, explicitUsable);
            }
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Object) yield break;

        // Equipment/familiar endpoints often return one descriptor object
        // rather than an id-keyed collection.
        if (HasAnyProperty(value, "itemId", "itemid", "whichitem", "id") &&
            (HasAnyProperty(value, "name", "itemName", "itemname", "quantity", "count") ||
             bucket is InventoryBucket.Equipment or InventoryBucket.Familiar))
        {
            yield return ParseEntry(value, null, bucket, options, itemMaster, explicitUsable);
            yield break;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (IsEnvelopeProperty(property.Name)) continue;
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.String or JsonValueKind.Number)
                yield return ParseEntry(property.Value, property.Name, bucket, options, itemMaster, explicitUsable);
        }
    }

    private static KoLInventoryStateItem ParseEntry(
        JsonElement value,
        string? fallbackItemId,
        InventoryBucket bucket,
        InventoryStateAdapterOptions options,
        ItemMaster? itemMaster,
        bool explicitUsable)
    {
        string? id = fallbackItemId;
        string? name = null;
        var quantity = 1;
        decimal? cost = null;
        DateTimeOffset? firstSeen = null;
        bool? usable = null;

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(value, "item", out var nested) &&
                nested.ValueKind == JsonValueKind.Object)
                value = nested;
            id = GetString(value, "itemId", "itemid", "id", "whichitem", "item") ?? id;
            name = GetString(value, "name", "itemName", "itemname", "displayName", "label");
            quantity = GetInt(value, "quantity", "count", "amount", "qty", "number", "total") ??
                (bucket is InventoryBucket.Equipment or InventoryBucket.Familiar ? 1 : quantity);
            cost = GetDecimal(value, "costBasis", "costbasis", "unitCost", "unit_cost", "acquisitionCost", "purchasePrice", "cost", "price");
            firstSeen = GetDateTime(value, "firstSeenAt", "firstSeen", "first_seen", "acquiredAt", "acquired", "observedAt");
            usable = GetBool(value, "usable", "isUsable", "canUse");
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(fallbackItemId) &&
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) &&
                fallbackItemId.All(char.IsDigit))
            {
                id = fallbackItemId;
                quantity = count;
            }
            else
            {
                id = text ?? id;
            }
        }
        else if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt32(out var numeric))
            {
                if (!string.IsNullOrWhiteSpace(fallbackItemId) &&
                    !fallbackItemId.All(char.IsDigit) &&
                    bucket is InventoryBucket.Equipment or InventoryBucket.Familiar)
                {
                    id = numeric.ToString(CultureInfo.InvariantCulture);
                    quantity = 1;
                }
                else
                {
                    quantity = numeric;
                }
            }
        }

        id = string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        if (id is null || id == "0")
            return new KoLInventoryStateItem("unknown", "Unknown item", 0, bucket);

        var metadata = itemMaster?.Find(id);
        var basis = options.Lookup(id);
        cost ??= basis?.CostBasis;
        firstSeen ??= basis?.FirstSeenAt;
        if (firstSeen is null && options.UseFetchedAtAsFirstSeenFallback)
            firstSeen = options.FetchedAt;
        name ??= metadata?.Name ?? id;
        var derivedUsable = explicitUsable || usable == true || IsUsable(metadata);
        return CreateItem(id, name, quantity, bucket, cost, firstSeen, derivedUsable, options, metadata);
    }

    private static KoLInventoryStateItem CreateItem(
        string itemId,
        string? name,
        int quantity,
        InventoryBucket bucket,
        decimal? cost,
        DateTimeOffset? firstSeen,
        bool? explicitUsable,
        InventoryStateAdapterOptions options,
        ItemMasterRecord? metadata = null)
    {
        metadata ??= options.ItemMaster?.Find(itemId);
        var usable = explicitUsable == true || IsUsable(metadata);
        if (firstSeen is null && options.UseFetchedAtAsFirstSeenFallback)
            firstSeen = options.FetchedAt;
        var basis = options.Lookup(itemId);
        return new KoLInventoryStateItem(
            itemId,
            metadata?.Name ?? (string.IsNullOrWhiteSpace(name) ? itemId : name),
            quantity,
            bucket,
            cost ?? basis?.CostBasis,
            firstSeen ?? basis?.FirstSeenAt,
            usable,
            metadata);
    }

    private static IReadOnlyList<KoLInventoryStateItem> MergePhysical(IEnumerable<KoLInventoryStateItem> items)
    {
        var merged = new Dictionary<(InventoryBucket Bucket, string ItemId), KoLInventoryStateItem>();
        foreach (var item in items)
        {
            if (item.Quantity <= 0 || string.Equals(item.ItemId, "unknown", StringComparison.OrdinalIgnoreCase)) continue;
            var key = (item.Bucket, item.ItemId);
            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = item;
                continue;
            }
            merged[key] = existing with
            {
                Quantity = existing.Quantity + item.Quantity,
                CostBasis = existing.CostBasis ?? item.CostBasis,
                FirstSeenAt = Min(existing.FirstSeenAt, item.FirstSeenAt),
                IsUsable = existing.IsUsable || item.IsUsable,
                Item = existing.Item ?? item.Item,
                Name = existing.Name == existing.ItemId ? item.Name : existing.Name
            };
        }
        return merged.Values.ToArray();
    }

    private static bool IsUsable(ItemMasterRecord? item)
        => item is not null && item.UseTags.Any(tag =>
            tag.Equals("usable", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("reusable", StringComparison.OrdinalIgnoreCase));

    private static InventoryStateAdapterOptions WithRootMetadata(
        JsonElement root,
        InventoryStateAdapterOptions options)
    {
        var costs = ParseRootCostMap(root, "costBasis", "costs");
        var firstSeen = ParseRootDateMap(root, "firstSeen", "firstSeenAt", "first_seen");
        if (costs.Count == 0 && firstSeen.Count == 0) return options;

        var mergedCosts = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        if (options.ItemCosts is not null)
            foreach (var entry in options.ItemCosts) mergedCosts[entry.Key] = entry.Value;
        foreach (var entry in costs) mergedCosts[entry.Key] = entry.Value;

        var mergedFirstSeen = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        if (options.FirstSeenByItem is not null)
            foreach (var entry in options.FirstSeenByItem) mergedFirstSeen[entry.Key] = entry.Value;
        foreach (var entry in firstSeen) mergedFirstSeen[entry.Key] = entry.Value;

        return options with { ItemCosts = mergedCosts, FirstSeenByItem = mergedFirstSeen };
    }

    private static IReadOnlyDictionary<string, decimal?> ParseRootCostMap(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(root, name, out var value) || value.ValueKind != JsonValueKind.Object) continue;
            var result = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                decimal? cost;
                if (property.Value.ValueKind == JsonValueKind.Object)
                    cost = GetDecimal(property.Value, "costBasis", "costbasis", "unitCost", "cost", "price");
                else
                    cost = GetDecimalValue(property.Value);
                if (cost is not null) result[property.Name.Trim()] = cost;
            }
            return result;
        }
        return new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, DateTimeOffset> ParseRootDateMap(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(root, name, out var value) || value.ValueKind != JsonValueKind.Object) continue;
            var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                var date = property.Value.ValueKind == JsonValueKind.Object
                    ? GetDateTime(property.Value, "firstSeenAt", "firstSeen", "acquiredAt", "acquired")
                    : GetDateTimeValue(property.Value);
                if (date is not null) result[property.Name.Trim()] = date.Value;
            }
            return result;
        }
        return new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
    }

    private static InventoryBucket ParseBucketName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return InventoryBucket.Unknown;
        if (KnownBuckets.TryGetValue(value.Trim(), out var bucket)) return bucket;
        return Enum.TryParse<InventoryBucket>(value.Trim(), true, out var parsed) ? parsed : InventoryBucket.Unknown;
    }

    private static bool IsEnvelopeProperty(string name)
        => name.Equals("fetchedAt", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("observedAt", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("stale", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("isStale", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("costBasis", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("costs", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("firstSeen", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("firstSeenAt", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)) return true;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static bool HasAnyProperty(JsonElement element, params string[] names)
        => names.Any(name => TryGetProperty(element, name, out _));

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        }
        return null;
    }

    private static int? GetInt(JsonElement element, params string[] names)
    {
        var value = GetString(element, names);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static decimal? GetDecimal(JsonElement element, params string[] names)
    {
        var value = GetString(element, names);
        return ParseDecimal(value);
    }

    private static decimal? GetDecimalValue(JsonElement value)
        => value.ValueKind == JsonValueKind.Number
            ? value.TryGetDecimal(out var result) && result >= 0m ? result : null
            : value.ValueKind == JsonValueKind.String ? ParseDecimal(value.GetString()) : null;

    private static decimal? ParseDecimal(string? value)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result) && result >= 0m
            ? result
            : null;

    private static DateTimeOffset? GetDateTime(JsonElement element, params string[] names)
    {
        var value = GetString(element, names);
        return ParseDateTime(value);
    }

    private static DateTimeOffset? GetDateTimeValue(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? ParseDateTime(value.GetString()) : null;

    private static DateTimeOffset? ParseDateTime(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result)
            ? result
            : null;

    private static bool? GetBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static DateTimeOffset? Min(DateTimeOffset? left, DateTimeOffset? right)
        => left is null ? right : right is null ? left : left <= right ? left : right;
}

/// <summary>Extension convenience for existing account read clients.</summary>
public static class AccountReadInventoryStateExtensions
{
    public static Task<KoLInventoryStateSnapshot> FetchInventoryStateAsync(
        this IAccountReadClient client,
        bool forceRefresh,
        CancellationToken cancellationToken)
        => FetchInventoryStateAsync(client, forceRefresh, null, null, cancellationToken);

    public static async Task<KoLInventoryStateSnapshot> FetchInventoryStateAsync(
        this IAccountReadClient client,
        bool forceRefresh = false,
        ItemMaster? itemMaster = null,
        InventoryStateAdapterOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var snapshot = await client.FetchInventoryAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        return KoLInventoryStateAdapter.FromSnapshot(snapshot, itemMaster, options);
    }
}

/// <summary>Short compatibility facade for hosts that omit the KoL prefix.</summary>
public static class InventoryStateAdapter
{
    public static KoLInventoryStateSnapshot FromSnapshot(
        KoLInventorySnapshot snapshot,
        ItemMaster? itemMaster = null,
        InventoryStateAdapterOptions? options = null)
        => KoLInventoryStateAdapter.FromSnapshot(snapshot, itemMaster, options);

    public static KoLInventoryStateSnapshot FromJson(
        string json,
        ItemMaster? itemMaster = null,
        InventoryStateAdapterOptions? options = null)
        => KoLInventoryStateAdapter.FromJson(json, itemMaster, options);

    public static KoLInventoryStateSnapshot FromBuckets(
        KoLInventoryBuckets buckets,
        DateTimeOffset fetchedAt,
        ItemMaster? itemMaster = null,
        InventoryStateAdapterOptions? options = null)
        => KoLInventoryStateAdapter.FromBuckets(buckets, fetchedAt, itemMaster, options);

    public static KoLInventoryStateSnapshot Parse(
        string json,
        ItemMaster? itemMaster = null,
        InventoryStateAdapterOptions? options = null)
        => KoLInventoryStateAdapter.FromJson(json, itemMaster, options);

    public static InventoryCostBasisLedger BuildCostBasisLedger(
        IEnumerable<KoLInventoryStateSnapshot> snapshots)
        => KoLInventoryStateAdapter.BuildCostBasisLedger(snapshots);

    public static InventoryCostBasisLedger BuildCostBasisLedger(
        IEnumerable<KoLInventorySnapshot> snapshots)
        => KoLInventoryStateAdapter.BuildCostBasisLedger(snapshots);

    public static IReadOnlyDictionary<string, DateTimeOffset> BuildFirstSeenIndex(
        IEnumerable<KoLInventorySnapshot> snapshots)
        => KoLInventoryStateAdapter.BuildFirstSeenIndex(snapshots);

    public static IReadOnlyDictionary<string, DateTimeOffset> BuildFirstSeenIndex(
        IEnumerable<KoLInventoryStateSnapshot> snapshots)
        => KoLInventoryStateAdapter.BuildFirstSeenIndex(snapshots);
}
