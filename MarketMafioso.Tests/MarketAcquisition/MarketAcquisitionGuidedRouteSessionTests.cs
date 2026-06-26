namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionGuidedRouteSessionTests
{
    [Fact]
    public void Start_BuildsLifestreamStopsFromPreparedPlan()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Zalera", "Maduin"));

        Assert.Equal("Active", session.Status);
        Assert.Equal("Zalera", session.ActiveStop?.WorldName);
        Assert.Equal("/li Zalera mb", session.ActiveStop?.LifestreamCommand);
        Assert.Equal(["Pending", "Pending"], session.Stops.Select(stop => stop.Status).ToArray());
    }

    [Fact]
    public void RecordCurrentWorld_MarksActiveStopArrived()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Zalera"));

        var result = session.RecordCurrentWorld("Zalera");

        Assert.True(result.Success);
        Assert.Equal("Arrived", session.ActiveStop?.Status);
        Assert.Contains("Zalera", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordCurrentWorld_ReportsWhenTravelHasNotReachedActiveStop()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Zalera"));

        var result = session.RecordCurrentWorld("Maduin");

        Assert.False(result.Success);
        Assert.Equal("Pending", session.ActiveStop?.Status);
        Assert.Contains("Zalera", result.Message, StringComparison.Ordinal);
        Assert.Contains("Maduin", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordProbe_AdvancesToNextStop()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Zalera", "Maduin"));

        var result = session.RecordProbe("Zalera", CreateDryRun(status: "Ready", quantity: 20, gil: 1_000));

        Assert.True(result.Success);
        Assert.Equal("Maduin", session.ActiveStop?.WorldName);
        Assert.Equal("Complete", session.Stops[0].Status);
        Assert.Equal(20u, session.Stops[0].WouldBuyQuantity);
        Assert.Equal(1_000u, session.Stops[0].WouldSpendGil);
    }

    [Fact]
    public void RecordProbe_CompletesAfterLastStop()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Zalera"));

        var result = session.RecordProbe("Zalera", CreateDryRun(status: "UnderProcured", quantity: 4, gil: 400));

        Assert.True(result.Success);
        Assert.Equal("Complete", session.Status);
        Assert.Null(session.ActiveStop);
        Assert.Equal("UnderProcured", session.Stops[0].DryRunStatus);
    }

    [Fact]
    public void RecordProbe_RejectsWrongWorld()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Zalera"));

        var result = session.RecordProbe("Maduin", CreateDryRun(status: "Ready", quantity: 20, gil: 1_000));

        Assert.False(result.Success);
        Assert.Equal("Pending", session.ActiveStop?.Status);
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionPlan CreatePlan(params string[] worlds) =>
        new()
        {
            RequestId = "request-1",
            Status = "Ready",
            WorldMode = "Recommended",
            ItemId = 2,
            RequestedQuantity = 999,
            PlannedQuantity = 999,
            PlannedGil = 10_000,
            PreparedAtUtc = DateTimeOffset.UnixEpoch,
            WorldBatches = worlds
                .Select(world => new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldBatch
                {
                    WorldName = world,
                    PlannedQuantity = 10,
                    PlannedGil = 100,
                    Listings = [],
                })
                .ToArray(),
        };

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionLiveDryRun CreateDryRun(
        string status,
        uint quantity,
        uint gil) =>
        new()
        {
            Status = status,
            Message = "Dry run result.",
            RequestedQuantity = 999,
            WouldBuyQuantity = quantity,
            WouldSpendGil = gil,
            Rows = [],
        };
}
