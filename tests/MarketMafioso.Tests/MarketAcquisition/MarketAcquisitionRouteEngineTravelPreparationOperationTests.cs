using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngineTravelPreparationOperationTests
{
    [Fact]
    public void ArchivedTravelUiBlock_RemainsBoundedAndCancelsOnRecordedStop()
    {
        var package = LoadArchivedTravelBlock();
        var blocked = Assert.Single(package.Events, routeEvent => routeEvent.EventName == "travel-ui-blocked");
        var repeated = Assert.Single(package.Events, routeEvent => routeEvent.EventName == "repeat");
        var stopped = Assert.Single(package.Events, routeEvent => routeEvent.EventName == "stopped");
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Siren";
        harness.Ui.TravelPreflightCanSend = false;
        harness.Ui.TravelPreflightBlockingAddons = blocked.Details["blockingAddons"].Split(", ");
        harness.Clock.UtcNow = blocked.RecordedAtUtc;
        harness.Clock.MonotonicMilliseconds = blocked.ElapsedMilliseconds;
        harness.Engine.Start(
            MarketAcquisitionRouteEngineTestData.Plan("Coeurl"),
            MarketAcquisitionRouteEngineTestData.AcceptedClaim(),
            enableDiagnostics: false,
            includeOpportunisticChecks: true);

        harness.Engine.TickRoute(isRequestBusy: false);
        harness.Clock.UtcNow = repeated.RecordedAtUtc;
        harness.Clock.MonotonicMilliseconds = repeated.ElapsedMilliseconds;
        harness.Engine.TickRoute(isRequestBusy: false);
        harness.Clock.UtcNow = stopped.RecordedAtUtc;
        harness.Clock.MonotonicMilliseconds = stopped.ElapsedMilliseconds;
        harness.Engine.Stop();

        var snapshot = harness.Engine.CreateSnapshot();
        Assert.Empty(harness.Ui.Commands);
        Assert.Equal(2, harness.Ui.TravelPreflightCallCount);
        Assert.Null(snapshot.ActiveOperation);
        Assert.Equal(MarketAcquisitionRouteOperationKind.TravelPreparation, snapshot.LastOperation?.Kind);
        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Cancelled, snapshot.LastOperation?.Disposition);
    }

    [Fact]
    public void ClearPreflight_CompletesPreparationThenSendsOneTravelCommand()
    {
        using var harness = PrepareTravel("Siren", "Coeurl");

        harness.Engine.TickRoute(isRequestBusy: false);

        var snapshot = harness.Engine.CreateSnapshot();
        Assert.Equal(["/li Coeurl mb"], harness.Ui.Commands);
        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Succeeded, snapshot.LastOperation?.Disposition);
        Assert.Null(snapshot.ActiveOperation);
    }

    [Fact]
    public void BlockedPreflight_CanClearBeforeDeadlineAndSendOneCommand()
    {
        using var harness = PrepareTravel("Siren", "Coeurl");
        harness.Ui.TravelPreflightCanSend = false;
        harness.Engine.TickRoute(isRequestBusy: false);
        Assert.Empty(harness.Ui.Commands);

        Advance(harness, TimeSpan.FromSeconds(1));
        harness.Ui.TravelPreflightCanSend = true;
        harness.Engine.TickRoute(isRequestBusy: false);

        Assert.Equal(["/li Coeurl mb"], harness.Ui.Commands);
        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Succeeded, harness.Engine.CreateSnapshot().LastOperation?.Disposition);
    }

    [Fact]
    public void BlockedPreflight_TimesOutBeforeAdditionalUiOrCommandIo()
    {
        using var harness = PrepareTravel("Siren", "Coeurl");
        harness.Ui.TravelPreflightCanSend = false;
        harness.Engine.TickRoute(isRequestBusy: false);
        Assert.Equal(1, harness.Ui.TravelPreflightCallCount);

        Advance(harness, TimeSpan.FromSeconds(30));
        harness.Engine.TickRoute(isRequestBusy: true);

        var snapshot = harness.Engine.CreateSnapshot();
        Assert.Equal("Failed", harness.Runner.State);
        Assert.Equal(1, harness.Ui.TravelPreflightCallCount);
        Assert.Empty(harness.Ui.Commands);
        Assert.Equal(MarketAcquisitionRouteOperationPhase.TimedOut, snapshot.LastOperation?.Phase);
    }

    [Fact]
    public void SameWorldArrival_DoesNotCreateTravelPreparationOperation()
    {
        using var harness = PrepareTravel("Coeurl", "Coeurl");

        harness.Engine.TickRoute(isRequestBusy: false);

        Assert.Empty(harness.Ui.Commands);
        Assert.Equal(0, harness.Ui.TravelPreflightCallCount);
        Assert.Null(harness.Engine.CreateSnapshot().LastOperation);
        Assert.Equal("Arrived", harness.Runner.ActiveStop?.Status);
    }

    [Fact]
    public void RejectedTravelCommand_FailsRouteAfterPreparationEvidence()
    {
        using var harness = PrepareTravel("Siren", "Coeurl");
        harness.Ui.ProcessCommandSucceeds = false;

        harness.Engine.TickRoute(isRequestBusy: false);

        Assert.Equal("Failed", harness.Runner.State);
        Assert.Single(harness.Ui.Commands);
        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Succeeded, harness.Engine.CreateSnapshot().LastOperation?.Disposition);
        Assert.Contains("not handled", harness.Runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CurrentWorldUnavailableFromStart_TimesOutWithoutTravelIo()
    {
        using var harness = PrepareTravel("Siren", "Coeurl");
        harness.Context.IsCurrentWorldAvailable = false;
        harness.Engine.TickRoute(isRequestBusy: false);
        Assert.Equal(MarketAcquisitionRouteOperationKind.TravelPreparation, harness.Engine.CreateSnapshot().ActiveOperation?.Kind);

        Advance(harness, TimeSpan.FromSeconds(30));
        harness.Engine.TickRoute(isRequestBusy: true);

        Assert.Equal("Failed", harness.Runner.State);
        Assert.Empty(harness.Ui.Commands);
        Assert.Equal(0, harness.Ui.TravelPreflightCallCount);
        Assert.Equal(0, harness.Ui.CloseMarketBoardCallCount);
        Assert.Equal(MarketAcquisitionRouteOperationPhase.TimedOut, harness.Engine.CreateSnapshot().LastOperation?.Phase);
    }

    private static MarketAcquisitionRouteEngineHarness PrepareTravel(string currentWorld, string targetWorld)
    {
        var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = currentWorld;
        harness.Engine.Start(
            MarketAcquisitionRouteEngineTestData.Plan(targetWorld),
            MarketAcquisitionRouteEngineTestData.AcceptedClaim(),
            enableDiagnostics: false,
            includeOpportunisticChecks: true);
        return harness;
    }

    private static MarketAcquisitionRouteDiagnosticPackage LoadArchivedTravelBlock()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "MarketAcquisition",
            "legacy-travel-ui-blocked");
        return MarketAcquisitionRouteDiagnosticPackageReader.Read(directory);
    }

    private static void Advance(MarketAcquisitionRouteEngineHarness harness, TimeSpan elapsed)
    {
        harness.Clock.UtcNow = harness.Clock.UtcNow.Add(elapsed);
        harness.Clock.MonotonicMilliseconds += (long)elapsed.TotalMilliseconds;
    }
}
