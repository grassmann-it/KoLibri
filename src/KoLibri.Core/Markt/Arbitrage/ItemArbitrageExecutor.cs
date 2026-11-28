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
    private readonly bool _gatewayDailySpentIncludesLedger;

    /// <param name="gateway">Previews and commits purchases for the account.</param>
    /// <param name="ledger">Tracks the account's reservations and spend.</param>
    /// <param name="account">Account used for purchase requests and ledger entries.</param>
    /// <param name="dailyBudget">When set, the reservation itself checks this daily limit against everything the ledger counts (also other processes).</param>
    /// <param name="gatewayDailySpentIncludesLedger">True when the gateway reports the ledger's own spend, as in
    /// <see cref="Create"/>. By default, the gateway reports independent spend counted in full alongside the ledger.</param>
    public ItemArbitrageExecutor(IMallPurchaseGateway gateway, ArbitrageSpendLedger ledger, string account,
        long? dailyBudget = null, bool gatewayDailySpentIncludesLedger = false)
    {
        _dailyBudget = dailyBudget;
        _gatewayDailySpentIncludesLedger = gatewayDailySpentIncludesLedger;
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _account = NormalizeAccount(account);
    }

    private static string NormalizeAccount(string account) => string.IsNullOrWhiteSpace(account) ? "default" : account;

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
        account = NormalizeAccount(account);
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
        return new ItemArbitrageExecutor(gateway, ledger, account, settings.Budget, gatewayDailySpentIncludesLedger: true);
    }

    /// <summary>
    /// Previews every step against fresh reads. Sends nothing. The steps of one run are counted together: a later
    /// step is checked against the budget as if the earlier ready steps were bought.
    /// </summary>
    public async Task<ArbitrageRunResult> PreviewAsync(ItemArbitragePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var results = new List<ArbitrageStepResult>();
        long plannedInRun = 0;
        foreach (var step in plan.Steps)
        {
            if (InvalidStep(step) is { } invalid) { results.Add(invalid); continue; }
            var preview = await PreviewStepAsync(step, cancellationToken).ConfigureAwait(false);
            var result = !preview.IsReady ? NotSent(step, preview)
                : AccountProblem(step, preview) ?? BudgetProblem(step, preview, plannedInRun)
                  ?? new ArbitrageStepResult(step, ArbitrageStepStatus.Previewed, step.Quantity, 0, "Ready; nothing sent.", []);
            if (result.Status == ArbitrageStepStatus.Previewed) plannedInRun += step.Total;
            results.Add(result);
        }
        return new ArbitrageRunResult(false, results);
    }

    /// <summary>
    /// The strictest of the own daily budget, the gateway policy's daily spend limit and the daily limit the
    /// gateway reports for the account.
    /// </summary>
    private long? EffectiveLimit(MallPurchasePreview preview)
    {
        long? effectiveLimit = _dailyBudget;
        if (_gateway is MallPurchaseGateway concrete && concrete.Policy.MaxDailySpend is { } policyLimit)
            effectiveLimit = effectiveLimit is { } b ? Math.Min(b, policyLimit) : policyLimit;
        if (preview.Account is { } account)
            effectiveLimit = effectiveLimit is { } c ? Math.Min(c, account.DailyLimit) : account.DailyLimit;
        return effectiveLimit;
    }

    /// <summary>What the gateway reports as spent today; 0 when its count is the ledger itself.</summary>
    private long GatewaySpent(MallPurchasePreview preview)
        => _gatewayDailySpentIncludesLedger ? 0 : Math.Max(0, preview.Account?.DailySpent ?? 0);

    /// <summary>A step whose numbers do not add up (or are not positive) is refused before anything is read or sent.</summary>
    private static ArbitrageStepResult? InvalidStep(ArbitrageStep step)
    {
        var valid = step.Quantity > 0 && step.UnitPrice >= 0;
        if (valid)
        {
            try { valid = checked(step.UnitPrice * step.Quantity) == step.Total; }
            catch (OverflowException) { valid = false; }
        }
        return valid ? null
            : new ArbitrageStepResult(step, ArbitrageStepStatus.NotSent, 0, 0,
                "The step is not consistent (quantity, unit price and total do not match); nothing was sent.",
                [MallPurchaseAbortReason.InvalidRequest]);
    }

    /// <summary>
    /// The preview must describe this account, fresh and signed in; otherwise the budget cannot be judged and
    /// nothing is bought (never "no limit" or "nothing spent").
    /// </summary>
    private ArbitrageStepResult? AccountProblem(ArbitrageStep step, MallPurchasePreview preview)
    {
        var account = preview.Account;
        if (account is null)
            return Refused(MallPurchaseAbortReason.StaleAccount, "The preview has no account state; nothing was sent.");
        if (account.IsStale)
            return Refused(MallPurchaseAbortReason.StaleAccount, "The account state of the preview is stale; nothing was sent.");
        if (!account.SessionValid)
            return Refused(MallPurchaseAbortReason.SessionInvalid, "The session of the preview is not valid; nothing was sent.");
        if (!string.Equals(account.AccountId, _account, StringComparison.OrdinalIgnoreCase))
            return Refused(MallPurchaseAbortReason.AccountChanged,
                $"The preview is for account '{account.AccountId}', not '{_account}'; nothing was sent.");
        return null;

        ArbitrageStepResult Refused(MallPurchaseAbortReason reason, string message)
            => new(step, ArbitrageStepStatus.NotSent, 0, 0, message, [reason]);
    }

    /// <summary>
    /// The same budget check the reservation makes, without reserving: a preview must not report a step as ready
    /// when the ledger, the gateway's independent spend and the earlier steps of this run
    /// (<paramref name="plannedInRun"/>) already use up the limit, or when the ledger is unreadable.
    /// </summary>
    private ArbitrageStepResult? BudgetProblem(ArbitrageStep step, MallPurchasePreview preview, long plannedInRun)
    {
        if (_ledger.Unreadable)
            return new(step, ArbitrageStepStatus.NotSent, 0, 0,
                $"The spend ledger is blocked, so nothing would be bought: {_ledger.Problem}", [MallPurchaseAbortReason.DailyLimitExceeded]);
        if (EffectiveLimit(preview) is not { } limit) return null;
        var counted = _ledger.CountedToday(_account, GatewaySpent(preview));
        bool fits;
        try { fits = limit >= 0 && checked(counted + plannedInRun + step.Total) <= limit; }
        catch (OverflowException) { fits = false; }
        if (fits) return null;
        return new(step, ArbitrageStepStatus.NotSent, 0, 0,
            string.Create(CultureInfo.InvariantCulture,
                $"The daily budget would be exceeded: {counted:N0} Meat already counted today, {plannedInRun:N0} planned by earlier steps, {step.Total:N0} more requested, limit {limit:N0}."),
            [MallPurchaseAbortReason.DailyLimitExceeded]);
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
        if (InvalidStep(step) is { } invalid) return invalid;
        var preview = await PreviewStepAsync(step, ct).ConfigureAwait(false);
        // The offer must still be the planned one: the request binds the planned price, so a moved price aborts.
        if (!preview.IsReady) return NotSent(step, preview);
        if (AccountProblem(step, preview) is { } accountProblem) return accountProblem;

        // Reserved (and written to disk) before the POST; settled only once meat and inventory prove it.
        // The reservation checks the budget and is saved in one cross-process transaction; if it cannot be
        // stored (invalid ledger, lock, write failure) nothing is sent.
        // The limit is the strictest of the own budget, the gateway policy and the gateway's daily limit.
        // Independent spend reported by the gateway counts in full.
        if (!_ledger.TryReserve(_account, step.Total, EffectiveLimit(preview), out var reservation, out var ledgerError,
                externalSpent: GatewaySpent(preview)))
            return new(step, ArbitrageStepStatus.NotSent, 0, 0, ledgerError ?? "The spend ledger refused the reservation.",
                [MallPurchaseAbortReason.DailyLimitExceeded]);
        MallPurchaseCommitResult commit;
        try
        {
            commit = await _gateway.CommitAsync(preview, ConfirmToken, ct).ConfigureAwait(false);
        }
        catch
        {
            _ledger.MarkUnclear(reservation);   // unknown whether the POST went out
            throw;
        }

        if (commit.Verified)
        {
            _ledger.Settle(reservation);
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
            _ledger.MarkUnclear(reservation);
            return new(step, ArbitrageStepStatus.Unclear, 0, 0,
                "KoL's answer could not be matched to a meat and inventory change. Check the inventory and meat before buying again. " + message,
                reasons);
        }
        _ledger.Release(reservation);
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
