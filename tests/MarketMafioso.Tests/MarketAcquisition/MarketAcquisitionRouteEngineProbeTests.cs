using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngineProbeTests
{
    [Fact]
    public void Probe_StaleReadRecordsPendingAndAlwaysClearsProbeFlag()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Maduin";
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);
        harness.Runner.RecordCurrentWorld("Maduin");
        harness.MarketBoard.Reads.Enqueue(new MarketBoardReadResult
        {
            Status = "ListingCacheSwitching",
            Message = "Switching item.",
            ReadState = MarketBoardListingReadState.SwitchingItem,
            ItemId = 7017,
            WorldName = "Maduin",
        });

        harness.Engine.ProbeLiveMarketBoard();

        var snapshot = harness.Engine.CreateSnapshot();
        Assert.NotNull(snapshot.MarketBoardReadResult);
        Assert.Null(snapshot.LiveCandidatePlan);
        Assert.False(snapshot.IsProbeRunning);
        Assert.Equal("Arrived", harness.Runner.ActiveStop?.Status);
    }
}
