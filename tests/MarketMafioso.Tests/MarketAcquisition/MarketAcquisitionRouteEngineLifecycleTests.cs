namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngineLifecycleTests
{
    [Fact]
    public void Start_BeginsRunnerAndResetsExecutionState()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();

        var result = harness.Engine.Start(
            MarketAcquisitionRouteEngineTestData.Plan("Maduin"),
            MarketAcquisitionRouteEngineTestData.AcceptedClaim(),
            enableDiagnostics: false,
            includeOpportunisticChecks: true);

        Assert.True(result.Success);
        Assert.Equal("Running", harness.Runner.State);
        Assert.True(harness.Engine.CreateSnapshot().IsRouteActive);
        Assert.Equal(0u, harness.Engine.CreateSnapshot().ActiveWorldPurchasedQuantity);
    }

    [Fact]
    public void Reset_StopsRouteAndClearsSnapshotState()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);

        harness.Engine.Reset("No route has started.");

        var snapshot = harness.Engine.CreateSnapshot();
        Assert.False(snapshot.IsRouteActive);
        Assert.Equal("No route has started.", snapshot.VisibleAcquisitionStatus);
        Assert.Null(snapshot.MarketBoardReadResult);
    }
}
