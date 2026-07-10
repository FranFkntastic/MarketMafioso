using MarketMafioso.MarketAcquisition;
using MarketMafioso.Automation.Travel;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngineWorldTravelOperationTests
{
    [Fact]
    public void AcceptedTravel_CreatesOwnedLeaseAndWaitsForObservedArrival()
    {
        using var harness = PrepareTravel();

        harness.Engine.TickRoute(isRequestBusy: false);

        var snapshot = harness.Engine.CreateSnapshot();
        Assert.Equal(["/li Coeurl mb"], harness.Ui.Commands);
        Assert.Equal(MarketAcquisitionRouteOperationKind.Travel, snapshot.ActiveOperation?.Kind);
        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Pending, snapshot.ActiveOperation?.Disposition);

        harness.MarketBoard.ApproachResult = MarketBoardApproachResult.Wait("Hold after arrival for travel-operation assertion.");
        harness.Context.CurrentWorld = "Coeurl";
        Advance(harness, TimeSpan.FromSeconds(2));
        harness.Engine.TickRoute(isRequestBusy: false);

        Assert.Empty(harness.TravelCleanup.CancelledLeases);
        Assert.Equal(MarketAcquisitionRouteOperationKind.Travel, harness.Engine.CreateSnapshot().LastOperation?.Kind);
        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Succeeded, harness.Engine.CreateSnapshot().LastOperation?.Disposition);
    }

    [Fact]
    public void TravelTimeout_CleansUpOwnedLeaseBeforeFailingRoute()
    {
        using var harness = PrepareTravel();
        harness.Engine.TickRoute(isRequestBusy: false);

        Advance(harness, TimeSpan.FromSeconds(120));
        harness.Engine.TickRoute(isRequestBusy: true);

        Assert.Equal("Failed", harness.Runner.State);
        var lease = Assert.Single(harness.TravelCleanup.CancelledLeases);
        Assert.True(lease.IsOwned);
        Assert.Equal("Coeurl", lease.TargetWorld);
        Assert.Equal(MarketAcquisitionRouteOperationPhase.TimedOut, harness.Engine.CreateSnapshot().LastOperation?.Phase);
    }

    [Fact]
    public void UnsupportedCleanup_FencesRestartUntilArrivalIsReconciled()
    {
        using var harness = PrepareTravel();
        harness.TravelCleanup.Result = new MarketAcquisitionTravelCleanupResult
        {
            Status = MarketAcquisitionTravelCleanupStatus.Unsupported,
            Message = "Lifestream cancellation is not lease-scoped.",
            UnresolvedExternalAutomation = true,
            AdapterCapability = "LeaseScopedCancellationUnavailable",
        };
        harness.Engine.TickRoute(isRequestBusy: false);

        harness.Engine.Stop();
        var blocked = harness.Engine.Start(
            MarketAcquisitionRouteEngineTestData.Plan("Coeurl"),
            MarketAcquisitionRouteEngineTestData.AcceptedClaim(),
            enableDiagnostics: false,
            includeOpportunisticChecks: true);

        Assert.False(blocked.Success);
        Assert.Contains("remains unresolved", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(harness.TravelCleanup.CancelledLeases);

        harness.Context.CurrentWorld = "Coeurl";
        var resumed = harness.Engine.Start(
            MarketAcquisitionRouteEngineTestData.Plan("Coeurl"),
            MarketAcquisitionRouteEngineTestData.AcceptedClaim(),
            enableDiagnostics: false,
            includeOpportunisticChecks: true);

        Assert.True(resumed.Success);
    }

    [Fact]
    public void CleanupAdapterFailure_DoesNotPreventLocalStop()
    {
        using var harness = PrepareTravel();
        harness.Engine.TickRoute(isRequestBusy: false);
        harness.TravelCleanup.ExceptionToThrow = new InvalidOperationException("adapter offline");

        var result = harness.Engine.Stop();

        Assert.True(result.Success);
        Assert.Equal("Stopped", harness.Runner.State);
        Assert.Single(harness.TravelCleanup.CancelledLeases);
    }

    [Fact]
    public void PauseDuringTravel_RequiresRestartInsteadOfResumingAnUnboundedWait()
    {
        using var harness = PrepareTravel();
        harness.Engine.TickRoute(isRequestBusy: false);

        var pause = harness.Engine.Pause();
        var resume = harness.Engine.Resume();

        Assert.True(pause.Success);
        Assert.False(resume.Success);
        Assert.Equal("Paused", harness.Runner.State);
        Assert.Contains("restart the route", resume.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(harness.TravelCleanup.CancelledLeases);
    }

    [Fact]
    public void StopDuringTravel_RecordsCleanupBeforeTerminalDiagnosticEvent()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Siren";
        harness.Engine.Start(
            MarketAcquisitionRouteEngineTestData.Plan("Coeurl"),
            MarketAcquisitionRouteEngineTestData.AcceptedClaim(),
            enableDiagnostics: true,
            includeOpportunisticChecks: true);
        harness.Engine.TickRoute(isRequestBusy: false);

        harness.Engine.Stop();

        var package = MarketAcquisitionRouteDiagnosticPackageReader.Read(
            Path.GetDirectoryName(harness.Runner.LastDiagnosticFilePath!)!);
        var cleanupRequestedIndex = package.Events.ToList().FindIndex(routeEvent =>
            routeEvent.EventName == "route-cleanup" &&
            routeEvent.Details["cleanupStatus"] == "Requested");
        var cleanupResult = Assert.Single(package.Events, routeEvent =>
            routeEvent.EventName == "route-cleanup" &&
            routeEvent.Details["cleanupStatus"] == "Cancelled");
        var stoppedIndex = package.Events.ToList().FindIndex(routeEvent => routeEvent.EventName == "stopped");

        Assert.True(cleanupRequestedIndex >= 0);
        Assert.True(cleanupRequestedIndex < stoppedIndex);
        Assert.Equal("Lifestream", cleanupResult.Details["dependency"]);
        Assert.Equal("Stop", cleanupResult.Details["terminalReason"]);
        Assert.Equal("False", cleanupResult.Details["unresolvedExternalAutomation"]);
    }

    [Fact]
    public void StopAfterRouteOwnedVnavmeshApproach_StopsOnlyTheRecordedLease()
    {
        using var harness = PrepareTravel();
        harness.Engine.TickRoute(isRequestBusy: false);
        harness.Context.CurrentWorld = "Coeurl";
        harness.MarketBoard.ApproachResult = MarketBoardApproachResult.Action(
            MarketBoardApproachActionKind.NavigationStarted,
            "vnavmesh is approaching the market board.");
        Advance(harness, TimeSpan.FromSeconds(2));
        harness.Engine.TickRoute(isRequestBusy: false);

        harness.Engine.Stop();

        var lease = Assert.Single(harness.MarketBoard.StoppedApproaches);
        Assert.Equal("VNavmesh", lease.Dependency);
        Assert.False(string.IsNullOrWhiteSpace(lease.RouteRunId));
    }

    private static MarketAcquisitionRouteEngineHarness PrepareTravel()
    {
        var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Siren";
        harness.Engine.Start(
            MarketAcquisitionRouteEngineTestData.Plan("Coeurl"),
            MarketAcquisitionRouteEngineTestData.AcceptedClaim(),
            enableDiagnostics: false,
            includeOpportunisticChecks: true);
        return harness;
    }

    private static void Advance(MarketAcquisitionRouteEngineHarness harness, TimeSpan elapsed)
    {
        harness.Clock.UtcNow = harness.Clock.UtcNow.Add(elapsed);
        harness.Clock.MonotonicMilliseconds += (long)elapsed.TotalMilliseconds;
    }
}
