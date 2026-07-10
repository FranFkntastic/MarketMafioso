using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionRouteEngineSnapshot
{
    public string StatusMessage { get; init; } = string.Empty;
    public string VisibleAcquisitionStatus { get; init; } = string.Empty;
    public bool IsRouteActive { get; init; }
    public bool IsProbeRunning { get; init; }
    public MarketBoardReadResult? MarketBoardReadResult { get; init; }
    public MarketBoardListingReconciliation? MarketBoardReconciliation { get; init; }
    public MarketAcquisitionLiveCandidatePlan? LiveCandidatePlan { get; init; }
    public MarketBoardPurchaseSession? PurchaseSession { get; init; }
    public MarketBoardPurchaseResult? LastPurchaseResult { get; init; }
    public uint ActiveWorldPurchasedQuantity { get; init; }
    public uint ActiveWorldSpentGil { get; init; }
    public uint ActiveLinePurchasedQuantity { get; init; }
    public uint ActiveLineSpentGil { get; init; }
    public string? LastDiagnosticFilePath { get; init; }
    public string? LastObservedListingsCsvPath { get; init; }
    public string? LastPurchaseRecordsCsvPath { get; init; }
    public MarketAcquisitionRouteRunSummary? LastRunSummary { get; init; }
    public MarketAcquisitionWorldCompletionSummary? LatestWorldCompletionSummary { get; init; }
}
