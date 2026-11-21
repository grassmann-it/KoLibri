using KolArbitrage.Core.Markt;

namespace KolArbitrage.Core.Markt;

/// <summary>Outcome of checking one Mall listing against own stores and blocks.</summary>
public sealed record OwnStoreExclusionDecision(
    bool IsExcluded,
    bool IsOwnStore,
    bool IsBlacklisted,
    string? Reason,
    SellerBlacklistEntry? BlacklistEntry = null,
    SellerBlacklistMatchKind MatchKind = SellerBlacklistMatchKind.None)
{
    public bool IsAllowed => !IsExcluded;

    public static OwnStoreExclusionDecision Allowed { get; } =
        new(false, false, false, null);
}

/// <summary>
/// Applies local own-store and seller/store blacklist rules to Mall
/// observations. It only compares in-memory values and never fetches data.
/// </summary>
public sealed class OwnStoreExclusionPolicy
{
    private readonly SellerBlacklistDatabase? _blacklist;

    public IReadOnlyList<SellerStoreIdentity> OwnStores { get; }

    public SellerBlacklistDatabase? Blacklist => _blacklist;

    public OwnStoreExclusionPolicy()
        : this(Array.Empty<SellerStoreIdentity>())
    {
    }

    public OwnStoreExclusionPolicy(
        SellerStoreIdentity ownStore,
        SellerBlacklistDatabase? blacklist = null)
        : this(new[] { ownStore ?? throw new ArgumentNullException(nameof(ownStore)) }, blacklist)
    {
    }

    public OwnStoreExclusionPolicy(
        string ownStoreId,
        string? ownStoreName = null,
        SellerBlacklistDatabase? blacklist = null)
        : this(new SellerStoreIdentity(storeId: ownStoreId, storeName: ownStoreName), blacklist)
    {
    }

    public OwnStoreExclusionPolicy(
        string ownStoreId,
        SellerBlacklistDatabase blacklist)
        : this(new SellerStoreIdentity(storeId: ownStoreId), blacklist)
    {
    }

    public OwnStoreExclusionPolicy(
        IEnumerable<SellerStoreIdentity> ownStores,
        SellerBlacklistDatabase? blacklist = null)
    {
        ArgumentNullException.ThrowIfNull(ownStores);
        OwnStores = ownStores
            .Where(identity => identity is not null && identity.HasIdentifier)
            .Select(identity => identity.Normalize())
            .ToArray();
        _blacklist = blacklist;
    }

    public OwnStoreExclusionDecision Evaluate(
        SellerStoreIdentity candidate,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var normalized = candidate.Normalize();
        if (OwnStores.Any(own => IsSameIdentity(own, normalized)))
        {
            return new OwnStoreExclusionDecision(
                IsExcluded: true,
                IsOwnStore: true,
                IsBlacklisted: false,
                Reason: "Listing belongs to an own store.");
        }

        if (_blacklist is not null)
        {
            var match = SellerBlacklistMatcher.Find(_blacklist, normalized, now);
            if (match.IsBlocked)
            {
                return new OwnStoreExclusionDecision(
                    IsExcluded: true,
                    IsOwnStore: false,
                    IsBlacklisted: true,
                    Reason: match.Reason,
                    BlacklistEntry: match.Entry,
                    MatchKind: match.MatchKind);
            }
        }

        return OwnStoreExclusionDecision.Allowed;
    }

    public OwnStoreExclusionDecision Evaluate(
        MallListing listing,
        DateTimeOffset? now = null) =>
        Evaluate(SellerStoreIdentity.FromListing(listing), now);

    public bool IsExcluded(SellerStoreIdentity candidate, DateTimeOffset? now = null) =>
        Evaluate(candidate, now).IsExcluded;

    public bool IsExcluded(MallListing listing, DateTimeOffset? now = null) =>
        Evaluate(listing, now).IsExcluded;

    public bool ShouldExclude(SellerStoreIdentity candidate, DateTimeOffset? now = null) =>
        IsExcluded(candidate, now);

    public bool ShouldExclude(MallListing listing, DateTimeOffset? now = null) =>
        IsExcluded(listing, now);

    public bool IsOwnStore(SellerStoreIdentity candidate) =>
        OwnStores.Any(own => IsSameIdentity(own, candidate.Normalize()));

    public bool IsOwnStore(MallListing listing) =>
        IsOwnStore(SellerStoreIdentity.FromListing(listing));

    /// <summary>Returns only independent listings, preserving source order.</summary>
    public IReadOnlyList<MallListing> Exclude(
        IEnumerable<MallListing> listings,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(listings);
        return listings.Where(listing => !IsExcluded(listing, now)).ToArray();
    }

    public IReadOnlyList<MallListing> Filter(
        IEnumerable<MallListing> listings,
        DateTimeOffset? now = null) => Exclude(listings, now);

    private static bool IsSameIdentity(SellerStoreIdentity left, SellerStoreIdentity right)
    {
        // A configured own identity may have one or more aliases. Compare only
        // corresponding fields; a missing observed field cannot prove a match.
        return SellerBlacklistEquals(left.SellerId, right.SellerId)
            || SellerBlacklistEquals(left.SellerName, right.SellerName)
            || SellerBlacklistEquals(left.SellerName, right.StoreName)
            || SellerBlacklistEquals(left.StoreId, right.StoreId)
            || SellerBlacklistEquals(left.StoreName, right.StoreName)
            || SellerBlacklistEquals(left.StoreName, right.SellerName);
    }

    private static bool SellerBlacklistEquals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Static convenience methods for one-off filtering.</summary>
public static class OwnStoreExclusion
{
    public static bool IsOwnStore(MallListing listing, string ownStoreId) =>
        new OwnStoreExclusionPolicy(ownStoreId).IsOwnStore(listing);

    public static bool IsOwnStore(
        MallListing listing,
        SellerStoreIdentity ownStore) =>
        new OwnStoreExclusionPolicy(ownStore).IsOwnStore(listing);

    public static IReadOnlyList<MallListing> Filter(
        IEnumerable<MallListing> listings,
        SellerStoreIdentity ownStore) =>
        new OwnStoreExclusionPolicy(ownStore).Filter(listings);

    public static IReadOnlyList<MallListing> Filter(
        IEnumerable<MallListing> listings,
        string ownStoreId) =>
        new OwnStoreExclusionPolicy(ownStoreId).Filter(listings);

    public static IReadOnlyList<MallListing> ExcludeOwnStore(
        IEnumerable<MallListing> listings,
        SellerStoreIdentity ownStore) => Filter(listings, ownStore);
}

/// <summary>Short policy alias for consumers that do not need the longer type name.</summary>
public static class OwnStorePolicy
{
    public static bool IsOwnStore(MallListing listing, SellerStoreIdentity ownStore) =>
        OwnStoreExclusion.IsOwnStore(listing, ownStore);

    public static IReadOnlyList<MallListing> Filter(
        IEnumerable<MallListing> listings,
        SellerStoreIdentity ownStore) =>
        OwnStoreExclusion.Filter(listings, ownStore);
}
