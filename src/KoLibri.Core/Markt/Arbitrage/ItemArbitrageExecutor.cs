using System.Globalization;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Markt;

public enum ArbitrageStepStatus
{
    /// <summary>Preview only: the gateway would accept this purchase; nothing was sent.</summary>
    Previewed,
    /// <summary>KoL answered and meat plus inventory moved exactly as expected.</summary>
    Purchased,
    /// <summary>Refused before any POST (price moved, stock, meat, budget, ...).</summary>
    NotSent,
    /// <summary>KoL answered with a clear refusal.</summary>
    Rejected,
    /// <summary>A POST may have been applied but meat/inventory do not prove it. Never retry; check the account.</summary>
    Unclear,
    /// <summary>An earlier step ended unclear, so this one was not tried.</summary>
    Skipped
}

public sealed record ArbitrageStepResult(
    ArbitrageStep Step,
    ArbitrageStepStatus Status,
    int Quantity,
    long Spent,
    string Message,
    IReadOnlyList<MallPurchaseAbortReason> Reasons);

public sealed record ArbitrageRunResult(
    bool Executed,
    IReadOnlyList<ArbitrageStepResult> Steps)
{
    public int UnitsBought => Steps.Where(s => s.Status == ArbitrageStepStatus.Purchased).Sum(s => s.Quantity);
    public long MeatSpent => Steps.Where(s => s.Status == ArbitrageStepStatus.Purchased).Sum(s => s.Spent);
    public bool Unclear => Steps.Any(s => s.Status == ArbitrageStepStatus.Unclear);
}

/// <summary>Reads the gateway needs to compare the account before and after a purchase.</summary>
public interface IArbitrageAccountReads
{
    Task<long> MeatAsync(CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, int>> InventoryAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Meat and inventory reads that only accept live data: a stale (cached) status or inventory throws, so a
/// purchase is refused with a readable message instead of being decided on an old copy.
/// </summary>
public sealed class LiveArbitrageAccountReads(IAccountReadClient api) : IArbitrageAccountReads
{
    public async Task<long> MeatAsync(CancellationToken cancellationToken)
    {
        var status = await api.FetchStatusAsync(true, cancellationToken).ConfigureAwait(false);
        if (status.IsStale)
            throw new InvalidOperationException("Meat could not be read live (only an old copy is available); nothing is bought until it can.");
        return status.Meat;
    }

    public async Task<IReadOnlyDictionary<string, int>> InventoryAsync(CancellationToken cancellationToken)
    {
        var inventory = await api.FetchInventoryAsync(true, cancellationToken).ConfigureAwait(false);
        if (inventory.IsStale)
            throw new InvalidOperationException("The inventory could not be read live (only an old copy is available); nothing is bought until it can.");
        return inventory.Items.GroupBy(i => i.ItemId)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity), StringComparer.Ordinal);
    }
}

/// <summary>
/// Runs the steps of an <see cref="ItemArbitragePlan"/> through the one existing purchase path:
/// <see cref="MallPurchaseGateway"/> with <see cref="HttpMallPurchaseTransport"/> (which builds the form with
/// <see cref="MallBuyClient.BuildPurchaseForm"/>). Each step is previewed first, sends at most one purchase POST,
/// is verified by the meat and inventory change, and its meat is reserved in the persisted
/// <see cref="ArbitrageSpendLedger"/> before the POST. An unclear step ends the run; nothing is retried.
/// </summary>
public sealed class ItemArbitrageExecutor
{
    private const string ConfirmToken = "arbitrage-confirmed";

    private readonly IMallPurchaseGateway _gateway;
    private readonly ArbitrageSpendLedger _ledger;
    private readonly string _account;
    private readonly long? _dailyBudget;

    /// <param name="dailyBudget">When set, the reservation itself checks this daily limit against everything the ledger counts (also other processes).</param>
    public ItemArbitrageExecutor(IMallPurchaseGateway gateway, ArbitrageSpendLedger ledger, string account, long? dailyBudget = null)
    {
        _dailyBudget = dailyBudget;
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _account = string.IsNullOrWhiteSpace(account) ? "default" : account;
    }

    /// <summary>Builds the executor on the HTTP transport for one signed-in account.</summary>
    public static ItemArbitrageExecutor Create(
        HttpClient http,
        AccountCredentials credentials,
        IMallReadClient mall,
        IArbitrageAccountReads reads,
        ArbitrageSpendLedger ledger,
        string account,
        ItemArbitragePlan plan,
        ItemArbitrageSettings settings,
        Uri? baseUri = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var buyClient = new MallBuyClient(http, baseUri ?? MallBuyClient.DefaultBaseUri);
        var observer = new Observer(mall, reads, ledger, account, settings.Budget);
        var options = new HttpMallPurchaseTransportOptions
        {
            PasswordHashProvider = async (request, ct) =>
                MallBuyClient.TryParseStoreId(request.StoreId, out var storeId)
                    ? await buyClient.ReadPasswordHashAsync(credentials, storeId, ct).ConfigureAwait(false)
                    : null
        };
        if (baseUri is not null)
            options = options with { BaseUri = baseUri, AllowedHosts = [baseUri.Host] };
        var transport = new HttpMallPurchaseTransport(http, options, observer.BeforeAsync, observer.AfterAsync);
        var gateway = new MallPurchaseGateway(transport, new MallPurchasePolicy
        {
            DryRun = false,
            RequireApproval = false,   // --execute / the MCP grant is the approval
            MaxDailySpend = settings.Budget,
            MeatReserve = settings.MeatReserve,   // re-checked against the fresh meat right before the POST
            MaxMeatPerPurchase = Math.Max(1, plan.Steps.Select(s => s.Total).DefaultIfEmpty(1).Max()),
            MaxQuantityPerPurchase = Math.Max(1, plan.Steps.Select(s => s.Quantity).DefaultIfEmpty(1).Max())
        });
        return new ItemArbitrageExecutor(gateway, ledger, account, settings.Budget);
    }

    /// <summary>Previews every step against fresh reads. Sends nothing.</summary>
    public async Task<ArbitrageRunResult> PreviewAsync(ItemArbitragePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var results = new List<ArbitrageStepResult>();
        foreach (var step in plan.Steps)
        {
            var preview = await PreviewStepAsync(step, cancellationToken).ConfigureAwait(false);
            results.Add(preview.IsReady
                ? new(step, ArbitrageStepStatus.Previewed, step.Quantity, 0, "Ready; nothing sent.", [])
                : NotSent(step, preview));
        }
        return new ArbitrageRunResult(false, results);
    }

    /// <summary>Buys the planned steps, one verified purchase per step.</summary>
    public async Task<ArbitrageRunResult> ExecuteAsync(ItemArbitragePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var results = new List<ArbitrageStepResult>();
        var stopped = false;
        foreach (var step in plan.Steps)
        {
            if (stopped)
            {
                results.Add(new(step, ArbitrageStepStatus.Skipped, 0, 0, "Not tried: an earlier step is unclear.", []));
                continue;
            }
            var result = await ExecuteStepAsync(step, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (result.Status == ArbitrageStepStatus.Unclear) stopped = true;
        }
        return new ArbitrageRunResult(true, results);
    }

    private Task<MallPurchasePreview> PreviewStepAsync(ArbitrageStep step, CancellationToken ct)
        => _gateway.PreviewAsync(new MallPurchaseRequest(step.StoreId, step.StoreId, step.ItemId, step.UnitPrice,
            step.Quantity, accountId: _account), ct);

    private async Task<ArbitrageStepResult> ExecuteStepAsync(ArbitrageStep step, CancellationToken ct)
    {
        var preview = await PreviewStepAsync(step, ct).ConfigureAwait(false);
        // The offer must still be the planned one: the request binds the planned price, so a moved price aborts.
        if (!preview.IsReady) return NotSent(step, preview);

        // Reserved (and written to disk) before the POST; settled only once meat and inventory prove it.
        // The reservation checks the budget and is saved in one cross-process transaction; if it cannot be
        // stored (invalid ledger, lock, write failure) nothing is sent.
        if (!_ledger.TryReserve(_account, step.Total, _dailyBudget, out var ledgerError))
            return new(step, ArbitrageStepStatus.NotSent, 0, 0, ledgerError ?? "The spend ledger refused the reservation.",
                [MallPurchaseAbortReason.DailyLimitExceeded]);
        MallPurchaseCommitResult commit;
        try
        {
            commit = await _gateway.CommitAsync(preview, ConfirmToken, ct).ConfigureAwait(false);
        }
        catch
        {
            _ledger.MarkUnclear(_account, step.Total);   // unknown whether the POST went out
            throw;
        }

        if (commit.Verified)
        {
            _ledger.Settle(_account, step.Total);
            return new(step, ArbitrageStepStatus.Purchased, step.Quantity, step.Total,
                string.Create(CultureInfo.InvariantCulture,
                    $"Bought {step.Quantity} for {step.Total:N0} meat; meat and inventory verified."), []);
        }

        var reasons = commit.AbortReasons;
        var message = commit.Receipt?.Message ?? commit.Messages.FirstOrDefault() ?? "";
        var maybeSent = commit.Receipt is { } receipt
            && receipt.MaybeSent;
        if (maybeSent || (reasons.Contains(MallPurchaseAbortReason.TransportFailure) && commit.Before is not null))
        {
            _ledger.MarkUnclear(_account, step.Total);
            return new(step, ArbitrageStepStatus.Unclear, 0, 0,
                "KoL's answer could not be matched to a meat and inventory change. Check the inventory and meat before buying again. " + message,
                reasons);
        }
        _ledger.Release(_account, step.Total);
        return commit.Receipt is not null
            ? new(step, ArbitrageStepStatus.Rejected, 0, 0, string.IsNullOrWhiteSpace(message) ? "KoL refused the purchase." : message, reasons)
            : new(step, ArbitrageStepStatus.NotSent, 0, 0,
                string.IsNullOrWhiteSpace(message) ? "The offer, limits or meat changed since the preview; nothing was sent." : message, reasons);
    }

    private static ArbitrageStepResult NotSent(ArbitrageStep step, MallPurchasePreview preview)
        => new(step, ArbitrageStepStatus.NotSent, 0, 0,
            preview.Warnings.FirstOrDefault() ?? "The preview has open problems; nothing was sent.", preview.AbortReasons);

    /// <summary>The fresh offer plus account observation the gateway checks before and after a purchase.</summary>
    private sealed class Observer(
        IMallReadClient mall, IArbitrageAccountReads reads, ArbitrageSpendLedger ledger, string account, long dailyLimit)
    {
        public Task<MallPurchaseObservation> BeforeAsync(MallPurchaseRequest request, CancellationToken ct)
            => ReadAsync(request, null, ct);

        public Task<MallPurchaseObservation> AfterAsync(MallPurchaseTransportCommit commit, CancellationToken ct)
            => ReadAsync(commit.Preview.Request, commit.Before, ct);

        private async Task<MallPurchaseObservation> ReadAsync(MallPurchaseRequest request, MallPurchaseObservation? before, CancellationToken ct)
        {
            var page = await mall.SearchItemsAsync(request.ItemId, forceRefresh: true, ct).ConfigureAwait(false);
            if (before is null && page.IsStale)
                throw new InvalidOperationException("The mall offer is stale; search again for a fresh offer.");
            var meat = await reads.MeatAsync(ct).ConfigureAwait(false);
            var items = await reads.InventoryAsync(ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var observedAt = page.FetchedAt < now ? page.FetchedAt : now;
            var listing = page.Items.FirstOrDefault(i => i.ItemId == request.ItemId)?.Listings
                .FirstOrDefault(l => l.StoreId == request.StoreId);
            var offer = listing is null
                ? new MallPurchaseOffer(request.SellerId, request.StoreId, request.ItemId, request.ExpectedUnitPrice, 0, observedAt)
                : new MallPurchaseOffer(listing.SellerId ?? request.SellerId, listing.StoreId, listing.ItemId,
                    listing.Price, listing.QuantityKnown ? listing.Quantity : 0, observedAt,
                    listing.LimitKind == MallLimitKind.Limited ? listing.PurchaseLimit : null,
                    listing.SellerName, listing.StoreName);
            var spent = before is null
                ? ledger.SpentToday(account)
                : before.Account.DailySpent + Math.Max(0, before.Account.Meat - meat);
            var state = new MallPurchaseAccountState(account, meat, spent, dailyLimit, items, now,
                sessionValid: true, isStale: page.IsStale);
            return new MallPurchaseObservation(offer, state, observedAt);
        }
    }
}
