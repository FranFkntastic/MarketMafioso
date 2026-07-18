using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteEnginePurchaseTests
{
    [Fact]
    public void BeginNextWorldPurchase_DryRunRecordsSimulationWithoutCallingPurchaseOrReporter()
    {
        var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Maduin";
        harness.Engine.Start(
            MarketAcquisitionRouteEngineTestData.Plan("Maduin"),
            MarketAcquisitionRouteEngineTestData.AcceptedClaim(),
            enableDiagnostics: false,
            includeOpportunisticChecks: false,
            executionMode: MarketAcquisitionExecutionMode.DryRun);
        harness.Runner.RecordCurrentWorld("Maduin");
        harness.Runner.RecordProbe("Maduin", MarketAcquisitionRouteEngineTestData.ReadyCandidatePlan());
        harness.MarketBoard.Reads.Enqueue(MarketAcquisitionRouteEngineTestData.ReadWithSafeListing("Maduin"));

        harness.Engine.BeginNextWorldPurchase();

        var snapshot = harness.Engine.CreateSnapshot();
        Assert.Equal(MarketAcquisitionExecutionMode.DryRun, snapshot.ExecutionMode);
        Assert.Equal(0, harness.Purchase.ExecuteCallCount);
        Assert.Equal(0, harness.Purchase.ConfirmCallCount);
        Assert.Empty(harness.Reporter.PurchaseAuditReports);
        Assert.Empty(harness.Reporter.RouteProgressReports);
        Assert.Equal(4u, harness.Runner.LastRunSummary?.PurchasedQuantity);
        Assert.Equal(3200u, harness.Runner.LastRunSummary?.SpentGil);
        Assert.Equal("Completed", harness.Runner.State);
        Assert.Contains("Would purchase", harness.Runner.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Purchased", harness.Runner.StatusMessage, StringComparison.Ordinal);
        Assert.True(harness.Ui.CloseMarketBoardCallCount > 0);
        var packageDirectory = Path.GetDirectoryName(Assert.IsType<string>(harness.Runner.LastDiagnosticFilePath));
        var manifest = File.ReadAllText(Path.Combine(Assert.IsType<string>(packageDirectory), "manifest.json"));
        var purchases = File.ReadAllText(Assert.IsType<string>(harness.Runner.LastPurchaseRecordsCsvPath));
        Assert.Contains("\"packageKind\":\"dry-run\"", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"captureStatus\":\"Complete\"", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DryRunWouldPurchase", purchases, StringComparison.Ordinal);
        harness.Engine.Dispose();
    }

    [Fact]
    public void Restart_DryRunPreservesNonSpendingExecutionMode()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        var plan = MarketAcquisitionRouteEngineTestData.Plan("Maduin");
        var claim = MarketAcquisitionRouteEngineTestData.AcceptedClaim();
        harness.Engine.Start(plan, claim, true, false, executionMode: MarketAcquisitionExecutionMode.DryRun);

        var result = harness.Engine.Restart(plan, claim);

        Assert.True(result.Success);
        Assert.Equal(MarketAcquisitionExecutionMode.DryRun, harness.Engine.CreateSnapshot().ExecutionMode);
        Assert.Equal(0, harness.Purchase.ExecuteCallCount);
        Assert.Empty(harness.Reporter.RouteProgressReports);
    }

    [Fact]
    public void BeginNextWorldPurchase_SelectionSentStartsPurchaseSession()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Maduin";
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), true, false);
        harness.Runner.RecordCurrentWorld("Maduin");
        harness.Runner.RecordProbe("Maduin", MarketAcquisitionRouteEngineTestData.ReadyCandidatePlan());
        harness.MarketBoard.Reads.Enqueue(MarketAcquisitionRouteEngineTestData.ReadWithSafeListing("Maduin"));
        harness.Purchase.PurchaseResults.Enqueue(new MarketBoardPurchaseResult
        {
            Status = "PurchaseSelectionSent",
            Message = "Selection sent.",
            Candidate = MarketAcquisitionRouteEngineTestData.Candidate("Maduin"),
            Diagnostics = new Dictionary<string, string>
            {
                ["adapterStatus"] = "ListingSelected",
                ["promptText"] = "Purchase this item?",
            },
        });

        harness.Engine.BeginNextWorldPurchase();

        Assert.Equal("WaitingForConfirmation", harness.Engine.CreateSnapshot().PurchaseSession?.Status);
        harness.Engine.Stop();
        var diagnostics = File.ReadAllText(Assert.IsType<string>(harness.Runner.LastDiagnosticFilePath));
        Assert.Contains("candidateListingId", diagnostics, StringComparison.Ordinal);
        Assert.Contains("candidateRetainerId", diagnostics, StringComparison.Ordinal);
        Assert.Contains("candidateUnitPrice", diagnostics, StringComparison.Ordinal);
        Assert.Contains("adapterStatus: ListingSelected", diagnostics, StringComparison.Ordinal);
        Assert.Contains("promptText: Purchase this item?", diagnostics, StringComparison.Ordinal);
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

        harness.Engine.MonitorMarketBoardPurchase();

        Assert.Equal(4u, harness.Runner.LastRunSummary?.PurchasedQuantity);
        Assert.Equal(3200u, harness.Runner.LastRunSummary?.SpentGil);
        Assert.Single(harness.Reporter.PurchaseAuditReports);
    }

    [Fact]
    public void MonitorMarketBoardPurchase_UnexpectedConfirmationRecordsFatalDiagnostic()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Maduin";
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), true, false);
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
            Status = "UnexpectedConfirmation",
            Message = "The purchase prompt did not match the guarded listing.",
            Candidate = MarketAcquisitionRouteEngineTestData.Candidate("Maduin"),
        });
        harness.Engine.BeginNextWorldPurchase();
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddSeconds(1);

        harness.Engine.MonitorMarketBoardPurchase();

        Assert.Equal("Failed", harness.Runner.State);
        var diagnostics = File.ReadAllText(Assert.IsType<string>(harness.Runner.LastDiagnosticFilePath));
        Assert.Contains("outcome: Fatal", diagnostics, StringComparison.Ordinal);
        Assert.Contains("nextAction: StopRoute", diagnostics, StringComparison.Ordinal);
    }
}
