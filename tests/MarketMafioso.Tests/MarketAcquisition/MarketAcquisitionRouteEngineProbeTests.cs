using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngineProbeTests
{
    [Fact]
    public void ProbePreparedPlan_WhenRouteIsIdlePublishesLiveCandidateSnapshot()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Maduin";
        harness.MarketBoard.Reads.Enqueue(MarketAcquisitionRouteEngineTestData.ReadWithSafeListing("Maduin"));

        harness.Engine.ProbePreparedPlan(
            MarketAcquisitionRouteEngineTestData.Plan("Maduin"),
            MarketAcquisitionRouteEngineTestData.AcceptedClaim());

        var snapshot = harness.Engine.CreateSnapshot();
        Assert.False(snapshot.IsRouteActive);
        Assert.False(snapshot.IsProbeRunning);
        Assert.Equal("Ready", snapshot.MarketBoardReadResult?.Status);
        Assert.Equal("Ready", snapshot.LiveCandidatePlan?.Status);
        Assert.True(SpinWait.SpinUntil(() => harness.Reporter.MarketObservationReports.Count == 1, TimeSpan.FromSeconds(2)));
        var observation = Assert.Single(harness.Reporter.MarketObservationReports);
        Assert.Equal("Maduin", observation.WorldName);
        Assert.Equal(7017u, observation.ItemId);
    }

    [Fact]
    public void ProbePreparedPlan_WhenRouteIsActiveRejectsSharedStateMutation()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        var plan = MarketAcquisitionRouteEngineTestData.Plan("Maduin");
        var claim = MarketAcquisitionRouteEngineTestData.AcceptedClaim();
        harness.Engine.Start(plan, claim, false, false);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            harness.Engine.ProbePreparedPlan(plan, claim));

        Assert.Contains("cannot run", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Running", harness.Runner.State);
    }

    [Fact]
    public void ProbePreparedPlan_IncompleteCoverageCanContinueWhileRouteIsIdle()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Maduin";
        harness.MarketBoard.Reads.Enqueue(new MarketBoardReadResult
        {
            Status = "Ready",
            ReadState = MarketBoardListingReadState.FreshPartial,
            ItemId = 7017,
            WorldName = "Maduin",
            ReportedListingCount = 2,
            ListingCapacity = 1,
            IsAtListingCapacity = true,
            IsListingCountTruncated = true,
            Listings =
            [
                new MarketBoardLiveListing
                {
                    ItemId = 7017,
                    WorldName = "Maduin",
                    ListingId = "expensive",
                    RetainerId = "retainer-expensive",
                    Quantity = 1,
                    UnitPrice = 2_000,
                },
            ],
        });

        harness.Engine.ProbePreparedPlan(
            MarketAcquisitionRouteEngineTestData.Plan("Maduin"),
            MarketAcquisitionRouteEngineTestData.AcceptedClaim());

        Assert.NotNull(harness.Ui.LastRequestedScrollRow);
        Assert.Equal("Idle", harness.Runner.State);
        Assert.Contains("Reading deeper", harness.Engine.CreateSnapshot().VisibleAcquisitionStatus);
    }

    [Fact]
    public void ProbePreparedPlan_ScrollFailurePreservesSpecificStatus()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Maduin";
        harness.Ui.ScrollSucceeds = false;
        harness.Ui.ScrollMessage = "Market board rows could not be scrolled.";
        harness.MarketBoard.Reads.Enqueue(new MarketBoardReadResult
        {
            Status = "Ready",
            ReadState = MarketBoardListingReadState.FreshPartial,
            ItemId = 7017,
            WorldName = "Maduin",
            ReportedListingCount = 2,
            ListingCapacity = 1,
            IsAtListingCapacity = true,
            IsListingCountTruncated = true,
            Listings =
            [
                new MarketBoardLiveListing
                {
                    ItemId = 7017,
                    WorldName = "Maduin",
                    ListingId = "expensive",
                    RetainerId = "retainer-expensive",
                    Quantity = 1,
                    UnitPrice = 2_000,
                },
            ],
        });

        harness.Engine.ProbePreparedPlan(
            MarketAcquisitionRouteEngineTestData.Plan("Maduin"),
            MarketAcquisitionRouteEngineTestData.AcceptedClaim());

        Assert.Equal("Market board rows could not be scrolled.", harness.Engine.CreateSnapshot().VisibleAcquisitionStatus);
    }

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

    [Fact]
    public void Probe_FreshReadPublishesWorldObservationWithCoverageMetadata()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Maduin";
        harness.Engine.Start(
            MarketAcquisitionRouteEngineTestData.Plan("Maduin"),
            MarketAcquisitionRouteEngineTestData.AcceptedClaim(),
            false,
            false);
        harness.Runner.RecordCurrentWorld("Maduin");
        harness.MarketBoard.Reads.Enqueue(MarketAcquisitionRouteEngineTestData.ReadWithSafeListing("Maduin"));

        harness.Engine.ProbeLiveMarketBoard();

        Assert.True(SpinWait.SpinUntil(() => harness.Reporter.MarketObservationReports.Count == 1, TimeSpan.FromSeconds(2)));
        var observation = Assert.Single(harness.Reporter.MarketObservationReports);
        Assert.Equal("Maduin", observation.WorldName);
        Assert.Equal(7017u, observation.ItemId);
        Assert.Equal(MarketBoardListingReadState.FreshComplete, observation.ReadResult.ReadState);
        Assert.NotEmpty(observation.ReadResult.Listings);
    }

    [Fact]
    public void EvidenceRefresh_FreshSafeListingsPublishesWithoutRouteProgressOrPurchasing()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Maduin";
        var claim = MarketAcquisitionRouteEngineTestData.AcceptedClaim();
        harness.Engine.StartEvidenceRefresh(
            MarketAcquisitionRouteEngineTestData.Plan("Maduin"),
            claim,
            enableDiagnostics: false);
        harness.Runner.RecordCurrentWorld("Maduin");
        harness.MarketBoard.Reads.Enqueue(MarketAcquisitionRouteEngineTestData.ReadWithSafeListing("Maduin"));

        harness.Engine.ProbeLiveMarketBoard();

        Assert.True(SpinWait.SpinUntil(() => harness.Reporter.MarketObservationReports.Count == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal("Completed", harness.Runner.State);
        Assert.Empty(harness.Reporter.RouteProgressReports);
        Assert.Equal(0u, harness.Runner.Stops[0].PurchasedQuantity);
    }
}
