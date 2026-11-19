using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Data;

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
        //    When permits are missing AND we have enough useless items to trade
        //    for one (3 worthless items → 1 hermit permit) AND there's at least
        //    one clover to open, the bridge emits the trade AND the clover step
        //    so the operator sees the full happy-path in the plan.
        if (snapshot.HermitPermits > 0 && snapshot.Clovers > 0
            && snapshot.ExpectedCloverSaleValue > snapshot.CloverFloorValue
            && snapshot.ExpectedCloverSaleValue > snapshot.AdventureReserveValue)
        {
            steps.Add(new ChoiceStepSpec("choice-clover", "1"));
        }
        else if (snapshot.HermitPermits == 0 && snapshot.Clovers > 0
            && snapshot.UselessItems >= 3)
        {
            // Trade 3 useless items for 1 permit, then open the clover. The
            // bridge emits both steps so the audit ledger captures the
            // operator-approved happy path. The bounded runner does not
            // execute the trade today — operators drive it through
            // `kolibri hermit trade --quantity N`.
            var tradeRuns = Math.Min(snapshot.Clovers, snapshot.UselessItems / 3);
            for (var i = 0; i < tradeRuns; i++)
                steps.Add(new ItemStepSpec("worthless-item", Quantity: 3,
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
            steps.Add(new ChoiceStepSpec(pull.ChoiceId, pull.OptionId));
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
    /// through a fresh bounded adventure run with the supplied approval token.
    /// Each choice is its own short-lived run: <c>StartAsync</c> posts to the
    /// choice URL, then <c>ExecutePendingAsync</c> follows up if KoL answers
    /// with a follow-up choice. Item-only steps (spade, bacon-machine) are
    /// surfaced in the result for the operator to handle through dedicated
    /// write paths; the bounded runner cannot buy from the mall today.
    /// </summary>
    public async Task<BridgeExecutionResult> ExecuteAsync(
        IReadOnlyList<AdventureStepSpec> steps,
        AdventureExecutionGateway gateway,
        AdventureActionAllowlist? extraAllowlist = null,
        TimeProvider? clock = null,
        BoundedChoiceChainOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(gateway);
        var choiceSteps = steps.OfType<ChoiceStepSpec>().ToArray();
        if (choiceSteps.Length == 0)
        {
            return new BridgeExecutionResult
            {
                Executed = false,
                Steps = steps,
                AllowlistedChoiceIds = Array.Empty<string>(),
                AuditPath = "<memory>"
            };
        }
        var allowlist = extraAllowlist ?? new AdventureActionAllowlist();
        foreach (var cs in choiceSteps)
            // The bounded runner keys allowlist on AdventureId; for free-pull
            // choices the "adventure id" *is* the choice id, and the option
            // id is passed verbatim. This is the same shortcut the existing
            // DailyRoutineExecutor uses.
            allowlist = allowlist.AddChoiceOption(cs.ChoiceId, cs.ChoiceId, cs.OptionId);
        var executedCount = 0;
        foreach (var cs in choiceSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runId = $"bridge-{cs.ChoiceId}-{Guid.NewGuid():N}";
            var request = new AdventureRunRequest
            {
                RunId = runId,
                // Choice pages don't have an AdventureId. We re-use the
                // choice id so the bounded runner can still key its lookup.
                AdventureId = cs.ChoiceId
            };
            try
            {
                // Step 1: hit the choice URL via the gateway's StartAsync.
                var started = await gateway.StartAsync(request, allowlist, cancellationToken).ConfigureAwait(false);
                // Step 2: pick up follow-up choices if KoL presented one.
                var runner = new BoundedChoiceChainRunner(gateway, clock ?? TimeProvider.System);
                var settings = (options ?? new BoundedChoiceChainOptions()).Normalize();
                await runner.RunAsync(runId, allowlist, settings, cancellationToken).ConfigureAwait(false);
                if (started.Status != AdventureExecutionStatus.Aborted)
                    executedCount++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Continue with the next step; partial failure is fine for
                // a "do what we can" daily run. The audit ledger records
                // each abort via the checkpoint store.
            }
        }
        return new BridgeExecutionResult
        {
            Executed = executedCount > 0,
            Steps = steps,
            AllowlistedChoiceIds = choiceSteps
                .Select(c => c.ChoiceId + ":" + c.OptionId).ToArray(),
            AuditPath = gateway.CheckpointStore is null ? "<memory>" : "<persisted>"
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
