using Xunit;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

public sealed class WorkflowPlannerTests
{
    private static KoLWorkflowSnapshot CompleteSnapshot => new()
    {
        MainSessionValid = true,
        MallbotSessionValid = true,
        MallbotMeat = 2_409_081,
        ForeignBribeBagPrice = 27_000,
        ForeignBribeBagStock = 463,
        PilotBagQuantity = 40,
        MallbotMeatReserve = 1_000_000,
        MainCanOpenBags = true,
        MainCanAdventure = true,
        MainCanUseHermit = true,
        AdventurePlanReady = true,
        PriceSourcesFresh = true,
        OwnShopScanned = true,
        InventoryScanned = true
    };

    [Fact]
    public void CompleteSnapshotMakesWorkflowActionsReadyOrApprovalBound()
    {
        var plan = new KoLWorkflowPlanner().Build(CompleteSnapshot);

        Assert.False(plan.HasBlockingStep);
        Assert.Equal(KoLWorkflowStepStatus.Ready, Step(plan, KoLWorkflowAction.PlanForeignBribeBagPurchase).Status);
        Assert.Equal(KoLWorkflowStepStatus.ApprovalRequired, Step(plan, KoLWorkflowAction.BuyForeignBribeBags).Status);
        Assert.Equal(KoLWorkflowStepStatus.ApprovalRequired, Step(plan, KoLWorkflowAction.TransferUnopenedBagsToMain).Status);
        Assert.Equal(KoLWorkflowStepStatus.ApprovalRequired, Step(plan, KoLWorkflowAction.OpenForeignBribeBagsOnMain).Status);
        Assert.Equal(KoLWorkflowStepStatus.Ready, Step(plan, KoLWorkflowAction.RankCoinmasterOutputs).Status);
        Assert.Equal(KoLWorkflowStepStatus.ApprovalRequired, Step(plan, KoLWorkflowAction.BuyBestCoinmasterOutputs).Status);
        Assert.Equal(KoLWorkflowStepStatus.ApprovalRequired, Step(plan, KoLWorkflowAction.TransferSellablesToMallbot).Status);
        Assert.Equal(KoLWorkflowStepStatus.ApprovalRequired, Step(plan, KoLWorkflowAction.RepriceAndRelist).Status);
        Assert.Equal(KoLWorkflowStepStatus.ApprovalRequired, Step(plan, KoLWorkflowAction.RotateExcessOneUnitSale).Status);
        Assert.Equal(KoLWorkflowStepStatus.ApprovalRequired, Step(plan, KoLWorkflowAction.BuyHermitElevenLeafClover).Status);
        Assert.Equal(KoLWorkflowStepStatus.Ready, Step(plan, KoLWorkflowAction.PlanDailyAdventure).Status);
        Assert.Equal(KoLWorkflowStepStatus.ApprovalRequired, Step(plan, KoLWorkflowAction.FarmOneDayTicket).Status);
    }

    [Theory]
    [InlineData("PriceSourcesFresh")]
    [InlineData("OwnShopScanned")]
    [InlineData("InventoryScanned")]
    public void MissingSnapshotScanBlocksAllReadyOrApprovalActions(string missingSignal)
    {
        var snapshot = CompleteSnapshot with
        {
            PriceSourcesFresh = missingSignal != "PriceSourcesFresh",
            OwnShopScanned = missingSignal != "OwnShopScanned",
            InventoryScanned = missingSignal != "InventoryScanned"
        };

        var plan = new KoLWorkflowPlanner().Build(snapshot);

        Assert.DoesNotContain(
            plan.Steps,
            step => step.Status is KoLWorkflowStepStatus.Ready or KoLWorkflowStepStatus.ApprovalRequired);
        Assert.False(plan.RequiresApproval);
        Assert.Contains(plan.Steps, step => step.Status == KoLWorkflowStepStatus.Blocked);
    }

    [Fact]
    public void MainSessionHealthBlocksReadyAndApprovalActions()
    {
        var plan = new KoLWorkflowPlanner().Build(CompleteSnapshot with { MainSessionValid = false });

        Assert.DoesNotContain(
            plan.Steps,
            step => step.Status is KoLWorkflowStepStatus.Ready or KoLWorkflowStepStatus.ApprovalRequired);
        Assert.False(plan.RequiresApproval);
    }

    [Fact]
    public void MallbotSessionHealthBlocksReadyAndApprovalActions()
    {
        var plan = new KoLWorkflowPlanner().Build(CompleteSnapshot with { MallbotSessionValid = false });

        Assert.DoesNotContain(
            plan.Steps,
            step => step.Status is KoLWorkflowStepStatus.Ready or KoLWorkflowStepStatus.ApprovalRequired);
        Assert.False(plan.RequiresApproval);
    }

    private static KoLWorkflowStep Step(KoLWorkflowPlan plan, KoLWorkflowAction action) =>
        Assert.Single(plan.Steps, step => step.Action == action);
}
