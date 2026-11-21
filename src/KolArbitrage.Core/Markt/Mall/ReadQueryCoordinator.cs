using System.Collections.Concurrent;
using KolArbitrage.Core.Basis;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Markt;
/// <summary>
/// Composes per-account status/inventory with Mall, Pricegun and shop reads.
/// It owns a short-lived composition cache and request coalescing while leaving
/// each underlying HTTP client and its policy unchanged.
/// </summary>
public class ReadQueryCoordinator : IReadQueryCoordinator, IReadSnapshotCoordinator, IReadCoordinator, IDisposable
{
    private readonly Dictionary<string, ReadAccountSource> _accounts;
    private readonly ReadQueryPolicy _policy;
    private readonly TimeProvider _clock;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, KoLStatusSnapshot> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, KoLInventorySnapshot> _inventory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Account, string Item), MallSnapshot> _mall = new();
    private readonly Dictionary<(string Account, string Item), PricegunSnapshot> _pricegun = new();
    private readonly Dictionary<string, IReadOnlyList<OwnShopListing>> _ownShop = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _ownShopFetchedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ShopSalesSnapshot> _shopSales = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<KoLStatusSnapshot>>> _statusInflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<KoLInventorySnapshot>>> _inventoryInflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Account, string Item), Lazy<Task<MallSnapshot>>> _mallInflight = new();
    private readonly ConcurrentDictionary<(string Account, string Item), Lazy<Task<PricegunSnapshot>>> _pricegunInflight = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<OwnShopListing>>>> _ownShopInflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<ShopSalesSnapshot>>> _shopSalesInflight = new(StringComparer.OrdinalIgnoreCase);
    private long _cacheVersion;
    private bool _disposed;

    public ReadQueryCoordinator(
        IEnumerable<ReadAccountSource> accounts,
        ReadQueryPolicy? policy = null,
        TimeProvider? clock = null,
        IMallReadClient? mall = null,
        IPricegunReadClient? pricegun = null,
        IShopSalesReadClient? shop = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        _accounts = accounts.ToDictionary(x => x.AccountId, StringComparer.OrdinalIgnoreCase);
        if (_accounts.Count == 0) throw new ArgumentException("At least one account source is required.", nameof(accounts));
        _policy = policy ?? new ReadQueryPolicy();
        _policy.Validate();
        _clock = clock ?? TimeProvider.System;
        GlobalMall = mall;
        GlobalPricegun = pricegun;
        GlobalShop = shop;
    }

    public ReadQueryCoordinator(
        ReadAccountSource account,
        ReadQueryPolicy? policy = null,
        TimeProvider? clock = null,
        IMallReadClient? mall = null,
        IPricegunReadClient? pricegun = null,
        IShopSalesReadClient? shop = null)
        : this(new[] { account }, policy, clock, mall, pricegun, shop) { }

    public ReadQueryCoordinator(
        IReadOnlyDictionary<string, IAccountReadClient> accounts,
        ReadQueryPolicy? policy = null,
        TimeProvider? clock = null,
        IMallReadClient? mall = null,
        IPricegunReadClient? pricegun = null,
        IShopSalesReadClient? shop = null)
        : this(accounts?.Select(x => new ReadAccountSource(x.Key, x.Value))
            ?? throw new ArgumentNullException(nameof(accounts)), policy, clock, mall, pricegun, shop) { }

    public ReadQueryCoordinator(
        string accountId,
        IAccountReadClient account,
        IMallReadClient? mall = null,
        IPricegunReadClient? pricegun = null,
        IShopSalesReadClient? shop = null,
        ReadQueryPolicy? policy = null,
        TimeProvider? clock = null)
        : this(new ReadAccountSource(accountId, account, mall, pricegun, shop), policy, clock) { }

    public ReadQueryCoordinator(
        IAccountReadClient account,
        IMallReadClient? mall = null,
        IPricegunReadClient? pricegun = null,
        IShopSalesReadClient? shop = null,
        ReadQueryPolicy? policy = null,
        TimeProvider? clock = null)
        : this("default", account, mall, pricegun, shop, policy, clock) { }

    public IMallReadClient? GlobalMall { get; }
    public IPricegunReadClient? GlobalPricegun { get; }
    public IShopSalesReadClient? GlobalShop { get; }
    public ReadQueryPolicy Policy => _policy;

    public Task<AccountReadObservation> ObserveAccountAsync(
        string accountId,
        IEnumerable<string>? itemIds = null,
        ReadQueryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var requestId = NewRequestId(options?.RequestId);
        return ObserveAccountCoreAsync(accountId, itemIds, options ?? new ReadQueryOptions(), requestId, cancellationToken);
    }

    /// <summary>Short alias for callers that use Read rather than Observe.</summary>
    public Task<AccountReadObservation> ReadAccountAsync(
        string accountId,
        IEnumerable<string>? itemIds = null,
        ReadQueryOptions? options = null,
        CancellationToken cancellationToken = default)
        => ObserveAccountAsync(accountId, itemIds, options, cancellationToken);

    public Task<AccountReadObservation> ReadAsync(
        string accountId,
        IEnumerable<string>? itemIds = null,
        ReadQueryOptions? options = null,
        CancellationToken cancellationToken = default)
        => ObserveAccountAsync(accountId, itemIds, options, cancellationToken);

    public async Task<ArbitrageBatchObservation> ObserveBatchAsync(
        ArbitrageObservationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var options = request.Options;
        var requestId = NewRequestId(request.RequestId);
        var accountIds = request.AccountIds.Count == 0
            ? _accounts.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
            : request.AccountIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var itemIds = request.ItemIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var accountTasks = accountIds.Select(id =>
            ObserveAccountCoreAsync(id, itemIds, options, requestId, cancellationToken)).ToArray();
        AccountReadObservation[] accounts = await Task.WhenAll(accountTasks).ConfigureAwait(false);

        var itemTasks = (
            from account in accounts
            from itemId in itemIds
            select ObserveItemAsync(account, itemId, options, requestId, cancellationToken)).ToArray();
        var items = itemTasks.Length == 0
            ? Array.Empty<ArbitrageItemObservation>()
            : await Task.WhenAll(itemTasks).ConfigureAwait(false);

        var diagnostics = accounts.SelectMany(x => x.Diagnostics)
            .Concat(items.SelectMany(x => DiagnosticsFor(x.Mall)))
            .Concat(items.SelectMany(x => DiagnosticsFor(x.Pricegun)))
            .ToArray();

        return new ArbitrageBatchObservation
        {
            RequestId = requestId,
            ObservedAt = _clock.GetUtcNow(),
            Accounts = accounts,
            Items = items,
            Diagnostics = diagnostics
        };
    }

    public Task<ArbitrageBatchObservation> ReadBatchAsync(
        ArbitrageObservationRequest request,
        CancellationToken cancellationToken = default)
        => ObserveBatchAsync(request, cancellationToken);

    public Task<ArbitrageBatchObservation> QueryAsync(
        ArbitrageObservationRequest request,
        CancellationToken cancellationToken = default)
        => ObserveBatchAsync(request, cancellationToken);

    public Task<ArbitrageBatchObservation> ObserveArbitrageAsync(
        ArbitrageObservationRequest request,
        CancellationToken cancellationToken = default)
        => ObserveBatchAsync(request, cancellationToken);

    public Task<ArbitrageBatchObservation> ReadAsync(
        ArbitrageObservationRequest request,
        CancellationToken cancellationToken = default)
        => ObserveBatchAsync(request, cancellationToken);

    public Task<ArbitrageBatchObservation> ObserveBatchAsync(
        IEnumerable<string>? accountIds,
        IEnumerable<string>? itemIds,
        ReadQueryOptions? options = null,
        CancellationToken cancellationToken = default)
        => ObserveBatchAsync(new ArbitrageObservationRequest(accountIds, itemIds, options), cancellationToken);

    public void Invalidate(string? accountId = null, string? itemId = null)
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _cacheVersion);
        var accountFilter = string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim();
        var itemFilter = string.IsNullOrWhiteSpace(itemId) ? null : itemId.Trim();
        lock (_cacheGate)
        {
            if (accountFilter is null && itemFilter is null)
            {
                _status.Clear();
                _inventory.Clear();
                _mall.Clear();
                _pricegun.Clear();
                _ownShop.Clear();
                _ownShopFetchedAt.Clear();
                _shopSales.Clear();
            }
            else
            {
                if (accountFilter is not null)
                {
                    _status.Remove(accountFilter);
                    _inventory.Remove(accountFilter);
                    _ownShop.Remove(accountFilter);
                    _ownShopFetchedAt.Remove(accountFilter);
                    _shopSales.Remove(accountFilter);
                }

                foreach (var key in _mall.Keys.Where(x =>
                    (accountFilter is null || string.Equals(accountFilter, x.Account, StringComparison.OrdinalIgnoreCase) ||
                        (itemFilter is not null && x.Account.Length == 0)) &&
                    (itemFilter is null || string.Equals(itemFilter, x.Item, StringComparison.OrdinalIgnoreCase))).ToArray())
                    _mall.Remove(key);
                foreach (var key in _pricegun.Keys.Where(x =>
                    (accountFilter is null || string.Equals(accountFilter, x.Account, StringComparison.OrdinalIgnoreCase) ||
                        (itemFilter is not null && x.Account.Length == 0)) &&
                    (itemFilter is null || string.Equals(itemFilter, x.Item, StringComparison.OrdinalIgnoreCase))).ToArray())
                    _pricegun.Remove(key);
            }
        }

        RemoveInflight(accountFilter, itemFilter);

        // Source clients are optional and may be shared between account
        // registrations. Let adapters decide whether they have a cache to
        // clear; the coordinator itself is always invalidated above.
        foreach (var source in _accounts.Values)
            InvalidateAdapter(source, accountFilter, itemFilter);
        InvalidateAdapter(new ReadAccountSource("global", new NullAccountReadClient(), GlobalMall, GlobalPricegun, GlobalShop), accountFilter, itemFilter);
    }

    public Task InvalidateAsync(string? accountId = null, string? itemId = null)
    {
        Invalidate(accountId, itemId);
        return Task.CompletedTask;
    }

    private async Task<AccountReadObservation> ObserveAccountCoreAsync(
        string accountId,
        IEnumerable<string>? itemIds,
        ReadQueryOptions options,
        string requestId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(accountId)) throw new ArgumentException("An account id is required.", nameof(accountId));
        options.Validate();
        if (!_accounts.TryGetValue(accountId.Trim(), out var source))
            throw new KeyNotFoundException($"Unknown account '{accountId}'.");

        var now = _clock.GetUtcNow();
        var diagnostics = new List<ReadQueryDiagnostic>();
        var status = await ReadStatusAsync(source, options, cancellationToken).ConfigureAwait(false);
        var inventory = await ReadInventoryAsync(source, options, cancellationToken).ConfigureAwait(false);
        var ownShop = await ReadOwnShopAsync(source, options, cancellationToken).ConfigureAwait(false);
        var shopSales = await ReadShopSalesAsync(source, options, cancellationToken).ConfigureAwait(false);
        AddDiagnostic(status, diagnostics);
        AddDiagnostic(inventory, diagnostics);
        AddDiagnostic(ownShop, diagnostics, optional: true);
        AddDiagnostic(shopSales, diagnostics, optional: true);

        return new AccountReadObservation
        {
            AccountId = source.AccountId,
            RequestId = requestId,
            ObservedAt = now,
            Status = AttachRequest(status, requestId),
            Inventory = AttachRequest(inventory, requestId),
            OwnShop = AttachRequest(ownShop, requestId),
            ShopSales = AttachRequest(shopSales, requestId),
            Diagnostics = diagnostics
        };
    }

    private async Task<ArbitrageItemObservation> ObserveItemAsync(
        AccountReadObservation account,
        string itemId,
        ReadQueryOptions options,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (!_accounts.TryGetValue(account.AccountId, out var source))
            throw new KeyNotFoundException($"Unknown account '{account.AccountId}'.");
        if (string.IsNullOrWhiteSpace(itemId))
            throw new ArgumentException("An item id is required.", nameof(itemId));
        itemId = itemId.Trim();

        var mall = await ReadMallAsync(source, itemId, options, cancellationToken).ConfigureAwait(false);
        var pricegun = await ReadPricegunAsync(source, itemId, options, cancellationToken).ConfigureAwait(false);
        var inventoryItem = FindInventoryItem(account.Inventory.Value, itemId);
        var observedAt = _clock.GetUtcNow();
        return new ArbitrageItemObservation
        {
            AccountId = account.AccountId,
            ItemId = itemId,
            RequestId = requestId,
            ObservedAt = observedAt,
            Mall = AttachRequest(mall, requestId, account.AccountId, itemId),
            Pricegun = AttachRequest(pricegun, requestId, account.AccountId, itemId),
            OwnShopListings = account.OwnShop.Value?.Where(x =>
                string.Equals(x.ItemId, itemId, StringComparison.OrdinalIgnoreCase)).ToArray()
                ?? Array.Empty<OwnShopListing>(),
            InventoryItem = inventoryItem is null
                ? ReadSnapshot<KoLInventoryItem>.Missing(ReadSnapshotKind.Inventory, requestId, observedAt, account.AccountId, itemId)
                : new ReadSnapshot<KoLInventoryItem>
                {
                    Value = inventoryItem,
                    RequestId = requestId,
                    AccountId = account.AccountId,
                    ItemId = itemId,
                    Kind = ReadSnapshotKind.Inventory,
                    ObservedAt = observedAt,
                    FetchedAt = account.Inventory.FetchedAt,
                    Age = account.Inventory.Age,
                    Freshness = account.Inventory.Freshness,
                    IsAllowed = account.Inventory.IsAllowed
                }
        };
    }

    private static KoLInventoryItem? FindInventoryItem(KoLInventorySnapshot? inventory, string itemId)
    {
        if (inventory is null) return null;
        return inventory.Items.FirstOrDefault(x =>
            string.Equals(x.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ReadSnapshot<KoLStatusSnapshot>> ReadStatusAsync(
        ReadAccountSource source,
        ReadQueryOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.ForceRefresh && TryGetFresh(_status, source.AccountId, _policy.Status, options, out var cached))
            return Classify(cached, ReadSnapshotKind.Status, source.AccountId, null, _policy.Status, options, sourceStale: cached.IsStale);

        var version = Volatile.Read(ref _cacheVersion);
        var lazy = _statusInflight.GetOrAdd(source.AccountId, _ => new Lazy<Task<KoLStatusSnapshot>>(
            () => source.Account.FetchStatusAsync(forceRefresh: true, cancellationToken: CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var value = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (version == Volatile.Read(ref _cacheVersion))
                lock (_cacheGate) _status[source.AccountId] = value;
            return Classify(value, ReadSnapshotKind.Status, source.AccountId, null, _policy.Status, options, sourceStale: value.IsStale);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (TryGetWithinStale(_status, source.AccountId, _policy.Status, out var stale))
                return Classify(stale, ReadSnapshotKind.Status, source.AccountId, null, _policy.Status, options, sourceStale: true);
            return ReadSnapshot<KoLStatusSnapshot>.Missing(ReadSnapshotKind.Status, string.Empty, _clock.GetUtcNow(), source.AccountId, null, exception);
        }
        finally { RemoveCompleted(_statusInflight, source.AccountId, lazy); }
    }

    private async Task<ReadSnapshot<KoLInventorySnapshot>> ReadInventoryAsync(
        ReadAccountSource source,
        ReadQueryOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.ForceRefresh && TryGetFresh(_inventory, source.AccountId, _policy.Inventory, options, out var cached))
            return Classify(cached, ReadSnapshotKind.Inventory, source.AccountId, null, _policy.Inventory, options, sourceStale: cached.IsStale);

        var version = Volatile.Read(ref _cacheVersion);
        var lazy = _inventoryInflight.GetOrAdd(source.AccountId, _ => new Lazy<Task<KoLInventorySnapshot>>(
            () => source.Account.FetchInventoryAsync(forceRefresh: true, cancellationToken: CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var value = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (version == Volatile.Read(ref _cacheVersion))
                lock (_cacheGate) _inventory[source.AccountId] = value;
            return Classify(value, ReadSnapshotKind.Inventory, source.AccountId, null, _policy.Inventory, options, sourceStale: value.IsStale);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (TryGetWithinStale(_inventory, source.AccountId, _policy.Inventory, out var stale))
                return Classify(stale, ReadSnapshotKind.Inventory, source.AccountId, null, _policy.Inventory, options, sourceStale: true);
            return ReadSnapshot<KoLInventorySnapshot>.Missing(ReadSnapshotKind.Inventory, string.Empty, _clock.GetUtcNow(), source.AccountId, null, exception);
        }
        finally { RemoveCompleted(_inventoryInflight, source.AccountId, lazy); }
    }

    private async Task<ReadSnapshot<MallSnapshot>> ReadMallAsync(
        ReadAccountSource source,
        string itemId,
        ReadQueryOptions options,
        CancellationToken cancellationToken)
    {
        var client = source.Mall ?? GlobalMall;
        if (client is null)
            return ReadSnapshot<MallSnapshot>.Missing(ReadSnapshotKind.Mall, string.Empty, _clock.GetUtcNow(), source.AccountId, itemId);
        var cacheAccount = source.Mall is null && GlobalMall is not null ? string.Empty : source.AccountId;
        var key = (cacheAccount, itemId.Trim());
        if (!options.ForceRefresh && TryGetFresh(_mall, key, _policy.Mall, options, out var cached))
            return Classify(cached, ReadSnapshotKind.Mall, source.AccountId, itemId, _policy.Mall, options, sourceStale: cached.IsStale);

        var version = Volatile.Read(ref _cacheVersion);
        var lazy = _mallInflight.GetOrAdd(key, _ => new Lazy<Task<MallSnapshot>>(
            () => client.SearchAsync(itemId, forceRefresh: true, cancellationToken: CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var value = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (version == Volatile.Read(ref _cacheVersion))
                lock (_cacheGate) _mall[key] = value;
            return Classify(value, ReadSnapshotKind.Mall, source.AccountId, itemId, _policy.Mall, options, sourceStale: value.IsStale);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (TryGetWithinStale(_mall, key, _policy.Mall, out var stale))
                return Classify(stale, ReadSnapshotKind.Mall, source.AccountId, itemId, _policy.Mall, options, sourceStale: true);
            return ReadSnapshot<MallSnapshot>.Missing(ReadSnapshotKind.Mall, string.Empty, _clock.GetUtcNow(), source.AccountId, itemId, exception);
        }
        finally { RemoveCompleted(_mallInflight, key, lazy); }
    }

    private async Task<ReadSnapshot<PricegunSnapshot>> ReadPricegunAsync(
        ReadAccountSource source,
        string itemId,
        ReadQueryOptions options,
        CancellationToken cancellationToken)
    {
        var client = source.Pricegun ?? GlobalPricegun;
        if (client is null)
            return ReadSnapshot<PricegunSnapshot>.Missing(ReadSnapshotKind.Pricegun, string.Empty, _clock.GetUtcNow(), source.AccountId, itemId);
        var cacheAccount = source.Pricegun is null && GlobalPricegun is not null ? string.Empty : source.AccountId;
        var key = (cacheAccount, itemId.Trim());
        if (!options.ForceRefresh && TryGetFresh(_pricegun, key, _policy.Pricegun, options, out var cached))
            return Classify(cached, ReadSnapshotKind.Pricegun, source.AccountId, itemId, _policy.Pricegun, options, sourceStale: cached.IsStale);

        var version = Volatile.Read(ref _cacheVersion);
        var lazy = _pricegunInflight.GetOrAdd(key, _ => new Lazy<Task<PricegunSnapshot>>(
            () => client.FetchAsync(itemId, forceRefresh: true, cancellationToken: CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var value = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (version == Volatile.Read(ref _cacheVersion))
                lock (_cacheGate) _pricegun[key] = value;
            return Classify(value, ReadSnapshotKind.Pricegun, source.AccountId, itemId, _policy.Pricegun, options, sourceStale: value.IsStale);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (TryGetWithinStale(_pricegun, key, _policy.Pricegun, out var stale))
                return Classify(stale, ReadSnapshotKind.Pricegun, source.AccountId, itemId, _policy.Pricegun, options, sourceStale: true);
            return ReadSnapshot<PricegunSnapshot>.Missing(ReadSnapshotKind.Pricegun, string.Empty, _clock.GetUtcNow(), source.AccountId, itemId, exception);
        }
        finally { RemoveCompleted(_pricegunInflight, key, lazy); }
    }

    private async Task<ReadSnapshot<IReadOnlyList<OwnShopListing>>> ReadOwnShopAsync(
        ReadAccountSource source,
        ReadQueryOptions options,
        CancellationToken cancellationToken)
    {
        var client = source.Mall ?? GlobalMall;
        if (client is null)
            return ReadSnapshot<IReadOnlyList<OwnShopListing>>.Missing(ReadSnapshotKind.OwnShop, string.Empty, _clock.GetUtcNow(), source.AccountId, null);
        if (!options.ForceRefresh && TryGetFreshOwnShop(source.AccountId, _policy.OwnShop, options, out var cached))
            return Classify(cached, source.AccountId, _policy.OwnShop, options);

        var version = Volatile.Read(ref _cacheVersion);
        var lazy = _ownShopInflight.GetOrAdd(source.AccountId, _ => new Lazy<Task<IReadOnlyList<OwnShopListing>>>(
            () => client.FetchOwnShopAsync(forceRefresh: true, cancellationToken: CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var value = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (version == Volatile.Read(ref _cacheVersion)) lock (_cacheGate)
            {
                _ownShop[source.AccountId] = value.ToArray();
                _ownShopFetchedAt[source.AccountId] = value.Count == 0
                    ? _clock.GetUtcNow()
                    : value.Max(x => x.ObservedAt);
            }
            return Classify(value, source.AccountId, _policy.OwnShop, options);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (TryGetOwnShopWithinStale(source.AccountId, _policy.OwnShop, out var stale))
                return Classify(stale, source.AccountId, _policy.OwnShop, options);
            return ReadSnapshot<IReadOnlyList<OwnShopListing>>.Missing(ReadSnapshotKind.OwnShop, string.Empty, _clock.GetUtcNow(), source.AccountId, null, exception);
        }
        finally { RemoveCompleted(_ownShopInflight, source.AccountId, lazy); }
    }

    private async Task<ReadSnapshot<ShopSalesSnapshot>> ReadShopSalesAsync(
        ReadAccountSource source,
        ReadQueryOptions options,
        CancellationToken cancellationToken)
    {
        var client = source.Shop ?? GlobalShop;
        if (client is null)
            return ReadSnapshot<ShopSalesSnapshot>.Missing(ReadSnapshotKind.ShopSales, string.Empty, _clock.GetUtcNow(), source.AccountId, null);
        if (!options.ForceRefresh && TryGetFresh(_shopSales, source.AccountId, _policy.ShopSales, options, out var cached))
            return Classify(cached, source.AccountId, _policy.ShopSales, options, sourceStale: cached.IsStale);

        var version = Volatile.Read(ref _cacheVersion);
        var lazy = _shopSalesInflight.GetOrAdd(source.AccountId, _ => new Lazy<Task<ShopSalesSnapshot>>(
            () => client.FetchSalesAsync(forceRefresh: true, cancellationToken: CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var value = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (version == Volatile.Read(ref _cacheVersion))
                lock (_cacheGate) _shopSales[source.AccountId] = value;
            return Classify(value, source.AccountId, _policy.ShopSales, options, sourceStale: value.IsStale);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (TryGetWithinStale(_shopSales, source.AccountId, _policy.ShopSales, out var stale))
                return Classify(stale with { IsStale = true }, source.AccountId, _policy.ShopSales, options);
            return ReadSnapshot<ShopSalesSnapshot>.Missing(ReadSnapshotKind.ShopSales, string.Empty, _clock.GetUtcNow(), source.AccountId, null, exception);
        }
        finally { RemoveCompleted(_shopSalesInflight, source.AccountId, lazy); }
    }

    private ReadSnapshot<T> Classify<T>(
        T value,
        ReadSnapshotKind kind,
        string accountId,
        string? itemId,
        SnapshotCachePolicy policy,
        ReadQueryOptions options,
        DateTimeOffset? fetchedAt = null,
        bool sourceStale = false)
        where T : class
    {
        var now = _clock.GetUtcNow();
        var fetched = fetchedAt ?? GetFetchedAt(value);
        var age = fetched is null ? TimeSpan.Zero : PositiveAge(now, fetched.Value);
        var maxAge = options.MaximumAge ?? policy.CacheTtl;
        var maxStale = policy.MaxStale;
        var freshness = sourceStale || age > maxAge
            ? age <= maxStale ? ReadFreshness.Stale : ReadFreshness.Expired
            : ReadFreshness.Fresh;
        var allowed = freshness == ReadFreshness.Fresh ||
            (freshness == ReadFreshness.Stale && options.AllowStale && !options.RequireFresh && _policy.AllowStale);
        return new ReadSnapshot<T>
        {
            Value = value,
            RequestId = string.Empty,
            AccountId = accountId,
            ItemId = itemId,
            Kind = kind,
            ObservedAt = now,
            FetchedAt = fetched,
            Age = age,
            Freshness = freshness,
            IsAllowed = allowed,
            ErrorCode = allowed ? null : freshness == ReadFreshness.Expired ? "expired" : "stale_rejected",
            ErrorMessage = allowed ? null : freshness == ReadFreshness.Expired
                ? $"{kind} snapshot is older than the maximum stale horizon."
                : $"{kind} snapshot is stale and the request requires fresh data."
        };
    }

    private ReadSnapshot<IReadOnlyList<OwnShopListing>> Classify(
        IReadOnlyList<OwnShopListing> value,
        string accountId,
        SnapshotCachePolicy policy,
        ReadQueryOptions options)
    {
        // Own-shop list has no enclosing timestamp. Use the newest listing's
        // observation as a conservative age marker; an empty list is observed
        // now because the client has no page timestamp to expose.
        var fetched = value.Count == 0 ? _clock.GetUtcNow() : value.Max(x => x.ObservedAt);
        return Classify(value, ReadSnapshotKind.OwnShop, accountId, null, policy, options, fetched);
    }

    private ReadSnapshot<ShopSalesSnapshot> Classify(
        ShopSalesSnapshot value,
        string accountId,
        SnapshotCachePolicy policy,
        ReadQueryOptions options,
        bool sourceStale = false)
        => Classify(value, ReadSnapshotKind.ShopSales, accountId, null, policy, options, value.FetchedAt, value.IsStale || sourceStale);

    private static DateTimeOffset? GetFetchedAt<T>(T value)
        where T : class
        => value switch
        {
            KoLStatusSnapshot status => status.FetchedAt,
            KoLInventorySnapshot inventory => inventory.FetchedAt,
            MallSnapshot mall => mall.FetchedAt,
            PricegunSnapshot pricegun => pricegun.FetchedAt,
            ShopSalesSnapshot sales => sales.FetchedAt,
            _ => null
        };

    private static TimeSpan PositiveAge(DateTimeOffset now, DateTimeOffset fetchedAt)
        => now > fetchedAt ? now - fetchedAt : TimeSpan.Zero;

    private bool TryGetFresh<T>(
        IReadOnlyDictionary<string, T> values,
        string key,
        SnapshotCachePolicy policy,
        ReadQueryOptions options,
        out T value)
        where T : class
    {
        lock (_cacheGate)
        {
            if (values.TryGetValue(key, out value!) && !options.ForceRefresh &&
                PositiveAge(_clock.GetUtcNow(), GetFetchedAt(value) ?? _clock.GetUtcNow()) <= (options.MaximumAge ?? policy.CacheTtl))
                return true;
        }
        value = default!;
        return false;
    }

    private bool TryGetFreshOwnShop(
        string accountId,
        SnapshotCachePolicy policy,
        ReadQueryOptions options,
        out IReadOnlyList<OwnShopListing> value)
    {
        lock (_cacheGate)
        {
            if (_ownShop.TryGetValue(accountId, out value!) &&
                _ownShopFetchedAt.TryGetValue(accountId, out var fetched) &&
                PositiveAge(_clock.GetUtcNow(), fetched) <= (options.MaximumAge ?? policy.CacheTtl))
                return true;
        }
        value = Array.Empty<OwnShopListing>();
        return false;
    }

    private bool TryGetOwnShopWithinStale(
        string accountId,
        SnapshotCachePolicy policy,
        out IReadOnlyList<OwnShopListing> value)
    {
        lock (_cacheGate)
        {
            if (_ownShop.TryGetValue(accountId, out value!) &&
                _ownShopFetchedAt.TryGetValue(accountId, out var fetched) &&
                PositiveAge(_clock.GetUtcNow(), fetched) <= policy.MaxStale)
                return true;
        }
        value = Array.Empty<OwnShopListing>();
        return false;
    }

    private bool TryGetFresh<T>(
        IReadOnlyDictionary<(string Account, string Item), T> values,
        (string Account, string Item) key,
        SnapshotCachePolicy policy,
        ReadQueryOptions options,
        out T value)
        where T : class
    {
        lock (_cacheGate)
        {
            if (values.TryGetValue(key, out value!) && !options.ForceRefresh &&
                PositiveAge(_clock.GetUtcNow(), GetFetchedAt(value) ?? _clock.GetUtcNow()) <= (options.MaximumAge ?? policy.CacheTtl))
                return true;
        }
        value = default!;
        return false;
    }

    private bool TryGetWithinStale<T>(
        IReadOnlyDictionary<string, T> values,
        string key,
        SnapshotCachePolicy policy,
        out T value)
        where T : class
    {
        lock (_cacheGate)
        {
            if (values.TryGetValue(key, out value!) &&
                PositiveAge(_clock.GetUtcNow(), GetFetchedAt(value) ?? _clock.GetUtcNow()) <= policy.MaxStale)
                return true;
        }
        value = default!;
        return false;
    }

    private bool TryGetWithinStale<T>(
        IReadOnlyDictionary<(string Account, string Item), T> values,
        (string Account, string Item) key,
        SnapshotCachePolicy policy,
        out T value)
        where T : class
    {
        lock (_cacheGate)
        {
            if (values.TryGetValue(key, out value!) &&
                PositiveAge(_clock.GetUtcNow(), GetFetchedAt(value) ?? _clock.GetUtcNow()) <= policy.MaxStale)
                return true;
        }
        value = default!;
        return false;
    }

    private static ReadSnapshot<T> AttachRequest<T>(ReadSnapshot<T> snapshot, string requestId, string? accountId = null, string? itemId = null)
        => snapshot with
        {
            RequestId = requestId,
            AccountId = accountId ?? snapshot.AccountId,
            ItemId = itemId ?? snapshot.ItemId
        };

    private static IEnumerable<ReadQueryDiagnostic> DiagnosticsFor<T>(ReadSnapshot<T> snapshot)
    {
        if (snapshot.IsUsable || snapshot.IsMissing) return Array.Empty<ReadQueryDiagnostic>();
        return new[]
        {
            new ReadQueryDiagnostic(
                snapshot.ErrorCode ?? snapshot.Freshness.ToString().ToLowerInvariant(),
                snapshot.ErrorMessage ?? $"{snapshot.Kind} snapshot is {snapshot.Freshness}.",
                snapshot.Kind,
                snapshot.AccountId,
                snapshot.ItemId,
                snapshot.IsError || snapshot.IsExpired)
        };
    }

    private static void AddDiagnostic<T>(ReadSnapshot<T> snapshot, ICollection<ReadQueryDiagnostic> diagnostics, bool optional = false)
    {
        if (snapshot.IsUsable || (optional && snapshot.IsMissing)) return;
        diagnostics.Add(new ReadQueryDiagnostic(
            snapshot.ErrorCode ?? snapshot.Freshness.ToString().ToLowerInvariant(),
            snapshot.ErrorMessage ?? $"{snapshot.Kind} snapshot is {snapshot.Freshness}.",
            snapshot.Kind,
            snapshot.AccountId,
            snapshot.ItemId,
            !optional && (snapshot.IsError || snapshot.IsExpired)));
    }

    private void InvalidateAdapter(ReadAccountSource source, string? accountId, string? itemId)
    {
        if (accountId is not null && !string.Equals(accountId, source.AccountId, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var adapter in new object?[] { source.Mall, source.Pricegun, source.Shop, GlobalMall, GlobalPricegun, GlobalShop }
            .Where(x => x is not null).Distinct(ReferenceEqualityComparer.Instance))
        {
            InvalidateAdapterObject(adapter!, itemId);
        }
    }

    private void RemoveInflight(string? accountId, string? itemId)
    {
        if (accountId is null && itemId is null)
        {
            _statusInflight.Clear();
            _inventoryInflight.Clear();
            _mallInflight.Clear();
            _pricegunInflight.Clear();
            _ownShopInflight.Clear();
            _shopSalesInflight.Clear();
            return;
        }

        if (accountId is not null)
        {
            _statusInflight.TryRemove(accountId, out _);
            _inventoryInflight.TryRemove(accountId, out _);
            _ownShopInflight.TryRemove(accountId, out _);
            _shopSalesInflight.TryRemove(accountId, out _);
        }
        foreach (var key in _mallInflight.Keys.Where(x =>
            (accountId is null || string.Equals(accountId, x.Account, StringComparison.OrdinalIgnoreCase) ||
                (itemId is not null && x.Account.Length == 0)) &&
            (itemId is null || string.Equals(itemId, x.Item, StringComparison.OrdinalIgnoreCase))).ToArray())
            _mallInflight.TryRemove(key, out _);
        foreach (var key in _pricegunInflight.Keys.Where(x =>
            (accountId is null || string.Equals(accountId, x.Account, StringComparison.OrdinalIgnoreCase) ||
                (itemId is not null && x.Account.Length == 0)) &&
            (itemId is null || string.Equals(itemId, x.Item, StringComparison.OrdinalIgnoreCase))).ToArray())
            _pricegunInflight.TryRemove(key, out _);
    }

    private static void InvalidateAdapterObject(object adapter, string? itemId)
    {
        switch (adapter)
        {
            case IReadCacheInvalidator invalidator:
                invalidator.Invalidate(itemId);
                break;
            case KoLAccountReadClient account:
                account.Invalidate();
                break;
            case KoLMallReadClient mall:
                mall.Invalidate(itemId);
                break;
            case KoLShopSalesReadClient shop:
                shop.Invalidate();
                break;
        }
    }

    private static string NewRequestId(string? requested)
        => string.IsNullOrWhiteSpace(requested) ? ReadRequestIds.New() : requested.Trim();

    private static void RemoveCompleted<TKey, TValue>(ConcurrentDictionary<TKey, Lazy<Task<TValue>>> map, TKey key, Lazy<Task<TValue>> lazy)
        where TKey : notnull
    {
        if (lazy.IsValueCreated && lazy.Value.IsCompleted)
            map.TryRemove(new KeyValuePair<TKey, Lazy<Task<TValue>>>(key, lazy));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ReadQueryCoordinator));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _statusInflight.Clear();
        _inventoryInflight.Clear();
        _mallInflight.Clear();
        _pricegunInflight.Clear();
        _ownShopInflight.Clear();
        _shopSalesInflight.Clear();
    }

    private sealed class NullAccountReadClient : IAccountReadClient
    {
        public Task<KoLStatusSnapshot> FetchStatusAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromException<KoLStatusSnapshot>(new InvalidOperationException("No global account source."));

        public Task<KoLInventorySnapshot> FetchInventoryAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromException<KoLInventorySnapshot>(new InvalidOperationException("No global account source."));
    }
}

/// <summary>Descriptive alias for the shared coordinator.</summary>
public sealed class SharedReadQueryCoordinator : ReadQueryCoordinator
{
    public SharedReadQueryCoordinator(
        IEnumerable<ReadAccountSource> accounts,
        ReadQueryPolicy? policy = null,
        TimeProvider? clock = null,
        IMallReadClient? mall = null,
        IPricegunReadClient? pricegun = null,
        IShopSalesReadClient? shop = null)
        : base(accounts, policy, clock, mall, pricegun, shop) { }

    public SharedReadQueryCoordinator(
        ReadAccountSource account,
        ReadQueryPolicy? policy = null,
        TimeProvider? clock = null,
        IMallReadClient? mall = null,
        IPricegunReadClient? pricegun = null,
        IShopSalesReadClient? shop = null)
        : base(account, policy, clock, mall, pricegun, shop) { }

    public SharedReadQueryCoordinator(
        IAccountReadClient account,
        IMallReadClient? mall = null,
        IPricegunReadClient? pricegun = null,
        IShopSalesReadClient? shop = null,
        ReadQueryPolicy? policy = null,
        TimeProvider? clock = null)
        : base(account, mall, pricegun, shop, policy, clock) { }
}

/// <summary>Compatibility alias for callers that use snapshot terminology.</summary>
public sealed class SnapshotCoordinator : ReadQueryCoordinator
{
    public SnapshotCoordinator(
        IEnumerable<ReadAccountSource> accounts,
        ReadQueryPolicy? policy = null,
        TimeProvider? clock = null,
        IMallReadClient? mall = null,
        IPricegunReadClient? pricegun = null,
        IShopSalesReadClient? shop = null)
        : base(accounts, policy, clock, mall, pricegun, shop) { }

    public SnapshotCoordinator(
        ReadAccountSource account,
        ReadQueryPolicy? policy = null,
        TimeProvider? clock = null,
        IMallReadClient? mall = null,
        IPricegunReadClient? pricegun = null,
        IShopSalesReadClient? shop = null)
        : base(account, policy, clock, mall, pricegun, shop) { }

    public SnapshotCoordinator(
        IAccountReadClient account,
        IMallReadClient? mall = null,
        IPricegunReadClient? pricegun = null,
        IShopSalesReadClient? shop = null,
        ReadQueryPolicy? policy = null,
        TimeProvider? clock = null)
        : base(account, mall, pricegun, shop, policy, clock) { }
}


