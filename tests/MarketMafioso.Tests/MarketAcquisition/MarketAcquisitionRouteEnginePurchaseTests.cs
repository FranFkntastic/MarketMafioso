using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteEnginePurchaseTests
{
    [Fact]
    public void BeginNextWorldPurchase_SelectionSentStartsPurchaseSession()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Maduin";
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);
        harness.Runner.RecordCurrentWorld("Maduin");
        harness.Runner.RecordProbe("Maduin", MarketAcquisitionRouteEngineTestData.ReadyCandidatePlan());
        harness.MarketBoard.Reads.Enqueue(MarketAcquisitionRouteEngineTestData.ReadWithSafeListing("Maduin"));
        harness.Purchase.PurchaseResults.Enqueue(new MarketBoardPurchaseResult
        {
            Status = "PurchaseSelectionSent",
            Message = "Selection sent.",
            Candidate = MarketAcquisitionRouteEngineTestData.Candidate("Maduin"),
        });

        harness.Engine.BeginNextWorldPurchase();

        Assert.Equal("WaitingForConfirmation", harness.Engine.CreateSnapshot().PurchaseSession?.Status);
    }

    [Fact]
    public void MonitorMarketBoardPurchase_CompletedPurchaseIncrementsCountersOnlyAfterListingRemoval()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Maduin";
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);
        harness.Runner.RecordCurrentWorld("Maduin");
        harness.Runner.RecordProbe("Maduin", MarketAcquisitionRouteEngineTestData.ReadyCandidatePlan());
        harness.MarketBoard.Reads.Enqueue(MarketAcquisitionRouteEngineTestData.ReadWithSafeListing("Maduin"));
        harness.Purchase.PurchaseResults.Enqueue(new MarketBoardPurchaseResult
        {
            Status = "PurchaseSelectionSent",
            Candidate = MarketAcquisitionRouteEngineTestData.Candidate("Maduin"),
        });
        harness.Purchase.ConfirmationResults.Enqueue(new MarketBoardPurchaseResult
        {
            Status = "ConfirmationSubmitted",
            Candidate = MarketAcquisitionRouteEngineTestData.Candidate("Maduin"),
        });
        harness.Engine.BeginNextWorldPurchase();
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddSeconds(1);
        harness.MarketBoard.Reads.Enqueue(new MarketBoardReadResult
        {
            Status = "NoListings",
            ReadState = MarketBoardListingReadState.FreshComplete,
            WorldName = "Maduin",
        });

        harness.Engine.MonitorMarketBoardPurchase(isRequestBusy: false);

        Assert.Equal(4u, harness.Runner.LastRunSummary?.PurchasedQuantity);
        Assert.Equal(3200u, harness.Runner.LastRunSummary?.SpentGil);
        Assert.Single(harness.Reporter.PurchaseAuditReports);
    }
}
