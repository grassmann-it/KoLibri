using KoLibri.Core.Basis;

namespace KoLibri.Core.Spiel;

public enum KoLWorkflowAction
{
    RefreshAccountSessions,
    ScanInventory,
    ScanOwnShopAndSales,
    RefreshPriceSources,
    PlanForeignBribeBagPurchase,
    BuyForeignBribeBags,
    TransferUnopenedBagsToMain,
    OpenForeignBribeBagsOnMain,
    RankCoinmasterOutputs,
    BuyBestCoinmasterOutputs,
    TransferSellablesToMallbot,
    RepriceAndRelist,
    RotateExcessOneUnitSale,
    BuyHermitElevenLeafClover,
    PlanDailyAdventure,
    FarmOneDayTicket,
    AuditAndPersist
}

public enum KoLWorkflowStepStatus
{
    Ready,
    ReadOnly,
    ApprovalRequired,
    Blocked,
    Deferred
}

public sealed record KoLWorkflowStep(
    int Sequence,
    string Phase,
    KoLWorkflowAction Action,
    AccountRole? Role,
    KoLWorkflowStepStatus Status,
    string Rationale,
    IReadOnlyList<string> Prerequisites);

/// <summary>Inputs used to build a dry-run account workflow.</summary>
public sealed record KoLWorkflowSnapshot
{
    public bool MainSessionValid { get; init; }
    public bool MallbotSessionValid { get; init; }
    public long MallbotMeat { get; init; }
    public long? ForeignBribeBagPrice { get; init; }
    public int ForeignBribeBagStock { get; init; }
    public int PilotBagQuantity { get; init; } = 40;
    public long MallbotMeatReserve { get; init; } = 1_000_000;
    public bool MainCanOpenBags { get; init; }
    public bool MainCanAdventure { get; init; }
    public bool MainCanUseHermit { get; init; }
    public bool AdventurePlanReady { get; init; }
    public bool PriceSourcesFresh { get; init; }
    public bool OwnShopScanned { get; init; }
    public bool InventoryScanned { get; init; }
}

public sealed record KoLWorkflowPlan(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<KoLWorkflowStep> Steps)
{
    public bool HasBlockingStep => Steps.Any(x => x.Status == KoLWorkflowStepStatus.Blocked);
    public bool RequiresApproval => Steps.Any(x => x.Status == KoLWorkflowStepStatus.ApprovalRequired);
}

/// <summary>
/// Builds a deterministic, dry-run-first plan for the two-account arbitrage loop.
/// It emits work; account adapters still have to refresh state before every mutation.
/// </summary>
public sealed class KoLWorkflowPlanner
{
    public KoLWorkflowPlan Build(KoLWorkflowSnapshot input, DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var s = input with
        {
            MallbotMeat = Math.Max(0, input.MallbotMeat),
            ForeignBribeBagStock = Math.Max(0, input.ForeignBribeBagStock),
            PilotBagQuantity = Math.Max(0, input.PilotBagQuantity),
            MallbotMeatReserve = Math.Max(0, input.MallbotMeatReserve)
        };
        var steps = new List<KoLWorkflowStep>();
        Add(steps, "state", KoLWorkflowAction.RefreshAccountSessions, null,
            s.MainSessionValid && s.MallbotSessionValid ? KoLWorkflowStepStatus.ReadOnly : KoLWorkflowStepStatus.Blocked,
            s.MainSessionValid && s.MallbotSessionValid ? "Both sessions are available for a fresh snapshot." : "Main and Mallbot must be logged in with valid sessions before the workflow runs.");
        Add(steps, "state", KoLWorkflowAction.ScanInventory, null,
            s.MainSessionValid && s.MallbotSessionValid ? KoLWorkflowStepStatus.ReadOnly : KoLWorkflowStepStatus.Blocked,
            "Scan the inventory of both accounts and mark reserved Crimbo/arbitrage stock.");
        Add(steps, "shop", KoLWorkflowAction.ScanOwnShopAndSales, AccountRole.Mallbot,
            s.MallbotSessionValid ? KoLWorkflowStepStatus.ReadOnly : KoLWorkflowStepStatus.Blocked,
            "Refresh own shop, sales and buyer data.");
        Add(steps, "pricing", KoLWorkflowAction.RefreshPriceSources, AccountRole.Mallbot,
            s.MallbotSessionValid ? KoLWorkflowStepStatus.ReadOnly : KoLWorkflowStepStatus.Blocked,
            "Refresh mall, Pricegun and known currency/ticket sources using the cache.");

        // A mutation or a Ready recommendation must be based on the same
        // observation set.  The scan actions above remain available when a
        // flag is false so callers can refresh the missing input; downstream
        // steps stay blocked until both sessions and all three observations
        // are healthy.
        var workflowSnapshotReady = s.MainSessionValid && s.MallbotSessionValid &&
                                    s.PriceSourcesFresh && s.OwnShopScanned && s.InventoryScanned;

        var affordable = s.ForeignBribeBagPrice is > 0
            ? Math.Min(s.PilotBagQuantity, Math.Max(0, (s.MallbotMeat - s.MallbotMeatReserve) / s.ForeignBribeBagPrice.Value))
            : 0;
        var bagReady = s.MallbotSessionValid && workflowSnapshotReady &&
                       s.ForeignBribeBagPrice is > 0 && affordable > 0 && s.ForeignBribeBagStock >= affordable;
        Add(steps, "bags", KoLWorkflowAction.PlanForeignBribeBagPurchase, AccountRole.Mallbot,
            bagReady ? KoLWorkflowStepStatus.Ready : KoLWorkflowStepStatus.Blocked,
            bagReady ? $"Pilot batch of {affordable} bags within the meat reserve." : "Bag price, stock, session or meat reserve are insufficient for the pilot plan.");
        Add(steps, "bags", KoLWorkflowAction.BuyForeignBribeBags, AccountRole.Mallbot,
            bagReady ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Blocked,
            "Purchase only after a price/stock refresh and an explicit commit.");
        Add(steps, "transfer", KoLWorkflowAction.TransferUnopenedBagsToMain, AccountRole.Mallbot,
            bagReady && s.MainSessionValid ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Blocked,
            "Move unopened bags to Main separately; verify quantities and item ID before commit.");
        Add(steps, "bags", KoLWorkflowAction.OpenForeignBribeBagsOnMain, AccountRole.Main,
            s.MainSessionValid && workflowSnapshotReady && s.MainCanOpenBags ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Blocked,
            "Main opens the pilot batch and logs every currency received.");
        Add(steps, "coinmasters", KoLWorkflowAction.RankCoinmasterOutputs, AccountRole.Main,
            s.MainSessionValid && workflowSnapshotReady ? KoLWorkflowStepStatus.Ready : KoLWorkflowStepStatus.Blocked,
            "Rank only tradable airport shop items by currency cost, mall and Pricegun.");
        Add(steps, "coinmasters", KoLWorkflowAction.BuyBestCoinmasterOutputs, AccountRole.Main,
            s.MainSessionValid && workflowSnapshotReady ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Blocked,
            "Buy tickets and shop items only with a positive net margin and within the daily budget.");
        Add(steps, "transfer", KoLWorkflowAction.TransferSellablesToMallbot, AccountRole.Main,
            s.MainSessionValid && s.MallbotSessionValid && workflowSnapshotReady ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Blocked,
            "Move tradable goods to Mallbot; exclude Crimbo reserves and account-bound items.");
        Add(steps, "shop", KoLWorkflowAction.RepriceAndRelist, AccountRole.Mallbot,
            s.MallbotSessionValid && workflowSnapshotReady ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Blocked,
            "Consider the price ladder, historical prices and own shop floor; relisting remains a commit step.");
        Add(steps, "shop", KoLWorkflowAction.RotateExcessOneUnitSale, AccountRole.Mallbot,
            s.MallbotSessionValid && workflowSnapshotReady ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Blocked,
            "Consider a single unit of genuine overstock as a short promo; enforce an independent mall/Pricegun reference, cooldown and minimum margin.");
        Add(steps, "hermit", KoLWorkflowAction.BuyHermitElevenLeafClover, AccountRole.Main,
            s.MainSessionValid && workflowSnapshotReady && s.MainCanUseHermit ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Deferred,
            "Check hermit stock before the adventure run, deduct the utility reserve and release only the safe surplus for Mallbot/sale.");
        Add(steps, "adventure", KoLWorkflowAction.PlanDailyAdventure, AccountRole.Main,
            s.MainSessionValid && workflowSnapshotReady && s.AdventurePlanReady ? KoLWorkflowStepStatus.Ready : KoLWorkflowStepStatus.Deferred,
            "Plan the daily skeleton/knucklebones goal and check resources before fights.");
        Add(steps, "adventure", KoLWorkflowAction.FarmOneDayTicket, AccountRole.Main,
            s.MainSessionValid && workflowSnapshotReady && s.MainCanAdventure ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Deferred,
            "Invest a one-day ticket only in a zone whose expected resale margin exceeds the ticket/adventure costs.");
        Add(steps, "audit", KoLWorkflowAction.AuditAndPersist, null, KoLWorkflowStepStatus.ReadOnly,
            "Log snapshot, decisions, transfers and sales with the plan ID.");

        return new KoLWorkflowPlan(generatedAt ?? DateTimeOffset.UtcNow, steps);
    }

    private static void Add(
        ICollection<KoLWorkflowStep> steps,
        string phase,
        KoLWorkflowAction action,
        AccountRole? role,
        KoLWorkflowStepStatus status,
        string rationale)
    {
        steps.Add(new KoLWorkflowStep(
            steps.Count + 1,
            phase,
            action,
            role,
            status,
            rationale,
            ["fresh account state", "approval for mutating actions"]));
    }
}
