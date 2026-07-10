using System;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngine
{
    private readonly MarketAcquisitionRouteRunner runner;
    private readonly IMarketAcquisitionRouteContext context;
    private readonly IMarketAcquisitionRouteUiAutomation uiAutomation;
    private readonly IMarketAcquisitionMarketBoardIo marketBoard;
    private readonly IMarketAcquisitionPurchaseIo purchase;
    private readonly IMarketAcquisitionRouteReporter reporter;
    private readonly IMarketAcquisitionRouteEvidenceRecorder evidence;
    private readonly IMarketAcquisitionRouteClock clock;
    private readonly MarketBoardListingReadAccumulator listingReadAccumulator = new();
    private readonly MarketBoardAutomationController purchaseAutomation = new();
    private readonly MarketAcquisitionRouteEngineState state = new();
    private MarketAcquisitionClaimView? claimedRequest;

    public MarketAcquisitionRouteEngine(
        MarketAcquisitionRouteRunner runner,
        IMarketAcquisitionRouteContext context,
        IMarketAcquisitionRouteUiAutomation uiAutomation,
        IMarketAcquisitionMarketBoardIo marketBoard,
        IMarketAcquisitionPurchaseIo purchase,
        IMarketAcquisitionRouteReporter reporter,
        IMarketAcquisitionRouteEvidenceRecorder evidence,
        IMarketAcquisitionRouteClock clock)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.uiAutomation = uiAutomation ?? throw new ArgumentNullException(nameof(uiAutomation));
        this.marketBoard = marketBoard ?? throw new ArgumentNullException(nameof(marketBoard));
        this.purchase = purchase ?? throw new ArgumentNullException(nameof(purchase));
        this.reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        this.evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public bool IsRouteActive =>
        runner.IsRunning ||
        runner.IsPaused ||
        state.ProbeRunning ||
        purchaseAutomation.PurchaseSession?.IsActive == true;

    public MarketAcquisitionRouteEngineSnapshot CreateSnapshot() => new()
    {
        StatusMessage = runner.StatusMessage,
        VisibleAcquisitionStatus = state.AcquisitionStatus,
        IsRouteActive = IsRouteActive,
        IsProbeRunning = state.ProbeRunning,
        MarketBoardReadResult = state.MarketBoardReadResult,
        MarketBoardReconciliation = state.MarketBoardReconciliation,
        LiveCandidatePlan = state.LiveCandidatePlan,
        PurchaseSession = purchaseAutomation.PurchaseSession,
        LastPurchaseResult = purchaseAutomation.LastPurchaseResult,
        ActiveWorldPurchasedQuantity = state.ActiveWorldPurchasedQuantity,
        ActiveWorldSpentGil = state.ActiveWorldSpentGil,
        ActiveLinePurchasedQuantity = state.ActiveLinePurchasedQuantity,
        ActiveLineSpentGil = state.ActiveLineSpentGil,
        LastDiagnosticFilePath = runner.LastDiagnosticFilePath,
        LastObservedListingsCsvPath = runner.LastObservedListingsCsvPath,
        LastPurchaseRecordsCsvPath = runner.LastPurchaseRecordsCsvPath,
        LastRunSummary = runner.LastRunSummary,
        LatestWorldCompletionSummary = runner.LatestWorldCompletionSummary,
    };

    public MarketAcquisitionRouteActionResult Start(
        MarketAcquisitionPlan plan,
        MarketAcquisitionClaimView claimed,
        bool enableDiagnostics,
        bool includeOpportunisticChecks)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(claimed);

        claimedRequest = claimed;
        ClearExecutionState();
        var result = runner.Start(plan, enableDiagnostics, includeOpportunisticChecks);
        state.AcquisitionStatus = result.Message;
        return result;
    }

    public MarketAcquisitionRouteActionResult Pause() => UpdateStatus(runner.Pause());

    public MarketAcquisitionRouteActionResult Resume() => UpdateStatus(runner.Resume());

    public MarketAcquisitionRouteActionResult Stop()
    {
        var result = runner.Stop();
        listingReadAccumulator.Clear();
        purchaseAutomation.Clear();
        return UpdateStatus(result);
    }

    public MarketAcquisitionRouteActionResult Restart(MarketAcquisitionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ClearExecutionState();
        return UpdateStatus(runner.Restart(plan));
    }

    public MarketAcquisitionRouteActionResult ReprepareAndRestart(MarketAcquisitionPlan plan, DateTimeOffset preparedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ClearExecutionState();
        return UpdateStatus(runner.ReprepareAndRestart(plan, preparedAtUtc));
    }

    public void Reset(string status)
    {
        runner.Reset(status);
        ClearExecutionState();
        state.AcquisitionStatus = status;
        claimedRequest = null;
    }

    private void ClearExecutionState()
    {
        state.ResetRouteExecutionState();
        listingReadAccumulator.Clear();
        purchaseAutomation.Clear();
    }

    private MarketAcquisitionRouteActionResult UpdateStatus(MarketAcquisitionRouteActionResult result)
    {
        state.AcquisitionStatus = result.Message;
        return result;
    }
}
