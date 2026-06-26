namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteProgressReporterTests
{
    [Theory]
    [InlineData("Running", "progress")]
    [InlineData("Paused", "progress")]
    [InlineData("Completed", "complete")]
    [InlineData("Failed", "fail")]
    public void ResolveAction_MapsRouteStateToLifecycleEndpoint(string routeState, string expectedAction)
    {
        var action = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteProgressReporter.ResolveAction(routeState);

        Assert.Equal(expectedAction, action);
    }
}
