using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;

/// <summary>
/// One item watched by the read-only scheduler.  Tradeability is deliberately
/// explicit: an entry added without <c>--tradeable true</c> remains blocked
/// until an operator records that fact.
/// </summary>
public sealed record WatchlistEntry
{
    public string WatchlistId { get; init; } = "default";
    public string ItemId { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public bool IsTradeable { get; init; }
    public bool TradeabilityKnown { get; init; }
    public bool IsAccountBound { get; init; }
    public bool CanPurchase { get; init; } = true;
    public bool CanSell { get; init; }
    public decimal TransferCost { get; init; }
    public bool Enabled { get; init; } = true;
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>One read-only observation retained by the scheduler.</summary>
public sealed record WatchlistCandidateObservation
{
    public string WatchlistId { get; init; } = "default";
    public string ItemId { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string SellerId { get; init; } = string.Empty;
    public string? SellerName { get; init; }
    public decimal UnitPrice { get; init; }
    public int AvailableQuantity { get; init; }
    public decimal ReferencePrice { get; init; }
    public decimal NetProfit { get; init; }
    public decimal MarginPercent { get; init; }
    public decimal LiquidityScore { get; init; }
    public int RecentSalesQuantity { get; init; }
    public decimal TransferCost { get; init; }
    public bool IsAccepted { get; init; }
    public bool IsPreviewOnly { get; init; } = true;
    public bool IsStale { get; init; }
    public bool IsTradeable { get; init; }
    public bool IsAccountBound { get; init; }
    public bool StaleGatePassed { get; init; }
    public bool TradeabilityGatePassed { get; init; }
    public bool LiquidityGatePassed { get; init; }
    public bool TransferCostGatePassed { get; init; }
    public IReadOnlyList<string> GateFailures { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    public DateTimeOffset MallFetchedAt { get; init; }
    public DateTimeOffset PricegunFetchedAt { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Error { get; init; }

    public string CandidateKey => $"{ItemId}:{SellerId}:{UnitPrice.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>JSON document persisted by the watchlist command.</summary>
public sealed record WatchlistStoreDocument
{
    public int Version { get; init; } = 1;
    public string WatchlistId { get; init; } = "default";
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<WatchlistEntry> Entries { get; init; } = [];
    public List<WatchlistCandidateObservation> Candidates { get; init; } = [];
}

/// <summary>
/// Small atomic JSON repository for the scheduler's watchlist and candidate
/// history.  It stores only read-side data and has no network dependencies.
/// </summary>
public class JsonWatchlistStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonWatchlistStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path.Trim();
        _json.Converters.Add(new JsonStringEnumConverter());
    }

    public string Path => _path;

    public async Task<WatchlistStoreDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return new WatchlistStoreDocument();
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<WatchlistStoreDocument>(stream, _json, cancellationToken)
                .ConfigureAwait(false) ?? new WatchlistStoreDocument();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Watchlist-Datei '{_path}' ist kein gültiges JSON.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(WatchlistStoreDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, document with { UpdatedAt = DateTimeOffset.UtcNow }, _json, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var root = string.IsNullOrWhiteSpace(appData)
            ? System.IO.Path.Combine(Environment.CurrentDirectory, ".kolarbitrage")
            : System.IO.Path.Combine(appData, "KolArbitrage");
        return System.IO.Path.Combine(root, "watchlist.json");
    }
}

/// <summary>Compatibility alias for callers that use the longer store name.</summary>
public sealed class JsonFileWatchlistStore : JsonWatchlistStore
{
    public JsonFileWatchlistStore(string? path = null) : base(path) { }
    public new static string DefaultPath() => JsonWatchlistStore.DefaultPath();
}

/// <summary>Compatibility alias for the scheduler candidate repository.</summary>
public sealed class WatchlistCandidateStore : JsonWatchlistStore
{
    public WatchlistCandidateStore(string? path = null) : base(path) { }
    public new static string DefaultPath() => JsonWatchlistStore.DefaultPath();
}

/// <summary>JSON result emitted for one scheduler pass.</summary>
public sealed record WatchlistScanResult
{
    public string WatchlistId { get; init; } = "default";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public int WorkerLimit { get; init; }
    public bool ReadOnly { get; init; } = true;
    public bool PurchaseExecuted { get; init; }
    public IReadOnlyList<WatchlistCandidateObservation> Candidates { get; init; } = Array.Empty<WatchlistCandidateObservation>();
    public int AcceptedCount => Candidates.Count(x => x.IsAccepted);
    public int RejectedCount => Candidates.Count(x => !x.IsAccepted);
}

/// <summary>
/// Read-only Mall/Pricegun watchlist scanner.  It intentionally depends on
/// read adapters only; there is no purchase or transfer transport here.
/// </summary>
public sealed class WatchlistScanner
{
    private readonly KoLMallReadClient _mall;
    private readonly PricegunReadClient _pricegun;
    private readonly TimeProvider _clock;

    public WatchlistScanner(KoLMallReadClient mall, PricegunReadClient pricegun, TimeProvider? clock = null)
    {
        _mall = mall ?? throw new ArgumentNullException(nameof(mall));
        _pricegun = pricegun ?? throw new ArgumentNullException(nameof(pricegun));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<WatchlistScanResult> ScanAsync(
        string watchlistId,
        IReadOnlyCollection<WatchlistEntry> entries,
        WatchlistScanOptions? options = null,
        IReadOnlyCollection<OwnShopListing>? ownShop = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        options ??= new WatchlistScanOptions();
        options.Validate();
        var started = _clock.GetUtcNow();
        var enabled = entries.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.ItemId)).ToArray();
        var ownPrices = (ownShop ?? Array.Empty<OwnShopListing>())
            .GroupBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Price).ToHashSet(), StringComparer.OrdinalIgnoreCase);
        using var workerGate = new SemaphoreSlim(options.Workers, options.Workers);
        var tasks = enabled.Select(entry => ScanEntryAsync(
            watchlistId, entry, ownPrices, options, workerGate, cancellationToken));
        var rows = (await Task.WhenAll(tasks).ConfigureAwait(false)).SelectMany(x => x).ToArray();
        return new WatchlistScanResult
        {
            WatchlistId = watchlistId,
            StartedAt = started,
            CompletedAt = _clock.GetUtcNow(),
            WorkerLimit = options.Workers,
            ReadOnly = true,
            PurchaseExecuted = false,
            Candidates = rows
        };
    }

    private async Task<IReadOnlyList<WatchlistCandidateObservation>> ScanEntryAsync(
        string watchlistId,
        WatchlistEntry entry,
        IReadOnlyDictionary<string, HashSet<long>> ownPrices,
        WatchlistScanOptions options,
        SemaphoreSlim workerGate,
        CancellationToken cancellationToken)
    {
        await workerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MallSnapshot mall;
            PricegunSnapshot history;
            try
            {
                // The two calls are independent, while workerGate bounds the
                // total number of item scans to the configured safe maximum.
                var mallTask = _mall.SearchAsync(entry.ItemId, options.Refresh, cancellationToken);
                var historyTask = _pricegun.FetchAsync(entry.ItemId, options.Refresh, cancellationToken);
                await Task.WhenAll(mallTask, historyTask).ConfigureAwait(false);
                mall = await mallTask.ConfigureAwait(false);
                history = await historyTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new[]
                {
                    ErrorRow(watchlistId, entry, exception.Message, _clock.GetUtcNow())
                };
            }

            var independent = mall.Listings
                .Where(x => !ownPrices.TryGetValue(entry.ItemId, out var prices) || !prices.Contains(x.Price))
                .ToArray();
            var snapshot = new PriceSnapshot(
                entry.ItemId,
                independent,
                history.Sales,
                ownShopListings: Array.Empty<OwnShopListing>(),
                mall.FetchedAt,
                history.FetchedAt,
                mall.IsStale,
                history.IsStale,
                mall.IsStale || history.IsStale,
                mall.SourceUri,
                history.SourceUri);
            var estimate = new MarketPriceEstimator(new MarketPriceEstimatorOptions
                {
                    TopN = options.TopN,
                    MaxAge = options.MaximumReferenceAge
                })
                .Estimate(snapshot, _clock.GetUtcNow());
            var listings = independent.Select(x => new UnderpricedPurchaseListing(
                x.ItemId, x.StoreId, x.Price, x.Quantity, x.ObservedAt, x.PurchaseLimit, x.StoreName)).ToArray();
            var tradeability = new Dictionary<string, UnderpricedItemTradeability>(StringComparer.OrdinalIgnoreCase)
            {
                [entry.ItemId] = new UnderpricedItemTradeability
                {
                    IsTradeable = entry.TradeabilityKnown && entry.IsTradeable,
                    IsGiftable = entry.TradeabilityKnown && entry.CanSell,
                    IsAccountBound = entry.IsAccountBound,
                    CanPurchase = entry.CanPurchase,
                    CanSell = entry.CanSell,
                    Reason = entry.TradeabilityKnown ? null : "Tradeability facts are not recorded for this watchlist item."
                }
            };
            var policy = new UnderpricedPurchaseLimits
            {
                Budget = options.Budget,
                Reserve = Math.Min(options.Reserve, options.Budget),
                MaxQuantityPerItem = options.MaxQuantityPerItem,
                MaxQuantityPerListing = options.MaxQuantityPerListing,
                MaxDistinctItems = options.MaxDistinctItems,
                MaxProposals = options.MaxProposals,
                MinimumMarginPercent = options.MinimumMarginPercent,
                MinimumAbsoluteProfit = options.MinimumAbsoluteProfit,
                MinimumAbsoluteSpreadPerUnit = options.MinimumAbsoluteSpreadPerUnit,
                MaximumReferenceAge = options.MaximumReferenceAge,
                MinimumLiquidityScore = options.MinimumLiquidityScore,
                MinimumRecentSalesQuantity = options.MinimumRecentSalesQuantity,
                RequireLiquidityData = true,
                OtherCostPerUnit = entry.TransferCost
            };
            var plan = new UnderpricedPurchasePlanner(policy, _clock).Plan(listings,
                new Dictionary<string, RobustPriceReference>(StringComparer.OrdinalIgnoreCase)
                {
                    [entry.ItemId] = ToReference(estimate)
                }, tradeability, policy);

            var stalePassed = !mall.IsStale && !history.IsStale && !estimate.IsStale &&
                _clock.GetUtcNow() - mall.FetchedAt <= options.MaximumReferenceAge &&
                _clock.GetUtcNow() - history.FetchedAt <= options.MaximumReferenceAge;
            var tradeabilityPassed = entry.TradeabilityKnown && entry.IsTradeable && !entry.IsAccountBound &&
                entry.CanPurchase && entry.CanSell;
            var liquidity = GetLiquidity(estimate);
            var recentSales = ToInt(estimate.PricegunQuantity);
            var liquidityPassed = recentSales >= options.MinimumRecentSalesQuantity &&
                liquidity >= options.MinimumLiquidityScore;
            var result = new List<WatchlistCandidateObservation>();
            foreach (var proposal in plan.Proposals)
            {
                var transferPassed = TransferCostPasses(proposal.ReferencePrice, proposal.UnitPrice, entry.TransferCost);
                var failures = new List<string>();
                if (!stalePassed) failures.Add("stale-data");
                if (!tradeabilityPassed) failures.Add(entry.IsAccountBound ? "account-bound" : "tradeability");
                if (!liquidityPassed) failures.Add("liquidity");
                if (!transferPassed) failures.Add("transfer-cost");
                var reasons = proposal.Reasons.Concat(failures.Select(GateMessage)).Distinct(StringComparer.Ordinal).ToArray();
                result.Add(new WatchlistCandidateObservation
                {
                    WatchlistId = watchlistId,
                    ItemId = entry.ItemId,
                    DisplayName = entry.DisplayName ?? history.DisplayName,
                    SellerId = proposal.SellerId,
                    SellerName = proposal.DisplayName,
                    UnitPrice = proposal.UnitPrice,
                    AvailableQuantity = proposal.AvailableQuantity,
                    ReferencePrice = proposal.ReferencePrice,
                    NetProfit = proposal.NetProfit,
                    MarginPercent = proposal.MarginPercent,
                    LiquidityScore = proposal.LiquidityScore,
                    RecentSalesQuantity = recentSales,
                    TransferCost = entry.TransferCost,
                    IsAccepted = proposal.IsAccepted && stalePassed && tradeabilityPassed && liquidityPassed && transferPassed,
                    IsPreviewOnly = true,
                    IsStale = !stalePassed,
                    IsTradeable = entry.IsTradeable,
                    IsAccountBound = entry.IsAccountBound,
                    StaleGatePassed = stalePassed,
                    TradeabilityGatePassed = tradeabilityPassed,
                    LiquidityGatePassed = liquidityPassed,
                    TransferCostGatePassed = transferPassed,
                    GateFailures = failures,
                    Reasons = reasons,
                    MallFetchedAt = mall.FetchedAt,
                    PricegunFetchedAt = history.FetchedAt,
                    ObservedAt = _clock.GetUtcNow()
                });
            }
            if (result.Count == 0)
            {
                result.Add(new WatchlistCandidateObservation
                {
                    WatchlistId = watchlistId,
                    ItemId = entry.ItemId,
                    DisplayName = entry.DisplayName ?? history.DisplayName,
                    IsStale = !stalePassed,
                    IsTradeable = entry.IsTradeable,
                    IsAccountBound = entry.IsAccountBound,
                    StaleGatePassed = stalePassed,
                    TradeabilityGatePassed = tradeabilityPassed,
                    LiquidityGatePassed = liquidityPassed,
                    TransferCostGatePassed = TransferCostPasses(estimate.ConservativeSellPrice ?? 0m, 0m, entry.TransferCost),
                    TransferCost = entry.TransferCost,
                    GateFailures = new[] { "no-independent-listing" }
                        .Concat(!stalePassed ? new[] { "stale-data" } : Array.Empty<string>())
                        .Concat(!tradeabilityPassed ? new[] { entry.IsAccountBound ? "account-bound" : "tradeability" } : Array.Empty<string>())
                        .Concat(!liquidityPassed ? new[] { "liquidity" } : Array.Empty<string>())
                        .ToArray(),
                    Reasons = new[] { "No independent Mall listing was observed." },
                    MallFetchedAt = mall.FetchedAt,
                    PricegunFetchedAt = history.FetchedAt,
                    ObservedAt = _clock.GetUtcNow()
                });
            }
            return result;
        }
        finally
        {
            workerGate.Release();
        }
    }

    private WatchlistCandidateObservation ErrorRow(string watchlistId, WatchlistEntry entry, string error, DateTimeOffset now)
        => new()
        {
            WatchlistId = watchlistId,
            ItemId = entry.ItemId,
            DisplayName = entry.DisplayName,
            IsStale = true,
            IsTradeable = entry.IsTradeable,
            IsAccountBound = entry.IsAccountBound,
            StaleGatePassed = false,
            TradeabilityGatePassed = entry.TradeabilityKnown && entry.IsTradeable && !entry.IsAccountBound,
            LiquidityGatePassed = false,
            TransferCostGatePassed = false,
            TransferCost = entry.TransferCost,
            GateFailures = new[] { "read-error", "stale-data", "liquidity" },
            Reasons = new[] { error },
            ObservedAt = now,
            Error = error
        };

    private static RobustPriceReference ToReference(MarketEstimate estimate)
        => new()
        {
            ItemId = estimate.ItemId,
            ConservativeSellPrice = estimate.ConservativeSellPrice,
            ReferencePrice = estimate.MallReferencePrice,
            PricegunMedian = estimate.PricegunMedian,
            WeightedMedian = estimate.MallWeightedMedian,
            SecondLowestMallPrice = estimate.SecondPrice,
            ExpectedSellPrice = estimate.ExpectedSellPrice,
            AsOf = estimate.AsOf,
            IsStale = estimate.IsStale,
            Confidence = estimate.Confidence,
            RecentSalesQuantity = ToInt(estimate.PricegunQuantity),
            ObservedQuantity = ToInt(estimate.CumulativeQuantityTopN)
        };

    private static decimal GetLiquidity(MarketEstimate estimate)
    {
        var recent = Math.Max(0, estimate.PricegunQuantity);
        var observed = Math.Max(0, estimate.CumulativeQuantityTopN);
        return observed > 0 ? Math.Clamp(recent / observed, 0m, 1m) : 0m;
    }

    private static int ToInt(long value) => value <= 0 ? 0 : value >= int.MaxValue ? int.MaxValue : (int)value;

    private static bool TransferCostPasses(decimal reference, decimal ask, decimal transferCost)
        => transferCost >= 0m && reference > 0m && reference - ask > transferCost;

    private static string GateMessage(string gate) => gate switch
    {
        "stale-data" => "Stale Mall or Pricegun data blocks the candidate.",
        "tradeability" => "Tradeability is unknown or the item cannot be traded/resold.",
        "account-bound" => "Account-bound items cannot be transferred for resale.",
        "liquidity" => "Recent sale volume or liquidity score is below the configured gate.",
        "transfer-cost" => "Transfer cost consumes the available spread.",
        _ => gate
    };
}

/// <summary>Safe defaults and gates for one read-only scheduler pass.</summary>
public sealed record WatchlistScanOptions
{
    public bool Refresh { get; init; }
    public int Workers { get; init; } = 8;
    public int TopN { get; init; } = 10;
    public decimal Budget { get; init; } = decimal.MaxValue;
    public decimal Reserve { get; init; }
    public int MaxQuantityPerItem { get; init; } = 100;
    public int MaxQuantityPerListing { get; init; } = 100;
    public int MaxDistinctItems { get; init; } = 100;
    public int MaxProposals { get; init; } = 100;
    public decimal MinimumMarginPercent { get; init; } = 0.10m;
    public decimal MinimumAbsoluteProfit { get; init; } = 1_000m;
    public decimal MinimumAbsoluteSpreadPerUnit { get; init; } = 100m;
    public TimeSpan MaximumReferenceAge { get; init; } = TimeSpan.FromHours(6);
    public decimal MinimumLiquidityScore { get; init; } = 0.05m;
    public int MinimumRecentSalesQuantity { get; init; } = 1;

    public void Validate()
    {
        if (Workers is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(Workers), "Workers must be between 1 and 20.");
        if (TopN <= 0) throw new ArgumentOutOfRangeException(nameof(TopN));
        if (Budget < 0 || Reserve < 0 || Reserve > Budget) throw new ArgumentOutOfRangeException(nameof(Reserve));
        if (MaxQuantityPerItem <= 0 || MaxQuantityPerListing <= 0 || MaxDistinctItems <= 0 || MaxProposals <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxQuantityPerItem));
        if (MinimumMarginPercent < 0 || MinimumAbsoluteProfit < 0 || MinimumAbsoluteSpreadPerUnit < 0)
            throw new ArgumentOutOfRangeException(nameof(MinimumMarginPercent));
        if (MaximumReferenceAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MaximumReferenceAge));
        if (MinimumLiquidityScore is < 0 or > 1 || MinimumRecentSalesQuantity < 0)
            throw new ArgumentOutOfRangeException(nameof(MinimumLiquidityScore));
    }
}

/// <summary>CLI implementation kept separate from unrelated commands.</summary>
internal static class WatchlistSchedulerCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        Func<AccountRole, Task<(KoLAccountReadClient Api, KoLMallReadClient Mall)>> readClient,
        JsonSerializerOptions jsonOptions)
    {
        var operation = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : "scan";
        var store = new JsonWatchlistStore(GetOption(args, "--store"));
        var watchlistId = GetOption(args, "--watchlist-id") ?? GetOption(args, "--watchlist") ?? "default";
        if (string.IsNullOrWhiteSpace(watchlistId)) throw new ArgumentException("--watchlist darf nicht leer sein.");
        return operation switch
        {
            "add" => await AddAsync(args, store, watchlistId, jsonOptions),
            "remove" or "delete" => await RemoveAsync(args, store, watchlistId, jsonOptions),
            "list" or "show" => await ListAsync(store, watchlistId, jsonOptions),
            "scan" or "run" => await ScanAsync(args, store, watchlistId, readClient, jsonOptions),
            _ => throw new ArgumentException($"Unbekannte watchlist-Aktion '{operation}'. Erwartet add, remove, list oder scan.")
        };
    }

    private static async Task<int> AddAsync(string[] args, JsonWatchlistStore store, string watchlistId, JsonSerializerOptions json)
    {
        var items = GetOptions(args, "--item");
        if (items.Count == 0 && args.Length > 1 && !args[1].StartsWith('-')) items = new[] { args[1] };
        if (items.Count == 0) throw new ArgumentException("watchlist add benötigt mindestens ein --item ITEM_ID.");
        var tradeable = ParseOptionalBool(GetOption(args, "--tradeable"));
        var accountBound = ParseOptionalBool(GetOption(args, "--account-bound"));
        var canSell = ParseOptionalBool(GetOption(args, "--can-sell"));
        var transferOption = GetOption(args, "--transfer-cost");
        var transfer = transferOption is null ? (decimal?)null : ParseDecimal(transferOption, 0m, "--transfer-cost");
        if (transfer is < 0) throw new ArgumentException("--transfer-cost darf nicht negativ sein.");
        var displayName = GetOption(args, "--name");
        var now = DateTimeOffset.UtcNow;
        var document = await store.LoadAsync();
        var entries = document.Entries.ToList();
        foreach (var item in items.Select(NormalizeItemId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var existing = entries.FirstOrDefault(x => IsInWatchlist(x, watchlistId)
                && string.Equals(x.ItemId, item, StringComparison.OrdinalIgnoreCase));
            var entry = new WatchlistEntry
            {
                WatchlistId = watchlistId,
                ItemId = item,
                DisplayName = displayName ?? existing?.DisplayName,
                IsTradeable = tradeable ?? existing?.IsTradeable ?? false,
                TradeabilityKnown = tradeable.HasValue || existing?.TradeabilityKnown == true,
                IsAccountBound = accountBound ?? existing?.IsAccountBound ?? false,
                CanPurchase = existing?.CanPurchase ?? true,
                CanSell = canSell ?? existing?.CanSell ?? false,
                TransferCost = transfer ?? existing?.TransferCost ?? 0m,
                Enabled = existing?.Enabled ?? true,
                AddedAt = existing?.AddedAt ?? now
            };
            entries.RemoveAll(x => IsInWatchlist(x, watchlistId)
                && string.Equals(x.ItemId, item, StringComparison.OrdinalIgnoreCase));
            entries.Add(entry);
        }
        await store.SaveAsync(document with { WatchlistId = watchlistId, Entries = entries });
        var selected = entries.Where(x => IsInWatchlist(x, watchlistId)).ToArray();
        Write(json, new { watchlistId, path = store.Path, saved = true, count = selected.Length, entries = selected });
        return 0;
    }

    private static async Task<int> RemoveAsync(string[] args, JsonWatchlistStore store, string watchlistId, JsonSerializerOptions json)
    {
        var items = GetOptions(args, "--item");
        if (items.Count == 0 && args.Length > 1 && !args[1].StartsWith('-')) items = new[] { args[1] };
        if (items.Count == 0) throw new ArgumentException("watchlist remove benötigt mindestens ein --item ITEM_ID.");
        var normalized = items.Select(NormalizeItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var document = await store.LoadAsync();
        var entries = document.Entries.Where(x => !IsInWatchlist(x, watchlistId)
                || !normalized.Contains(x.ItemId)).ToList();
        await store.SaveAsync(document with { WatchlistId = watchlistId, Entries = entries });
        var selected = entries.Where(x => IsInWatchlist(x, watchlistId)).ToArray();
        Write(json, new { watchlistId, path = store.Path, saved = true, removed = normalized.Count, count = selected.Length, entries = selected });
        return 0;
    }

    private static async Task<int> ListAsync(JsonWatchlistStore store, string watchlistId, JsonSerializerOptions json)
    {
        var document = await store.LoadAsync();
        Write(json, new
        {
            watchlistId,
            path = store.Path,
            count = document.Entries.Count(x => IsInWatchlist(x, watchlistId)),
            candidateCount = document.Candidates.Count(x => string.Equals(x.WatchlistId, watchlistId, StringComparison.OrdinalIgnoreCase)),
            entries = document.Entries.Where(x => IsInWatchlist(x, watchlistId)).ToArray(),
            candidates = document.Candidates.Where(x => string.Equals(x.WatchlistId, watchlistId, StringComparison.OrdinalIgnoreCase)).TakeLast(100)
        });
        return 0;
    }

    private static async Task<int> ScanAsync(
        string[] args,
        JsonWatchlistStore store,
        string watchlistId,
        Func<AccountRole, Task<(KoLAccountReadClient Api, KoLMallReadClient Mall)>> readClient,
        JsonSerializerOptions json)
    {
        if (HasFlag(args, "--offline"))
        {
            var offlineDocument = await store.LoadAsync();
            Write(json, new
            {
                watchlistId,
                path = store.Path,
                mode = "offline",
                readOnly = true,
                purchaseExecuted = false,
                count = offlineDocument.Candidates.Count,
                candidates = offlineDocument.Candidates.TakeLast(100)
            });
            return 0;
        }
        var role = ParseRole(GetOption(args, "--role") ?? "mallbot");
        var workers = ParseInt(GetOption(args, "--workers"), 8, 1, 20, "--workers");
        var intervalOption = GetOption(args, "--interval") ?? GetOption(args, "--interval-seconds");
        var interval = ParseDouble(intervalOption, 300d, "--interval");
        if (interval <= 0) throw new ArgumentException("--interval muss positiv sein.");
        var mode = GetOption(args, "--mode")?.ToLowerInvariant();
        if (mode is not null && mode is not ("once" or "periodic"))
            throw new ArgumentException("--mode muss once oder periodic sein.");
        var periodic = HasFlag(args, "--periodic") || mode == "periodic";
        var once = HasFlag(args, "--once") || mode == "once" || (!periodic && !HasOption(args, "--iterations"));
        var iterations = ParseInt(GetOption(args, "--iterations"), once ? 1 : int.MaxValue, 1, 10_000, "--iterations");
        var refresh = HasFlag(args, "--refresh");
        var budget = ParseDecimal(GetOption(args, "--budget"), decimal.MaxValue, "--budget");
        var reserve = ParseDecimal(GetOption(args, "--reserve"), 1_000_000m, "--reserve");
        var transferOverrideText = GetOption(args, "--transfer-cost");
        var transferOverride = transferOverrideText is null
            ? (decimal?)null
            : ParseDecimal(transferOverrideText, 0m, "--transfer-cost");
        if (transferOverride is < 0) throw new ArgumentException("--transfer-cost darf nicht negativ sein.");
        var maxAgeHours = ParseDouble(GetOption(args, "--max-age-hours"), 6d, "--max-age-hours");
        var options = new WatchlistScanOptions
        {
            Refresh = refresh,
            Workers = workers,
            Budget = budget,
            Reserve = Math.Min(reserve, budget),
            MinimumMarginPercent = ParseDecimal(GetOption(args, "--min-margin"), 0.10m, "--min-margin"),
            MinimumAbsoluteProfit = ParseDecimal(GetOption(args, "--min-profit"), 1_000m, "--min-profit"),
            MinimumAbsoluteSpreadPerUnit = ParseDecimal(GetOption(args, "--min-spread"), 100m, "--min-spread"),
            MinimumLiquidityScore = ParseDecimal(GetOption(args, "--min-liquidity"), 0.05m, "--min-liquidity"),
            MinimumRecentSalesQuantity = ParseInt(GetOption(args, "--min-sales"), 1, 0, int.MaxValue, "--min-sales"),
            MaximumReferenceAge = TimeSpan.FromHours(maxAgeHours),
            MaxQuantityPerItem = ParseInt(GetOption(args, "--max-item"), 100, 1, 1_000_000, "--max-item"),
            MaxQuantityPerListing = ParseInt(GetOption(args, "--max-listing"), 100, 1, 1_000_000, "--max-listing"),
            MaxDistinctItems = ParseInt(GetOption(args, "--max-items"), 100, 1, 1_000_000, "--max-items"),
            MaxProposals = ParseInt(GetOption(args, "--max-proposals"), 100, 1, 1_000_000, "--max-proposals")
        };
        options.Validate();

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler? handler = null;
        handler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            using var pricegunHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var pricegun = new PricegunReadClient(pricegunHttp);
            var document = await store.LoadAsync(cancellation.Token);
            var itemOverrides = GetOptions(args, "--item").Select(NormalizeItemId).ToArray();
            var entries = document.Entries.Where(x => IsInWatchlist(x, watchlistId)).ToArray();
            if (itemOverrides.Length > 0)
            {
                var byId = entries.ToDictionary(x => x.ItemId, StringComparer.OrdinalIgnoreCase);
                entries = itemOverrides.Select(item => byId.TryGetValue(item, out var entry)
                    ? entry
                    : new WatchlistEntry { ItemId = item }).ToArray();
            }
            if (entries.Length == 0) throw new ArgumentException("Watchlist ist leer. Zuerst 'watchlist add --item ITEM_ID' verwenden.");
            if (transferOverride is { } transfer)
                entries = entries.Select(entry => entry with { TransferCost = transfer }).ToArray();

            var run = 0;
            do
            {
                var clients = await readClient(role);
                var ownShop = Array.Empty<OwnShopListing>() as IReadOnlyList<OwnShopListing>;
                try { ownShop = await clients.Mall.FetchOwnShopAsync(refresh, cancellation.Token); }
                catch { /* Own-shop exclusion is best effort; no mutation is possible here. */ }
                var effectiveBudget = budget;
                if (effectiveBudget == decimal.MaxValue)
                {
                    try { effectiveBudget = Math.Max(0m, (await clients.Api.FetchStatusAsync(refresh, cancellation.Token)).Meat); }
                    catch { effectiveBudget = decimal.MaxValue; }
                }
                var effectiveOptions = options with { Budget = effectiveBudget, Reserve = Math.Min(options.Reserve, effectiveBudget) };
                var scanner = new WatchlistScanner(clients.Mall, pricegun);
                var result = await scanner.ScanAsync(watchlistId, entries, effectiveOptions, ownShop, cancellation.Token);
                document = document with
                {
                    WatchlistId = watchlistId,
                    Candidates = document.Candidates.Concat(result.Candidates).TakeLast(5_000).ToList()
                };
                await store.SaveAsync(document, cancellation.Token);
                Write(json, new
                {
                    watchlistId,
                    path = store.Path,
                    mode = once ? "once" : "periodic",
                    iteration = run + 1,
                    role = role.ToString(),
                    result.WorkerLimit,
                    result.ReadOnly,
                    result.PurchaseExecuted,
                    result.StartedAt,
                    result.CompletedAt,
                    result.AcceptedCount,
                    result.RejectedCount,
                    candidates = result.Candidates
                });
                run++;
                if (once || run >= iterations) break;
                await Task.Delay(TimeSpan.FromSeconds(interval), cancellation.Token);
            } while (!cancellation.IsCancellationRequested);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static AccountRole ParseRole(string value) => value.ToLowerInvariant() switch
    {
        "main" => AccountRole.Main,
        "mallbot" => AccountRole.Mallbot,
        _ => throw new ArgumentException("Role muss main oder mallbot sein.")
    };

    private static string NormalizeItemId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Trim().All(char.IsDigit))
            throw new ArgumentException($"Ungültige ITEM_ID '{value}'. Erwartet eine numerische Item-ID.");
        return value.Trim();
    }

    private static decimal ParseDecimal(string? value, decimal fallback, string option)
        => value is null ? fallback : decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new ArgumentException($"{option} benötigt eine Zahl.");

    private static double ParseDouble(string? value, double fallback, string option)
        => value is null ? fallback : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new ArgumentException($"{option} benötigt eine Zahl.");

    private static int ParseInt(string? value, int fallback, int min, int max, string option)
    {
        if (value is null) return fallback;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException($"{option} benötigt eine Ganzzahl.");
        return Math.Clamp(parsed, min, max);
    }

    private static bool ParseBool(string? value, bool fallback)
        => value is null ? fallback : bool.TryParse(value, out var parsed) ? parsed : throw new ArgumentException("Boolescher Wert erwartet.");

    private static bool? ParseOptionalBool(string? value)
        => value is null ? null : bool.TryParse(value, out var parsed) ? parsed : throw new ArgumentException("Boolescher Wert erwartet.");

    private static bool HasFlag(string[] args, string name) => args.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
    private static bool HasOption(string[] args, string name) => args.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static IReadOnlyList<string> GetOptions(string[] args, string name)
    {
        var values = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Length || args[index + 1].StartsWith('-')) throw new ArgumentException($"{name} benötigt einen Wert.");
            values.Add(args[++index]);
        }
        return values;
    }

    private static void Write<T>(JsonSerializerOptions options, T value)
        => Console.WriteLine(JsonSerializer.Serialize(value, options));

    private static bool IsInWatchlist(WatchlistEntry entry, string watchlistId)
        => string.Equals(entry.WatchlistId, watchlistId, StringComparison.OrdinalIgnoreCase);
}
