using System;
using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngine
{
    private static readonly TimeSpan RouteMonitorInterval = TimeSpan.FromMilliseconds(500);
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

    public MarketAcquisitionRouteEngineTickResult TickRoute(bool isRequestBusy)
    {
        if (isRequestBusy || state.ProbeRunning || !runner.IsRunning)
            return MarketAcquisitionRouteEngineTickResult.Idle();

        var now = clock.UtcNow;
        if (now < state.NextRouteMonitorUtc)
            return MarketAcquisitionRouteEngineTickResult.Idle("Waiting for next route monitor tick.");

        state.NextRouteMonitorUtc = now.Add(RouteMonitorInterval);
        try
        {
            var activeStop = runner.ActiveStop;
            if (activeStop == null)
                return MarketAcquisitionRouteEngineTickResult.Idle("Route has no active stop.");

            if (string.Equals(activeStop.Status, "Pending", StringComparison.OrdinalIgnoreCase))
                HandlePendingStop(activeStop);
            else if (!context.IsCurrentWorldAvailable)
                UpdateStatus(runner.RecordCurrentWorldUnavailable());
            else
                HandleWorldScopedStop(activeStop, context.GetCurrentWorldName());

            if (runner.ActiveStop is { Status: "Purchasing" } &&
                purchaseAutomation.PurchaseSession?.IsActive != true)
            {
                BeginNextWorldPurchase();
            }

            ReportRouteProgress();
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, state.NextRouteMonitorUtc);
        }
        catch (Exception ex)
        {
            var result = runner.FailRoute($"Unable to monitor guided route. {ex.Message}", ex);
            state.AcquisitionStatus = result.Message;
            ReportRouteProgress();
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, state.NextRouteMonitorUtc);
        }
    }

    public void ProbeLiveMarketBoard()
    {
        try
        {
            ProbeLiveMarketBoardCore();
            var activeStop = runner.ActiveStop;
            if (activeStop is { Status: "Arrived" } &&
                !string.Equals(state.MarketBoardReadResult?.Status, "Ready", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(state.MarketBoardReadResult?.Status, "NoSearchItem", StringComparison.OrdinalIgnoreCase))
                    runner.ClearSearchSubmission("Market board results did not expose a searched item id.");

                UpdateStatus(runner.BeginProbe(
                    $"Arrived on {activeStop.WorldName}; waiting for live listings. {state.MarketBoardReadResult?.Message ?? "Market board read has not completed."}"));
            }
        }
        catch (Exception ex)
        {
            var activeStop = runner.ActiveStop;
            var activeLine = claimedRequest == null ? null : GetActiveRouteLine(claimedRequest);
            var itemLabel = activeLine == null ? "active item" : FormatItem(activeLine);
            var worldLabel = activeStop?.WorldName ??
                             (context.IsCurrentWorldAvailable ? context.GetCurrentWorldName() : "unknown world");
            var message = $"Live market board probe failed for {itemLabel} on {worldLabel}. {ex.Message}";
            runner.FailRoute(message, ex);
            state.AcquisitionStatus = message;
        }
        finally
        {
            if (runner.ActiveStop?.Status != "Arrived")
                runner.ClearSearchSubmission("Route advanced or stopped before the next live listing read.");

            state.ProbeRunning = false;
            state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
            ReportRouteProgress();
        }
    }

    private void HandlePendingStop(MarketAcquisitionGuidedRouteStop activeStop)
    {
        if (runner.MarketBoardCloseRequiredBeforeTravel)
        {
            if (uiAutomation.TryCloseMarketBoardWindows())
            {
                state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(250);
                return;
            }

            UpdateStatus(runner.RecordMarketBoardClosedBeforeTravel());
            state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
            return;
        }

        var currentWorld = context.IsCurrentWorldAvailable ? context.GetCurrentWorldName() : null;
        if (context.IsCurrentWorldAvailable &&
            !activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase) &&
            !EnsureRouteTravelUiIsClear())
        {
            state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
            return;
        }

        UpdateStatus(runner.PreparePendingStopForCurrentWorld(
            context.IsCurrentWorldAvailable,
            currentWorld,
            uiAutomation.ProcessCommand));
        state.NextRouteMonitorUtc = clock.UtcNow.AddSeconds(2);
    }

    private bool EnsureRouteTravelUiIsClear()
    {
        var preflight = uiAutomation.CheckTravelPreflight();
        if (preflight.CanSendCommand)
            return true;

        UpdateStatus(runner.RecordTravelBlockedByUi(preflight));
        return false;
    }

    private void HandleWorldScopedStop(MarketAcquisitionGuidedRouteStop activeStop, string currentWorld)
    {
        if (!activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
        {
            UpdateStatus(runner.RecordCurrentWorld(currentWorld));
            return;
        }

        if (string.Equals(activeStop.Status, "TravelCommandSent", StringComparison.OrdinalIgnoreCase))
            UpdateStatus(runner.RecordCurrentWorld(currentWorld));

        if (runner.ActiveStop?.Status == "Arrived")
            HandleArrivedStop(currentWorld);
    }

    private void HandleArrivedStop(string currentWorld)
    {
        var claimed = claimedRequest ?? throw new InvalidOperationException("No dashboard request is accepted.");
        if (!runner.SearchSubmitted)
        {
            var approachResult = marketBoard.OpenOrApproachMarketBoard();
            UpdateStatus(runner.RecordMarketBoardApproach(approachResult));
            if (approachResult.MarketBoardTravelNeeded)
            {
                if (!EnsureRouteTravelUiIsClear())
                {
                    state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
                    return;
                }

                UpdateStatus(runner.ExecuteMarketBoardTravelCommand(uiAutomation.ProcessCommand));
                state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(750);
                return;
            }

            if (!approachResult.ReadyToSearch)
            {
                state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(250);
                return;
            }

            var activeLine = GetActiveRouteLine(claimed);
            var searchResult = marketBoard.SearchItem(activeLine.ItemId, activeLine.ItemName);
            UpdateStatus(runner.RecordSearchResult(searchResult, clock.UtcNow));
            if (!searchResult.ReadyForListings)
            {
                state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
                return;
            }
        }

        state.NextRouteMonitorUtc = clock.UtcNow;
        UpdateStatus(runner.BeginProbe($"Arrived on {currentWorld}. Reading live listings for {FormatItem(GetActiveRouteLine(claimed))}."));
        state.ProbeRunning = true;
        ProbeLiveMarketBoard();
    }

    private void ProbeLiveMarketBoardCore()
    {
        var plan = runner.ActivePlan ??
                   throw new InvalidOperationException("Prepare a live candidate plan before probing live market board listings.");
        var claimed = claimedRequest ?? throw new InvalidOperationException("No dashboard request is accepted.");
        var activeLine = GetActiveRouteLine(claimed);
        var activeSubtask = runner.ActiveStop?.ActiveItemSubtask;
        var currentWorld = context.GetCurrentWorldName();

        state.MarketBoardReconciliation = null;
        state.LiveCandidatePlan = null;
        state.MarketBoardReadResult = listingReadAccumulator.Merge(marketBoard.ReadCurrentListings(currentWorld));

        var canBuildLiveCandidatePlan = state.MarketBoardReadResult.Status is "Ready" or "NoListings";
        state.MarketBoardReconciliation = state.MarketBoardReadResult.Status == "Ready"
            ? activeSubtask == null
                ? MarketBoardListingReconciler.Reconcile(plan, currentWorld, state.MarketBoardReadResult.ItemId, state.MarketBoardReadResult.Listings)
                : MarketBoardListingReconciler.Reconcile(plan, activeSubtask, currentWorld, state.MarketBoardReadResult.ItemId, state.MarketBoardReadResult.Listings)
            : null;
        if (!state.MarketBoardReadResult.IsFresh)
        {
            if (runner.IsRunning)
                runner.RecordListingReadPending(currentWorld, state.MarketBoardReadResult);

            state.AcquisitionStatus = state.MarketBoardReadResult.Message;
            return;
        }

        var totals = ResolveActiveRouteLinePurchaseTotals(activeSubtask);
        state.LiveCandidatePlan = canBuildLiveCandidatePlan
            ? activeSubtask == null
                ? MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(activeLine, plan, currentWorld, state.MarketBoardReadResult, totals.PurchasedQuantity, totals.SpentGil)
                : MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(activeLine, plan, activeSubtask, currentWorld, state.MarketBoardReadResult, totals.PurchasedQuantity, totals.SpentGil)
            : null;
        if (state.LiveCandidatePlan != null && TryContinueVisibleListingRead(currentWorld, state.MarketBoardReadResult, state.LiveCandidatePlan))
            return;

        var probeResult = runner.IsRunning && runner.ActiveStop is { Status: "Arrived" } && state.LiveCandidatePlan != null
            ? runner.RecordProbe(currentWorld, state.LiveCandidatePlan)
            : null;
        if (probeResult?.Success == true && state.LiveCandidatePlan != null)
            evidence.RecordProbeVisit(currentWorld, activeLine, activeSubtask, state.LiveCandidatePlan, claimed.Id, state.ProgressNonce);

        state.AcquisitionStatus = state.MarketBoardReconciliation == null
            ? state.MarketBoardReadResult.Message
            : $"Live listing reconciliation {state.MarketBoardReconciliation.Status}; live candidates {state.LiveCandidatePlan?.Status ?? "Unavailable"}.";
        if (probeResult != null)
            state.AcquisitionStatus = $"{state.AcquisitionStatus} Route: {probeResult.Message}";
    }

    private bool TryContinueVisibleListingRead(
        string currentWorld,
        MarketBoardReadResult readResult,
        MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        if (!runner.IsRunning || !listingReadAccumulator.TryBeginContinuation(readResult, candidatePlan, out var continuation))
            return false;

        if (!uiAutomation.TryScrollMarketBoardListingsToRow(continuation.RequestedRow, out var scrollMessage))
        {
            state.AcquisitionStatus = scrollMessage;
            runner.RecordListingReadPending(currentWorld, readResult with { Message = $"{continuation.Message} {scrollMessage}" });
            return false;
        }

        var message = $"{continuation.Message} {scrollMessage}";
        var pending = runner.RecordListingReadPending(currentWorld, readResult with { Message = message });
        state.AcquisitionStatus = pending.Success ? message : pending.Message;
        state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
        return true;
    }

    private MarketAcquisitionRequestView GetActiveRouteLine(MarketAcquisitionRequestView claimed)
    {
        var activeSubtask = runner.ActiveStop?.ActiveItemSubtask;
        return activeSubtask == null
            ? claimed
            : claimed with
            {
                ItemId = activeSubtask.ItemId,
                ItemName = activeSubtask.ItemName,
                QuantityMode = activeSubtask.QuantityMode,
                Quantity = activeSubtask.RequestedQuantity,
                HqPolicy = activeSubtask.HqPolicy,
                MaxUnitPrice = activeSubtask.MaxUnitPrice,
                MaxTotalGil = activeSubtask.GilCap,
            };
    }

    private MarketAcquisitionRouteLinePurchaseTotals ResolveActiveRouteLinePurchaseTotals(MarketAcquisitionWorldItemSubtask? activeSubtask)
    {
        if (activeSubtask == null)
            return new MarketAcquisitionRouteLinePurchaseTotals(state.ActiveWorldPurchasedQuantity, state.ActiveWorldSpentGil);

        var completed = runner.GetLinePurchaseTotals(activeSubtask.LineId);
        return new MarketAcquisitionRouteLinePurchaseTotals(
            checked(completed.PurchasedQuantity + state.ActiveLinePurchasedQuantity),
            checked(completed.SpentGil + state.ActiveLineSpentGil));
    }

    private static string FormatItem(MarketAcquisitionRequestView line) =>
        string.IsNullOrWhiteSpace(line.ItemName) ? line.ItemId.ToString() : $"{line.ItemName} ({line.ItemId})";

    private void BeginNextWorldPurchase()
    {
        // Purchase selection moves in the next slice; this guard keeps the tick extraction behaviorally inert until then.
    }

    private void ReportRouteProgress()
    {
        // Reporting is moved after route execution is fully engine-owned.
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
