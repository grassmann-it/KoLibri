using System.Collections.Concurrent;
using KolArbitrage.Core.Basis;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Markt;

/// <summary>Kind of source represented by a coordinated read.</summary>
public enum ReadSnapshotKind
{
    Unknown,
    Status,
    Inventory,
    Mall,
    Pricegun,
    OwnShop,
    ShopSales
}

/// <summary>Age classification applied by the read coordinator.</summary>
public enum ReadFreshness
{
    Unknown,
    Fresh,
    Stale,
    Expired,
    Missing,
    Error
}

/// <summary>How a query treats a snapshot that is past its normal TTL.</summary>
public enum StaleReadMode
{
    Allow,
    Reject
}

/// <summary>
/// Source-specific freshness limits used by the coordinator. The underlying
/// clients retain their own cache policy; this policy is a second gate that
/// makes a composed observation safe to consume as one read.
/// </summary>
public sealed record ReadQueryPolicy
{
    public SnapshotCachePolicy Status { get; init; } = new()
    {
        CacheTtl = TimeSpan.FromMinutes(2),
        MaxStale = TimeSpan.FromHours(12)
    };

    public SnapshotCachePolicy Inventory { get; init; } = new()
    {
        CacheTtl = TimeSpan.FromMinutes(2),
        MaxStale = TimeSpan.FromHours(12)
    };

    public SnapshotCachePolicy Mall { get; init; } = new()
    {
        CacheTtl = TimeSpan.FromMinutes(20),
        MaxStale = TimeSpan.FromHours(12)
    };

    public SnapshotCachePolicy Pricegun { get; init; } = new()
    {
        CacheTtl = TimeSpan.FromMinutes(20),
        MaxStale = TimeSpan.FromHours(12)
    };

    public SnapshotCachePolicy OwnShop { get; init; } = new()
    {
        CacheTtl = TimeSpan.FromMinutes(20),
        MaxStale = TimeSpan.FromHours(12)
    };

    public SnapshotCachePolicy ShopSales { get; init; } = new()
    {
        CacheTtl = TimeSpan.FromMinutes(15),
        MaxStale = TimeSpan.FromHours(24)
    };

    public SnapshotCachePolicy StatusPolicy => Status;
    public SnapshotCachePolicy InventoryPolicy => Inventory;
    public SnapshotCachePolicy MallPolicy => Mall;
    public SnapshotCachePolicy PricegunPolicy => Pricegun;
    public SnapshotCachePolicy OwnShopPolicy => OwnShop;
    public SnapshotCachePolicy ShopSalesPolicy => ShopSales;

    public StaleReadMode StaleMode { get; init; } = StaleReadMode.Allow;

    public bool AllowStale
    {
        get => StaleMode == StaleReadMode.Allow;
        init => StaleMode = value ? StaleReadMode.Allow : StaleReadMode.Reject;
    }

    public bool RequireFresh
    {
        get => StaleMode == StaleReadMode.Reject;
        init => StaleMode = value ? StaleReadMode.Reject : StaleReadMode.Allow;
    }

    /// <summary>Sets one TTL for every source when a uniform policy is desired.</summary>
    public TimeSpan CacheTtl
    {
        get => Status.CacheTtl;
        init
        {
            Status = Status with { CacheTtl = value };
            Inventory = Inventory with { CacheTtl = value };
            Mall = Mall with { CacheTtl = value };
            Pricegun = Pricegun with { CacheTtl = value };
            OwnShop = OwnShop with { CacheTtl = value };
            ShopSales = ShopSales with { CacheTtl = value };
        }
    }

    public TimeSpan Ttl
    {
        get => CacheTtl;
        init => CacheTtl = value;
    }

    /// <summary>Sets one stale horizon for every source when desired.</summary>
    public TimeSpan MaxStale
    {
        get => Status.MaxStale;
        init
        {
            Status = Status with { MaxStale = value };
            Inventory = Inventory with { MaxStale = value };
            Mall = Mall with { MaxStale = value };
            Pricegun = Pricegun with { MaxStale = value };
            OwnShop = OwnShop with { MaxStale = value };
            ShopSales = ShopSales with { MaxStale = value };
        }
    }

    public TimeSpan StaleTtl
    {
        get => MaxStale;
        init => MaxStale = value;
    }

    public void Validate()
    {
        Status.Validate();
        Inventory.Validate();
        Mall.Validate();
        Pricegun.Validate();
        OwnShop.Validate();
        ShopSales.Validate();
    }
}

/// <summary>Options for one coordinated read request.</summary>
public sealed record ReadQueryOptions
{
    public bool ForceRefresh { get; init; }
    public bool AllowStale { get; init; } = true;
    public bool RequireFresh { get; init; }
    public bool UseStale
    {
        get => AllowStale && !RequireFresh;
        init
        {
            AllowStale = value;
            RequireFresh = !value;
        }
    }
    public StaleReadMode StaleMode
    {
        get => RequireFresh || !AllowStale ? StaleReadMode.Reject : StaleReadMode.Allow;
        init
        {
            RequireFresh = value == StaleReadMode.Reject;
            AllowStale = value == StaleReadMode.Allow;
        }
    }
    public TimeSpan? MaximumAge { get; init; }
    public TimeSpan? MaxAge
    {
        get => MaximumAge;
        init => MaximumAge = value;
    }

    /// <summary>Caller-supplied id copied to every envelope in the response.</summary>
    public string? RequestId { get; init; }

    internal void Validate()
    {
        if (MaximumAge is { } age && age <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaximumAge));
        if (!string.IsNullOrWhiteSpace(RequestId) && RequestId.Any(char.IsControl))
            throw new ArgumentException("RequestId cannot contain control characters.", nameof(RequestId));
    }
}

/// <summary>Request for a bounded batch of arbitrage observations.</summary>
public record ArbitrageObservationRequest
{
    public IReadOnlyCollection<string> AccountIds { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> ItemIds { get; init; } = Array.Empty<string>();
    public ReadQueryOptions Options { get; init; } = new();
    public string? RequestId
    {
        get => Options.RequestId;
        init => Options = Options with { RequestId = value };
    }

    public void Validate()
    {
        if (AccountIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Account ids cannot be empty.", nameof(AccountIds));
        if (ItemIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Item ids cannot be empty.", nameof(ItemIds));
        Options.Validate();
    }

    public ArbitrageObservationRequest() { }

    public ArbitrageObservationRequest(
        IEnumerable<string>? accountIds,
        IEnumerable<string>? itemIds,
        ReadQueryOptions? options = null)
    {
        AccountIds = Normalize(accountIds);
        ItemIds = Normalize(itemIds);
        Options = options ?? new ReadQueryOptions();
    }

    private static IReadOnlyCollection<string> Normalize(IEnumerable<string>? values)
        => values?.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();
}

/// <summary>Compatibility name for hosts that call a read a query.</summary>
public record ReadQueryRequest : ArbitrageObservationRequest
{
    public ReadQueryRequest() { }

    public ReadQueryRequest(
        IEnumerable<string>? accountIds,
        IEnumerable<string>? itemIds,
        ReadQueryOptions? options = null)
        : base(accountIds, itemIds, options) { }
}

/// <summary>Read-only Pricegun contract used by the coordinator.</summary>
public interface IPricegunReadClient
{
    Task<PricegunSnapshot> FetchAsync(
        string itemId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional cache invalidation contract for read adapters.</summary>
public interface IReadCacheInvalidator
{
    void Invalidate(string? itemId = null);
}

/// <summary>Generates correlation ids shared by read and audit workflows.</summary>
public static class ReadRequestIds
{
    public static string New() => AuditIds.NewRequestId();
    public static string NewRequestId() => New();
}

/// <summary>
/// Adapter that exposes the existing Pricegun client behind the coordinator's
/// interface. The existing HTTP client is intentionally left untouched.
/// </summary>
public sealed class PricegunReadClientAdapter : IPricegunReadClient, IReadCacheInvalidator
{
    private readonly PricegunReadClient _client;

    public PricegunReadClientAdapter(PricegunReadClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<PricegunSnapshot> FetchAsync(
        string itemId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
        => _client.FetchAsync(itemId, forceRefresh, cancellationToken);

    public void Invalidate(string? itemId = null) => _client.Invalidate(itemId);
}

/// <summary>Convenience adapter factory for existing Core clients.</summary>
public static class ReadClientAdapterExtensions
{
    public static IPricegunReadClient AsReadClient(this PricegunReadClient client)
        => new PricegunReadClientAdapter(client);
}

/// <summary>Per-account collection of read-only source adapters.</summary>
public class ReadAccountSource
{
    public ReadAccountSource(
        string accountId,
        IAccountReadClient account,
        IMallReadClient? mall = null,
        IPricegunReadClient? pricegun = null,
        IShopSalesReadClient? shop = null)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("An account id is required.", nameof(accountId));
        AccountId = accountId.Trim();
        Account = account ?? throw new ArgumentNullException(nameof(account));
        Mall = mall;
        Pricegun = pricegun;
        Shop = shop;
    }

    public string AccountId { get; }
    public IAccountReadClient Account { get; }
    public IMallReadClient? Mall { get; }
    public IPricegunReadClient? Pricegun { get; }
    public IShopSalesReadClient? Shop { get; }
    public IAccountReadClient AccountClient => Account;
    public IMallReadClient? MallClient => Mall;
    public IPricegunReadClient? PricegunClient => Pricegun;
    public IShopSalesReadClient? ShopClient => Shop;
}

/// <summary>Compatibility name for account registrations.</summary>
public sealed class AccountReadSource : ReadAccountSource
{
    public AccountReadSource(
        string accountId,
        IAccountReadClient account,
        IMallReadClient? mall = null,
        IPricegunReadClient? pricegun = null,
        IShopSalesReadClient? shop = null)
        : base(accountId, account, mall, pricegun, shop) { }
}

/// <summary>Compatibility name for account registrations.</summary>
public sealed class AccountReadRegistration : ReadAccountSource
{
    public AccountReadRegistration(
        string accountId,
        IAccountReadClient account,
        IMallReadClient? mall = null,
        IPricegunReadClient? pricegun = null,
        IShopSalesReadClient? shop = null)
        : base(accountId, account, mall, pricegun, shop) { }
}

/// <summary>
/// One source value with a request id, age and an explicit freshness gate.
/// Values outside the stale horizon remain available for diagnostics but are
/// marked unusable so a caller cannot accidentally trade on them.
/// </summary>
public sealed record ReadSnapshot<T>
{
    public T? Value { get; init; }
    public T? Data => Value;
    public T? Payload => Value;
    public T? Snapshot => Value;
    public string RequestId { get; init; } = string.Empty;
    public string? AccountId { get; init; }
    public string? ItemId { get; init; }
    public ReadSnapshotKind Kind { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }
    public TimeSpan Age { get; init; }
    public ReadFreshness Freshness { get; init; }
    public bool IsAllowed { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public Exception? Exception { get; init; }

    public ReadFreshness State => Freshness;
    public bool IsFresh => Freshness == ReadFreshness.Fresh;
    public bool IsStale => Freshness == ReadFreshness.Stale;
    public bool Stale => IsStale;
    public bool IsExpired => Freshness == ReadFreshness.Expired;
    public bool IsMissing => Freshness == ReadFreshness.Missing;
    public bool IsError => Freshness == ReadFreshness.Error;
    public bool HasValue => Value is not null;
    public bool IsUsable => HasValue && IsAllowed;

    internal static ReadSnapshot<T> Missing(
        ReadSnapshotKind kind,
        string requestId,
        DateTimeOffset observedAt,
        string? accountId,
        string? itemId,
        Exception? error = null)
        => new()
        {
            Kind = kind,
            RequestId = requestId,
            ObservedAt = observedAt,
            AccountId = accountId,
            ItemId = itemId,
            Freshness = error is null ? ReadFreshness.Missing : ReadFreshness.Error,
            IsAllowed = false,
            ErrorCode = error is null ? "missing" : "read_failed",
            ErrorMessage = error?.Message,
            Exception = error
        };
}

/// <summary>Diagnostic retained on a composed read instead of hiding source failures.</summary>
public sealed record ReadQueryDiagnostic(
    string Code,
    string Message,
    ReadSnapshotKind Kind = ReadSnapshotKind.Unknown,
    string? AccountId = null,
    string? ItemId = null,
    bool IsError = false);

/// <summary>All account-level snapshots read for one request.</summary>
public sealed record AccountReadObservation
{
    public string AccountId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public DateTimeOffset ObservedAt { get; init; }
    public ReadSnapshot<KoLStatusSnapshot> Status { get; init; } = new();
    public ReadSnapshot<KoLInventorySnapshot> Inventory { get; init; } = new();
    public ReadSnapshot<IReadOnlyList<OwnShopListing>> OwnShop { get; init; } = new();
    public ReadSnapshot<ShopSalesSnapshot> ShopSales { get; init; } = new();
    public IReadOnlyList<ReadQueryDiagnostic> Diagnostics { get; init; } = Array.Empty<ReadQueryDiagnostic>();
    public KoLStatusSnapshot? StatusSnapshot => Status.Value;
    public KoLInventorySnapshot? InventorySnapshot => Inventory.Value;
    public IReadOnlyList<OwnShopListing>? OwnShopListings => OwnShop.Value;
    public ShopSalesSnapshot? ShopSalesSnapshot => ShopSales.Value;

    /// <summary>Status and inventory are required for account observations.</summary>
    public bool IsUsable => Status.IsUsable && Inventory.IsUsable;
    public bool IsStale => Status.IsStale || Inventory.IsStale || OwnShop.IsStale || ShopSales.IsStale;
    public bool IsFresh => Status.IsFresh && Inventory.IsFresh;
}

/// <summary>Market observations for one item and account.</summary>
public sealed record ArbitrageItemObservation
{
    public string AccountId { get; init; } = string.Empty;
    public string ItemId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public DateTimeOffset ObservedAt { get; init; }
    public ReadSnapshot<MallSnapshot> Mall { get; init; } = new();
    public ReadSnapshot<PricegunSnapshot> Pricegun { get; init; } = new();
    public ReadSnapshot<KoLInventoryItem> InventoryItem { get; init; } = new();
    public IReadOnlyList<OwnShopListing> OwnShopListings { get; init; } = Array.Empty<OwnShopListing>();
    public IReadOnlyList<OwnShopListing> OwnShop => OwnShopListings;
    public MallSnapshot? MallSnapshot => Mall.Value;
    public PricegunSnapshot? PricegunSnapshot => Pricegun.Value;

    public bool IsUsable => Mall.IsUsable || Pricegun.IsUsable;
    public bool IsStale => Mall.IsStale || Pricegun.IsStale;
    public bool IsFresh => !IsStale
        && (Mall.IsFresh || Mall.IsMissing)
        && (Pricegun.IsFresh || Pricegun.IsMissing)
        && (Mall.IsFresh || Pricegun.IsFresh);
}

/// <summary>Batch read returned to arbitrage and comparison workflows.</summary>
public record ArbitrageBatchObservation
{
    public string RequestId { get; init; } = string.Empty;
    public DateTimeOffset ObservedAt { get; init; }
    public IReadOnlyList<AccountReadObservation> Accounts { get; init; } = Array.Empty<AccountReadObservation>();
    public IReadOnlyList<ArbitrageItemObservation> Items { get; init; } = Array.Empty<ArbitrageItemObservation>();
    public IReadOnlyList<ReadQueryDiagnostic> Diagnostics { get; init; } = Array.Empty<ReadQueryDiagnostic>();

    public IReadOnlyList<AccountReadObservation> AccountSnapshots => Accounts;
    public IReadOnlyList<ArbitrageItemObservation> ItemSnapshots => Items;
    public IReadOnlyDictionary<string, IReadOnlyList<ArbitrageItemObservation>> ItemsById =>
        Items.GroupBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<ArbitrageItemObservation>)x.ToArray(), StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, AccountReadObservation> AccountsById =>
        Accounts.ToDictionary(x => x.AccountId, StringComparer.OrdinalIgnoreCase);
    public bool IsStale => Accounts.Any(x => x.IsStale) || Items.Any(x => x.IsStale);
    public bool IsFresh => !IsStale && Accounts.All(x => x.IsFresh) && Items.All(x => x.IsFresh);
    public bool IsComplete => Diagnostics.All(x => !x.IsError) && Accounts.All(x => x.IsUsable);
    public bool IsUsable => IsComplete && Items.All(x => x.IsUsable);
}

/// <summary>Compatibility name for callers that use “read observation” terminology.</summary>
public record ReadObservationBatch : ArbitrageBatchObservation;

/// <summary>Shared composition boundary for account and market reads.</summary>
public interface IReadQueryCoordinator
{
    Task<AccountReadObservation> ObserveAccountAsync(
        string accountId,
        IEnumerable<string>? itemIds = null,
        ReadQueryOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<AccountReadObservation> ReadAccountAsync(
        string accountId,
        IEnumerable<string>? itemIds = null,
        ReadQueryOptions? options = null,
        CancellationToken cancellationToken = default)
        => ObserveAccountAsync(accountId, itemIds, options, cancellationToken);

    Task<AccountReadObservation> ReadAsync(
        string accountId,
        IEnumerable<string>? itemIds = null,
        ReadQueryOptions? options = null,
        CancellationToken cancellationToken = default)
        => ObserveAccountAsync(accountId, itemIds, options, cancellationToken);

    Task<ArbitrageBatchObservation> ObserveBatchAsync(
        ArbitrageObservationRequest request,
        CancellationToken cancellationToken = default);

    Task<ArbitrageBatchObservation> ReadBatchAsync(
        ArbitrageObservationRequest request,
        CancellationToken cancellationToken = default)
        => ObserveBatchAsync(request, cancellationToken);

    Task<ArbitrageBatchObservation> QueryAsync(
        ArbitrageObservationRequest request,
        CancellationToken cancellationToken = default)
        => ObserveBatchAsync(request, cancellationToken);

    Task<ArbitrageBatchObservation> ObserveArbitrageAsync(
        ArbitrageObservationRequest request,
        CancellationToken cancellationToken = default)
        => ObserveBatchAsync(request, cancellationToken);

    Task<ArbitrageBatchObservation> ReadAsync(
        ArbitrageObservationRequest request,
        CancellationToken cancellationToken = default)
        => ObserveBatchAsync(request, cancellationToken);

    void Invalidate(string? accountId = null, string? itemId = null);

    Task InvalidateAsync(string? accountId = null, string? itemId = null)
    {
        Invalidate(accountId, itemId);
        return Task.CompletedTask;
    }
}

/// <summary>Alias retained for hosts that name the service a snapshot coordinator.</summary>
public interface IReadSnapshotCoordinator : IReadQueryCoordinator { }

/// <summary>Compatibility alias for hosts that use the shorter coordinator name.</summary>
public interface IReadCoordinator : IReadQueryCoordinator { }
