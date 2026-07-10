using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngine : IDisposable
{
    private static readonly TimeSpan RouteMonitorInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MarketBoardPurchaseConfirmationWatchdog = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MarketBoardPurchaseInitialMonitorDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MarketBoardPurchaseListingRemovalWatchdog = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MarketBoardPurchaseMonitorInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan UniversalisFreshnessVerificationDelay = TimeSpan.FromSeconds(10);
    private readonly MarketAcquisitionRouteRunner runner;
    private readonly IMarketAcquisitionRouteContext context;
    private readonly IMarketAcquisitionRouteUiAutomation uiAutomation;
    private readonly IMarketAcquisitionMarketBoardIo marketBoard;
    private readonly IMarketAcquisitionPurchaseIo purchase;
    private readonly IMarketAcquisitionRouteEvidenceRecorder evidence;
    private readonly MarketAcquisitionRouteReportDispatcher reportDispatcher;
    private readonly IMarketAcquisitionRouteClock clock;
    private readonly MarketBoardListingReadAccumulator listingReadAccumulator = new();
    private readonly MarketBoardAutomationController purchaseAutomation = new();
    private readonly MarketAcquisitionRouteEngineState state = new();
    private CancellationTokenSource freshnessCancellation = new();
    private MarketAcquisitionClaimView? claimedRequest;

    public MarketAcquisitionRouteEngine(
        MarketAcquisitionRouteRunner runner,
        IMarketAcquisitionRouteContext context,
        IMarketAcquisitionRouteUiAutomation uiAutomation,
        IMarketAcquisitionMarketBoardIo marketBoard,
        IMarketAcquisitionPurchaseIo purchase,
        IMarketAcquisitionRouteReporter reporter,
        IMarketAcquisitionRouteEvidenceRecorder evidence,
        MarketAcquisitionClaimLifecycleController claimLifecycle,
        IMarketAcquisitionRouteCallbackDispatcher callbackDispatcher,
        IMarketAcquisitionRouteClock clock)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.uiAutomation = uiAutomation ?? throw new ArgumentNullException(nameof(uiAutomation));
        this.marketBoard = marketBoard ?? throw new ArgumentNullException(nameof(marketBoard));
        this.purchase = purchase ?? throw new ArgumentNullException(nameof(purchase));
        this.evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        reportDispatcher = new MarketAcquisitionRouteReportDispatcher(
            reporter ?? throw new ArgumentNullException(nameof(reporter)),
            claimLifecycle ?? throw new ArgumentNullException(nameof(claimLifecycle)),
            callbackDispatcher ?? throw new ArgumentNullException(nameof(callbackDispatcher)));
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
        IsRunning = runner.IsRunning,
        IsPaused = runner.IsPaused,
        CanRestart = runner.CanRestart,
        CanFinalizeInputCaptureLog = runner.CanFinalizeInputCaptureLog,
        CompletedOrProbedStopCount = runner.CompletedOrProbedStops.Count,
        RouteState = runner.State,
        ActiveStop = runner.ActiveStop,
        Stops = runner.Stops,
        ActivePlan = runner.ActivePlan,
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
        LastRunDiagnosticSummary = runner.LastRunDiagnosticSummary,
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
        reportDispatcher.BeginSession(claimed);
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
        reportDispatcher.ResetSession();
        freshnessCancellation.Cancel();
        return UpdateStatus(result);
    }

    public MarketAcquisitionRouteActionResult Restart(
        MarketAcquisitionPlan plan,
        MarketAcquisitionClaimView claimed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(claimed);
        claimedRequest = claimed;
        ClearExecutionState();
        reportDispatcher.BeginSession(claimed);
        return UpdateStatus(runner.Restart(plan));
    }

    public MarketAcquisitionRouteActionResult ReprepareAndRestart(
        MarketAcquisitionPlan plan,
        DateTimeOffset preparedAtUtc,
        MarketAcquisitionClaimView claimed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(claimed);
        claimedRequest = claimed;
        ClearExecutionState();
        reportDispatcher.BeginSession(claimed);
        return UpdateStatus(runner.ReprepareAndRestart(plan, preparedAtUtc));
    }

    public void Reset(string status)
    {
        runner.Reset(status);
        ClearExecutionState();
        state.AcquisitionStatus = status;
        claimedRequest = null;
    }

    public MarketAcquisitionRouteActionResult CaptureInputState(string label) =>
        runner.RecordInputCapture(label, marketBoard.CaptureInputState());

    public MarketAcquisitionRouteActionResult FinalizeInputCaptureLog() =>
        runner.FinalizeInputCaptureLog();

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
            ProbeLiveMarketBoardCore(
                runner.ActivePlan ?? throw new InvalidOperationException("Prepare a live candidate plan before probing live market board listings."),
                claimedRequest ?? throw new InvalidOperationException("No dashboard request is accepted."),
                recordRouteResult: true);
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

    public void ProbePreparedPlan(MarketAcquisitionPlan plan, MarketAcquisitionClaimView claimed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(claimed);
        if (IsRouteActive)
            throw new InvalidOperationException("A prepared-plan probe cannot run while a route or purchase is active.");

        claimedRequest = claimed;
        state.ProbeRunning = true;
        try
        {
            ProbeLiveMarketBoardCore(plan, claimed, recordRouteResult: false);
        }
        catch (Exception ex)
        {
            state.AcquisitionStatus = $"Live market board probe failed: {ex.Message}";
        }
        finally
        {
            state.ProbeRunning = false;
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

    private void ProbeLiveMarketBoardCore(
        MarketAcquisitionPlan plan,
        MarketAcquisitionClaimView claimed,
        bool recordRouteResult)
    {
        var activeLine = GetActiveRouteLine(claimed);
        var activeSubtask = recordRouteResult ? runner.ActiveStop?.ActiveItemSubtask : null;
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

        var totals = recordRouteResult
            ? ResolveActiveRouteLinePurchaseTotals(activeSubtask)
            : default;
        state.LiveCandidatePlan = canBuildLiveCandidatePlan
            ? activeSubtask == null
                ? MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(activeLine, plan, currentWorld, state.MarketBoardReadResult, totals.PurchasedQuantity, totals.SpentGil)
                : MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(activeLine, plan, activeSubtask, currentWorld, state.MarketBoardReadResult, totals.PurchasedQuantity, totals.SpentGil)
            : null;
        if (state.LiveCandidatePlan != null &&
            TryContinueVisibleListingRead(
                currentWorld,
                state.MarketBoardReadResult,
                state.LiveCandidatePlan,
                requireRunningRoute: recordRouteResult))
            return;

        var probeResult = recordRouteResult && runner.IsRunning && runner.ActiveStop is { Status: "Arrived" } && state.LiveCandidatePlan != null
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
        MarketAcquisitionLiveCandidatePlan candidatePlan,
        bool requireRunningRoute = true)
    {
        if ((requireRunningRoute && !runner.IsRunning) ||
            !listingReadAccumulator.TryBeginContinuation(readResult, candidatePlan, out var continuation))
            return false;

        if (!uiAutomation.TryScrollMarketBoardListingsToRow(continuation.RequestedRow, out var scrollMessage))
        {
            state.AcquisitionStatus = scrollMessage;
            if (requireRunningRoute)
            {
                var scrollPending = runner.RecordListingReadPending(
                    currentWorld,
                    readResult with { Message = $"{continuation.Message} {scrollMessage}" });
                state.AcquisitionStatus = scrollPending.Success ? scrollMessage : scrollPending.Message;
            }

            return true;
        }

        var message = $"{continuation.Message} {scrollMessage}";
        if (!requireRunningRoute)
        {
            state.AcquisitionStatus = message;
            return true;
        }

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

    public void BeginNextWorldPurchase()
    {
        var activeStop = runner.ActiveStop;
        if (activeStop is not { Status: "Purchasing" })
            return;

        var claimed = claimedRequest ?? throw new InvalidOperationException("No dashboard request is accepted.");
        var plan = runner.ActivePlan ?? throw new InvalidOperationException("No market acquisition plan is prepared.");
        var currentWorld = context.GetCurrentWorldName();
        if (!activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Cannot purchase on {currentWorld}; active route stop is {activeStop.WorldName}.");

        if (!string.Equals(state.ActiveWorldPurchaseBatchWorld, activeStop.WorldName, StringComparison.OrdinalIgnoreCase))
        {
            state.ActiveWorldPurchaseBatchWorld = activeStop.WorldName;
            state.ActiveWorldPurchasedQuantity = 0;
            state.ActiveWorldSpentGil = 0;
        }

        var activeLine = GetActiveRouteLine(claimed);
        var activeLineId = GetActiveRouteLineId(claimed);
        if (!string.Equals(state.ActivePurchaseLineId, activeLineId, StringComparison.Ordinal))
        {
            state.ActivePurchaseLineId = activeLineId;
            state.ActiveLinePurchasedQuantity = 0;
            state.ActiveLineSpentGil = 0;
            if (activeStop.ActiveItemSubtask != null)
                ReportAcquisitionLineProgress(activeStop.ActiveItemSubtask, "Running", 0, 0,
                    $"Started purchasing {FormatItem(activeLine)} on {activeStop.WorldName}.");
        }

        var freshRead = listingReadAccumulator.Merge(marketBoard.ReadCurrentListings(currentWorld));
        state.MarketBoardReadResult = freshRead;
        if (!freshRead.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase))
        {
            if (!freshRead.IsFresh)
            {
                runner.RecordListingReadPending(currentWorld, freshRead);
                state.AcquisitionStatus = $"Waiting for fresh market listings. {freshRead.Message}";
                state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
                return;
            }

            throw new InvalidOperationException(freshRead.Message);
        }

        var totals = ResolveActiveRouteLinePurchaseTotals(activeStop.ActiveItemSubtask);
        state.LiveCandidatePlan = activeStop.ActiveItemSubtask == null
            ? MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(activeLine, plan, currentWorld, freshRead, totals.PurchasedQuantity, totals.SpentGil)
            : MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(activeLine, plan, activeStop.ActiveItemSubtask, currentWorld, freshRead, totals.PurchasedQuantity, totals.SpentGil);
        if (TryContinueVisibleListingRead(currentWorld, freshRead, state.LiveCandidatePlan))
            return;

        var selection = purchase.ExecuteFirstCandidate(state.LiveCandidatePlan, freshRead);
        var now = clock.UtcNow;
        purchaseAutomation.RecordPurchaseSelection(selection, now, MarketBoardPurchaseConfirmationWatchdog);
        runner.RecordAutomationSnapshot(CreatePurchaseSelectionSnapshot(selection));

        if (selection.Status.Equals("NoCandidate", StringComparison.OrdinalIgnoreCase))
        {
            if (ShouldFailWorldPurchaseBatchOnNoCandidate(state.LiveCandidatePlan))
            {
                UpdateStatus(runner.FailRoute(state.LiveCandidatePlan.Message));
                ReportRouteProgress();
                return;
            }

            CompleteActiveWorldPurchaseBatch(currentWorld);
            return;
        }

        if (ClassifyPurchaseSelectionOutcome(selection.Status) == MarketBoardAutomationOutcome.Recoverable)
        {
            state.AcquisitionStatus = $"Purchase: {selection.Status}. {selection.Message}";
            state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(250);
            return;
        }

        if (!selection.Status.Equals("PurchaseSelectionSent", StringComparison.OrdinalIgnoreCase) || selection.Candidate == null)
        {
            UpdateStatus(runner.FailRoute($"World purchase batch stopped: {selection.Message}"));
            ReportRouteProgress();
            return;
        }

        purchaseAutomation.ScheduleNextMonitor(now, MarketBoardPurchaseInitialMonitorDelay);
        state.AcquisitionStatus = $"Purchase: {selection.Status}. {selection.Message}";
    }

    public MarketAcquisitionRouteEngineTickResult MonitorMarketBoardPurchase()
    {
        var previousSession = purchaseAutomation.PurchaseSession;
        if (previousSession?.IsActive != true)
            return MarketAcquisitionRouteEngineTickResult.Idle();

        var now = clock.UtcNow;
        if (!purchaseAutomation.IsMonitorDue(now))
            return MarketAcquisitionRouteEngineTickResult.Idle("Waiting for purchase monitor tick.");

        try
        {
            var tick = purchaseAutomation.MonitorPurchase(
                now,
                MarketBoardPurchaseMonitorInterval,
                MarketBoardPurchaseListingRemovalWatchdog,
                candidate => purchase.TryConfirmPendingPurchase(candidate),
                () => marketBoard.ReadCurrentListings(context.GetCurrentWorldName()));
            if (!tick.DidWork)
                return MarketAcquisitionRouteEngineTickResult.Idle("Purchase monitor had no due work.");

            ApplyPurchaseMonitorTick(tick, previousSession);
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, purchaseAutomation.NextMonitorUtc);
        }
        catch (Exception ex)
        {
            purchaseAutomation.RecordMonitorFailure("PurchaseMonitorFailed", ex.Message);
            state.AcquisitionStatus = $"Purchase monitor failed: {ex.Message}";
            runner.FailRoute(state.AcquisitionStatus, ex);
            ReportRouteProgress();
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, purchaseAutomation.NextMonitorUtc);
        }
    }

    private void ApplyPurchaseMonitorTick(MarketBoardPurchaseMonitorTick tick, MarketBoardPurchaseSession previousSession)
    {
        if (tick.ConfirmationResult != null)
        {
            var candidate = tick.ConfirmationResult.Candidate ?? previousSession.Candidate;
            runner.RecordAutomationSnapshot(CreatePurchaseConfirmationSnapshot(tick.ConfirmationResult, candidate));
        }

        if (tick.FreshRead != null)
        {
            state.MarketBoardReadResult = tick.FreshRead;
            if (tick.FreshReadSession != null)
                runner.RecordAutomationSnapshot(tick.FreshReadSession.CreateFreshReadSnapshot(tick.FreshRead));
        }

        var session = tick.Session ?? previousSession;
        state.AcquisitionStatus = $"Purchase: {session.Status}. {session.Message}";
        if (session.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = session.Candidate;
            state.ActiveWorldPurchasedQuantity = checked(state.ActiveWorldPurchasedQuantity + candidate.Quantity);
            state.ActiveWorldSpentGil = checked(state.ActiveWorldSpentGil + candidate.TotalGil);
            state.ActiveLinePurchasedQuantity = checked(state.ActiveLinePurchasedQuantity + candidate.Quantity);
            state.ActiveLineSpentGil = checked(state.ActiveLineSpentGil + candidate.TotalGil);
            ReportConfirmedPurchase(candidate, state.ActiveLinePurchasedQuantity, state.ActiveLineSpentGil);
            ClearMarketBoardAutomationState();
            if (state.MarketBoardReadResult?.Status is "MarketBoardNotOpen" or "NoListings")
                CompleteActiveWorldPurchaseBatch(context.GetCurrentWorldName());
            else
                BeginNextWorldPurchase();
        }
        else if (!session.IsActive)
        {
            UpdateStatus(runner.FailRoute($"World purchase batch stopped: {session.Message}"));
            ReportRouteProgress();
        }
    }

    private void CompleteActiveWorldPurchaseBatch(string currentWorld)
    {
        var activeSubtask = runner.ActiveStop?.ActiveItemSubtask;
        if (activeSubtask != null)
        {
            var lineStatus = ResolveZeroPurchaseLineStatus(state.LiveCandidatePlan, state.ActiveLinePurchasedQuantity, state.ActiveLineSpentGil);
            ReportAcquisitionLineProgress(activeSubtask, lineStatus, state.ActiveLinePurchasedQuantity, state.ActiveLineSpentGil,
                $"Completed {FormatItem(GetActiveRouteLine(claimedRequest!))} on {currentWorld}: purchased {state.ActiveLinePurchasedQuantity:N0}, spent {state.ActiveLineSpentGil:N0} gil.");
        }

        var result = runner.RecordWorldPurchaseBatchComplete(
            currentWorld,
            activeSubtask == null ? state.ActiveWorldPurchasedQuantity : state.ActiveLinePurchasedQuantity,
            activeSubtask == null ? state.ActiveWorldSpentGil : state.ActiveLineSpentGil,
            state.ActiveLinePurchasedQuantity == 0 && state.ActiveLineSpentGil == 0
                ? ResolveZeroPurchaseLineStatus(state.LiveCandidatePlan, state.ActiveLinePurchasedQuantity, state.ActiveLineSpentGil)
                : null,
            state.ActiveLinePurchasedQuantity == 0 && state.ActiveLineSpentGil == 0 ? state.LiveCandidatePlan?.Message : null);
        state.AcquisitionStatus = result.Message;
        ClearMarketBoardAutomationState();

        var nextStop = runner.ActiveStop;
        if (nextStop == null || !nextStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
        {
            state.ActiveWorldPurchasedQuantity = 0;
            state.ActiveWorldSpentGil = 0;
            state.ActiveWorldPurchaseBatchWorld = null;
            state.ActivePurchaseLineId = null;
            state.ActiveLinePurchasedQuantity = 0;
            state.ActiveLineSpentGil = 0;
        }
        else if (activeSubtask != null && nextStop.ActiveItemSubtask != null &&
                 !activeSubtask.LineId.Equals(nextStop.ActiveItemSubtask.LineId, StringComparison.Ordinal))
        {
            ResetMarketBoardStateForNextRouteItem();
            state.ActiveWorldPurchaseBatchWorld = nextStop.WorldName;
        }

        ReportRouteProgress();
        if (result.Success &&
            runner.LatestWorldCompletionSummary?.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase) == true)
        {
            _ = ReportUniversalisFreshnessAsync(currentWorld, freshnessCancellation.Token);
        }
    }

    private async Task ReportUniversalisFreshnessAsync(string worldName, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(UniversalisFreshnessVerificationDelay, cancellationToken).ConfigureAwait(false);
            await runner.VerifyWorldFreshnessAsync(worldName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                state.AcquisitionStatus = $"Unable to record Universalis freshness diagnostics: {ex.Message}";
        }
    }

    private void ResetMarketBoardStateForNextRouteItem()
    {
        state.MarketBoardReadResult = null;
        state.MarketBoardReconciliation = null;
        state.LiveCandidatePlan = null;
        ClearMarketBoardAutomationState();
        runner.ClearSearchSubmission("Advancing to next route item.");
        uiAutomation.TryCloseMarketBoardWindows();
        state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(250);
    }

    private void ClearMarketBoardAutomationState()
    {
        listingReadAccumulator.Clear();
        purchaseAutomation.Clear();
    }

    private void ReportConfirmedPurchase(MarketBoardPurchaseCandidate candidate, uint linePurchasedQuantity, uint lineSpentGil)
    {
        var claimed = claimedRequest;
        var activeSubtask = runner.ActiveStop?.ActiveItemSubtask;
        if (claimed == null || activeSubtask == null || string.IsNullOrWhiteSpace(claimed.ClaimToken))
            return;

        var lineId = string.IsNullOrWhiteSpace(activeSubtask.LineId) ? GetActiveRouteLineId(claimed) : activeSubtask.LineId;
        var worldName = string.IsNullOrWhiteSpace(candidate.WorldName) ? context.GetCurrentWorldName() : candidate.WorldName;
        var message = $"Purchased {candidate.Quantity:N0} {FormatItem(GetActiveRouteLine(claimed))} on {worldName} for {candidate.TotalGil:N0} gil.";
        runner.RecordPurchaseAudit(lineId, activeSubtask.ItemName, worldName, candidate.ListingId, candidate.RetainerId, candidate.Quantity, candidate.TotalGil, "Purchased", activeSubtask.Source);
        runner.RecordLineProgress(lineId, activeSubtask.ItemName, "Running", linePurchasedQuantity, lineSpentGil, message, activeSubtask.Source);
        evidence.RecordPurchaseVisit(candidate, activeSubtask, worldName, claimed.Id, state.ProgressNonce);
        ReportPurchaseAudit(claimed, lineId, activeSubtask.ItemName, candidate, worldName, message);
        ReportLineProgress(claimed, lineId, activeSubtask.ItemName, "Running", linePurchasedQuantity, lineSpentGil, message, null);
    }

    private void ReportAcquisitionLineProgress(MarketAcquisitionWorldItemSubtask subtask, string status, uint purchasedQuantity, uint spentGil, string message)
    {
        var claimed = claimedRequest;
        if (claimed == null || string.IsNullOrWhiteSpace(claimed.ClaimToken))
            return;

        var lineId = string.IsNullOrWhiteSpace(subtask.LineId) ? GetActiveRouteLineId(claimed) : subtask.LineId;
        runner.RecordLineProgress(lineId, subtask.ItemName, status, purchasedQuantity, spentGil, message, subtask.Source);
        ReportLineProgress(claimed, lineId, subtask.ItemName, status, purchasedQuantity, spentGil, message, null);
    }

    private void ReportPurchaseAudit(MarketAcquisitionClaimView claimed, string lineId, string? itemName, MarketBoardPurchaseCandidate candidate, string worldName, string message)
    {
        if (!reportDispatcher.CanReport)
            return;

        var sequence = ++state.ProgressReportSequence;
        reportDispatcher.EnqueuePurchaseAudit(
            new MarketAcquisitionPurchaseAuditReport(
                claimed.Id,
                claimed.ClaimToken,
                state.ProgressNonce,
                sequence,
                lineId,
                worldName,
                candidate.ItemId,
                itemName,
                candidate,
                message));
    }

    private void ReportLineProgress(MarketAcquisitionClaimView claimed, string lineId, string? itemName, string status, uint purchasedQuantity, uint spentGil, string message, string? reason)
    {
        if (!reportDispatcher.CanReport)
            return;

        var sequence = ++state.ProgressReportSequence;
        reportDispatcher.EnqueueLineProgress(
            new MarketAcquisitionLineProgressReport(
                claimed.Id,
                claimed.ClaimToken,
                state.ProgressNonce,
                sequence,
                lineId,
                itemName,
                status,
                purchasedQuantity,
                spentGil,
                message,
                reason));
    }

    private string GetActiveRouteLineId(MarketAcquisitionClaimView claimed)
    {
        var lineId = runner.ActiveStop?.ActiveItemSubtask?.LineId;
        return !string.IsNullOrWhiteSpace(lineId) ? lineId : claimed.Id;
    }

    private static string ResolveZeroPurchaseLineStatus(MarketAcquisitionLiveCandidatePlan? candidatePlan, uint purchasedQuantity, uint spentGil) =>
        purchasedQuantity > 0 || spentGil > 0
            ? "Complete"
            : MarketAcquisitionLiveCandidateStatuses.IsIncompleteListingCoverage(candidatePlan?.Status)
                ? "SkippedIncompleteListingCoverage"
                : "SkippedNoLiveStock";

    internal static bool ShouldFailWorldPurchaseBatchOnNoCandidate(MarketAcquisitionLiveCandidatePlan? candidatePlan) =>
        MarketAcquisitionLiveCandidateStatuses.IsIncompleteListingCoverage(candidatePlan?.Status);

    private static MarketBoardAutomationSnapshot CreatePurchaseSelectionSnapshot(MarketBoardPurchaseResult result)
    {
        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["resultMessage"] = result.Message,
        };
        if (result.Candidate != null)
        {
            details["candidateItemId"] = result.Candidate.ItemId.ToString();
            details["candidateWorld"] = result.Candidate.WorldName;
            details["candidateListingId"] = result.Candidate.ListingId;
            details["candidateRetainerId"] = result.Candidate.RetainerId;
            details["candidateRetainerName"] = result.Candidate.RetainerName;
            details["candidateQuantity"] = result.Candidate.Quantity.ToString();
            details["candidateUnitPrice"] = result.Candidate.UnitPrice.ToString();
            details["candidateTotalGil"] = result.Candidate.TotalGil.ToString();
        }

        foreach (var pair in result.Diagnostics)
            details[pair.Key] = pair.Value;

        return MarketBoardAutomationSnapshot.Create(
            "BuyListing",
            "Selection",
            "ClickableMarketBoardListing",
            result.Status,
            ClassifyPurchaseSelectionOutcome(result.Status),
            ChoosePurchaseSelectionNextAction(result.Status),
            details);
    }

    private static MarketBoardAutomationSnapshot CreatePurchaseConfirmationSnapshot(MarketBoardPurchaseResult result, MarketBoardPurchaseCandidate candidate) =>
        MarketBoardAutomationSnapshot.Create("BuyListing", "Confirmation", "PurchasePrompt", result.Status,
            result.Status is "ConfirmationSubmitted" or "ConfirmationPending" ? MarketBoardAutomationOutcome.InProgress : MarketBoardAutomationOutcome.Fatal,
            result.Status switch
            {
                "ConfirmationSubmitted" => "VerifyListingRemoval",
                "ConfirmationPending" => "ContinueMonitoring",
                _ => "StopRoute",
            },
            new Dictionary<string, string?>
            {
                ["candidateItemId"] = candidate.ItemId.ToString(),
                ["candidateWorld"] = candidate.WorldName,
                ["candidateListingId"] = candidate.ListingId,
                ["candidateRetainerId"] = candidate.RetainerId,
                ["candidateRetainerName"] = candidate.RetainerName,
                ["candidateQuantity"] = candidate.Quantity.ToString(),
                ["candidateUnitPrice"] = candidate.UnitPrice.ToString(),
                ["candidateTotalGil"] = candidate.TotalGil.ToString(),
                ["confirmationAddon"] = result.ConfirmationAddonName,
                ["confirmationPromptText"] = result.ConfirmationPromptText,
            });

    private static MarketBoardAutomationOutcome ClassifyPurchaseSelectionOutcome(string status) => status switch
    {
        "PurchaseSelectionSent" => MarketBoardAutomationOutcome.InProgress,
        "NoCandidate" => MarketBoardAutomationOutcome.ExpectedAlternate,
        "MarketBoardNotOpen" or "InfoProxyUnavailable" or "ListingListUnavailable" or "ListingListNotReady" => MarketBoardAutomationOutcome.Recoverable,
        _ => MarketBoardAutomationOutcome.Fatal,
    };

    private static string ChoosePurchaseSelectionNextAction(string status) => status switch
    {
        "PurchaseSelectionSent" => "WaitForConfirmation",
        "NoCandidate" => "CompleteWorldBatch",
        "MarketBoardNotOpen" => "ReopenMarketBoard",
        _ => "StopRoute",
    };

    public void ReportRouteProgress()
    {
        var claimed = claimedRequest;
        if (claimed == null || string.IsNullOrWhiteSpace(claimed.ClaimToken) || !reportDispatcher.CanReport ||
            string.Equals(runner.State, "Idle", StringComparison.OrdinalIgnoreCase))
            return;

        var routeState = runner.State;
        if (!MarketAcquisitionRouteProgressReporter.CanReportForRouteState(routeState) ||
            !MarketAcquisitionRouteProgressReporter.CanReportForRequestStatus(claimed.Status))
            return;

        var message = runner.StatusMessage;
        var activeStop = runner.ActiveStop;
        var report = new MarketAcquisitionRouteProgressReport(
            claimed.Id,
            claimed.ClaimToken,
            routeState,
            state.ProgressNonce,
            ++state.ProgressReportSequence,
            activeStop == null ? null : $"{activeStop.DataCenter}:{activeStop.WorldName}",
            activeStop?.WorldName,
            activeStop?.Status ?? routeState,
            message);
        reportDispatcher.EnqueueRouteProgress(report);
    }

    private void ClearExecutionState()
    {
        state.ResetRouteExecutionState();
        listingReadAccumulator.Clear();
        purchaseAutomation.Clear();
        reportDispatcher.ResetSession();
        freshnessCancellation.Cancel();
        freshnessCancellation.Dispose();
        freshnessCancellation = new CancellationTokenSource();
    }

    private MarketAcquisitionRouteActionResult UpdateStatus(MarketAcquisitionRouteActionResult result)
    {
        state.AcquisitionStatus = result.Message;
        return result;
    }

    public void Dispose()
    {
        purchaseAutomation.Dispose();
        reportDispatcher.Dispose();
        freshnessCancellation.Cancel();
        freshnessCancellation.Dispose();
        runner.Dispose();
    }
}
