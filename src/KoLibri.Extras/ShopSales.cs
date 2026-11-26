// Moved from KoLibri.Core (0.5 step 3, docs/lib-api-0.5.md §6.2). The Core copy stays until MCP and Desktop move over.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace KoLibri.Extras;


/// <summary>
/// A single sale from the authenticated character's own-store activity log.
/// BuyerKey is an HMAC-derived local identifier; raw buyer names are never
/// returned by the read client. ItemName is the display name from KoL because
/// the sales activity page does not include a numeric item id.
/// </summary>
public sealed record SaleRecord(
    string BuyerKey,
    string ItemName,
    int Quantity,
    long TotalPrice,
    long UnitPrice,
    DateTimeOffset? SoldAt,
    DateTimeOffset ObservedAt,
    // Kept only in memory for the local CRM sync. JSON reports deliberately
    // omit it so a normal analytics export still contains no raw buyer name.
    [property: JsonIgnore] string? RawBuyerName = null);

/// <summary>
/// Generates stable-in-process, non-reversible buyer identifiers. Pass a
/// caller-owned secret to retain the same identifiers between processes; when
/// omitted, a random process key is used so names cannot be correlated across
/// separate runs by accident.
/// </summary>
public sealed class BuyerIdentityHasher
{
    private static readonly Lazy<byte[]> ProcessKey = new(CreateProcessKey, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly byte[] _key;

    public BuyerIdentityHasher(byte[]? key = null)
    {
        _key = key is { Length: > 0 } ? key.ToArray() : ProcessKey.Value.ToArray();
    }

    public string Hash(string buyerName)
    {
        var normalized = Normalize(buyerName);
        if (normalized.Length == 0) throw new ArgumentException("Buyer name is required.", nameof(buyerName));

        using var hmac = new HMACSHA256(_key);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        // 96 bits is enough for a local report while avoiding raw names.
        return $"buyer_{Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    public static string Normalize(string buyerName)
        => (buyerName ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    private static byte[] CreateProcessKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }
}



public sealed record ShopBuyerAnalyticsOptions
{
    public long HighValueMeatThreshold { get; init; } = 100_000;
    public int BulkQuantityThreshold { get; init; } = 10;
    public int RepeatPurchaseThreshold { get; init; } = 2;
    public TimeSpan BurstWindow { get; init; } = TimeSpan.FromMinutes(5);
    public int BurstSaleThreshold { get; init; } = 3;
    /// <summary>Already-hashed keys for accounts the owner explicitly configured.</summary>
    public IReadOnlySet<string> KnownAccountBuyerKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public void Validate()
    {
        if (HighValueMeatThreshold < 0) throw new ArgumentOutOfRangeException(nameof(HighValueMeatThreshold));
        if (BulkQuantityThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(BulkQuantityThreshold));
        if (RepeatPurchaseThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(RepeatPurchaseThreshold));
        if (BurstWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(BurstWindow));
        if (BurstSaleThreshold <= 1) throw new ArgumentOutOfRangeException(nameof(BurstSaleThreshold));
    }
}

public sealed record ShopBuyerProfile(
    string BuyerKey,
    int SaleCount,
    int DistinctItemCount,
    int TotalQuantity,
    long TotalMeat,
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen,
    decimal AverageUnitPrice,
    IReadOnlyList<string> ItemNames,
    IReadOnlyList<string> Flags);

public sealed record ShopBuyerLinkCandidate(
    string BuyerKeyA,
    string BuyerKeyB,
    decimal Score,
    string Confidence,
    IReadOnlyList<string> Signals);

public sealed record ShopBuyerAnalyticsReport(
    DateTimeOffset GeneratedAt,
    int SaleCount,
    IReadOnlyList<ShopBuyerProfile> Buyers,
    IReadOnlyList<ShopBuyerLinkCandidate> PossibleSharedOperators);

/// <summary>
/// Local CRM-style summaries for the store owner. Signals describe purchase
/// patterns only; they do not establish that characters share an account.
/// </summary>
public static class ShopBuyerAnalytics
{
    public static ShopBuyerAnalyticsReport Analyze(
        IEnumerable<SaleRecord> sales,
        ShopBuyerAnalyticsOptions? options = null,
        DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(sales);
        options ??= new ShopBuyerAnalyticsOptions();
        options.Validate();
        var rows = sales.Where(x => !string.IsNullOrWhiteSpace(x.BuyerKey) && x.Quantity > 0).ToArray();
        var groups = rows.GroupBy(x => x.BuyerKey, StringComparer.Ordinal).ToArray();
        var profiles = groups
            .Select(group => BuildProfile(group.Key, group.ToArray(), options))
            .OrderByDescending(x => x.TotalMeat)
            .ThenBy(x => x.BuyerKey, StringComparer.Ordinal)
            .ToArray();

        return new ShopBuyerAnalyticsReport(
            generatedAt ?? DateTimeOffset.UtcNow,
            rows.Length,
            profiles,
            FindSharedOperatorCandidates(groups, options));
    }

    private static ShopBuyerProfile BuildProfile(string key, IReadOnlyList<SaleRecord> sales, ShopBuyerAnalyticsOptions options)
    {
        var totalQuantity = sales.Sum(x => x.Quantity);
        var totalMeat = sales.Sum(x => x.TotalPrice);
        var timestamps = sales.Where(x => x.SoldAt.HasValue).Select(x => x.SoldAt!.Value).OrderBy(x => x).ToArray();
        var flags = new List<string>();
        if (sales.Count >= options.RepeatPurchaseThreshold) flags.Add("repeat-buyer");
        if (totalMeat >= options.HighValueMeatThreshold) flags.Add("high-value");
        if (totalQuantity >= options.BulkQuantityThreshold) flags.Add("bulk");
        if (options.KnownAccountBuyerKeys.Contains(key)) flags.Add("configured-account-match");
        if (HasBurst(timestamps, options.BurstWindow, options.BurstSaleThreshold)) flags.Add("burst-pattern-low-confidence");

        return new ShopBuyerProfile(
            key,
            sales.Count,
            sales.Select(x => x.ItemName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            totalQuantity,
            totalMeat,
            timestamps.Length == 0 ? null : timestamps[0],
            timestamps.Length == 0 ? null : timestamps[^1],
            totalQuantity == 0 ? 0m : (decimal)totalMeat / totalQuantity,
            sales.Select(x => x.ItemName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(30).ToArray(),
            flags);
    }

    private static IReadOnlyList<ShopBuyerLinkCandidate> FindSharedOperatorCandidates(
        IReadOnlyList<IGrouping<string, SaleRecord>> groups,
        ShopBuyerAnalyticsOptions options)
    {
        var candidates = new List<ShopBuyerLinkCandidate>();
        for (var i = 0; i < groups.Count; i++)
        for (var j = i + 1; j < groups.Count; j++)
        {
            var left = groups[i].ToArray();
            var right = groups[j].ToArray();
            if (left.Length < options.RepeatPurchaseThreshold || right.Length < options.RepeatPurchaseThreshold) continue;
            var leftItems = left.Select(x => x.ItemName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rightItems = right.Select(x => x.ItemName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var overlap = leftItems.Intersect(rightItems, StringComparer.OrdinalIgnoreCase).Count();
            var union = leftItems.Union(rightItems, StringComparer.OrdinalIgnoreCase).Count();
            if (union == 0) continue;
            var itemSimilarity = (decimal)overlap / union;
            var closeTimes = 0;
            foreach (var first in left.Where(x => x.SoldAt.HasValue))
                if (right.Where(x => x.SoldAt.HasValue).Any(second => Math.Abs((first.SoldAt!.Value - second.SoldAt!.Value).TotalMinutes) <= 2))
                    closeTimes++;
            var timingSimilarity = (decimal)closeTimes / Math.Max(left.Length, right.Length);
            var score = itemSimilarity * 0.55m + timingSimilarity * 0.45m;
            if (score < 0.8m) continue;
            candidates.Add(new ShopBuyerLinkCandidate(
                groups[i].Key,
                groups[j].Key,
                decimal.Round(score, 3),
                "weak",
                ["overlapping-items", "nearby-purchase-times", "pattern-only; manual review required"]));
        }
        return candidates.OrderByDescending(x => x.Score).ToArray();
    }

    private static bool HasBurst(IReadOnlyList<DateTimeOffset> timestamps, TimeSpan window, int threshold)
    {
        for (var i = 0; i < timestamps.Count; i++)
        {
            var end = timestamps[i] + window;
            var count = 0;
            for (var j = i; j < timestamps.Count && timestamps[j] <= end; j++) count++;
            if (count >= threshold) return true;
        }
        return false;
    }
}
