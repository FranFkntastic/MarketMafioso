using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngineSearchOperationTests
{
    [Fact]
    public void ArchivedRealSearchRun_ReplaysPendingProgressionThenSuccess()
    {
        var observations = LoadArchivedSearchObservations();
        using var harness = PrepareAtPendingStop("Maduin");
        var startedAtUtc = harness.Clock.UtcNow;
        var startedAtMonotonic = harness.Clock.MonotonicMilliseconds;

        foreach (var observation in observations)
        {
            harness.Clock.UtcNow = startedAtUtc.AddMilliseconds(observation.ElapsedMilliseconds);
            harness.Clock.MonotonicMilliseconds = startedAtMonotonic + observation.ElapsedMilliseconds;
            harness.MarketBoard.Searches.Enqueue(new MarketBoardItemSearchResult
            {
                Status = observation.Details["status"],
                Message = observation.Message,
            });

            harness.Engine.TickRoute(isRequestBusy: false);
        }

        var snapshot = harness.Engine.CreateSnapshot();
        Assert.Equal(3, harness.MarketBoard.SearchRequests.Count);
        Assert.Null(snapshot.ActiveOperation);
        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Succeeded, snapshot.LastOperation?.Disposition);
        Assert.Equal(MarketAcquisitionRouteOperationKind.ItemSearch, snapshot.LastOperation?.Kind);
    }

    [Fact]
    public void SearchSubmitFailed_FailsRouteAndDoesNotSubmitAgain()
    {
        using var harness = PrepareAtPendingStop("Maduin");
        AdvanceToSearch(harness);
        harness.MarketBoard.Searches.Enqueue(new MarketBoardItemSearchResult
        {
            Status = "SearchSubmitFailed",
            Message = "Could not submit market board item search for Varnish (7017); see diagnostics.",
        });

        harness.Engine.TickRoute(isRequestBusy: false);
        Advance(harness, TimeSpan.FromSeconds(1));
        harness.Engine.TickRoute(isRequestBusy: false);

        var snapshot = harness.Engine.CreateSnapshot();
        Assert.Equal("Failed", harness.Runner.State);
        Assert.Single(harness.MarketBoard.SearchRequests);
        Assert.Null(snapshot.ActiveOperation);
        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Failed, snapshot.LastOperation?.Disposition);
        Assert.Contains("Could not submit", harness.Runner.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingSearch_TimesOutThroughBoundedOperation()
    {
        using var harness = PrepareAtPendingStop("Maduin");
        AdvanceToSearch(harness);
        harness.MarketBoard.Searches.Enqueue(PendingSearch());
        harness.Engine.TickRoute(isRequestBusy: false);

        Advance(harness, TimeSpan.FromSeconds(15));
        harness.MarketBoard.Searches.Enqueue(PendingSearch());
        harness.Engine.TickRoute(isRequestBusy: false);

        var snapshot = harness.Engine.CreateSnapshot();
        Assert.Equal("Failed", harness.Runner.State);
        Assert.Single(harness.MarketBoard.SearchRequests);
        Assert.Equal(MarketAcquisitionRouteOperationPhase.TimedOut, snapshot.LastOperation?.Phase);
        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Failed, snapshot.LastOperation?.Disposition);
        Assert.Contains("timed out", harness.Runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownSearchStatus_FailsClosed()
    {
        using var harness = PrepareAtPendingStop("Maduin");
        AdvanceToSearch(harness);
        harness.MarketBoard.Searches.Enqueue(new MarketBoardItemSearchResult
        {
            Status = "UnexpectedNewStatus",
            Message = "Unexpected status.",
        });

        harness.Engine.TickRoute(isRequestBusy: false);

        Assert.Equal("Failed", harness.Runner.State);
        Assert.Contains("unsupported terminal status", harness.Runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Failed, harness.Engine.CreateSnapshot().LastOperation?.Disposition);
    }

    [Theory]
    [InlineData("MarketBoardNotOpen", MarketAcquisitionRouteOperationDisposition.Pending)]
    [InlineData("ModeReset", MarketAcquisitionRouteOperationDisposition.Pending)]
    [InlineData("StaleListingsClosed", MarketAcquisitionRouteOperationDisposition.Pending)]
    [InlineData("SearchSent", MarketAcquisitionRouteOperationDisposition.Pending)]
    [InlineData("ItemSelectionSent", MarketAcquisitionRouteOperationDisposition.Pending)]
    [InlineData("ItemOpenSent", MarketAcquisitionRouteOperationDisposition.Pending)]
    [InlineData("ListingsReady", MarketAcquisitionRouteOperationDisposition.Succeeded)]
    [InlineData("SearchSubmitFailed", MarketAcquisitionRouteOperationDisposition.Failed)]
    [InlineData("UnexpectedNewStatus", MarketAcquisitionRouteOperationDisposition.Failed)]
    public void SearchStatusMapping_IsExhaustiveAndFailsClosed(
        string status,
        MarketAcquisitionRouteOperationDisposition expected)
    {
        var result = new MarketBoardItemSearchResult { Status = status };

        Assert.Equal(expected, MarketAcquisitionRouteEngine.ClassifyItemSearchResult(result));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActiveSearch_TimesOutEvenWhenRequestBusyOrWorldDiverges(bool requestBusy)
    {
        using var harness = PrepareAtPendingStop("Maduin");
        AdvanceToSearch(harness);
        harness.MarketBoard.Searches.Enqueue(PendingSearch());
        harness.Engine.TickRoute(isRequestBusy: false);
        Assert.Single(harness.MarketBoard.SearchRequests);

        Advance(harness, TimeSpan.FromSeconds(15));
        if (!requestBusy)
            harness.Context.CurrentWorld = "Zalera";
        harness.Engine.TickRoute(isRequestBusy: requestBusy);

        Assert.Equal("Failed", harness.Runner.State);
        Assert.Single(harness.MarketBoard.SearchRequests);
        Assert.Equal(MarketAcquisitionRouteOperationPhase.TimedOut, harness.Engine.CreateSnapshot().LastOperation?.Phase);
    }

    private static MarketAcquisitionRouteEngineHarness PrepareAtPendingStop(string worldName)
    {
        var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = worldName;
        harness.Engine.Start(
            MarketAcquisitionRouteEngineTestData.Plan(worldName),
            MarketAcquisitionRouteEngineTestData.AcceptedClaim(),
            enableDiagnostics: false,
            includeOpportunisticChecks: true);
        harness.Engine.TickRoute(isRequestBusy: false);
        return harness;
    }

    private static void AdvanceToSearch(MarketAcquisitionRouteEngineHarness harness) =>
        Advance(harness, TimeSpan.FromSeconds(3));

    private static void Advance(MarketAcquisitionRouteEngineHarness harness, TimeSpan elapsed)
    {
        harness.Clock.UtcNow = harness.Clock.UtcNow.Add(elapsed);
        harness.Clock.MonotonicMilliseconds += (long)elapsed.TotalMilliseconds;
    }

    private static MarketBoardItemSearchResult PendingSearch() => new()
    {
        Status = "SearchSent",
        Message = "Searching market board item list for Varnish (7017).",
    };

    private static IReadOnlyList<MarketAcquisitionRouteDiagnosticEvent> LoadArchivedSearchObservations()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "MarketAcquisition",
            "legacy-item-search-success");
        return MarketAcquisitionRouteDiagnosticPackageReader.Read(directory)
            .Events
            .Where(routeEvent => routeEvent.EventName == "item-search")
            .ToArray();
    }
}
