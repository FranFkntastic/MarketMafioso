namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteRunnerTests
{
    [Fact]
    public void Start_ConstructsRunningRoute()
    {
        using var runner = CreateRunner();

        var result = runner.Start(CreatePlan("Maduin", "Rafflesia"));

        Assert.True(result.Success);
        Assert.Equal("Running", runner.State);
        Assert.Equal("Maduin", runner.ActiveStop?.WorldName);
        Assert.Equal(["Pending", "Pending"], runner.Stops.Select(stop => stop.Status).ToArray());
        Assert.Contains("Maduin", runner.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_WithDiagnosticsCreatesRouteLog()
    {
        var directory = CreateTempDirectory();
        using var runner = new MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunner(directory);

        runner.Start(CreateMultiItemWorldPlan("Maduin"), enableDiagnostics: true);
        runner.ExecutePendingTravelCommand(_ => true);

        Assert.NotNull(runner.LastDiagnosticFilePath);
        var text = ReadLog(runner.LastDiagnosticFilePath!);
        Assert.Contains("Market acquisition route diagnostics started.", text, StringComparison.Ordinal);
        Assert.Contains("route-start", text, StringComparison.Ordinal);
        Assert.Contains("requestId: request-1", text, StringComparison.Ordinal);
        Assert.Contains("worldMode: Recommended", text, StringComparison.Ordinal);
        Assert.Contains("worldCount: 1", text, StringComparison.Ordinal);
        Assert.Contains("firstStop: Maduin", text, StringComparison.Ordinal);
        Assert.Contains("firstItem: Varnish (7017)", text, StringComparison.Ordinal);
        Assert.Contains("travel-command", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordLineProgress_WritesLineIdentityToDiagnostics()
    {
        var directory = CreateTempDirectory();
        using var runner = new MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunner(directory);
        runner.Start(MarketAcquisitionTestPlans.MultiLineSingleWorld(), enableDiagnostics: true);

        runner.RecordLineProgress(
            lineId: "batch-1-line-2",
            itemName: "Silver Ingot",
            status: "Running",
            purchasedQuantity: 5,
            spentGil: 2500,
            message: "Bought safe listing.");

        var text = ReadLog(runner.LastDiagnosticFilePath!);
        Assert.Contains("line-progress", text, StringComparison.Ordinal);
        Assert.Contains("lineId: batch-1-line-2", text, StringComparison.Ordinal);
        Assert.Contains("itemName: Silver Ingot", text, StringComparison.Ordinal);
        Assert.Contains("purchasedQuantity: 5", text, StringComparison.Ordinal);
        Assert.Contains("spentGil: 2500", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordPurchaseAudit_WritesListingIdentityToDiagnostics()
    {
        var directory = CreateTempDirectory();
        using var runner = new MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunner(directory);
        runner.Start(MarketAcquisitionTestPlans.MultiLineSingleWorld(), enableDiagnostics: true);

        runner.RecordPurchaseAudit(
            lineId: "batch-1-line-2",
            itemName: "Silver Ingot",
            worldName: "Siren",
            listingId: "listing-1",
            retainerId: "retainer-1",
            quantity: 5,
            totalGil: 2500,
            result: "Purchased");

        var text = ReadLog(runner.LastDiagnosticFilePath!);
        Assert.Contains("purchase-audit", text, StringComparison.Ordinal);
        Assert.Contains("lineId: batch-1-line-2", text, StringComparison.Ordinal);
        Assert.Contains("world: Siren", text, StringComparison.Ordinal);
        Assert.Contains("listingId: listing-1", text, StringComparison.Ordinal);
        Assert.Contains("retainerId: retainer-1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordWorldPurchaseBatchComplete_StoresPostRunSummaryWithDiagnosticArtifacts()
    {
        var directory = CreateTempDirectory();
        using var runner = new MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunner(directory);
        runner.Start(MarketAcquisitionTestPlans.MultiLineSingleWorld(), enableDiagnostics: true);
        runner.RecordCurrentWorld("Siren");

        runner.RecordProbe("Siren", MarketAcquisitionTestPlans.ReadyCandidatePlan(10, 1_000));
        runner.RecordWorldPurchaseBatchComplete("Siren", 10, 1_000);
        runner.RecordProbe("Siren", MarketAcquisitionTestPlans.ReadyCandidatePlan(20, 2_000));
        runner.RecordWorldPurchaseBatchComplete("Siren", 20, 2_000);

        var summary = Assert.IsType<MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunSummary>(runner.LastRunSummary);
        Assert.Equal(30u, summary.PurchasedQuantity);
        Assert.Equal(3_000u, summary.SpentGil);
        Assert.Equal(1, summary.CompletedWorldCount);
        Assert.Equal(2, summary.CompletedLineCount);
        Assert.Equal(runner.LastDiagnosticFilePath, summary.DiagnosticsPath);
        Assert.Equal(runner.LastObservedListingsCsvPath, summary.ObservedListingsCsvPath);
        Assert.Equal(runner.LastPurchaseRecordsCsvPath, summary.PurchaseRecordsCsvPath);
    }

    [Fact]
    public async Task VerifyLatestWorldFreshnessAsync_RecordsPurchasedListingResultsForCompletedWorld()
    {
        var directory = CreateTempDirectory();
        var calls = new List<(string WorldName, uint ItemId, string[] ListingIds)>();
        using var runner = new MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunner(
            directory,
            (worldName, itemId, _, purchasedListingIds, _) =>
            {
                calls.Add((worldName, itemId, purchasedListingIds.ToArray()));
                return Task.FromResult(MarketMafioso.MarketAcquisition.UniversalisFreshnessResult.Confirmed("fresh"));
            });
        runner.Start(MarketAcquisitionTestPlans.MultiLineSingleWorld(), enableDiagnostics: true);
        runner.RecordCurrentWorld("Siren");

        runner.RecordProbe("Siren", MarketAcquisitionTestPlans.ReadyCandidatePlan(10, 1_000));
        runner.RecordPurchaseAudit(
            lineId: "batch-1-line-1",
            itemName: "Fire Shard",
            worldName: "Siren",
            listingId: "listing-fire",
            retainerId: "retainer-fire",
            quantity: 10,
            totalGil: 1_000,
            result: "Purchased");
        runner.RecordWorldPurchaseBatchComplete("Siren", 10, 1_000);

        runner.RecordProbe("Siren", MarketAcquisitionTestPlans.ReadyCandidatePlan(20, 2_000));
        runner.RecordPurchaseAudit(
            lineId: "batch-1-line-2",
            itemName: "Lightning Shard",
            worldName: "Siren",
            listingId: "listing-lightning",
            retainerId: "retainer-lightning",
            quantity: 20,
            totalGil: 2_000,
            result: "Purchased");
        runner.RecordWorldPurchaseBatchComplete("Siren", 20, 2_000);

        var result = await runner.VerifyLatestWorldFreshnessAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Completed", runner.State);
        Assert.Equal(["listing-fire"], calls.Single(call => call.ItemId == 2).ListingIds);
        Assert.Equal(["listing-lightning"], calls.Single(call => call.ItemId == 4).ListingIds);
        var text = ReadLog(runner.LastDiagnosticFilePath!);
        Assert.Contains("universalis-freshness", text, StringComparison.Ordinal);
        Assert.Contains("itemId: 2", text, StringComparison.Ordinal);
        Assert.Contains("listing-fire", text, StringComparison.Ordinal);
        Assert.Contains("itemId: 4", text, StringComparison.Ordinal);
        Assert.Contains("listing-lightning", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyLatestWorldFreshnessAsync_SummarizesUnconfirmedAndUnavailableWarnings()
    {
        var directory = CreateTempDirectory();
        using var runner = new MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunner(
            directory,
            (_, itemId, _, _, _) =>
            {
                var result = itemId == 2
                    ? MarketMafioso.MarketAcquisition.UniversalisFreshnessResult.Unconfirmed("stale")
                    : MarketMafioso.MarketAcquisition.UniversalisFreshnessResult.Unavailable("HTTP 503");
                return Task.FromResult(result);
            });
        runner.Start(MarketAcquisitionTestPlans.MultiLineSingleWorld(), enableDiagnostics: true);
        runner.RecordCurrentWorld("Siren");

        runner.RecordProbe("Siren", MarketAcquisitionTestPlans.ReadyCandidatePlan(10, 1_000));
        runner.RecordPurchaseAudit(
            lineId: "batch-1-line-1",
            itemName: "Fire Shard",
            worldName: "Siren",
            listingId: "listing-fire",
            retainerId: "retainer-fire",
            quantity: 10,
            totalGil: 1_000,
            result: "Purchased");
        runner.RecordWorldPurchaseBatchComplete("Siren", 10, 1_000);

        runner.RecordProbe("Siren", MarketAcquisitionTestPlans.ReadyCandidatePlan(20, 2_000));
        runner.RecordPurchaseAudit(
            lineId: "batch-1-line-2",
            itemName: "Lightning Shard",
            worldName: "Siren",
            listingId: "listing-lightning",
            retainerId: "retainer-lightning",
            quantity: 20,
            totalGil: 2_000,
            result: "Purchased");
        runner.RecordWorldPurchaseBatchComplete("Siren", 20, 2_000);

        var result = await runner.VerifyLatestWorldFreshnessAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, runner.LastRunDiagnosticSummary.FreshnessConfirmedCount);
        Assert.Equal(1, runner.LastRunDiagnosticSummary.FreshnessUnconfirmedCount);
        Assert.Equal(1, runner.LastRunDiagnosticSummary.FreshnessUnavailableCount);
        Assert.Equal(2, runner.LastRunDiagnosticSummary.Warnings.Count);
        Assert.Contains(runner.LastRunDiagnosticSummary.Warnings, warning => warning.Contains("Fire Shard", StringComparison.Ordinal));
        Assert.Contains(runner.LastRunDiagnosticSummary.Warnings, warning => warning.Contains("Lightning Shard", StringComparison.Ordinal));

        var text = ReadLog(runner.LastDiagnosticFilePath!);
        Assert.Contains("universalis-freshness-warning", text, StringComparison.Ordinal);
        Assert.Contains("status: Unconfirmed", text, StringComparison.Ordinal);
        Assert.Contains("status: Unavailable", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutePendingTravelCommand_SendsCurrentStopCommand()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));
        string? command = null;

        var result = runner.ExecutePendingTravelCommand(value =>
        {
            command = value;
            return true;
        });

        Assert.True(result.Success);
        Assert.Equal("/li Maduin mb", command);
        Assert.Equal("TravelCommandSent", runner.ActiveStop?.Status);
    }

    [Fact]
    public void PreparePendingStopForCurrentWorld_MarksArrivedWithoutSendingCommand()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));

        var result = runner.PreparePendingStopForCurrentWorld(
            currentWorldIsValid: true,
            currentWorld: "Maduin",
            _ => throw new InvalidOperationException("Should not execute Lifestream when already on the stop world."));

        Assert.True(result.Success);
        Assert.Equal("Arrived", runner.ActiveStop?.Status);
        Assert.Contains("Arrived on Maduin", runner.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteMarketBoardTravelCommand_SendsLocalMarketBoardCommandOnce()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));
        runner.RecordCurrentWorld("Maduin");
        List<string> commands = [];

        var first = runner.ExecuteMarketBoardTravelCommand(value =>
        {
            commands.Add(value);
            return true;
        });
        var second = runner.ExecuteMarketBoardTravelCommand(value =>
        {
            commands.Add(value);
            return true;
        });

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(["/li mb"], commands);
        Assert.Equal("Arrived", runner.ActiveStop?.Status);
        Assert.Contains("Waiting for Lifestream", runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExecuteMarketBoardTravelCommand_ReportsUnhandledCommand()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));
        runner.RecordCurrentWorld("Maduin");

        var result = runner.ExecuteMarketBoardTravelCommand(_ => false);

        Assert.False(result.Success);
        Assert.Equal("Arrived", runner.ActiveStop?.Status);
        Assert.Contains("not handled", runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreparePendingStopForCurrentWorld_SendsCommandWhenCurrentWorldDiffers()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));
        string? command = null;

        var result = runner.PreparePendingStopForCurrentWorld(
            currentWorldIsValid: true,
            currentWorld: "Zalera",
            value =>
            {
                command = value;
                return true;
            });

        Assert.True(result.Success);
        Assert.Equal("/li Maduin mb", command);
        Assert.Equal("TravelCommandSent", runner.ActiveStop?.Status);
    }

    [Fact]
    public void Pause_PreventsProgressionUntilResume()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));

        runner.Pause();
        var pausedResult = runner.ExecutePendingTravelCommand(_ => throw new InvalidOperationException("Should not execute."));

        Assert.False(pausedResult.Success);
        Assert.Equal("Paused", runner.State);
        Assert.Equal("Pending", runner.ActiveStop?.Status);

        runner.Resume();
        var resumedResult = runner.ExecutePendingTravelCommand(_ => true);

        Assert.True(resumedResult.Success);
        Assert.Equal("Running", runner.State);
        Assert.Equal("TravelCommandSent", runner.ActiveStop?.Status);
    }

    [Fact]
    public void Stop_MarksRouteStopped()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));

        var result = runner.Stop();

        Assert.True(result.Success);
        Assert.Equal("Stopped", runner.State);
        Assert.Null(runner.ActiveStop);
        Assert.Contains("stopped", runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Restart_RebuildsRouteFromPlan()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));
        runner.ExecutePendingTravelCommand(_ => true);

        var result = runner.Restart(CreatePlan("Rafflesia"));

        Assert.True(result.Success);
        Assert.Equal("Running", runner.State);
        Assert.Equal("Rafflesia", runner.ActiveStop?.WorldName);
        Assert.Equal("Pending", runner.ActiveStop?.Status);
    }

    [Fact]
    public void Reset_ReturnsRunnerToIdle()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));

        runner.Reset("No route has started.");

        Assert.Equal("Idle", runner.State);
        Assert.Null(runner.ActiveStop);
        Assert.Empty(runner.Stops);
        Assert.Equal("No route has started.", runner.StatusMessage);
    }

    [Fact]
    public void RecordProbe_WithNoSafeListingsAdvancesStopAndCompletesRoute()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));
        runner.ExecutePendingTravelCommand(_ => true);
        runner.RecordCurrentWorld("Maduin");

        var result = runner.RecordProbe("Maduin", CreateCandidatePlan(status: "NoSafeListings", quantity: 0, gil: 0));

        Assert.True(result.Success);
        Assert.Equal("Completed", runner.State);
        Assert.Null(runner.ActiveStop);
        Assert.Equal("Complete", runner.Stops[0].Status);
        Assert.Equal(0u, runner.Stops[0].WouldBuyQuantity);
    }

    [Fact]
    public void RecordListingReadPending_DoesNotAdvanceArrivedStop()
    {
        var directory = CreateTempDirectory();
        using var runner = new MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunner(directory);
        runner.Start(CreateMultiItemWorldPlan("Maduin"), enableDiagnostics: true);
        runner.ExecutePendingTravelCommand(_ => true);
        runner.RecordCurrentWorld("Maduin");

        var result = runner.RecordListingReadPending(
            "Maduin",
            new MarketMafioso.Automation.MarketBoard.MarketBoardReadResult
            {
                Status = "ListingCacheSwitching",
                Message = "Market board listing cache is still switching.",
                ReadState = MarketMafioso.Automation.MarketBoard.MarketBoardListingReadState.SwitchingItem,
                ItemId = 7017,
                WorldName = "Maduin",
                RawItemIdMismatchCounts = new Dictionary<uint, int>
                {
                    [5066] = 2,
                },
            });

        Assert.True(result.Success);
        Assert.Equal("Running", runner.State);
        Assert.Equal("Arrived", runner.ActiveStop?.Status);
        Assert.Equal("Maduin", runner.ActiveStop?.WorldName);

        var text = ReadLog(runner.LastDiagnosticFilePath!);
        Assert.Contains("listing-read-pending", text, StringComparison.Ordinal);
        Assert.Contains("readState: SwitchingItem", text, StringComparison.Ordinal);
        Assert.Contains("rawItemIdMismatchCounts: 5066=2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordListingReadPending_FailsRouteAfterFreshnessTimeout()
    {
        using var runner = CreateRunner();
        runner.Start(CreateMultiItemWorldPlan("Maduin"));
        runner.ExecutePendingTravelCommand(_ => true);
        runner.RecordCurrentWorld("Maduin");
        var read = new MarketMafioso.Automation.MarketBoard.MarketBoardReadResult
        {
            Status = "ListingCacheSwitching",
            Message = "Market board listing cache is still switching.",
            ReadState = MarketMafioso.Automation.MarketBoard.MarketBoardListingReadState.SwitchingItem,
            ItemId = 7017,
            WorldName = "Maduin",
            RawItemIdMismatchCounts = new Dictionary<uint, int>
            {
                [5066] = 2,
            },
        };
        var startedAt = new DateTimeOffset(2026, 6, 30, 2, 30, 0, TimeSpan.Zero);

        _ = runner.RecordListingReadPending("Maduin", read, startedAt);
        var result = runner.RecordListingReadPending("Maduin", read, startedAt.AddSeconds(16));

        Assert.False(result.Success);
        Assert.Equal("Failed", runner.State);
        Assert.Contains("did not become fresh", runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Varnish", runner.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordProbe_WithSafeListingsStartsPurchasing()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));
        runner.ExecutePendingTravelCommand(_ => true);
        runner.RecordCurrentWorld("Maduin");

        var result = runner.RecordProbe("Maduin", CreateCandidatePlan(status: "Ready", quantity: 10, gil: 100));

        Assert.True(result.Success);
        Assert.Equal("Running", runner.State);
        Assert.Equal("Purchasing", runner.ActiveStop?.Status);
        Assert.Equal(10u, runner.Stops[0].WouldBuyQuantity);
        Assert.Contains("Purchasing", runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordWorldPurchaseBatchComplete_AdvancesStopAndRequiresMarketBoardCloseBeforeTravel()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Rafflesia", "Zalera"));
        runner.RecordCurrentWorld("Rafflesia");
        runner.RecordProbe("Rafflesia", CreateCandidatePlan(status: "Ready", quantity: 10, gil: 100));

        var result = runner.RecordWorldPurchaseBatchComplete("Rafflesia", purchasedQuantity: 10, spentGil: 100);

        Assert.True(result.Success);
        Assert.Equal("Pending", runner.ActiveStop?.Status);
        Assert.Equal("Complete", runner.Stops[0].Status);
        Assert.True(runner.MarketBoardCloseRequiredBeforeTravel);
        Assert.Equal(10u, runner.Stops[0].PurchasedQuantity);
        Assert.Equal(100u, runner.Stops[0].SpentGil);
    }

    [Fact]
    public void RecordWorldPurchaseBatchComplete_AdvancesSameWorldItemWithoutRequiringTravelClose()
    {
        using var runner = CreateRunner();
        runner.Start(CreateMultiItemWorldPlan("Maduin", "Rafflesia"));
        runner.RecordCurrentWorld("Maduin");
        runner.RecordProbe("Maduin", CreateCandidatePlan(status: "Ready", quantity: 10, gil: 100));

        var result = runner.RecordWorldPurchaseBatchComplete("Maduin", purchasedQuantity: 10, spentGil: 100);

        Assert.True(result.Success);
        Assert.Equal("Running", runner.State);
        Assert.Equal("Maduin", runner.ActiveStop?.WorldName);
        Assert.Equal("Arrived", runner.ActiveStop?.Status);
        Assert.Equal("line-2", runner.ActiveStop?.ActiveItemSubtask?.LineId);
        Assert.False(runner.MarketBoardCloseRequiredBeforeTravel);
    }

    [Fact]
    public void RecordWorldPurchaseBatchComplete_WritesWorldCompletionSummaryAfterFinalLine()
    {
        using var runner = CreateRunner();
        runner.Start(CreateMultiItemWorldPlan("Maduin"), enableDiagnostics: true);
        runner.RecordCurrentWorld("Maduin");
        runner.RecordProbe("Maduin", CreateCandidatePlan(status: "Ready", quantity: 10, gil: 100));

        var firstLine = runner.RecordWorldPurchaseBatchComplete("Maduin", purchasedQuantity: 10, spentGil: 100);
        var secondLine = runner.RecordProbe("Maduin", CreateCandidatePlan(status: "NoSafeListings", quantity: 0, gil: 0));

        Assert.True(firstLine.Success);
        Assert.True(secondLine.Success);
        Assert.NotNull(runner.LatestWorldCompletionSummary);
        Assert.Equal("Maduin", runner.LatestWorldCompletionSummary!.WorldName);
        Assert.Equal("Dynamis", runner.LatestWorldCompletionSummary.DataCenter);
        Assert.Equal(10u, runner.LatestWorldCompletionSummary.PurchasedQuantity);
        Assert.Equal(100u, runner.LatestWorldCompletionSummary.SpentGil);
        Assert.Equal(1, runner.LatestWorldCompletionSummary.CompletedLineCount);
        Assert.Equal(1, runner.LatestWorldCompletionSummary.SkippedLineCount);

        var text = ReadLog(runner.LastDiagnosticFilePath!);
        Assert.Contains("world-summary", text, StringComparison.Ordinal);
        Assert.Contains("completedLineCount: 1", text, StringComparison.Ordinal);
        Assert.Contains("skippedLineCount: 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordProbe_WritesOriginalSubtaskAndLiveObservationDiagnostics()
    {
        using var runner = CreateRunner();
        runner.Start(CreateMultiItemWorldPlan("Maduin"), enableDiagnostics: true);
        runner.RecordCurrentWorld("Maduin");

        var result = runner.RecordProbe(
            "Maduin",
            CreateObservedCandidatePlan(
                status: "NoSafeListings",
                quantity: 0,
                gil: 0,
                rows:
                [
                    CreateLiveCandidateRow(decision: "Skipped", reason: "AboveThreshold", quantity: 5, unitPrice: 200),
                ]));

        Assert.True(result.Success);
        Assert.Equal("line-2", runner.ActiveStop?.ActiveItemSubtask?.LineId);
        var text = ReadLog(runner.LastDiagnosticFilePath!);
        Assert.Contains("probe-result", text, StringComparison.Ordinal);
        Assert.Contains("itemId: 7017", text, StringComparison.Ordinal);
        Assert.Contains("itemName: Varnish", text, StringComparison.Ordinal);
        Assert.Contains("subtaskSource: Planned", text, StringComparison.Ordinal);
        Assert.Contains("observedQuantity: 5", text, StringComparison.Ordinal);
        Assert.Contains("observedGil: 1000", text, StringComparison.Ordinal);
        Assert.Contains("skipReasons: AboveThreshold=1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordProbe_RequiresMarketBoardCloseBeforeNextTravel()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Rafflesia", "Zalera"));
        runner.RecordCurrentWorld("Rafflesia");

        var probe = runner.RecordProbe("Rafflesia", CreateCandidatePlan(status: "NoSafeListings", quantity: 0, gil: 0));
        var blocked = runner.ExecutePendingTravelCommand(_ => throw new InvalidOperationException("Travel should wait for market board close."));

        Assert.True(probe.Success);
        Assert.True(blocked.Success);
        Assert.True(runner.MarketBoardCloseRequiredBeforeTravel);
        Assert.Equal("Pending", runner.ActiveStop?.Status);
        Assert.Contains("market board", blocked.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordMarketBoardClosedBeforeTravel_AllowsNextTravel()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Rafflesia", "Zalera"));
        runner.RecordCurrentWorld("Rafflesia");
        runner.RecordProbe("Rafflesia", CreateCandidatePlan(status: "NoSafeListings", quantity: 0, gil: 0));
        string? command = null;

        var closed = runner.RecordMarketBoardClosedBeforeTravel();
        var travel = runner.ExecutePendingTravelCommand(value =>
        {
            command = value;
            return true;
        });

        Assert.True(closed.Success);
        Assert.True(travel.Success);
        Assert.False(runner.MarketBoardCloseRequiredBeforeTravel);
        Assert.Equal("/li Zalera mb", command);
        Assert.Equal("TravelCommandSent", runner.ActiveStop?.Status);
    }

    [Fact]
    public void RecordSearchResult_DoesNotMarkSubmittedWhenModeWasOnlyReset()
    {
        var directory = CreateTempDirectory();
        using var runner = new MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunner(directory);
        runner.Start(CreatePlan("Maduin"), enableDiagnostics: true);
        runner.RecordCurrentWorld("Maduin");

        var result = runner.RecordSearchResult(new MarketMafioso.Automation.MarketBoard.MarketBoardItemSearchResult
        {
            Status = "ModeReset",
            Message = "Resetting market board item search mode before submitting.",
            Details = new Dictionary<string, string?>
            {
                ["mode"] = "Wishlist",
            },
        });

        Assert.True(result.Success);
        Assert.False(runner.SearchSubmitted);
        var text = ReadLog(runner.LastDiagnosticFilePath!);
        Assert.Contains("mode: Wishlist", text, StringComparison.Ordinal);
        Assert.Contains("searchSubmitted: False", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordSearchResult_DoesNotMarkSubmittedWhenTextSearchWasOnlySent()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));
        runner.RecordCurrentWorld("Maduin");

        var result = runner.RecordSearchResult(new MarketMafioso.Automation.MarketBoard.MarketBoardItemSearchResult
        {
            Status = "SearchSent",
            Message = "Searching market board for Varnish (7017).",
        });

        Assert.True(result.Success);
        Assert.False(runner.SearchSubmitted);
        Assert.Contains("Waiting", runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordSearchResult_DoesNotMarkSubmittedWhenItemOpenWasOnlySent()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));
        runner.RecordCurrentWorld("Maduin");
        var startedAt = DateTimeOffset.UnixEpoch;

        var result = runner.RecordSearchResult(new MarketMafioso.Automation.MarketBoard.MarketBoardItemSearchResult
        {
            Status = "ItemOpenSent",
            Message = "Opening market board listings for Varnish (7017).",
        }, startedAt);

        Assert.True(result.Success);
        Assert.False(runner.SearchSubmitted);
        Assert.Contains("Waiting", runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordSearchResult_FailsWhenItemSearchAutomationExceedsWatchdog()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));
        runner.RecordCurrentWorld("Maduin");
        var startedAt = DateTimeOffset.UnixEpoch;

        var first = runner.RecordSearchResult(new MarketMafioso.Automation.MarketBoard.MarketBoardItemSearchResult
        {
            Status = "SearchSent",
            Message = "Searching market board for Varnish (7017).",
        }, startedAt);

        var timedOut = runner.RecordSearchResult(new MarketMafioso.Automation.MarketBoard.MarketBoardItemSearchResult
        {
            Status = "SearchSent",
            Message = "Searching market board for Varnish (7017).",
        }, startedAt.AddSeconds(16));

        Assert.True(first.Success);
        Assert.False(timedOut.Success);
        Assert.Equal("Failed", runner.State);
        Assert.False(runner.SearchSubmitted);
        Assert.Contains("timed out", runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordSearchResult_LogsAutomationSnapshotWhenSearchTimesOut()
    {
        var directory = CreateTempDirectory();
        using var runner = new MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunner(directory);
        runner.Start(CreatePlan("Maduin"), enableDiagnostics: true);
        runner.RecordCurrentWorld("Maduin");
        var startedAt = DateTimeOffset.UnixEpoch;

        runner.RecordSearchResult(new MarketMafioso.Automation.MarketBoard.MarketBoardItemSearchResult
        {
            Status = "SearchSent",
            Message = "Searching market board for Varnish (7017).",
            Details = new Dictionary<string, string?>
            {
                ["searchSource"] = "AutofocusedTextInputRewrite",
                ["searchButtonEnabledAfterCallbacks"] = false.ToString(),
            },
        }, startedAt);

        runner.RecordSearchResult(new MarketMafioso.Automation.MarketBoard.MarketBoardItemSearchResult
        {
            Status = "SearchSent",
            Message = "Searching market board for Varnish (7017).",
            Details = new Dictionary<string, string?>
            {
                ["searchSource"] = "AutofocusedTextInputRewrite",
                ["searchButtonEnabledAfterCallbacks"] = false.ToString(),
            },
        }, startedAt.AddSeconds(16));

        var text = ReadLog(runner.LastDiagnosticFilePath!);
        Assert.Contains("automation-snapshot", text, StringComparison.Ordinal);
        Assert.Contains("step: SearchItem", text, StringComparison.Ordinal);
        Assert.Contains("phase: TimedOut", text, StringComparison.Ordinal);
        Assert.Contains("expected: ItemSearchResultReady", text, StringComparison.Ordinal);
        Assert.Contains("observed: SearchSent", text, StringComparison.Ordinal);
        Assert.Contains("outcome: Fatal", text, StringComparison.Ordinal);
        Assert.Contains("nextAction: CaptureInputState", text, StringComparison.Ordinal);
        Assert.Contains("searchSource: AutofocusedTextInputRewrite", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordSearchResult_MarksSubmittedOnlyWhenListingsAreReady()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));
        runner.RecordCurrentWorld("Maduin");

        var result = runner.RecordSearchResult(new MarketMafioso.Automation.MarketBoard.MarketBoardItemSearchResult
        {
            Status = "ListingsReady",
            Message = "Market board listings are open for Varnish (7017).",
        });

        Assert.True(result.Success);
        Assert.True(runner.SearchSubmitted);
    }

    [Fact]
    public void RecordInputCapture_CreatesDiagnosticsWhenRouteIsNotRunning()
    {
        using var runner = CreateRunner();

        var result = runner.RecordInputCapture("before-purchase-click", new MarketMafioso.Automation.MarketBoard.MarketBoardInputCapture
        {
            Status = "Captured",
            Message = "Captured current market board UI/input state.",
            Details = new Dictionary<string, string?>
            {
                ["itemSearchVisible"] = true.ToString(),
                ["selectYesnoVisible"] = false.ToString(),
                ["focusedNode"] = "AtkComponentTextInput#12",
            },
        });

        Assert.True(result.Success);
        Assert.NotNull(runner.LastDiagnosticFilePath);
        Assert.EndsWith(".log", runner.LastDiagnosticFilePath!, StringComparison.Ordinal);
        Assert.Contains("input-capture-", Path.GetFileName(runner.LastDiagnosticFilePath!), StringComparison.Ordinal);
        var text = ReadLog(runner.LastDiagnosticFilePath!);
        Assert.Contains("input-capture", text, StringComparison.Ordinal);
        Assert.Contains("label: before-purchase-click", text, StringComparison.Ordinal);
        Assert.Contains("itemSearchVisible: True", text, StringComparison.Ordinal);
        Assert.Contains("selectYesnoVisible: False", text, StringComparison.Ordinal);
        Assert.Contains("focusedNode: AtkComponentTextInput#12", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordAutomationSnapshot_WritesSnapshotToActiveRouteDiagnostics()
    {
        var directory = CreateTempDirectory();
        using var runner = new MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunner(directory);
        runner.Start(CreatePlan("Maduin"), enableDiagnostics: true);

        var result = runner.RecordAutomationSnapshot(
            MarketMafioso.Automation.MarketBoard.MarketBoardAutomationSnapshot.Create(
                "BuyListing",
                "AfterConfirmation",
                "ListingRemoved",
                "MarketBoardNotOpen",
                MarketMafioso.Automation.MarketBoard.MarketBoardAutomationOutcome.ExpectedAlternate,
                "TreatListingAsRemoved"));

        Assert.True(result.Success);
        var text = ReadLog(runner.LastDiagnosticFilePath!);
        Assert.Contains("automation-snapshot", text, StringComparison.Ordinal);
        Assert.Contains("step: BuyListing", text, StringComparison.Ordinal);
        Assert.Contains("outcome: ExpectedAlternate", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalizeInputCaptureLog_ClosesStandaloneCaptureDiagnostics()
    {
        using var runner = CreateRunner();
        runner.RecordInputCapture("before-purchase-click", new MarketMafioso.Automation.MarketBoard.MarketBoardInputCapture
        {
            Status = "Captured",
            Message = "Captured current market board UI/input state.",
        });

        var result = runner.FinalizeInputCaptureLog();

        Assert.True(result.Success);
        Assert.False(runner.CanFinalizeInputCaptureLog);
        Assert.Contains("finalized", runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
        var text = ReadLog(runner.LastDiagnosticFilePath!);
        Assert.Contains("input-capture-finalized", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalizeInputCaptureLog_DoesNotCloseRouteDiagnostics()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"), enableDiagnostics: true);
        runner.RecordInputCapture("during-route", new MarketMafioso.Automation.MarketBoard.MarketBoardInputCapture
        {
            Status = "Captured",
            Message = "Captured current market board UI/input state.",
        });

        var result = runner.FinalizeInputCaptureLog();

        Assert.False(result.Success);
        Assert.False(runner.CanFinalizeInputCaptureLog);
        Assert.Contains("route diagnostics", runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
        runner.ExecutePendingTravelCommand(_ => true);
        var text = ReadLog(runner.LastDiagnosticFilePath!);
        Assert.Contains("travel-command", text, StringComparison.Ordinal);
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunner CreateRunner() =>
        new(CreateTempDirectory());

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionPlan CreatePlan(params string[] worlds) =>
        new()
        {
            RequestId = "request-1",
            Status = "Ready",
            WorldMode = "Recommended",
            ItemId = 7017,
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

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionPlan CreateMultiItemWorldPlan(params string[] worlds) =>
        CreatePlan(worlds) with
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
                            ItemId = 7017,
                            ItemName = "Varnish",
                            WorldName = world,
                            PlannedQuantity = 10,
                            PlannedGil = 1_000,
                        },
                        new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldItemSubtask
                        {
                            LineId = "line-2",
                            LineOrdinal = 1,
                            ItemId = 5064,
                            ItemName = "Silver Ingot",
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

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan CreateObservedCandidatePlan(
        string status,
        uint quantity,
        uint gil,
        IReadOnlyList<MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidateRow> rows) =>
        new()
        {
            Status = status,
            Message = "Live candidate result.",
            ReadableListingCount = rows.Count,
            ReportedListingCount = rows.Count,
            ListingCapacity = Math.Max(rows.Count, 10),
            RequestedQuantity = 999,
            WouldBuyQuantity = quantity,
            WouldSpendGil = gil,
            Rows = rows,
        };

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidateRow CreateLiveCandidateRow(
        string decision,
        string reason,
        uint quantity,
        uint unitPrice) =>
        new()
        {
            Decision = decision,
            Reason = reason,
            Message = "Test row.",
            LiveListing = new MarketMafioso.Automation.MarketBoard.MarketBoardLiveListing
            {
                ItemId = 2,
                WorldName = "Maduin",
                ListingId = Guid.NewGuid().ToString("N"),
                RetainerName = "Test Retainer",
                Quantity = quantity,
                UnitPrice = unitPrice,
            },
        };

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MarketMafiosoTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string ReadLog(string filePath)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

