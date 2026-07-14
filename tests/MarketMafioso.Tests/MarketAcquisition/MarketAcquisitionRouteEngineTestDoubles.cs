using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Travel;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

internal sealed class FakeRouteClock : IMarketAcquisitionRouteClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-07-10T12:00:00Z");
    public long MonotonicMilliseconds { get; set; } = 1_000;
}

internal sealed class FakeRouteContext : IMarketAcquisitionRouteContext
{
    public bool IsCurrentWorldAvailable { get; set; } = true;
    public string CurrentWorld { get; set; } = "Siren";

    public string GetCurrentWorldName() => IsCurrentWorldAvailable
        ? CurrentWorld
        : throw new InvalidOperationException("Current world is unavailable.");

    public bool TryGetCharacterScope(out string characterName, out string homeWorld)
    {
        characterName = "Tester";
        homeWorld = "Siren";
        return true;
    }
}

internal sealed class FakeRouteUiAutomation : IMarketAcquisitionRouteUiAutomation
{
    public List<string> Commands { get; } = [];
    public bool ProcessCommandSucceeds { get; set; } = true;
    public bool TravelPreflightCanSend { get; set; } = true;
    public IReadOnlyList<string> TravelPreflightBlockingAddons { get; set; } = [];
    public int TravelPreflightCallCount { get; private set; }
    public Queue<bool> CloseMarketBoardResults { get; } = [];
    public int CloseMarketBoardCallCount { get; private set; }
    public bool ScrollSucceeds { get; set; } = true;
    public string ScrollMessage { get; set; } = "Requested deeper listings.";
    public int? LastRequestedScrollRow { get; private set; }
    public bool ProcessCommand(string command)
    {
        Commands.Add(command);
        return ProcessCommandSucceeds;
    }

    public bool TryCloseMarketBoardWindows()
    {
        CloseMarketBoardCallCount++;
        return CloseMarketBoardResults.Count == 0 || CloseMarketBoardResults.Dequeue();
    }

    public AutomationTravelPreflightResult CheckTravelPreflight()
    {
        TravelPreflightCallCount++;
        return new AutomationTravelPreflightResult
        {
            CanSendCommand = TravelPreflightCanSend,
            Message = TravelPreflightCanSend ? "No blocking UI is open." : "Close blocking UI before travel.",
            BlockingAddons = TravelPreflightBlockingAddons,
        };
    }

    public bool TryScrollMarketBoardListingsToRow(int requestedRow, out string message)
    {
        LastRequestedScrollRow = requestedRow;
        message = ScrollMessage;
        return ScrollSucceeds;
    }
}

internal sealed class FakeMarketBoardIo : IMarketAcquisitionMarketBoardIo
{
    public Queue<MarketBoardReadResult> Reads { get; } = [];
    public Queue<MarketBoardItemSearchResult> Searches { get; } = [];
    public List<(uint ItemId, string? ItemName)> SearchRequests { get; } = [];
    public MarketBoardApproachResult ApproachResult { get; set; } = MarketBoardApproachResult.Ready("Market board is ready.");
    public MarketBoardApproachResult OpenOrApproachMarketBoard() => ApproachResult;
    public List<MarketAcquisitionApproachLease> StoppedApproaches { get; } = [];
    public MarketAcquisitionApproachCleanupResult ApproachCleanupResult { get; set; } = new()
    {
        Status = MarketAcquisitionTravelCleanupStatus.Cancelled,
        Message = "Owned vnavmesh approach cancelled.",
        AdapterCapability = "Test",
    };
    public MarketAcquisitionApproachCleanupResult StopOwnedApproach(MarketAcquisitionApproachLease lease)
    {
        StoppedApproaches.Add(lease);
        return ApproachCleanupResult;
    }
    public MarketBoardItemSearchResult SearchItem(uint itemId, string? itemName)
    {
        SearchRequests.Add((itemId, itemName));
        return Searches.Count == 0 ? new() { Status = "ListingsReady" } : Searches.Dequeue();
    }
    public MarketBoardReadResult ReadCurrentListings(string currentWorld) => Reads.Count == 0 ? new()
        {
            Status = "NoListings",
            Message = "No listings.",
            ReadState = MarketBoardListingReadState.FreshComplete,
            WorldName = currentWorld,
        } : Reads.Dequeue();

    public MarketBoardInputCapture CaptureInputState() => new() { Status = "Captured" };
}

internal sealed class FakePurchaseIo : IMarketAcquisitionPurchaseIo
{
    public Queue<MarketBoardPurchaseResult> PurchaseResults { get; } = [];
    public Queue<MarketBoardPurchaseResult> ConfirmationResults { get; } = [];
    public MarketBoardPurchaseResult ExecuteFirstCandidate(MarketAcquisitionLiveCandidatePlan candidatePlan, MarketBoardReadResult freshRead) =>
        PurchaseResults.Count == 0 ? new() { Status = "NoCandidate" } : PurchaseResults.Dequeue();
    public MarketBoardPurchaseResult TryConfirmPendingPurchase(MarketBoardPurchaseCandidate candidate) =>
        ConfirmationResults.Count == 0 ? new() { Status = "ConfirmationPending", Candidate = candidate } : ConfirmationResults.Dequeue();
}

internal sealed class FakeRouteReporter : IMarketAcquisitionRouteReporter
{
    public bool CanReport { get; set; } = true;
    public List<MarketAcquisitionRouteProgressReport> RouteProgressReports { get; } = [];
    public List<MarketAcquisitionPurchaseAuditReport> PurchaseAuditReports { get; } = [];
    public List<MarketAcquisitionMarketObservationReport> MarketObservationReports { get; } = [];

    public Task<MarketAcquisitionRouteProgressReportOutcome> ReportRouteProgressAsync(MarketAcquisitionRouteProgressReport report, CancellationToken cancellationToken)
    {
        RouteProgressReports.Add(report);
        return Task.FromResult(new MarketAcquisitionRouteProgressReportOutcome("progress", new MarketAcquisitionRequestView { Status = "Running" }));
    }

    public Task ReportPurchaseAuditAsync(MarketAcquisitionPurchaseAuditReport report, CancellationToken cancellationToken)
    {
        PurchaseAuditReports.Add(report);
        return Task.CompletedTask;
    }

    public Task ReportLineProgressAsync(MarketAcquisitionLineProgressReport report, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ReportMarketObservationAsync(MarketAcquisitionMarketObservationReport report, CancellationToken cancellationToken)
    {
        MarketObservationReports.Add(report);
        return Task.CompletedTask;
    }
}

internal sealed class FakeRouteEvidenceRecorder : IMarketAcquisitionRouteEvidenceRecorder
{
    public void RecordProbeVisit(string currentWorld, MarketAcquisitionRequestView activeLine, MarketAcquisitionWorldItemSubtask? activeSubtask, MarketAcquisitionLiveCandidatePlan candidatePlan, string? requestId, string routeRunId)
    {
    }

    public void RecordPurchaseVisit(MarketBoardPurchaseCandidate candidate, MarketAcquisitionWorldItemSubtask activeSubtask, string worldName, string? requestId, string routeRunId)
    {
    }
}

internal sealed class ImmediateRouteCallbackDispatcher : IMarketAcquisitionRouteCallbackDispatcher
{
    public Task DispatchAsync(Action callback)
    {
        callback();
        return Task.CompletedTask;
    }
}

internal sealed class MarketAcquisitionRouteEngineHarness : IDisposable
{
    private MarketAcquisitionClaimView? persistedClaim;
    public FakeRouteClock Clock { get; } = new();
    public FakeRouteContext Context { get; } = new();
    public FakeRouteUiAutomation Ui { get; } = new();
    public FakeRouteTravelCleanup TravelCleanup { get; } = new();
    public FakeMarketBoardIo MarketBoard { get; } = new();
    public FakePurchaseIo Purchase { get; } = new();
    public FakeRouteReporter Reporter { get; } = new();
    public MarketAcquisitionRouteRunner Runner { get; }
    public MarketAcquisitionRouteEngine Engine { get; }

    private MarketAcquisitionRouteEngineHarness()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MarketMafiosoRouteEngineTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Runner = new MarketAcquisitionRouteRunner(directory);
        Engine = new MarketAcquisitionRouteEngine(
            Runner,
            Context,
            Ui,
            TravelCleanup,
            MarketBoard,
            Purchase,
            Reporter,
            new FakeRouteEvidenceRecorder(),
            new MarketAcquisitionClaimLifecycleController(
                new Configuration(),
                () => persistedClaim,
                value => persistedClaim = value,
                () => null,
                () => null,
                () => { },
                _ => { },
                () => Runner.StatusMessage,
                () => { }),
            new ImmediateRouteCallbackDispatcher(),
            Clock);
    }

    public static MarketAcquisitionRouteEngineHarness Create() => new();

    public void Dispose() => Engine.Dispose();
}

internal sealed class FakeRouteTravelCleanup : IMarketAcquisitionRouteTravelCleanup
{
    public List<MarketAcquisitionTravelLease> CancelledLeases { get; } = [];
    public MarketAcquisitionTravelCleanupResult Result { get; set; } = new()
    {
        Status = MarketAcquisitionTravelCleanupStatus.Cancelled,
        Message = "Owned Lifestream travel cancelled.",
        AdapterCapability = "Test",
    };
    public Exception? ExceptionToThrow { get; set; }

    public MarketAcquisitionTravelCleanupResult CancelOwnedTravel(MarketAcquisitionTravelLease lease)
    {
        CancelledLeases.Add(lease);
        if (ExceptionToThrow != null)
            throw ExceptionToThrow;

        return Result;
    }
}

internal static class MarketAcquisitionRouteEngineTestData
{
    public static MarketAcquisitionPlan Plan(string worldName) => new()
    {
        RequestId = "request-1",
        Status = "Ready",
        WorldMode = "Recommended",
        Lines = [new MarketAcquisitionPlanLine
        {
            LineId = "line-1",
            ItemId = 7017,
            ItemName = "Varnish",
            QuantityMode = "TargetQuantity",
            RequestedQuantity = 4,
            HqPolicy = "Either",
            MaxUnitPrice = 1000,
            GilCap = 4000,
        }],
        WorldBatches = [new MarketAcquisitionWorldBatch
        {
            WorldName = worldName,
            DataCenter = "Dynamis",
            ItemSubtasks = [new MarketAcquisitionWorldItemSubtask
            {
                LineId = "line-1",
                ItemId = 7017,
                ItemName = "Varnish",
                WorldName = worldName,
                DataCenter = "Dynamis",
                QuantityMode = "TargetQuantity",
                RequestedQuantity = 4,
                HqPolicy = "Either",
                MaxUnitPrice = 1000,
                GilCap = 4000,
            }],
        }],
    };

    public static MarketAcquisitionClaimView AcceptedClaim() => new()
    {
        Id = "request-1",
        ClaimToken = "claim-token",
        Status = "AcceptedInPlugin",
        ItemId = 7017,
        ItemName = "Varnish",
        QuantityMode = "TargetQuantity",
        Quantity = 4,
        HqPolicy = "Either",
        MaxUnitPrice = 1000,
        MaxTotalGil = 4000,
    };

    public static MarketAcquisitionLiveCandidatePlan ReadyCandidatePlan() => new()
    {
        Status = "Ready",
        Message = "Ready.",
        RequestedQuantity = 4,
        WouldBuyQuantity = 4,
        WouldSpendGil = 3200,
    };

    public static MarketBoardPurchaseCandidate Candidate(string worldName) => new()
    {
        ItemId = 7017,
        WorldName = worldName,
        ListingId = "listing-1",
        RetainerId = "retainer-1",
        Quantity = 4,
        UnitPrice = 800,
    };

    public static MarketBoardReadResult ReadWithSafeListing(string worldName) => new()
    {
        Status = "Ready",
        ReadState = MarketBoardListingReadState.FreshComplete,
        ItemId = 7017,
        WorldName = worldName,
        Listings = [new MarketBoardLiveListing
        {
            ItemId = 7017,
            WorldName = worldName,
            ListingId = "listing-1",
            RetainerId = "retainer-1",
            Quantity = 4,
            UnitPrice = 800,
        }],
    };
}
