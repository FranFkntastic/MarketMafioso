namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionGuidedRouteSessionTests
{
    [Fact]
    public void Start_BuildsLifestreamStopsFromPreparedPlan()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Zalera", "Maduin"));

        Assert.Equal("Active", session.Status);
        Assert.Equal("Zalera", session.ActiveStop?.WorldName);
        Assert.Equal("Crystal", session.ActiveStop?.DataCenter);
        Assert.Equal("/li Zalera mb", session.ActiveStop?.LifestreamCommand);
        Assert.Equal(["Pending", "Pending"], session.Stops.Select(stop => stop.Status).ToArray());
    }

    [Fact]
    public void Start_CarriesWorldItemSubtasksIntoStops()
    {
        var plan = CreatePlan("Maduin") with
        {
            WorldBatches =
            [
                new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldBatch
                {
                    WorldName = "Maduin",
                    DataCenter = "Dynamis",
                    PlannedQuantity = 30,
                    PlannedGil = 3_000,
                    ItemSubtasks =
                    [
                        new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldItemSubtask
                        {
                            LineId = "line-1",
                            LineOrdinal = 0,
                            ItemId = 2,
                            ItemName = "Fire Shard",
                            WorldName = "Maduin",
                            DataCenter = "Dynamis",
                            PlannedQuantity = 10,
                            PlannedGil = 1_000,
                        },
                        new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldItemSubtask
                        {
                            LineId = "line-2",
                            LineOrdinal = 1,
                            ItemId = 4,
                            ItemName = "Lightning Shard",
                            WorldName = "Maduin",
                            DataCenter = "Dynamis",
                            PlannedQuantity = 20,
                            PlannedGil = 2_000,
                        },
                    ],
                },
            ],
        };

        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(plan);

        var activeStop = Assert.IsType<MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteStop>(session.ActiveStop);
        Assert.Equal(["line-1", "line-2"], activeStop.ItemSubtasks.Select(subtask => subtask.LineId).ToArray());
        Assert.Equal(["Planned", "Planned"], activeStop.ItemSubtasks.Select(subtask => subtask.Source).ToArray());
    }

    [Fact]
    public void Start_InitializesLineStatesFromWorldItemSubtasks()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(
            MarketAcquisitionTestPlans.MultiLineSingleWorld());

        var activeStop = Assert.IsType<MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteStop>(session.ActiveStop);
        Assert.Equal(["batch-1-line-1", "batch-1-line-2"], activeStop.LineStates.Select(line => line.LineId).ToArray());
        Assert.Equal(["Pending", "Pending"], activeStop.LineStates.Select(line => line.Status).ToArray());
    }

    [Fact]
    public void StartWithOpportunisticChecksAddsUnplannedLinesToWorldStop()
    {
        var plan = MarketAcquisitionTestPlans.MultiLineTwoWorlds(firstWorldHasOnlyFirstLine: true);

        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(
            plan,
            includeOpportunisticChecks: true);

        var firstStop = Assert.IsType<MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteStop>(session.ActiveStop);
        Assert.Equal(["batch-1-line-1", "batch-1-line-2"], firstStop.ItemSubtasks.Select(subtask => subtask.LineId).ToArray());
        Assert.Contains(firstStop.ItemSubtasks, subtask =>
            subtask.LineId == plan.Lines[0].LineId &&
            subtask.Source == "Planned");
        Assert.Contains(firstStop.ItemSubtasks, subtask =>
            subtask.LineId == plan.Lines[1].LineId &&
            subtask.Source == "Opportunistic" &&
            subtask.PlannedQuantity == 0 &&
            subtask.WorldName == "Siren");
        Assert.Equal(["Planned", "Opportunistic"], firstStop.LineStates.Select(line => line.Source).ToArray());
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
    public void ExecuteActiveStop_SendsLifestreamCommand()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Zalera"));
        string? command = null;

        var result = session.ExecuteActiveStop(value =>
        {
            command = value;
            return true;
        });

        Assert.True(result.Success);
        Assert.Equal("/li Zalera mb", command);
        Assert.Equal("TravelCommandSent", session.ActiveStop?.Status);
    }

    [Fact]
    public void ExecuteActiveStop_ReportsUnhandledCommand()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Zalera"));

        var result = session.ExecuteActiveStop(_ => false);

        Assert.False(result.Success);
        Assert.Equal("Pending", session.ActiveStop?.Status);
        Assert.Contains("not handled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldMonitorActiveStop_IsTrueAfterTravelCommandUntilProbeCompletes()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Zalera"));

        Assert.False(session.ShouldMonitorActiveStop);

        session.ExecuteActiveStop(_ => true);

        Assert.True(session.ShouldMonitorActiveStop);

        session.RecordCurrentWorld("Zalera");

        Assert.True(session.ShouldMonitorActiveStop);

        session.RecordProbe("Zalera", CreateCandidatePlan(status: "Ready", quantity: 20, gil: 1_000));

        Assert.True(session.ShouldMonitorActiveStop);

        session.RecordWorldPurchaseBatchComplete("Zalera", purchasedQuantity: 20, spentGil: 1_000);

        Assert.False(session.ShouldMonitorActiveStop);
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
    public void RecordCurrentWorldUnavailable_ReportsExpectedTravelTransition()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Zalera"));
        session.ExecuteActiveStop(_ => true);

        var result = session.RecordCurrentWorldUnavailable();

        Assert.False(result.Success);
        Assert.Equal("TravelCommandSent", session.ActiveStop?.Status);
        Assert.Contains("Waiting", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Zalera", result.Message, StringComparison.Ordinal);
        Assert.Contains("unavailable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordProbe_WithSafeListingsStartsPurchasing()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Zalera", "Maduin"));

        var result = session.RecordProbe("Zalera", CreateCandidatePlan(status: "Ready", quantity: 20, gil: 1_000));

        Assert.True(result.Success);
        Assert.Equal("Zalera", session.ActiveStop?.WorldName);
        Assert.Equal("Purchasing", session.Stops[0].Status);
        Assert.Equal(20u, session.Stops[0].WouldBuyQuantity);
        Assert.Equal(1_000u, session.Stops[0].WouldSpendGil);
    }

    [Fact]
    public void RecordProbe_WithNoSafeListingsAdvancesToNextItemSubtaskOnSameWorld()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreateMultiItemWorldPlan());

        var result = session.RecordProbe("Maduin", CreateCandidatePlan(status: "NoSafeListings", quantity: 0, gil: 0));

        Assert.True(result.Success);
        Assert.Equal("Active", session.Status);
        Assert.Equal("Arrived", session.ActiveStop?.Status);
        Assert.Equal("line-2", session.ActiveStop?.ActiveItemSubtask?.LineId);
        Assert.Equal(1, session.ActiveStop?.CompletedItemSubtaskCount);
    }

    [Fact]
    public void RecordWorldPurchaseBatchComplete_AdvancesToNextStop()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Zalera", "Maduin"));
        session.RecordProbe("Zalera", CreateCandidatePlan(status: "Ready", quantity: 20, gil: 1_000));

        var result = session.RecordWorldPurchaseBatchComplete("Zalera", purchasedQuantity: 20, spentGil: 1_000);

        Assert.True(result.Success);
        Assert.Equal("Maduin", session.ActiveStop?.WorldName);
        Assert.Equal("Complete", session.Stops[0].Status);
        Assert.Equal(20u, session.Stops[0].PurchasedQuantity);
        Assert.Equal(1_000u, session.Stops[0].SpentGil);
    }

    [Fact]
    public void RecordWorldPurchaseBatchComplete_AdvancesToNextItemSubtaskBeforeNextWorld()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreateMultiItemWorldPlan("Maduin", "Rafflesia"));
        session.RecordProbe("Maduin", CreateCandidatePlan(status: "Ready", quantity: 10, gil: 1_000));

        var result = session.RecordWorldPurchaseBatchComplete("Maduin", purchasedQuantity: 10, spentGil: 1_000);

        Assert.True(result.Success);
        Assert.Equal("Maduin", session.ActiveStop?.WorldName);
        Assert.Equal("Arrived", session.ActiveStop?.Status);
        Assert.Equal("line-2", session.ActiveStop?.ActiveItemSubtask?.LineId);
        Assert.Equal(10u, session.ActiveStop?.PurchasedQuantity);
        Assert.Equal(1_000u, session.ActiveStop?.SpentGil);
    }

    [Fact]
    public void RecordWorldPurchaseBatchComplete_AccumulatesActiveLineTotals()
    {
        var plan = MarketAcquisitionTestPlans.MultiLineSingleWorld();
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(plan);
        session.RecordCurrentWorld("Siren");
        session.RecordProbe("Siren", MarketAcquisitionTestPlans.ReadyCandidatePlan(quantity: 10, gil: 500));

        var result = session.RecordWorldPurchaseBatchComplete("Siren", purchasedQuantity: 10, spentGil: 500);

        Assert.True(result.Success);
        var stop = Assert.Single(session.Stops);
        var firstLine = Assert.Single(stop.LineStates, line => line.LineId == plan.Lines[0].LineId);
        Assert.Equal((uint)10, firstLine.PurchasedQuantity);
        Assert.Equal((uint)500, firstLine.SpentGil);
        Assert.Equal("Complete", firstLine.Status);
        var secondLine = Assert.Single(stop.LineStates, line => line.LineId == plan.Lines[1].LineId);
        Assert.Equal("Pending", secondLine.Status);
    }

    [Fact]
    public void RecordProbe_CompletesAfterLastStopWhenNoSafeListingsRemain()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Zalera"));

        var result = session.RecordProbe("Zalera", CreateCandidatePlan(status: "NoSafeListings", quantity: 0, gil: 0));

        Assert.True(result.Success);
        Assert.Equal("Complete", session.Status);
        Assert.Null(session.ActiveStop);
        Assert.Equal("NoSafeListings", session.Stops[0].LiveCandidateStatus);
    }

    [Fact]
    public void RecordWorldPurchaseBatchComplete_CompletesAfterLastPurchasingStop()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Zalera"));
        session.RecordProbe("Zalera", CreateCandidatePlan(status: "Ready", quantity: 4, gil: 400));

        var result = session.RecordWorldPurchaseBatchComplete("Zalera", purchasedQuantity: 4, spentGil: 400);

        Assert.True(result.Success);
        Assert.Equal("Complete", session.Status);
        Assert.Null(session.ActiveStop);
        Assert.Equal("Ready", session.Stops[0].LiveCandidateStatus);
        Assert.Equal(4u, session.Stops[0].PurchasedQuantity);
        Assert.Equal(400u, session.Stops[0].SpentGil);
    }

    [Fact]
    public void RecordProbe_RejectsWrongWorld()
    {
        var session = MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Zalera"));

        var result = session.RecordProbe("Maduin", CreateCandidatePlan(status: "Ready", quantity: 20, gil: 1_000));

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

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionPlan CreateMultiItemWorldPlan(params string[] worlds)
    {
        if (worlds.Length == 0)
            worlds = ["Maduin"];

        return CreatePlan(worlds) with
        {
            WorldBatches = worlds
                .Select((world, index) => new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldBatch
                {
                    WorldName = world,
                    DataCenter = MarketMafioso.MarketAcquisition.MarketAcquisitionPlanner.ResolveNorthAmericaDataCenter(world),
                    PlannedQuantity = 30,
                    PlannedGil = 3_000,
                    ItemSubtasks = index == 0
                    ?
                    [
                        new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldItemSubtask
                        {
                            LineId = "line-1",
                            LineOrdinal = 0,
                            ItemId = 2,
                            ItemName = "Fire Shard",
                            WorldName = world,
                            PlannedQuantity = 10,
                            PlannedGil = 1_000,
                        },
                        new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldItemSubtask
                        {
                            LineId = "line-2",
                            LineOrdinal = 1,
                            ItemId = 4,
                            ItemName = "Lightning Shard",
                            WorldName = world,
                            PlannedQuantity = 20,
                            PlannedGil = 2_000,
                        },
                    ]
                    : [],
                    Listings = [],
                })
                .ToArray(),
        };
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan CreateCandidatePlan(
        string status,
        uint quantity,
        uint gil) =>
        new()
        {
            Status = status,
            Message = "Live candidate result.",
            RequestedQuantity = 999,
            WouldBuyQuantity = quantity,
            WouldSpendGil = gil,
            Rows = [],
        };
}
