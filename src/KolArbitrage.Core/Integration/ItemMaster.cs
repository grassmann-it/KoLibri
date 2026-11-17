using System.Collections.Immutable;
using System.Globalization;
using System.Net;

namespace KolArbitrage.Core.Integration;

/// <summary>
/// Access markers used by KoLmafia's <c>items.txt</c> catalog. The catalog
/// stores these markers as a compact string in its access column.
/// </summary>
[Flags]
public enum ItemAccessFlags
{
    None = 0,
    Quest = 1 << 0,
    Giftable = 1 << 1,
    Tradeable = 1 << 2,
    Discardable = 1 << 3,
    AccountBound = 1 << 4,
    QuestItem = Quest,
    Gift = Giftable,
    Trade = Tradeable,
    Discard = Discardable,
    Bound = AccountBound
}

/// <summary>Capabilities used by item scanners and eligibility checks.</summary>
[Flags]
public enum ItemEligibilityFlags
{
    None = 0,
    Tradeable = 1 << 0,
    Giftable = 1 << 1,
    AccountBound = 1 << 2,
    NpcSellable = 1 << 3,
    Autosellable = 1 << 4,
    Quest = 1 << 5,
    Discardable = 1 << 6
}

/// <summary>Known relationships between a packaged item and its opened item.</summary>
public enum ItemVariantKind
{
    None = 0,
    Packaged = 1,
    Opened = 2
}

/// <summary>Optional provenance label attached by a catalog importer.</summary>
public enum ItemSourceTag
{
    KoLmafiaItemsTxt = 0,
    Mall = 1,
    Npc = 2,
    Coinmaster = 3,
    Crafting = 4,
    Adventure = 5,
    Event = 6,
    Seasonal = 7,
    Iotm = 8,
    Packaged = 9,
    Opened = 10,
    AccountBound = 11
}

/// <summary>One explicit packaged/opened relationship.</summary>
public sealed record ItemVariantLink(string PackagedItemId, string OpenedItemId, string? Tag = null)
{
    public string PackagedId => PackagedItemId;
    public string OpenedId => OpenedItemId;

    public ItemVariantLink Normalize()
        => this with
        {
            PackagedItemId = ItemMasterRecord.NormalizeItemId(PackagedItemId),
            OpenedItemId = ItemMasterRecord.NormalizeItemId(OpenedItemId),
            Tag = string.IsNullOrWhiteSpace(Tag) ? null : Tag.Trim()
        };
}

/// <summary>
/// Stable, offline item data imported from KoLmafia's items.txt.
/// The first eight constructor arguments mirror the current row layout:
/// item id, name, description id, image, use, access, autosell and plural.
/// </summary>
public sealed record ItemMasterRecord
{
    public ItemMasterRecord(
        string itemId,
        string name,
        string? descriptionId = null,
        string? image = null,
        string? use = null,
        string? access = null,
        int autosellPrice = 0,
        string? plural = null,
        IEnumerable<string>? sourceTags = null,
        ItemVariantKind variantKind = ItemVariantKind.None,
        string? packagedItemId = null,
        string? openedItemId = null)
    {
        ItemId = NormalizeItemId(itemId);
        Name = CleanText(name, nameof(name));
        DescriptionId = NormalizeOptional(descriptionId);
        Image = NormalizeOptional(image);
        Use = NormalizeOptional(use);
        RawAccess = NormalizeOptional(access) ?? string.Empty;
        AccessFlags = ParseAccessFlags(RawAccess);
        AutosellPrice = autosellPrice;
        Plural = NormalizeOptional(plural);
        UseTags = ParseUseTags(Use);
        SourceTags = NormalizeTags(sourceTags);
        VariantKind = variantKind == ItemVariantKind.None ? InferVariantKind(Name, UseTags) : variantKind;
        PackagedItemId = NormalizeOptional(packagedItemId);
        OpenedItemId = NormalizeOptional(openedItemId);

        if (VariantKind == ItemVariantKind.Packaged)
            SourceTags = AddTag(SourceTags, "packaged");
        else if (VariantKind == ItemVariantKind.Opened)
            SourceTags = AddTag(SourceTags, "opened");
    }

    public string ItemId { get; init; }
    public string Name { get; init; }
    public string? DescriptionId { get; init; }
    public string? Image { get; init; }
    public string? Use { get; init; }
    public string Access => RawAccess;
    public IReadOnlySet<string> ItemUseTags => UseTags;
    public string RawAccess { get; init; }
    public ItemAccessFlags AccessFlags { get; init; }
    public int AutosellPrice { get; init; }
    public string? Plural { get; init; }
    public IReadOnlySet<string> UseTags { get; init; }
    public IReadOnlySet<string> SourceTags { get; init; }
    public ItemVariantKind VariantKind { get; init; }
    public string? PackagedItemId { get; init; }
    public string? OpenedItemId { get; init; }

    public string Id => ItemId;
    public string ItemNumber => ItemId;
    public string? DescId => DescriptionId;
    public int NpcPrice => AutosellPrice;
    public int NpcSellPrice => AutosellPrice;
    public int Autosell => AutosellPrice;
    public int NpcValue => AutosellPrice;
    public bool IsAutosellable => AutosellPrice > 0;
    public bool CanAutosell => IsAutosellable;
    public bool IsNpcSellable => IsAutosellable;
    public bool CanSellToNpc => IsAutosellable;
    public bool IsTradeable => AccessFlags.HasFlag(ItemAccessFlags.Tradeable);
    public bool IsGiftable => AccessFlags.HasFlag(ItemAccessFlags.Giftable);
    public bool CanTrade => IsTradeable;
    public bool CanGift => IsGiftable;
    /// <summary>
    /// Whether the item can be acquired through a Mall listing.  Mall trade
    /// and gift transfer are independent capabilities in KoL; a trade-only
    /// row (the <c>t</c> access marker) is still a valid Mall item.
    /// </summary>
    public bool IsMallTradeable => IsTradeable && !IsAccountBound;
    public bool MallTradeable => IsMallTradeable;
    public bool CanBuyFromMall => IsMallTradeable;
    /// <summary>Whether the item can be transferred using KoL's gift flow.</summary>
    public bool IsGiftTransferable => IsGiftable && !IsAccountBound;
    public bool GiftTransferable => IsGiftTransferable;
    public bool CanTransferAsGift => IsGiftTransferable;
    public bool CanTransferByGift => IsGiftTransferable;
    public bool IsQuestItem => AccessFlags.HasFlag(ItemAccessFlags.Quest);
    public bool IsDiscardable => AccessFlags.HasFlag(ItemAccessFlags.Discardable);
    public bool IsAccountBound
        => AccessFlags.HasFlag(ItemAccessFlags.AccountBound) || (!IsTradeable && !IsGiftable);
    public bool IsBound => IsAccountBound;
    public bool IsPackaged => VariantKind == ItemVariantKind.Packaged;
    public bool IsOpened => VariantKind == ItemVariantKind.Opened;
    public string? PackagedVariantId => PackagedItemId;
    public string? OpenedVariantId => OpenedItemId;
    public string? VariantOfItemId => IsPackaged ? OpenedItemId : IsOpened ? PackagedItemId : null;

    public ItemEligibilityFlags EligibilityFlags
    {
        get
        {
            var result = ItemEligibilityFlags.None;
            if (IsTradeable) result |= ItemEligibilityFlags.Tradeable;
            if (IsGiftable) result |= ItemEligibilityFlags.Giftable;
            if (IsAccountBound) result |= ItemEligibilityFlags.AccountBound;
            if (IsNpcSellable) result |= ItemEligibilityFlags.NpcSellable;
            if (IsAutosellable) result |= ItemEligibilityFlags.Autosellable;
            if (IsQuestItem) result |= ItemEligibilityFlags.Quest;
            if (IsDiscardable) result |= ItemEligibilityFlags.Discardable;
            return result;
        }
    }

    public ItemEligibilityFlags Eligibility => EligibilityFlags;

    public bool HasSourceTag(string tag)
        => !string.IsNullOrWhiteSpace(tag) && SourceTags.Contains(tag.Trim());

    public bool HasSourceTag(ItemSourceTag tag)
        => SourceTagKinds.Contains(tag);

    /// <summary>Enum projection for callers that prefer strongly typed source labels.</summary>
    public IReadOnlySet<ItemSourceTag> SourceTagKinds
    {
        get
        {
            var result = new HashSet<ItemSourceTag>();
            foreach (var tag in SourceTags)
            {
                if (tag.Equals("kolmafia-items.txt", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("kolmafia", StringComparison.OrdinalIgnoreCase)) result.Add(ItemSourceTag.KoLmafiaItemsTxt);
                else if (Enum.TryParse<ItemSourceTag>(tag, true, out var parsed)) result.Add(parsed);
            }
            if (IsPackaged) result.Add(ItemSourceTag.Packaged);
            if (IsOpened) result.Add(ItemSourceTag.Opened);
            if (IsAccountBound) result.Add(ItemSourceTag.AccountBound);
            if (IsNpcSellable) result.Add(ItemSourceTag.Npc);
            return result.ToImmutableHashSet();
        }
    }

    public IReadOnlySet<string> SourceLabels => SourceTags;

    public ItemMasterRecord WithSourceTags(params string[] tags)
        => this with { SourceTags = NormalizeTags(SourceTags.Concat(tags ?? Array.Empty<string>())) };

    public ItemMasterRecord WithVariant(ItemVariantKind kind, string? packagedItemId = null, string? openedItemId = null)
    {
        var tags = SourceTags;
        if (kind == ItemVariantKind.Packaged) tags = AddTag(tags, "packaged");
        if (kind == ItemVariantKind.Opened) tags = AddTag(tags, "opened");
        return this with
        {
            VariantKind = kind,
            SourceTags = tags,
            PackagedItemId = NormalizeOptional(packagedItemId),
            OpenedItemId = NormalizeOptional(openedItemId)
        };
    }

    public bool Equals(ItemMasterRecord? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        return ItemId == other.ItemId && Name == other.Name && DescriptionId == other.DescriptionId &&
            Image == other.Image && Use == other.Use && RawAccess == other.RawAccess &&
            AccessFlags == other.AccessFlags && AutosellPrice == other.AutosellPrice && Plural == other.Plural &&
            VariantKind == other.VariantKind && PackagedItemId == other.PackagedItemId &&
            OpenedItemId == other.OpenedItemId && UseTags.SetEquals(other.UseTags) &&
            SourceTags.SetEquals(other.SourceTags);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ItemId, StringComparer.Ordinal);
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(DescriptionId, StringComparer.Ordinal);
        hash.Add(Image, StringComparer.Ordinal);
        hash.Add(Use, StringComparer.Ordinal);
        hash.Add(RawAccess, StringComparer.Ordinal);
        hash.Add(AccessFlags);
        hash.Add(AutosellPrice);
        hash.Add(Plural, StringComparer.Ordinal);
        hash.Add(VariantKind);
        hash.Add(PackagedItemId, StringComparer.Ordinal);
        hash.Add(OpenedItemId, StringComparer.Ordinal);
        foreach (var tag in UseTags.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) hash.Add(tag, StringComparer.OrdinalIgnoreCase);
        foreach (var tag in SourceTags.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) hash.Add(tag, StringComparer.OrdinalIgnoreCase);
        return hash.ToHashCode();
    }

    internal static string NormalizeItemId(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            throw new ArgumentException("Item id is required.", nameof(itemId));
        var value = itemId.Trim();
        if (!value.All(char.IsDigit))
            throw new FormatException($"Item id '{itemId}' is not numeric.");
        return value;
    }

    internal static ItemAccessFlags ParseAccessFlags(string? access)
    {
        var flags = ItemAccessFlags.None;
        foreach (var marker in (access ?? string.Empty).Trim().ToLowerInvariant())
        {
            flags |= marker switch
            {
                'q' => ItemAccessFlags.Quest,
                'g' => ItemAccessFlags.Giftable,
                't' => ItemAccessFlags.Tradeable,
                'd' => ItemAccessFlags.Discardable,
                'b' => ItemAccessFlags.AccountBound,
                _ => ItemAccessFlags.None
            };
        }
        return flags;
    }

    internal static IReadOnlySet<string> ParseUseTags(string? use)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in (use ?? string.Empty).Split(',', ';', '|'))
        {
            if (!string.IsNullOrWhiteSpace(token)) result.Add(token.Trim().ToLowerInvariant());
        }
        return result.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static ItemVariantKind InferVariantKind(string name, IReadOnlySet<string> useTags)
    {
        var value = name.Trim().ToLowerInvariant();
        // KoLmafia's use column is not consistent across item generations;
        // some packaged IotM forms are identified only by their display name.
        // Keep the name markers deliberately data-driven and independent of a
        // list of current IotMs so future monthly items are covered too.
        if (useTags.Contains("package") || useTags.Contains("packaged") ||
            useTags.Contains("box") || useTags.Contains("boxed") ||
            useTags.Contains("loot box") || useTags.Contains("loot-box") ||
            useTags.Contains("lootbox") || useTags.Contains("scabbarded") ||
            useTags.Contains("shrink-wrapped") || useTags.Contains("shrink wrapped") ||
            useTags.Contains("shrinkwrapped") || useTags.Contains("wrapped") ||
            useTags.Contains("discreetly-wrapped") || useTags.Contains("discreetly wrapped") ||
            useTags.Contains("discreetlywrapped") ||
            useTags.Contains("unopened") ||
            value.StartsWith("packaged ", StringComparison.Ordinal) ||
            value.StartsWith("unopened ", StringComparison.Ordinal) ||
            value.Contains("unopened", StringComparison.Ordinal) ||
            value.Contains("(unopened)", StringComparison.Ordinal) ||
            value.StartsWith("wrapped ", StringComparison.Ordinal) ||
            value.Contains("(wrapped)", StringComparison.Ordinal) ||
            value.Contains("boxed", StringComparison.Ordinal) ||
            value.Contains("scabbarded", StringComparison.Ordinal) ||
            value.Contains("shrink-wrapped", StringComparison.Ordinal) ||
            value.Contains("shrink wrapped", StringComparison.Ordinal) ||
            value.Contains("shrinkwrapped", StringComparison.Ordinal) ||
            value.Contains("loot box", StringComparison.Ordinal) ||
            value.Contains("loot-box", StringComparison.Ordinal) ||
            value.Contains("lootbox", StringComparison.Ordinal) ||
            value.Contains("discreetly-wrapped", StringComparison.Ordinal) ||
            value.Contains("discreetly wrapped", StringComparison.Ordinal) ||
            value.Contains("discreetlywrapped", StringComparison.Ordinal))
            return ItemVariantKind.Packaged;
        if (useTags.Contains("open") || useTags.Contains("opened") ||
            value.StartsWith("opened ", StringComparison.Ordinal) ||
            value.StartsWith("unwrapped ", StringComparison.Ordinal) ||
            value.Contains("(opened)", StringComparison.Ordinal) ||
            value.Contains("(unwrapped)", StringComparison.Ordinal))
            return ItemVariantKind.Opened;
        return ItemVariantKind.None;
    }

    internal static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : WebUtility.HtmlDecode(value.Trim());

    internal static string CleanText(string? value, string parameterName)
    {
        var result = NormalizeOptional(value);
        if (result is null) throw new ArgumentException("Value is required.", parameterName);
        return result;
    }

    internal static IReadOnlySet<string> NormalizeTags(IEnumerable<string>? tags)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tags is not null)
        {
            foreach (var tag in tags)
                if (!string.IsNullOrWhiteSpace(tag)) result.Add(tag.Trim());
        }
        return result.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlySet<string> AddTag(IReadOnlySet<string> tags, string tag)
        => NormalizeTags(tags.Append(tag));
}

/// <summary>Convenient projection of the eligibility bits for a catalog row.</summary>
public sealed record ItemEligibility(
    bool IsTradeable,
    bool IsGiftable,
    bool IsAccountBound,
    bool IsNpcSellable,
    bool IsAutosellable,
    bool IsQuestItem = false,
    bool IsDiscardable = false)
{
    public bool IsBound => IsAccountBound;
    public ItemEligibilityFlags Flags
    {
        get
        {
            var flags = ItemEligibilityFlags.None;
            if (IsTradeable) flags |= ItemEligibilityFlags.Tradeable;
            if (IsGiftable) flags |= ItemEligibilityFlags.Giftable;
            if (IsAccountBound) flags |= ItemEligibilityFlags.AccountBound;
            if (IsNpcSellable) flags |= ItemEligibilityFlags.NpcSellable;
            if (IsAutosellable) flags |= ItemEligibilityFlags.Autosellable;
            if (IsQuestItem) flags |= ItemEligibilityFlags.Quest;
            if (IsDiscardable) flags |= ItemEligibilityFlags.Discardable;
            return flags;
        }
    }

    public static ItemEligibility From(ItemMasterRecord item)
        => new(item.IsTradeable, item.IsGiftable, item.IsAccountBound,
            item.IsNpcSellable, item.IsAutosellable, item.IsQuestItem, item.IsDiscardable);
}

/// <summary>Immutable lookup of imported item records.</summary>
public sealed class ItemMaster : IReadOnlyCollection<ItemMasterRecord>
{
    private readonly ImmutableArray<ItemMasterRecord> _items;
    private readonly ImmutableDictionary<string, ItemMasterRecord> _byId;
    private readonly ImmutableDictionary<string, ImmutableArray<ItemMasterRecord>> _byName;
    private readonly ImmutableDictionary<string, ItemVariantLink> _packagedToOpened;
    private readonly ImmutableDictionary<string, ItemVariantLink> _openedToPackaged;

    public ItemMaster(IEnumerable<ItemMasterRecord> items, IEnumerable<ItemVariantLink>? variants = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var supplied = items.ToImmutableArray();
        var links = (variants ?? Array.Empty<ItemVariantLink>()).Select(x => x.Normalize()).ToList();
        foreach (var item in supplied)
        {
            if (item.IsPackaged && !string.IsNullOrWhiteSpace(item.OpenedItemId))
                links.Add(new ItemVariantLink(item.ItemId, item.OpenedItemId!).Normalize());
            else if (item.IsOpened && !string.IsNullOrWhiteSpace(item.PackagedItemId))
                links.Add(new ItemVariantLink(item.PackagedItemId!, item.ItemId).Normalize());
        }
        links = links
            .GroupBy(x => x.PackagedItemId, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
        AddInferredVariantLinks(supplied, links);
        var packagedIds = links.ToDictionary(x => x.PackagedItemId, x => x.OpenedItemId, StringComparer.OrdinalIgnoreCase);
        var openedIds = links.ToDictionary(x => x.OpenedItemId, x => x.PackagedItemId, StringComparer.OrdinalIgnoreCase);
        _items = supplied.Select(item =>
        {
            var isPackaged = packagedIds.ContainsKey(item.ItemId);
            var isOpened = openedIds.ContainsKey(item.ItemId);
            return item with
            {
                VariantKind = isPackaged ? ItemVariantKind.Packaged :
                    isOpened ? ItemVariantKind.Opened : item.VariantKind,
                PackagedItemId = openedIds.TryGetValue(item.ItemId, out var packaged) ? packaged : item.PackagedItemId,
                OpenedItemId = packagedIds.TryGetValue(item.ItemId, out var opened) ? opened : item.OpenedItemId
            };
        }).ToImmutableArray();
        _byId = _items
            .GroupBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        _byName = _items
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(x => x.Key, x => x.ToImmutableArray(), StringComparer.OrdinalIgnoreCase);

        _packagedToOpened = links.ToImmutableDictionary(x => x.PackagedItemId, StringComparer.OrdinalIgnoreCase);
        _openedToPackaged = links.ToImmutableDictionary(x => x.OpenedItemId, StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _items.Length;
    public IReadOnlyList<ItemMasterRecord> Items => _items;
    public IEnumerable<ItemMasterRecord> TradeableItems => _items.Where(x => x.IsTradeable);
    public IEnumerable<ItemMasterRecord> GiftableItems => _items.Where(x => x.IsGiftable);
    public IEnumerable<ItemMasterRecord> NpcSellableItems => _items.Where(x => x.IsNpcSellable);

    public ItemMasterRecord? Find(string? itemId)
        => itemId is not null && _byId.TryGetValue(itemId.Trim(), out var item) ? item : null;

    public bool TryGet(string itemId, out ItemMasterRecord item)
    {
        item = null!;
        if (string.IsNullOrWhiteSpace(itemId)) return false;
        return _byId.TryGetValue(itemId.Trim(), out item!);
    }

    public ItemMasterRecord Get(string itemId)
        => TryGet(itemId, out var item)
            ? item
            : throw new KeyNotFoundException($"Item '{itemId}' is not in the item master.");

    public IReadOnlyList<ItemMasterRecord> FindByName(string name)
        => name is not null && _byName.TryGetValue(name.Trim(), out var items)
            ? items
            : Array.Empty<ItemMasterRecord>();

    public bool TryGetByName(string name, out IReadOnlyList<ItemMasterRecord> items)
    {
        items = Array.Empty<ItemMasterRecord>();
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!_byName.TryGetValue(name.Trim(), out var found)) return false;
        items = found;
        return true;
    }

    public bool TryGetOpenedVariant(string packagedItemId, out ItemMasterRecord opened)
    {
        opened = null!;
        if (!_packagedToOpened.TryGetValue(packagedItemId.Trim(), out var link)) return false;
        return TryGet(link.OpenedItemId, out opened!);
    }

    public bool TryGetPackagedVariant(string openedItemId, out ItemMasterRecord packaged)
    {
        packaged = null!;
        if (!_openedToPackaged.TryGetValue(openedItemId.Trim(), out var link)) return false;
        return TryGet(link.PackagedItemId, out packaged!);
    }

    public ItemMasterRecord? GetOpenedVariant(string packagedItemId)
        => TryGetOpenedVariant(packagedItemId, out var opened) ? opened : null;

    public ItemMasterRecord? GetPackagedVariant(string openedItemId)
        => TryGetPackagedVariant(openedItemId, out var packaged) ? packaged : null;

    public IEnumerator<ItemMasterRecord> GetEnumerator() => ((IEnumerable<ItemMasterRecord>)_items).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private static void AddInferredVariantLinks(
        IReadOnlyList<ItemMasterRecord> items,
        ICollection<ItemVariantLink> links)
    {
        var knownPackaged = links.Select(x => x.PackagedItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownOpened = links.Select(x => x.OpenedItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var packaged in items.Where(x => x.IsPackaged))
        {
            if (knownPackaged.Contains(packaged.ItemId)) continue;
            var candidate = items.Where(x => !x.IsPackaged && !knownOpened.Contains(x.ItemId))
                .Where(x => AreAdjacentIds(packaged.ItemId, x.ItemId))
                .OrderBy(x => Math.Abs(ParseNumericId(x.ItemId) - ParseNumericId(packaged.ItemId)))
                .FirstOrDefault();
            if (candidate is null) continue;
            links.Add(new ItemVariantLink(packaged.ItemId, candidate.ItemId, "inferred-adjacent"));
            knownPackaged.Add(packaged.ItemId);
            knownOpened.Add(candidate.ItemId);
        }
    }

    private static bool AreAdjacentIds(string left, string right)
    {
        if (!long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var a) ||
            !long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var b)) return false;
        return Math.Abs(a - b) == 1;
    }

    private static long ParseNumericId(string value)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ? result : long.MaxValue;
}

/// <summary>Parser options for an offline KoLmafia items.txt snapshot.</summary>
public sealed record KoLmafiaItemsTxtParserOptions
{
    public bool IgnoreMalformedRows { get; init; } = true;
    public bool IncludeSourceTag { get; init; } = true;
    public string SourceTag { get; init; } = "kolmafia-items.txt";
    public IReadOnlyList<ItemVariantLink> Variants { get; init; } = Array.Empty<ItemVariantLink>();
}

/// <summary>Parsed catalog plus row diagnostics, useful when a snapshot is partial.</summary>
public sealed record ItemMasterParseResult(ItemMaster Catalog, IReadOnlyList<string> Diagnostics)
{
    public IReadOnlyList<ItemMasterRecord> Items => Catalog.Items;
}

/// <summary>
/// Offline parser for KoLmafia's tab-separated items.txt resource. No network
/// access is performed; callers can provide a checked-in fixture or stream.
/// </summary>
public static class KoLmafiaItemsTxtParser
{
    public static ItemMasterParseResult ParseWithDiagnostics(string text, KoLmafiaItemsTxtParserOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var reader = new StringReader(text);
        return ParseWithDiagnostics(reader, options);
    }

    public static ItemMasterParseResult ParseWithDiagnostics(TextReader reader, KoLmafiaItemsTxtParserOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        options ??= new KoLmafiaItemsTxtParserOptions();
        var rows = new List<ItemMasterRecord>();
        var diagnostics = new List<string>();
        var lineNumber = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            var trimmedLine = line.TrimStart('\uFEFF', ' ', '\t');
            if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith('#')) continue;
            line = trimmedLine;
            var fields = line.Split('\t');
            // The first line in some snapshots is a numeric format/version
            // marker; item rows with only an id are placeholders and carry no
            // metadata. Both are intentionally ignored.
            if (fields.Length == 1 && IsNumeric(fields[0])) continue;
            if (fields.Length < 7)
            {
                AddMalformed(options, diagnostics, lineNumber, "expected at least 7 tab-separated columns");
                continue;
            }

            // Current rows start with id. Accept historical name/id rows too.
            var idIndex = IsNumeric(fields[0]) ? 0 : IsNumeric(fields[1]) ? 1 : -1;
            if (idIndex < 0 || string.IsNullOrWhiteSpace(fields[idIndex == 0 ? 1 : 0]))
            {
                AddMalformed(options, diagnostics, lineNumber, "missing numeric item id or name");
                continue;
            }
            var nameIndex = idIndex == 0 ? 1 : 0;
            var description = fields[2];
            var image = fields[3];
            var use = fields[4];
            var access = fields[5];
            if (!int.TryParse(fields[6].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var autosell))
            {
                AddMalformed(options, diagnostics, lineNumber, "autosell is not an integer");
                continue;
            }

            var tags = options.IncludeSourceTag && !string.IsNullOrWhiteSpace(options.SourceTag)
                ? new[] { options.SourceTag }
                : Array.Empty<string>();
            try
            {
                rows.Add(new ItemMasterRecord(
                    fields[idIndex], fields[nameIndex], description, image, use, access,
                    autosell, fields.Length > 7 ? fields[7] : null, tags));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                AddMalformed(options, diagnostics, lineNumber, ex.Message);
            }
        }

        return new ItemMasterParseResult(new ItemMaster(rows, options.Variants), diagnostics);
    }

    public static ItemMaster Parse(string text, KoLmafiaItemsTxtParserOptions? options = null)
        => ParseWithDiagnostics(text, options).Catalog;

    public static ItemMaster ParseCatalog(string text, KoLmafiaItemsTxtParserOptions? options = null)
        => Parse(text, options);

    public static ItemMaster Parse(TextReader reader, KoLmafiaItemsTxtParserOptions? options = null)
        => ParseWithDiagnostics(reader, options).Catalog;

    public static ItemMaster ParseFile(string path, KoLmafiaItemsTxtParserOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = File.OpenText(path);
        return Parse(reader, options);
    }

    public static IReadOnlyList<ItemMasterRecord> ParseItems(string text, KoLmafiaItemsTxtParserOptions? options = null)
        => Parse(text, options).Items;

    public static ItemMasterRecord? ParseLine(string line, KoLmafiaItemsTxtParserOptions? options = null)
        => Parse(line, options).Items.SingleOrDefault();

    private static bool IsNumeric(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().All(char.IsDigit);

    private static void AddMalformed(KoLmafiaItemsTxtParserOptions options, ICollection<string> diagnostics, int lineNumber, string message)
    {
        var diagnostic = $"Line {lineNumber}: {message}.";
        if (options.IgnoreMalformedRows) diagnostics.Add(diagnostic);
        else throw new FormatException(diagnostic);
    }
}

// Short aliases keep the parser discoverable for callers using generic naming.
public static class ItemMasterParser
{
    public static ItemMaster Parse(string text, KoLmafiaItemsTxtParserOptions? options = null)
        => KoLmafiaItemsTxtParser.Parse(text, options);
    public static ItemMaster ParseFile(string path, KoLmafiaItemsTxtParserOptions? options = null)
        => KoLmafiaItemsTxtParser.ParseFile(path, options);
    public static ItemMasterParseResult ParseWithDiagnostics(string text, KoLmafiaItemsTxtParserOptions? options = null)
        => KoLmafiaItemsTxtParser.ParseWithDiagnostics(text, options);
}

/// <summary>Compatibility alias using the source file's singular name.</summary>
public static class KoLmafiaItemParser
{
    public static ItemMaster Parse(string text, KoLmafiaItemsTxtParserOptions? options = null)
        => KoLmafiaItemsTxtParser.Parse(text, options);
    public static ItemMasterRecord? ParseLine(string line, KoLmafiaItemsTxtParserOptions? options = null)
        => KoLmafiaItemsTxtParser.ParseLine(line, options);
    public static ItemMaster ParseFile(string path, KoLmafiaItemsTxtParserOptions? options = null)
        => KoLmafiaItemsTxtParser.ParseFile(path, options);
}
