using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Data;
using KolArbitrage.Core.Integration;

namespace KolArbitrage.Core.Daily;

/// <summary>
/// One decision made by the AutoCombat-style daily bridge. Mirrors the
/// <see cref="KolArbitrage.Core.Adventure.AutoCombatDecision"/> contract
/// (action + reason + audit) so the upstream AutoCombat evaluator can
/// later replace the predicate layer without touching the bridge.
/// </summary>
public sealed record DailyDecision
{
    public required string Surface { get; init; }
    public required bool ShouldAct { get; init; }
    public required string ChoiceId { get; init; }
    public required string OptionId { get; init; }
    public required string Reason { get; init; }
    public required IReadOnlyList<AdventureStepSpec> Steps { get; init; }
}

/// <summary>
/// Bridges the player's live <see cref="AccountDailyState"/> and the
/// <see cref="DailyRoutineSnapshot"/> to typed <see cref="AdventureStepSpec"/>
/// sequences. Each surface (campfire, hermit, internet-shop, bacons,
/// bribes) gets its own predicate; the bridge composes them in order and
/// emits the steps the runner can submit.
///
/// Today the predicates are hardcoded; tomorrow they live behind an
/// <c>AutoCombatPolicy</c> so the operator can swap them out per class
/// or per run.
/// </summary>
public sealed class AutoCombatDailyBridge
{
    /// <summary>Maximum number of 11-leaf clovers the Hermit sells per day.
    /// KoL's stock page shows "3 left in stock for today" on a fresh visit;
    /// the cap keeps the plan inside that budget.</summary>
    public const int HermitDailyStock = 3;

    /// <summary>
    /// Live outcome of one AutoCombat bridge run. Each <see cref="AdventureStepSpec"/>
    /// is mapped to a single bounded-runner request and recorded as an audit row.
    /// </summary>
    public sealed record BridgeExecutionResult
    {
        public required bool Executed { get; init; }
        public required IReadOnlyList<AdventureStepSpec> Steps { get; init; }
        public required IReadOnlyList<string> AllowlistedChoiceIds { get; init; }
        public required string? AuditPath { get; init; }
    }

    public BridgeExecutionResult Plan(
        AccountDailyState state,
        DailyRoutineSnapshot snapshot,
        BaconSnapshot? bacon = null)
    {
        var steps = BuildSteps(state, snapshot, bacon);
        return new BridgeExecutionResult
        {
            Executed = steps.Count > 0,
            Steps = steps,
            AllowlistedChoiceIds = steps.OfType<ChoiceStepSpec>()
                .Select(c => c.ChoiceId + ":" + c.OptionId).ToArray(),
            AuditPath = null
        };
    }

    public IReadOnlyList<AdventureStepSpec> BuildSteps(
        AccountDailyState state,
        DailyRoutineSnapshot snapshot,
        BaconSnapshot? bacon = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);

        var steps = new List<AdventureStepSpec>();

        // 1. Campfire: burn a leaf only if the reserve is healthy AND the day is useful.
        //    Day Shortner: only when the leaf reserve would drop below the floor.
        var leafAvailable = snapshot.CampfireLeaves > snapshot.CampfireReserve;
        var dayUseful = snapshot.MainMeat > 0; // we already got meat; day is useful by default.
        if (leafAvailable && dayUseful)
        {
            steps.Add(new ChoiceStepSpec("choice-campfire", "1"));
        }
        if (snapshot.DayShortnerPrice < long.MaxValue
            && snapshot.DayShortnerPrice > 0
            && snapshot.MainMeat - snapshot.DayShortnerPrice >= 200_000
            && (!leafAvailable || !dayUseful))
        {
            // The daily shortner is worth buying only when the leaf reserve
            // is too thin to burn. The bridge never auto-buys a shortner when
            // there is already a healthy leaf reserve.
            steps.Add(new ChoiceStepSpec("choice-day-shortner", "1"));
        }

        // 2. Hermit: open a clover only when the expected sale beats the reserve value.
        //    The Hermit sells one 11-leaf clover per Hermit Permit per day
        //    (the stock is "3 left in stock for today" on a fresh visit).
        //    We open as many clovers as we have permits for, capped by the
        //    clover count. When permits run out but enough worthless items
        //    are on hand we emit the trade intent as well — one worthless
        //    item buys one permit.
        if (snapshot.HermitPermits > 0 && snapshot.Clovers > 0
            && snapshot.ExpectedCloverSaleValue > snapshot.CloverFloorValue
            && snapshot.ExpectedCloverSaleValue > snapshot.AdventureReserveValue)
        {
            // The Hermit's daily stock is small ("3 left in stock for
            // today"), and each clover costs one worthless item. We cap the
            // number of trades at the permit count and the daily stock so
            // the plan never proposes more than the Hermit can supply.
            var tradeRuns = Math.Min(
                Math.Min(snapshot.Clovers, snapshot.HermitPermits),
                HermitDailyStock);
            for (var i = 0; i < tradeRuns; i++)
                steps.Add(new ChoiceStepSpec("choice-clover", "1"));
        }
        else if (snapshot.HermitPermits == 0 && snapshot.Clovers > 0
            && snapshot.UselessItems >= 1)
        {
            // One worthless item buys one Hermit Permit, one permit buys one
            // clover. The bridge emits the permit-trade as an item step and
            // the clover-open as a choice step so the audit ledger captures
            // the operator-approved happy path.
            var tradeRuns = Math.Min(
                Math.Min(snapshot.Clovers, snapshot.UselessItems),
                HermitDailyStock);
            for (var i = 0; i < tradeRuns; i++)
                steps.Add(new ItemStepSpec("worthless-item", Quantity: 1,
                    MonsterId: null, Round: null));
            for (var i = 0; i < tradeRuns; i++)
                steps.Add(new ChoiceStepSpec("choice-clover", "1"));
        }
        if (snapshot.WishBuyQuantity > 0 && snapshot.WishUnitPrice > 0
            && snapshot.MainMeat - snapshot.WishBuyQuantity * snapshot.WishUnitPrice >= 200_000)
        {
            steps.Add(new ChoiceStepSpec("choice-hermit-wish", "1"));
        }

        // 3. Internet-shop free pulls: skip anything already claimed.
        var alreadyClaimed = new HashSet<string>(
            snapshot.AlreadyPulledItemIds, StringComparer.OrdinalIgnoreCase);
        foreach (var pull in InternetShopCatalog.DefaultPulls)
        {
            if (alreadyClaimed.Contains(pull.DisplayName)) continue;
            // The Lucky Surprise Egg and the Internet Bacon share the
            // storage-pull mechanism but pull different item ids, so the
            // option id distinguishes them (1 = egg, 2 = bacon).
            var optionId = pull.DisplayName.Contains("Internet Bacon", StringComparison.OrdinalIgnoreCase)
                ? "2"
                : pull.OptionId;
            steps.Add(new ChoiceStepSpec(pull.ChoiceId, optionId));
        }

        // 4. Archaeologist + no spade: buy one from the mall. The bridge
        //    intentionally emits a synthetic "buy-spade" step so the
        //    bounded runner can pick it up. The actual mall purchase needs
        //    a separate write path that lives outside this read-only
        //    bridge; for now we surface it as a step the operator can
        //    approve manually.
        if (state.IsClass("archaeologist") && state.QuantityOf("shovel") == 0)
        {
            steps.Add(new ItemStepSpec("shovel", Quantity: 1, MonsterId: null,
                Round: null));
        }

        // 5. Bacons: when the spread beats the minimum profit and the budget
        //    fits, emit a buy step. The bridge reads a separate BaconSnapshot
        //    (the bacon planner already owns its own thresholds).
        if (bacon is not null
            && bacon.MallPricePerMachine > 0
            && bacon.PricegunMedianSellPrice > 0
            && bacon.PricegunMedianSellPrice - bacon.MallPricePerMachine >= bacon.MinimumProfitPerUnit
            && bacon.AvailableMeat - bacon.MallPricePerMachine * Math.Max(1, bacon.MaximumBuyQuantity) >= 200_000)
        {
            steps.Add(new ItemStepSpec("bacon-machine", Quantity: bacon.MaximumBuyQuantity,
                MonsterId: null, Round: null));
        }

        return steps;
    }

    /// <summary>
    /// Live execution path. Takes a pre-built plan and routes each choice step
    /// through the best matching executor. Free-pull choices (campfire leaf,
    /// internet-shop pulls, bacon machine, hermit clover, hermit wish) hit a
    /// direct <see cref="FreePullDispatcher"/>; everything else falls back to
    /// the bounded adventure runner with an allowlist keyed on the choice id.
    /// Item-only steps (spade, bacon-machine as item) are surfaced in the
    /// result for the operator to handle through dedicated write paths.
    ///
    /// When <paramref name="audit"/> is supplied the bridge writes a
    /// hash-chained audit row per choice step, plus one wrap-up row when
    /// the run completes. The audit path is returned in
    /// <see cref="BridgeExecutionResult.AuditPath"/>.
    /// </summary>
    public async Task<BridgeExecutionResult> ExecuteAsync(
        IReadOnlyList<AdventureStepSpec> steps,
        AdventureExecutionGateway gateway,
        FreePullDispatcher? freePullDispatcher = null,
        AccountCredentials? freePullCredentials = null,
        AdventureActionAllowlist? extraAllowlist = null,
        TimeProvider? clock = null,
        BoundedChoiceChainOptions? options = null,
        BridgeAuditLog? audit = null,
        string? operationId = null,
        string? accountId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(gateway);
        var choiceSteps = steps.OfType<ChoiceStepSpec>().ToArray();
        var opId = operationId ?? Guid.NewGuid().ToString("N");
        var source = "AutoCombatDailyBridge";
        if (choiceSteps.Length == 0)
        {
            if (audit is not null)
            {
                await audit.AppendAsync(new AuditEvent(
                    AuditEventPhase.Preview, opId, source,
                    requestId: $"bridge-empty-{Guid.NewGuid():N}",
                    action: "bridge.execute.empty",
                    status: "no-op",
                    message: "Bridge had no choice steps; nothing to execute.",
                    accountId: accountId), cancellationToken).ConfigureAwait(false);
            }
            return new BridgeExecutionResult
            {
                Executed = false,
                Steps = steps,
                AllowlistedChoiceIds = Array.Empty<string>(),
                AuditPath = audit?.FilePath
            };
        }
        var allowlist = extraAllowlist ?? new AdventureActionAllowlist();
        foreach (var cs in choiceSteps)
            allowlist = allowlist.AddChoiceOption(cs.ChoiceId, cs.ChoiceId, cs.OptionId);
        var executedCount = 0;
        string? lastFreePullSnippet = null;
        // Item-only steps: the Hermit permit purchase. One worthless item
        // buys one permit, so the step's quantity is the number of permits.
        var permitSteps = steps.OfType<ItemStepSpec>()
            .Where(s => string.Equals(s.ItemId, "worthless-item", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var ps in permitSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (freePullDispatcher is null || freePullCredentials is null)
            {
                if (audit is not null)
                {
                    await audit.AppendAsync(new AuditEvent(
                        AuditEventPhase.Preview, opId, source,
                        requestId: Guid.NewGuid().ToString("N"),
                        action: $"bridge.item.hermit-permit.{ps.Quantity}.skipped",
                        status: "no-credentials",
                        message: freePullDispatcher is null
                            ? "No free-pull dispatcher configured for this role."
                            : "No stored credentials for this role.",
                        accountId: accountId), cancellationToken).ConfigureAwait(false);
                }
                break;
            }
            try
            {
                var result = await freePullDispatcher.BuyPermitAsync(
                    freePullCredentials, ps.Quantity, cancellationToken).ConfigureAwait(false);
                lastFreePullSnippet = result.BodySnippet;
                if (result.Accepted) executedCount++;
                if (audit is not null)
                {
                    await audit.AppendAsync(new AuditEvent(
                        AuditEventPhase.Commit, opId, source,
                        requestId: result.RequestId,
                        action: $"bridge.item.hermit-permit.{ps.Quantity}",
                        status: result.Accepted ? "submitted" : $"http-{result.StatusCode}",
                        message: result.BodySnippet,
                        accountId: accountId), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (audit is not null)
                {
                    await audit.AppendAsync(new AuditEvent(
                        AuditEventPhase.Verify, opId, source,
                        requestId: Guid.NewGuid().ToString("N"),
                        action: $"bridge.item.hermit-permit.{ps.Quantity}",
                        status: "exception",
                        message: ex.Message,
                        accountId: accountId), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        foreach (var cs in choiceSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var whichChoice = FreePullDispatcher.WhichChoiceFor(cs.ChoiceId);
            var canFreePull = freePullDispatcher is not null
                && freePullCredentials is not null
                && whichChoice > 0;
            string outcome;
            string requestId;
            try
            {
                if (canFreePull)
                {
                    var option = int.TryParse(cs.OptionId, out var parsed) ? parsed : 1;
                    var result = await freePullDispatcher!.PullAsync(
                        freePullCredentials!, whichChoice, option, cancellationToken).ConfigureAwait(false);
                    requestId = result.RequestId;
                    outcome = result.Accepted ? "submitted" : $"http-{result.StatusCode}";
                    if (result.Accepted) executedCount++;
                    lastFreePullSnippet = result.Location is { } loc
                        ? $"[{result.StatusCode}] -> {loc} | {(result.BodySnippet.Length > 0 ? result.BodySnippet : "(empty body)")}"
                        : result.BodySnippet;
                }
                else
                {
                    var runId = $"bridge-{cs.ChoiceId}-{Guid.NewGuid():N}";
                    var request = new AdventureRunRequest
                    {
                        RunId = runId,
                        AdventureId = cs.ChoiceId
                    };
                    var started = await gateway.StartAsync(request, allowlist, cancellationToken).ConfigureAwait(false);
                    var runner = new BoundedChoiceChainRunner(gateway, clock ?? TimeProvider.System);
                    var settings = (options ?? new BoundedChoiceChainOptions()).Normalize();
                    await runner.RunAsync(runId, allowlist, settings, cancellationToken).ConfigureAwait(false);
                    requestId = runId;
                    outcome = started.Status != AdventureExecutionStatus.Aborted ? "submitted" : "aborted";
                    if (started.Status != AdventureExecutionStatus.Aborted) executedCount++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome = "exception";
                requestId = Guid.NewGuid().ToString("N");
                if (audit is not null)
                {
                    await audit.AppendAsync(new AuditEvent(
                        AuditEventPhase.Verify, opId, source,
                        requestId: requestId,
                        action: $"bridge.choice.{cs.ChoiceId}.{cs.OptionId}",
                        status: outcome,
                        message: ex.Message,
                        accountId: accountId), cancellationToken).ConfigureAwait(false);
                }
                continue;
            }
            if (audit is not null)
            {
                await audit.AppendAsync(new AuditEvent(
                    AuditEventPhase.Commit, opId, source,
                    requestId: requestId,
                    action: $"bridge.choice.{cs.ChoiceId}.{cs.OptionId}",
                    status: outcome,
                    message: $"Posted choice {cs.ChoiceId}:{cs.OptionId} ({outcome}).",
                    accountId: accountId), cancellationToken).ConfigureAwait(false);
            }
            // Echo KoL's response body into the audit so the operator can see
            // what the server actually said ("you have already claimed
            // today's pull", a redirect to the login page, the item granted,
            // …). Without this the audit only says "submitted" which hides
            // every server-side refusal.
            if (audit is not null && lastFreePullSnippet is not null)
            {
                await audit.AppendAsync(new AuditEvent(
                    AuditEventPhase.Verify, opId, source,
                    requestId: requestId,
                    action: $"bridge.choice.{cs.ChoiceId}.{cs.OptionId}.response",
                    status: outcome,
                    message: lastFreePullSnippet,
                    accountId: accountId), cancellationToken).ConfigureAwait(false);
            }
        }
        if (audit is not null)
        {
            await audit.AppendAsync(new AuditEvent(
                AuditEventPhase.Verify, opId, source,
                requestId: $"bridge-summary-{Guid.NewGuid():N}",
                action: "bridge.execute.summary",
                status: executedCount > 0 ? "partial-or-complete" : "aborted",
                message: $"{executedCount}/{choiceSteps.Length} choice steps submitted.",
                accountId: accountId), cancellationToken).ConfigureAwait(false);
            await audit.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        return new BridgeExecutionResult
        {
            Executed = executedCount > 0,
            Steps = steps,
            AllowlistedChoiceIds = choiceSteps
                .Select(c => c.ChoiceId + ":" + c.OptionId).ToArray(),
            AuditPath = audit?.FilePath ?? (gateway.CheckpointStore is null ? "<memory>" : "<persisted>")
        };
    }

    public DailyDecision Decide(string surface, AccountDailyState state, DailyRoutineSnapshot snapshot, BaconSnapshot? bacon = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        var steps = BuildSteps(state, snapshot, bacon);
        AdventureStepSpec[] surfaceSteps = steps.Where(s => Matches(surface, s)).ToArray();
        var shouldAct = surfaceSteps.Length > 0;
        var first = surfaceSteps.Length > 0 ? surfaceSteps[0] : null;
        return new DailyDecision
        {
            Surface = surface,
            ShouldAct = shouldAct,
            ChoiceId = first is ChoiceStepSpec c ? c.ChoiceId : string.Empty,
            OptionId = first is ChoiceStepSpec c2 ? c2.OptionId : string.Empty,
            Reason = surfaceSteps.Length == 0
                ? $"No decision: snapshot does not meet the {surface} predicate."
                : $"Decision: {surfaceSteps.Length} step(s) prepared for {surface}.",
            Steps = surfaceSteps
        };
    }

    private static bool Matches(string surface, AdventureStepSpec step)
    {
        return surface.ToLowerInvariant() switch
        {
            "campfire" => step is ChoiceStepSpec cs && cs.ChoiceId.Contains("campfire", StringComparison.OrdinalIgnoreCase),
            "hermit" => step is ChoiceStepSpec cs2 && (cs2.ChoiceId.Contains("clover", StringComparison.OrdinalIgnoreCase)
                || cs2.ChoiceId.Contains("wish", StringComparison.OrdinalIgnoreCase)),
            "internet-shop" => step is ChoiceStepSpec cs3 && (cs3.ChoiceId.Contains("lucky-surprise-egg", StringComparison.OrdinalIgnoreCase)
                || cs3.ChoiceId.Contains("internet-bacon", StringComparison.OrdinalIgnoreCase)
                || cs3.ChoiceId.Contains("bacon-machine", StringComparison.OrdinalIgnoreCase)),
            "bacon" => step is ItemStepSpec its && its.ItemId == "bacon-machine",
            "spade" => step is ItemStepSpec its2 && its2.ItemId == "shovel",
            _ => false
        };
    }
}
