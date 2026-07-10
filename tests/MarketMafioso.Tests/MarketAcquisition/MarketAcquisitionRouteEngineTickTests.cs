namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngineTickTests
{
    [Fact]
    public void Tick_PendingStopOnDifferentWorldSendsTravelCommandWhenPreflightPasses()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Zalera";
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);

        harness.Engine.TickRoute(isRequestBusy: false);

        Assert.Equal(["/li Maduin mb"], harness.Ui.Commands);
        Assert.Equal("TravelCommandSent", harness.Runner.ActiveStop?.Status);
    }

    [Fact]
    public void Tick_PendingStopBlockedByUiDoesNotSendTravelCommand()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Zalera";
        harness.Ui.TravelPreflightCanSend = false;
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);

        harness.Engine.TickRoute(isRequestBusy: false);

        Assert.Empty(harness.Ui.Commands);
        Assert.Equal("Pending", harness.Runner.ActiveStop?.Status);
        Assert.Contains("travel", harness.Runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }
}
