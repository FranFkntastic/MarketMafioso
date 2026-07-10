using System;
using System.Threading;
using System.Threading.Tasks;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Travel;

namespace MarketMafioso.MarketAcquisition;

public interface IMarketAcquisitionRouteClock
{
    DateTimeOffset UtcNow { get; }
    long MonotonicMilliseconds { get; }
}

public sealed class SystemMarketAcquisitionRouteClock : IMarketAcquisitionRouteClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public long MonotonicMilliseconds => Environment.TickCount64;
}

public interface IMarketAcquisitionRouteContext
{
    bool IsCurrentWorldAvailable { get; }
    string GetCurrentWorldName();
    bool TryGetCharacterScope(out string characterName, out string homeWorld);
}

public interface IMarketAcquisitionRouteUiAutomation
{
    bool ProcessCommand(string command);
    bool TryCloseMarketBoardWindows();
    AutomationTravelPreflightResult CheckTravelPreflight();
    bool TryScrollMarketBoardListingsToRow(int requestedRow, out string message);
}

public interface IMarketAcquisitionMarketBoardIo
{
    MarketBoardApproachResult OpenOrApproachMarketBoard();
    MarketBoardItemSearchResult SearchItem(uint itemId, string? itemName);
    MarketBoardReadResult ReadCurrentListings(string currentWorld);
    MarketBoardInputCapture CaptureInputState();
}

public interface IMarketAcquisitionPurchaseIo
{
    MarketBoardPurchaseResult ExecuteFirstCandidate(
        MarketAcquisitionLiveCandidatePlan candidatePlan,
        MarketBoardReadResult freshRead);

    MarketBoardPurchaseResult TryConfirmPendingPurchase(MarketBoardPurchaseCandidate candidate);
}

public interface IMarketAcquisitionRouteReporter
{
    bool CanReport { get; }
    Task<MarketAcquisitionRouteProgressReportOutcome> ReportRouteProgressAsync(
        MarketAcquisitionRouteProgressReport report,
        CancellationToken cancellationToken);
    Task ReportPurchaseAuditAsync(MarketAcquisitionPurchaseAuditReport report, CancellationToken cancellationToken);
    Task ReportLineProgressAsync(MarketAcquisitionLineProgressReport report, CancellationToken cancellationToken);
}

public interface IMarketAcquisitionRouteEvidenceRecorder
{
    void RecordProbeVisit(
        string currentWorld,
        MarketAcquisitionRequestView activeLine,
        MarketAcquisitionWorldItemSubtask? activeSubtask,
        MarketAcquisitionLiveCandidatePlan candidatePlan,
        string? requestId,
        string routeRunId);

    void RecordPurchaseVisit(
        MarketBoardPurchaseCandidate candidate,
        MarketAcquisitionWorldItemSubtask activeSubtask,
        string worldName,
        string? requestId,
        string routeRunId);
}

public interface IMarketAcquisitionRouteCallbackDispatcher
{
    Task DispatchAsync(Action callback);
}

public sealed record MarketAcquisitionRouteProgressReport(
    string RequestId,
    string ClaimToken,
    string RouteState,
    string AttemptId,
    long Sequence,
    string? RouteStopId,
    string? ActiveWorld,
    string Phase,
    string Message);

public sealed record MarketAcquisitionRouteProgressReportOutcome(
    string Action,
    MarketAcquisitionRequestView Request);

public sealed record MarketAcquisitionPurchaseAuditReport(
    string RequestId,
    string ClaimToken,
    string AttemptId,
    long Sequence,
    string LineId,
    string WorldName,
    uint ItemId,
    string? ItemName,
    MarketBoardPurchaseCandidate Candidate,
    string Message);

public sealed record MarketAcquisitionLineProgressReport(
    string RequestId,
    string ClaimToken,
    string AttemptId,
    long Sequence,
    string LineId,
    string? ItemName,
    string Status,
    uint PurchasedQuantity,
    uint SpentGil,
    string Message,
    string? Reason);
