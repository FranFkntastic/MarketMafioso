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

    [Theory]
    [InlineData("AcceptedInPlugin")]
    [InlineData("Running")]
    public void CanReportForRequestStatus_AllowsServerLifecycleSourceStates(string requestStatus)
    {
        Assert.True(MarketMafioso.MarketAcquisition.MarketAcquisitionRouteProgressReporter.CanReportForRequestStatus(requestStatus));
    }

    [Theory]
    [InlineData("PendingPickup")]
    [InlineData("Claimed")]
    [InlineData("Failed")]
    [InlineData("Complete")]
    [InlineData("Cancelled")]
    [InlineData(null)]
    public void CanReportForRequestStatus_BlocksStatesThatServerRejects(string? requestStatus)
    {
        Assert.False(MarketMafioso.MarketAcquisition.MarketAcquisitionRouteProgressReporter.CanReportForRequestStatus(requestStatus));
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Paused")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    public void CanReportForRouteState_AllowsServerMeaningfulRouteStates(string routeState)
    {
        Assert.True(MarketMafioso.MarketAcquisition.MarketAcquisitionRouteProgressReporter.CanReportForRouteState(routeState));
    }

    [Theory]
    [InlineData("Idle")]
    [InlineData("Stopped")]
    [InlineData("")]
    [InlineData(null)]
    public void CanReportForRouteState_BlocksLocalOnlyRouteStates(string? routeState)
    {
        Assert.False(MarketMafioso.MarketAcquisition.MarketAcquisitionRouteProgressReporter.CanReportForRouteState(routeState));
    }

    [Fact]
    public void CreateIdempotencyKey_IncludesRouteNonce()
    {
        var firstRoute = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteProgressReporter.CreateIdempotencyKey(
            "plugin-1",
            "route-a",
            1);
        var secondRoute = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteProgressReporter.CreateIdempotencyKey(
            "plugin-1",
            "route-b",
            1);

        Assert.Equal("plugin-1-route-route-a-1", firstRoute);
        Assert.Equal("plugin-1-route-route-b-1", secondRoute);
        Assert.NotEqual(firstRoute, secondRoute);
    }
}
