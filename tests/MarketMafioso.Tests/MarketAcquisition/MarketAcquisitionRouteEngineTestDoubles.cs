using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Travel;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

internal sealed class FakeRouteClock : IMarketAcquisitionRouteClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-07-10T12:00:00Z");
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
    public bool TravelPreflightCanSend { get; set; } = true;
    public bool ProcessCommand(string command)
    {
        Commands.Add(command);
        return true;
    }

    public bool TryCloseMarketBoardWindows() => true;
    public AutomationTravelPreflightResult CheckTravelPreflight() => new()
    {
        CanSendCommand = TravelPreflightCanSend,
        Message = TravelPreflightCanSend ? "No blocking UI is open." : "Close blocking UI before travel.",
    };

    public bool TryScrollMarketBoardListingsToRow(int requestedRow, out string message)
    {
        message = "Requested deeper listings.";
        return true;
    }
}

internal sealed class FakeMarketBoardIo : IMarketAcquisitionMarketBoardIo
{
    public Queue<MarketBoardReadResult> Reads { get; } = [];
    public Queue<MarketBoardItemSearchResult> Searches { get; } = [];
    public MarketBoardApproachResult ApproachResult { get; set; } = MarketBoardApproachResult.Ready("Market board is ready.");
    public MarketBoardApproachResult OpenOrApproachMarketBoard() => ApproachResult;
    public MarketBoardItemSearchResult SearchItem(uint itemId, string? itemName) => Searches.Count == 0 ? new() { Status = "ListingsReady" } : Searches.Dequeue();
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
    public MarketBoardPurchaseResult ExecuteFirstCandidate(MarketAcquisitionLiveCandidatePlan candidatePlan, MarketBoardReadResult freshRead) => new()
    {
        Status = "NoCandidate",
    };

    public MarketBoardPurchaseResult TryConfirmPendingPurchase(MarketBoardPurchaseCandidate candidate) => new()
    {
        Status = "ConfirmationPending",
        Candidate = candidate,
    };
}

internal sealed class FakeRouteReporter : IMarketAcquisitionRouteReporter
{
    public bool CanReport => true;

    public Task<MarketAcquisitionRouteProgressReportOutcome> ReportRouteProgressAsync(MarketAcquisitionRouteProgressReport report, CancellationToken cancellationToken) =>
        Task.FromResult(new MarketAcquisitionRouteProgressReportOutcome("progress", new MarketAcquisitionRequestView()));

    public Task ReportPurchaseAuditAsync(MarketAcquisitionPurchaseAuditReport report, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ReportLineProgressAsync(MarketAcquisitionLineProgressReport report, CancellationToken cancellationToken) => Task.CompletedTask;
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

internal sealed class MarketAcquisitionRouteEngineHarness : IDisposable
{
    public FakeRouteClock Clock { get; } = new();
    public FakeRouteContext Context { get; } = new();
    public FakeRouteUiAutomation Ui { get; } = new();
    public FakeMarketBoardIo MarketBoard { get; } = new();
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
            MarketBoard,
            new FakePurchaseIo(),
            new FakeRouteReporter(),
            new FakeRouteEvidenceRecorder(),
            Clock);
    }

    public static MarketAcquisitionRouteEngineHarness Create() => new();

    public void Dispose() => Runner.Dispose();
}

internal static class MarketAcquisitionRouteEngineTestData
{
    public static MarketAcquisitionPlan Plan(string worldName) => new()
    {
        RequestId = "request-1",
        Status = "Ready",
        WorldMode = "Recommended",
        Lines = [new MarketAcquisitionPlanLine { LineId = "line-1", ItemId = 7017, ItemName = "Varnish" }],
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
            }],
        }],
    };

    public static MarketAcquisitionClaimView AcceptedClaim() => new()
    {
        Id = "request-1",
        ClaimToken = "claim-token",
        Status = "AcceptedInPlugin",
    };
}
