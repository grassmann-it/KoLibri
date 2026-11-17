using KolArbitrage.Core.Accounts;

namespace KolArbitrage.Core.Integration;

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
            s.MainSessionValid && s.MallbotSessionValid ? "Beide Sessions sind für einen frischen Snapshot verfügbar." : "Main und Mallbot müssen vor Workflow-Ausführung gültig eingeloggt sein.");
        Add(steps, "state", KoLWorkflowAction.ScanInventory, null,
            s.MainSessionValid && s.MallbotSessionValid ? KoLWorkflowStepStatus.ReadOnly : KoLWorkflowStepStatus.Blocked,
            "Inventar beider Konten scannen und reservierte Crimbo-/Arbitrage-Bestände markieren.");
        Add(steps, "shop", KoLWorkflowAction.ScanOwnShopAndSales, AccountRole.Mallbot,
            s.MallbotSessionValid ? KoLWorkflowStepStatus.ReadOnly : KoLWorkflowStepStatus.Blocked,
            "Eigenen Shop, Verkäufe und Käuferdaten aktualisieren.");
        Add(steps, "pricing", KoLWorkflowAction.RefreshPriceSources, AccountRole.Mallbot,
            s.MallbotSessionValid ? KoLWorkflowStepStatus.ReadOnly : KoLWorkflowStepStatus.Blocked,
            "Mall, Pricegun und bekannte Currency-/Ticketquellen mit Cache aktualisieren.");

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
            bagReady ? $"Pilotcharge {affordable} Bags innerhalb des Meat-Reserves." : "Bagpreis, Bestand, Session oder Meat-Reserve reichen für den Pilotplan nicht.");
        Add(steps, "bags", KoLWorkflowAction.BuyForeignBribeBags, AccountRole.Mallbot,
            bagReady ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Blocked,
            "Kauf erst nach Preis-/Bestands-Refresh und explizitem Commit.");
        Add(steps, "transfer", KoLWorkflowAction.TransferUnopenedBagsToMain, AccountRole.Mallbot,
            bagReady && s.MainSessionValid ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Blocked,
            "Ungeöffnete Bags getrennt zum Main verschieben; Mengen und Item-ID vor Commit prüfen.");
        Add(steps, "bags", KoLWorkflowAction.OpenForeignBribeBagsOnMain, AccountRole.Main,
            s.MainSessionValid && workflowSnapshotReady && s.MainCanOpenBags ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Blocked,
            "Main öffnet die Pilotcharge und protokolliert jede erhaltene Currency.");
        Add(steps, "coinmasters", KoLWorkflowAction.RankCoinmasterOutputs, AccountRole.Main,
            s.MainSessionValid && workflowSnapshotReady ? KoLWorkflowStepStatus.Ready : KoLWorkflowStepStatus.Blocked,
            "Nur handelbare Airport-Shop-Items anhand Currency-Kosten, Mall und Pricegun ranken.");
        Add(steps, "coinmasters", KoLWorkflowAction.BuyBestCoinmasterOutputs, AccountRole.Main,
            s.MainSessionValid && workflowSnapshotReady ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Blocked,
            "Tickets und Shopitems nur bei positiver Netto-Marge und Tagesbudget kaufen.");
        Add(steps, "transfer", KoLWorkflowAction.TransferSellablesToMallbot, AccountRole.Main,
            s.MainSessionValid && s.MallbotSessionValid && workflowSnapshotReady ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Blocked,
            "Handelbare Ware zum Mallbot verschieben; Crimbo-Reserven und Account-bound Items ausschließen.");
        Add(steps, "shop", KoLWorkflowAction.RepriceAndRelist, AccountRole.Mallbot,
            s.MallbotSessionValid && workflowSnapshotReady ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Blocked,
            "Preisleiter, historische Preise und eigenen Shop-Floor berücksichtigen; Relisting bleibt Commit-Schritt.");
        Add(steps, "shop", KoLWorkflowAction.RotateExcessOneUnitSale, AccountRole.Mallbot,
            s.MallbotSessionValid && workflowSnapshotReady ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Blocked,
            "Ein einzelnes Stück echter Überbestände als kurze Promo prüfen; unabhängige Mall-/Pricegun-Referenz, Cooldown und Mindestmarge erzwingen.");
        Add(steps, "hermit", KoLWorkflowAction.BuyHermitElevenLeafClover, AccountRole.Main,
            s.MainSessionValid && workflowSnapshotReady && s.MainCanUseHermit ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Deferred,
            "Hermit-Bestand vor dem Adventure-Run prüfen, Utility-Reserve abziehen und nur den sicheren Überschuss für Mallbot/Verkauf freigeben.");
        Add(steps, "adventure", KoLWorkflowAction.PlanDailyAdventure, AccountRole.Main,
            s.MainSessionValid && workflowSnapshotReady && s.AdventurePlanReady ? KoLWorkflowStepStatus.Ready : KoLWorkflowStepStatus.Deferred,
            "Tägliches Skeleton-/Knucklebones-Ziel planen und Ressourcen vor Kämpfen prüfen.");
        Add(steps, "adventure", KoLWorkflowAction.FarmOneDayTicket, AccountRole.Main,
            s.MainSessionValid && workflowSnapshotReady && s.MainCanAdventure ? KoLWorkflowStepStatus.ApprovalRequired : KoLWorkflowStepStatus.Deferred,
            "One-Day-Ticket nur in eine Zone investieren, deren erwartete Resale-Marge die Ticket-/Adventurekosten übersteigt.");
        Add(steps, "audit", KoLWorkflowAction.AuditAndPersist, null, KoLWorkflowStepStatus.ReadOnly,
            "Snapshot, Entscheidungen, Transfers und Verkäufe mit Plan-ID protokollieren.");

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
