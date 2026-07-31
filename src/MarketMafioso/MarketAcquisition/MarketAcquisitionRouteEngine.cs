using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Travel;
using MarketMafioso.MarketAcquisition.ExactAuthority;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngine : IDisposable
{
    private static readonly TimeSpan RouteMonitorInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MarketBoardItemSearchOperationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TravelPreparationOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SameDataCenterWorldTravelArrivalOperationTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DataCenterTravelArrivalOperationTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan MarketBoardPurchaseConfirmationWatchdog = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MarketBoardPurchaseInitialMonitorDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MarketBoardPurchaseOutcomeWatchdog = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UniversalisFreshnessVerificationDelay = TimeSpan.FromSeconds(10);
    private readonly MarketAcquisitionRouteRunner runner;
    private readonly IMarketAcquisitionRouteContext context;
    private readonly IMarketAcquisitionRouteUiAutomation uiAutomation;
    private readonly IMarketAcquisitionRouteTravelCleanup travelCleanup;
    private readonly IMarketAcquisitionMarketBoardIo marketBoard;
    private readonly IMarketAcquisitionPurchaseIo purchase;
    private readonly IMarketAcquisitionRouteEvidenceRecorder evidence;
    private readonly MarketAcquisitionRouteReportDispatcher reportDispatcher;
    private readonly IMarketAcquisitionRouteClock clock;
    private readonly MarketBoardListingReadAccumulator listingReadAccumulator = new();
    private readonly MarketBoardAutomationController purchaseAutomation = new();
    private readonly MarketAcquisitionRouteOperationExecutor operationExecutor = new();
    private readonly MarketAcquisitionRouteEngineState state = new();
    private CancellationTokenSource freshnessCancellation = new();
    private MarketAcquisitionClaimView? claimedRequest;
    private MarketAcquisitionTravelLease? activeTravelLease;
    private MarketAcquisitionTravelLease? unresolvedTravelLease;
    private MarketAcquisitionApproachLease? activeApproachLease;
    private bool travelInterruptedByCleanup;
    private long operationSequence;
    private readonly IExactAcquisitionRouteExecutionStateStore exactAcquisitionStateStore;
    private readonly IShardAcquisitionCheckpointCoordinator? shardCheckpoints;
    private ExactAcquisitionRouteAuthoritySession? exactAcquisitionAuthority;
    private ExactAcquisitionDryRunScenario exactAcquisitionDryRunScenario;
    private bool exactAcquisitionDryRunFaultEligible;
    private bool exactAcquisitionDryRunFaultInjected;
    private bool exactAcquisitionDryRunNoViableConsumed;

    public MarketAcquisitionRouteEngine(
        MarketAcquisitionRouteRunner runner,
        IMarketAcquisitionRouteContext context,
        IMarketAcquisitionRouteUiAutomation uiAutomation,
        IMarketAcquisitionRouteTravelCleanup travelCleanup,
        IMarketAcquisitionMarketBoardIo marketBoard,
        IMarketAcquisitionPurchaseIo purchase,
        IMarketAcquisitionRouteReporter reporter,
        IMarketAcquisitionRouteEvidenceRecorder evidence,
        MarketAcquisitionClaimLifecycleController claimLifecycle,
        IMarketAcquisitionRouteCallbackDispatcher callbackDispatcher,
        IMarketAcquisitionRouteClock clock,
        IExactAcquisitionRouteExecutionStateStore exactAcquisitionStateStore,
        IMarketAcquisitionReportOutbox? reportOutbox = null,
        IShardAcquisitionCheckpointCoordinator? shardCheckpoints = null)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.uiAutomation = uiAutomation ?? throw new ArgumentNullException(nameof(uiAutomation));
        this.travelCleanup = travelCleanup ?? throw new ArgumentNullException(nameof(travelCleanup));
        this.marketBoard = marketBoard ?? throw new ArgumentNullException(nameof(marketBoard));
        this.purchase = purchase ?? throw new ArgumentNullException(nameof(purchase));
        this.evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        reportDispatcher = new MarketAcquisitionRouteReportDispatcher(
            reporter ?? throw new ArgumentNullException(nameof(reporter)),
            claimLifecycle ?? throw new ArgumentNullException(nameof(claimLifecycle)),
            callbackDispatcher ?? throw new ArgumentNullException(nameof(callbackDispatcher)),
            reportOutbox);
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.exactAcquisitionStateStore = exactAcquisitionStateStore ?? throw new ArgumentNullException(nameof(exactAcquisitionStateStore));
        this.shardCheckpoints = shardCheckpoints;
    }

    public bool IsRouteActive =>
        runner.IsRunning ||
        runner.IsPaused ||
        exactAcquisitionAuthority?.State.Phase is ExactAcquisitionRouteAuthorityPhase.Preparing or
            ExactAcquisitionRouteAuthorityPhase.Active or ExactAcquisitionRouteAuthorityPhase.RecoveryNeeded ||
        state.ProbeRunning ||
        operationExecutor.ActiveSnapshot != null ||
        purchaseAutomation.PurchaseSession?.IsActive == true ||
        shardCheckpoints?.IsActive == true;

    public ExactAcquisitionDryRunScenario ArmedExactAcquisitionDryRunScenario => exactAcquisitionDryRunScenario;
    public bool IsExactAcquisitionDryRunFaultEligible => exactAcquisitionDryRunFaultEligible;
    public bool WasExactAcquisitionDryRunFaultInjected => exactAcquisitionDryRunFaultInjected;

    public bool ArmExactAcquisitionDryRunScenario(ExactAcquisitionDryRunScenario scenario)
    {
        if (IsRouteActive)
            return false;
        exactAcquisitionDryRunScenario = scenario;
        exactAcquisitionDryRunFaultInjected = false;
        exactAcquisitionDryRunNoViableConsumed = false;
        return true;
    }

    public bool ConsumeNoViableExactAcquisitionDryRunScenario()
    {
        if (!exactAcquisitionDryRunFaultEligible || !exactAcquisitionDryRunFaultInjected || exactAcquisitionDryRunNoViableConsumed ||
            exactAcquisitionDryRunScenario != ExactAcquisitionDryRunScenario.NoViableRecovery || !NeedsExactAcquisitionRecovery)
            return false;
        exactAcquisitionDryRunNoViableConsumed = true;
        return true;
    }

    public MarketAcquisitionRouteEngineSnapshot CreateSnapshot() => new()
    {
        ExecutionMode = state.ExecutionMode,
        StatusMessage = runner.StatusMessage,
        VisibleAcquisitionStatus = state.AcquisitionStatus,
        IsRouteActive = IsRouteActive,
        IsRunning = runner.IsRunning,
        IsPaused = runner.IsPaused,
        CanRestart = runner.CanRestart && state.ManualRecoveryBlockedReason == null,
        CanRecover =
            runner.CanRecover &&
            state.ManualRecoveryBlockedReason == null &&
            exactAcquisitionAuthority is null,
        RecoveryBlockedReason = state.ManualRecoveryBlockedReason,
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
        ActiveOperation = operationExecutor.ActiveSnapshot,
        LastOperation = operationExecutor.LastSnapshot,
        PurchaseSession = purchaseAutomation.PurchaseSession,
        LastPurchaseResult = purchaseAutomation.LastPurchaseResult,
        PurchaseEvidenceState = purchase.PurchaseEvidenceState,
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
        ExactAcquisitionExecution = exactAcquisitionAuthority?.State,
        ShardCheckpoint = shardCheckpoints?.Snapshot,
    };

    public MarketAcquisitionRouteActionResult Start(
        MarketAcquisitionPlan plan,
        MarketAcquisitionClaimView claimed,
        bool enableDiagnostics,
        bool includeOpportunisticChecks,
        ExactAcquisitionExecutionContract? exactAcquisitionContract = null,
        MarketAcquisitionRequestDocument? workbenchDocument = null,
        MarketAcquisitionExecutionMode executionMode = MarketAcquisitionExecutionMode.Live,
        MarketAcquisitionRouteDiagnosticsLevel diagnosticsLevel = MarketAcquisitionRouteDiagnosticsLevel.FullTrace)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(claimed);
        if (!TryReconcileUnresolvedTravelLease(out var reconciliationFailure))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(reconciliationFailure));

        claimedRequest = claimed;
        ClearExecutionState();
        state.ExecutionMode = executionMode;
        exactAcquisitionDryRunFaultEligible = executionMode == MarketAcquisitionExecutionMode.DryRun &&
                                       exactAcquisitionContract?.Transfer.DryRunOnly == true;
        exactAcquisitionDryRunFaultInjected = false;
        exactAcquisitionDryRunNoViableConsumed = false;
        var routePlan = plan;
        if (exactAcquisitionContract is not null)
        {
            if (exactAcquisitionContract.Transfer.DryRunOnly && executionMode != MarketAcquisitionExecutionMode.DryRun)
            {
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(
                    "This diagnostic External plan contract is permanently restricted to non-spending dry runs."));
            }
            if (workbenchDocument is null)
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail("External plan Route start requires its finalized Workbench document."));
            try
            {
                IExactAcquisitionRouteExecutionStateStore authorityStore = exactAcquisitionStateStore;
                if (executionMode == MarketAcquisitionExecutionMode.DryRun)
                {
                    routePlan = ExactAcquisitionDryRunExecutionStateRestorer.RestoreRemainingPlan(
                        exactAcquisitionContract,
                        workbenchDocument,
                        claimed,
                        plan,
                        exactAcquisitionStateStore.Restore());
                    authorityStore = new RestoreOnlyExactAcquisitionRouteExecutionStateStore(exactAcquisitionStateStore);
                }
                exactAcquisitionAuthority = ExactAcquisitionRouteAuthoritySession.Consume(
                    exactAcquisitionContract,
                    workbenchDocument,
                    routePlan,
                    claimed,
                    authorityStore);
                exactAcquisitionAuthority.CompletePreflight(routePlan);
            }
            catch (Exception exception)
            {
                var message = $"External plan preflight stopped before travel or purchase: {exception.Message}";
                exactAcquisitionAuthority?.Pause(message);
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(message));
            }
        }
        else
        {
            exactAcquisitionAuthority = null;
        }
        if (executionMode == MarketAcquisitionExecutionMode.Live && shardCheckpoints is not null)
        {
            var checkpointPreflight = shardCheckpoints.Prepare(routePlan, state.ProgressNonce);
            if (!checkpointPreflight.Success)
            {
                var message = $"Shard storage preflight stopped before travel or purchase: {checkpointPreflight.Message}";
                exactAcquisitionAuthority?.Pause(message);
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(message));
            }
        }
        if (executionMode == MarketAcquisitionExecutionMode.Live)
            reportDispatcher.BeginSession(claimed);
        MarketAcquisitionRouteActionResult result;
        try
        {
            var effectiveDiagnosticsLevel = MarketAcquisitionRouteDiagnosticsPolicy.Resolve(
                enableDiagnostics ? diagnosticsLevel : MarketAcquisitionRouteDiagnosticsLevel.Off,
                executionMode);
            result = runner.Start(
                routePlan,
                effectiveDiagnosticsLevel != MarketAcquisitionRouteDiagnosticsLevel.Off,
                includeOpportunisticChecks,
                executionMode,
                effectiveDiagnosticsLevel);
        }
        catch (Exception exception) when (exactAcquisitionAuthority is not null)
        {
            exactAcquisitionAuthority.Pause($"External plan Route start failed before travel: {exception.Message}");
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(exactAcquisitionAuthority.State.Message));
        }
        if (!result.Success && exactAcquisitionAuthority is not null)
        {
            exactAcquisitionAuthority.Pause($"External plan Route start stopped safely: {result.Message}");
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(exactAcquisitionAuthority.State.Message));
        }
        state.AcquisitionStatus = result.Message;
        return result;
    }

    public bool NeedsExactAcquisitionRecovery => exactAcquisitionAuthority?.State.NeedsRecovery == true;

    public MarketAcquisitionClaimView CreateExactAcquisitionRecoveryClaim(MarketAcquisitionClaimView claim) =>
        exactAcquisitionAuthority?.CreateRecoveryClaim(claim) ??
        throw new InvalidOperationException("No exact-acquisition recovery is pending.");

    public MarketAcquisitionRouteActionResult StartExactAcquisitionRecovery(
        MarketAcquisitionPlan plan,
        MarketAcquisitionClaimView remainingClaim,
        MarketAcquisitionRequestDocument workbenchDocument)
    {
        if (exactAcquisitionAuthority is null || !exactAcquisitionAuthority.State.NeedsRecovery)
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail("No exact-acquisition recovery is pending."));
        try
        {
            exactAcquisitionAuthority.ValidateCurrentDocument(workbenchDocument);
            exactAcquisitionAuthority.BeginRecovery(plan);
            claimedRequest = remainingClaim;
            ClearExecutionState(preserveExecutionMode: true);
            if (state.ExecutionMode == MarketAcquisitionExecutionMode.Live)
                reportDispatcher.BeginSession(remainingClaim);
            var result = runner.Start(
                plan,
                enableDiagnostics: state.ExecutionMode == MarketAcquisitionExecutionMode.DryRun,
                includeOpportunisticChecks: false,
                state.ExecutionMode);
            if (!result.Success)
            {
                exactAcquisitionAuthority.Pause($"Recovery start stopped safely: {result.Message}");
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(exactAcquisitionAuthority.State.Message));
            }
            return UpdateStatus(result);
        }
        catch (Exception exception)
        {
            exactAcquisitionAuthority.Pause($"Recovery preflight stopped safely: {exception.Message}");
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(exactAcquisitionAuthority.State.Message));
        }
    }

    public void PauseExactAcquisitionRecovery(string message)
    {
        exactAcquisitionAuthority?.Pause(message);
        state.AcquisitionStatus = message;
    }

    public void RequestExactAcquisitionRecovery(MarketAcquisitionRequestDocument workbenchDocument)
    {
        if (exactAcquisitionAuthority?.State.Phase != ExactAcquisitionRouteAuthorityPhase.Paused)
            return;
        try
        {
            exactAcquisitionAuthority.ValidateCurrentDocument(workbenchDocument);
            exactAcquisitionAuthority.RequestRecovery();
        }
        catch (Exception exception)
        {
            exactAcquisitionAuthority.Pause(exception.Message);
        }
        state.AcquisitionStatus = exactAcquisitionAuthority.State.Message;
    }

    private MarketAcquisitionRouteActionResult TransitionToExactAcquisitionRecovery(string reason)
    {
        if (exactAcquisitionAuthority is null)
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(reason));

        CleanupOwnedApproach("exact-acquisition recovery");
        CleanupOwnedTravel("exact-acquisition recovery");
        CancelActiveOperation("Visible market rows changed; preparing exact-acquisition recovery.");
        if (runner.IsRunning || runner.IsPaused)
            runner.Stop();
        exactAcquisitionAuthority.RequestRecovery($"{reason} Refreshing and optimizing the complete remaining exact-quality route.");
        state.AcquisitionStatus = exactAcquisitionAuthority.State.Message;
        return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(exactAcquisitionAuthority.State.Message));
    }

    internal MarketAcquisitionRouteActionResult? EnforceExactAcquisitionCandidateAuthority(
        MarketAcquisitionWorldItemSubtask subtask,
        MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        if (exactAcquisitionAuthority is null)
            return null;
        if (exactAcquisitionDryRunFaultEligible && !exactAcquisitionDryRunFaultInjected &&
            exactAcquisitionDryRunScenario is ExactAcquisitionDryRunScenario.ChangedListingRecovery or ExactAcquisitionDryRunScenario.NoViableRecovery)
        {
            exactAcquisitionDryRunFaultInjected = true;
            return TransitionToExactAcquisitionRecovery(
                exactAcquisitionDryRunScenario == ExactAcquisitionDryRunScenario.ChangedListingRecovery
                    ? "Diagnostic dry run substituted a changed visible row after preflight."
                    : "Diagnostic dry run removed every in-envelope visible row after preflight.");
        }
        var authorization = exactAcquisitionAuthority.AuthorizeCandidate(subtask, candidatePlan);
        return authorization.IsValid
            ? null
            : TransitionToExactAcquisitionRecovery(
                authorization.Error ?? "Visible market rows exceeded exact-acquisition authority.");
    }

    public MarketAcquisitionRouteActionResult StartEvidenceRefresh(
        MarketAcquisitionPlan plan,
        MarketAcquisitionClaimView claimed,
        bool enableDiagnostics,
        MarketAcquisitionRouteDiagnosticsLevel diagnosticsLevel = MarketAcquisitionRouteDiagnosticsLevel.FullTrace)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(claimed);
        if (!TryReconcileUnresolvedTravelLease(out var reconciliationFailure))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(reconciliationFailure));

        claimedRequest = claimed;
        ClearExecutionState();
        state.EvidenceRefreshOnly = true;
        reportDispatcher.BeginSession(claimed);
        var result = runner.Start(
            plan,
            enableDiagnostics,
            includeOpportunisticChecks: false,
            diagnosticsLevel: enableDiagnostics ? diagnosticsLevel : MarketAcquisitionRouteDiagnosticsLevel.Off);
        state.AcquisitionStatus = result.Success
            ? $"Evidence refresh started. {result.Message}"
            : result.Message;
        return result;
    }

    public MarketAcquisitionRouteActionResult Pause()
    {
        travelInterruptedByCleanup = activeTravelLease != null;
        CleanupOwnedApproach("Pause");
        CleanupOwnedTravel("Pause");
        CancelActiveOperation("Route paused; active operation cancelled.");
        var result = runner.Pause();
        if (result.Success)
            exactAcquisitionAuthority?.Pause("External plan route paused; no purchase authority is active.");
        return UpdateStatus(result);
    }

    public MarketAcquisitionRouteActionResult Resume()
    {
        if (shardCheckpoints?.IsActive == true)
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(
                $"Route resume is locked while purchased shards are being reconciled: {shardCheckpoints.Snapshot.Message}"));
        if (travelInterruptedByCleanup)
        {
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(
                "World travel was interrupted while paused; restart the route only after reconciling the current world."));
        }

        if (!TryReconcileUnresolvedTravelLease(out var reconciliationFailure))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(reconciliationFailure));

        if (exactAcquisitionAuthority is not null && runner.ActivePlan is { } plan)
        {
            try
            {
                exactAcquisitionAuthority.CompletePreflight(plan);
            }
            catch (Exception exception)
            {
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail($"External plan resume preflight failed: {exception.Message}"));
            }
        }
        return UpdateStatus(runner.Resume());
    }

    public MarketAcquisitionRouteActionResult Stop()
    {
        CaptureManualRecoverySafetyBlock();
        evidence.Flush();
        uiAutomation.TryCloseMarketBoardWindows();
        CleanupOwnedApproach("Stop");
        CleanupOwnedTravel("Stop");
        CancelActiveOperation("Route stopped.");
        var result = runner.Stop();
        if (result.Success)
            exactAcquisitionAuthority?.Pause("External plan route stopped; persisted purchases remain reconciled for a later restart.");
        listingReadAccumulator.Clear();
        purchaseAutomation.Clear();
        reportDispatcher.ResetSession();
        freshnessCancellation.Cancel();
        return UpdateStatus(result);
    }

    public MarketAcquisitionRouteActionResult Recover(MarketAcquisitionClaimView claimed)
    {
        ArgumentNullException.ThrowIfNull(claimed);
        if (!runner.CanRecover)
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(
                $"Route cannot be recovered while {runner.State}."));
        if (state.ManualRecoveryBlockedReason is { } blockedReason)
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(blockedReason));
        if (exactAcquisitionAuthority is not null)
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(
                "Use exact-acquisition recovery for this retained route."));
        if (shardCheckpoints?.IsActive == true)
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(
                $"Route recovery is locked while purchased shards are being reconciled: {shardCheckpoints.Snapshot.Message}"));
        if (!context.IsCurrentWorldAvailable)
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(
                "Route recovery is waiting for the current world to become available."));
        if (!TryReconcileUnresolvedTravelLease(out var reconciliationFailure, allowIdleResolution: true))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(reconciliationFailure));

        CleanupOwnedApproach("Recovery");
        CleanupOwnedTravel("Recovery");
        CancelActiveOperation("Recovering retained route progress.");
        claimedRequest = claimed;
        listingReadAccumulator.Clear();
        purchaseAutomation.Clear();
        state.MarketBoardReadResult = null;
        state.MarketBoardReconciliation = null;
        state.LiveCandidatePlan = null;
        state.PurchaseRecoveryPreviousBrowseOperationId = null;
        state.PurchaseRecoveryRefreshRequired = false;
        state.UseProjectedMarketBoardSnapshot = false;
        state.NextRouteMonitorUtc = clock.UtcNow;
        reportDispatcher.BeginSession(claimed);
        freshnessCancellation.Cancel();
        freshnessCancellation.Dispose();
        freshnessCancellation = new CancellationTokenSource();

        var result = runner.Recover(context.GetCurrentWorldName());
        if (result.Success)
            state.ManualRecoveryBlockedReason = null;
        return UpdateStatus(result);
    }

    public MarketAcquisitionRouteActionResult Restart(
        MarketAcquisitionPlan plan,
        MarketAcquisitionClaimView claimed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(claimed);
        if (state.ManualRecoveryBlockedReason is { } blockedReason)
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(blockedReason));
        CleanupOwnedApproach("Replacement");
        CleanupOwnedTravel("Replacement");
        if (!TryReconcileUnresolvedTravelLease(out var reconciliationFailure))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(reconciliationFailure));
        claimedRequest = claimed;
        ClearExecutionState(preserveExecutionMode: true);
        if (exactAcquisitionAuthority is not null)
        {
            try
            {
                exactAcquisitionAuthority.BeginRecovery(plan);
            }
            catch (Exception exception)
            {
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail($"External plan restart preflight failed: {exception.Message}"));
            }
        }
        if (state.ExecutionMode == MarketAcquisitionExecutionMode.Live)
            reportDispatcher.BeginSession(claimed);
        var result = runner.Restart(plan);
        if (!result.Success && exactAcquisitionAuthority is not null)
        {
            exactAcquisitionAuthority.Pause($"External plan restart stopped safely: {result.Message}");
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(exactAcquisitionAuthority.State.Message));
        }
        return UpdateStatus(result);
    }

    public MarketAcquisitionRouteActionResult ReprepareAndRestart(
        MarketAcquisitionPlan plan,
        DateTimeOffset preparedAtUtc,
        MarketAcquisitionClaimView claimed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(claimed);
        if (state.ManualRecoveryBlockedReason is { } blockedReason)
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(blockedReason));
        CleanupOwnedApproach("Replacement");
        CleanupOwnedTravel("Replacement");
        if (!TryReconcileUnresolvedTravelLease(out var reconciliationFailure))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(reconciliationFailure));
        claimedRequest = claimed;
        ClearExecutionState(preserveExecutionMode: true);
        if (exactAcquisitionAuthority is not null)
        {
            try
            {
                exactAcquisitionAuthority.BeginRecovery(plan);
            }
            catch (Exception exception)
            {
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail($"External plan restart preflight failed: {exception.Message}"));
            }
        }
        if (state.ExecutionMode == MarketAcquisitionExecutionMode.Live)
            reportDispatcher.BeginSession(claimed);
        var result = runner.ReprepareAndRestart(plan, preparedAtUtc);
        if (!result.Success && exactAcquisitionAuthority is not null)
        {
            exactAcquisitionAuthority.Pause($"External plan restart stopped safely: {result.Message}");
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(exactAcquisitionAuthority.State.Message));
        }
        return UpdateStatus(result);
    }

    public void Reset(string status)
    {
        CleanupOwnedApproach("Reset");
        CleanupOwnedTravel("Reset");
        CancelActiveOperation(status);
        runner.Reset(status);
        ClearExecutionState();
        state.AcquisitionStatus = status;
        claimedRequest = null;
    }

    public MarketAcquisitionRouteActionResult CaptureInputState(string label) =>
        runner.RecordInputCapture(label, marketBoard.CaptureInputState());

    public MarketAcquisitionRouteActionResult FinalizeInputCaptureLog() =>
        runner.FinalizeInputCaptureLog();

    public MarketPurchaseTerminalResolutionResult ReconcileTerminalPurchaseEvidence(
        bool purchaseOccurred,
        string resolution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolution);
        if (purchase.PurchaseEvidenceState is not { } terminal || terminal is PendingMarketPurchase)
            return new(MarketPurchaseTerminalResolutionStatus.NoTerminalEvidence, "No terminal purchase evidence requires reconciliation.");
        if (terminal is ConfirmedMarketPurchase && !purchaseOccurred)
            return new(MarketPurchaseTerminalResolutionStatus.InvalidDisposition,
                "Confirmed server purchase evidence must be applied; it cannot be discarded as a failed purchase.");

        if (purchaseOccurred)
        {
            var intent = terminal.Intent;
            var candidate = new MarketBoardPurchaseCandidate
            {
                ItemId = intent.ItemId,
                WorldName = intent.WorldName,
                ListingId = intent.ListingId,
                RetainerId = intent.RetainerId ?? string.Empty,
                UnitPrice = intent.UnitPrice,
                Quantity = intent.Quantity,
                IsHq = intent.IsHighQuality,
            };
            uint nextWorldQuantity;
            uint nextWorldGil;
            uint nextLineQuantity;
            uint nextLineGil;
            try
            {
                nextWorldQuantity = checked(state.ActiveWorldPurchasedQuantity + candidate.Quantity);
                nextWorldGil = checked(state.ActiveWorldSpentGil + candidate.TotalGil);
                nextLineQuantity = checked(state.ActiveLinePurchasedQuantity + candidate.Quantity);
                nextLineGil = checked(state.ActiveLineSpentGil + candidate.TotalGil);
                if (exactAcquisitionAuthority is not null)
                {
                    var activePlan = runner.ActivePlan ??
                                     throw new InvalidOperationException("The retained exact-acquisition plan is unavailable.");
                    exactAcquisitionAuthority.RecordPurchase(intent.LineId, candidate, activePlan);
                }
            }
            catch (Exception exception)
            {
                return new(MarketPurchaseTerminalResolutionStatus.InvalidDisposition,
                    $"Purchase reconciliation could not preserve the confirmed purchase: {exception.Message}");
            }

            var applied = purchase.ResolvePurchaseEvidence(
                intent.IntentId,
                MarketPurchaseTerminalDisposition.AppliedExactlyOnce,
                clock.UtcNow,
                resolution.Trim());
            if (!applied.IsResolved)
                return applied;

            state.ActiveWorldPurchasedQuantity = nextWorldQuantity;
            state.ActiveWorldSpentGil = nextWorldGil;
            state.ActiveLinePurchasedQuantity = nextLineQuantity;
            state.ActiveLineSpentGil = nextLineGil;
            CompleteTerminalPurchaseReconciliation(
                $"Purchase outcome reconciled: listing {candidate.ListingId} was purchased. Recovering will refresh live listings before continuing.");
            try
            {
                ReportConfirmedPurchase(candidate, nextLineQuantity, nextLineGil);
            }
            catch (Exception exception)
            {
                state.AcquisitionStatus =
                    $"Purchase outcome was reconciled and route recovery is unlocked, but purchase reporting failed: {exception.Message}";
            }
            return applied;
        }

        var reconciled = purchase.ResolvePurchaseEvidence(
            terminal.Intent.IntentId,
            MarketPurchaseTerminalDisposition.ManuallyReconciled,
            clock.UtcNow,
            resolution.Trim());
        if (reconciled.IsResolved)
        {
            CompleteTerminalPurchaseReconciliation(
                $"Purchase outcome reconciled: listing {terminal.Intent.ListingId} was not purchased. The retained route can continue.");
        }
        return reconciled;
    }

    private void CompleteTerminalPurchaseReconciliation(string message)
    {
        state.ManualRecoveryBlockedReason = null;
        state.MarketBoardReadResult = null;
        state.MarketBoardReconciliation = null;
        state.LiveCandidatePlan = null;
        state.UseProjectedMarketBoardSnapshot = false;
        state.PurchaseRecoveryRefreshRequired = false;
        state.PurchaseRecoveryPreviousBrowseOperationId = null;
        ClearMarketBoardAutomationState();
        state.AcquisitionStatus = message;
        if (exactAcquisitionAuthority is not null)
            exactAcquisitionAuthority.RequestRecovery(message);
    }

    public MarketAcquisitionRouteEngineTickResult TickRoute(bool isRequestBusy)
    {
        if (shardCheckpoints?.IsActive == true)
        {
            var checkpoint = shardCheckpoints.Tick();
            state.AcquisitionStatus = checkpoint.Message;
            if (checkpoint.Failed)
            {
                exactAcquisitionAuthority?.Pause(checkpoint.Message);
                if (runner.IsRunning || runner.IsPaused)
                    UpdateStatus(FailRoute(checkpoint.Message));
            }
            else if (checkpoint.Completed && checkpoint.ResumeRoute && runner.IsPaused)
            {
                UpdateStatus(runner.Resume());
                state.AcquisitionStatus = checkpoint.Message;
            }
            ReportRouteProgress();
            return checkpoint.Worked
                ? MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, state.NextRouteMonitorUtc)
                : MarketAcquisitionRouteEngineTickResult.Idle(state.AcquisitionStatus);
        }

        if (TryFailExpiredOperation())
        {
            ReportRouteProgress();
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, state.NextRouteMonitorUtc);
        }

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
            var result = FailRoute($"Unable to monitor guided route. {ex.Message}", ex);
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
            FailRoute(message, ex);
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
        var currentWorld = context.IsCurrentWorldAvailable ? context.GetCurrentWorldName() : null;
        var requiresTravelPreparation = runner.MarketBoardCloseRequiredBeforeTravel ||
            !context.IsCurrentWorldAvailable ||
            !activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase);
        MarketAcquisitionRouteOperationSnapshot? preparation = null;
        if (requiresTravelPreparation ||
            operationExecutor.ActiveSnapshot?.Kind == MarketAcquisitionRouteOperationKind.TravelPreparation)
        {
            preparation = EnsureTravelPreparationOperation(activeStop);
        }

        if (runner.MarketBoardCloseRequiredBeforeTravel)
        {
            if (uiAutomation.TryCloseMarketBoardWindows())
            {
                ObserveTravelPreparationOperation(
                    preparation!,
                    MarketAcquisitionRouteOperationDisposition.Pending,
                    $"Waiting for market board windows to close before traveling to {activeStop.WorldName}.",
                    new Dictionary<string, string?>
                    {
                        ["preparationState"] = "MarketBoardCloseRequested",
                    });
                state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(250);
                return;
            }

            UpdateStatus(runner.RecordMarketBoardClosedBeforeTravel());
        }

        if (preparation != null)
        {
            if (!context.IsCurrentWorldAvailable)
            {
                ObserveTravelPreparationOperation(
                    preparation,
                    MarketAcquisitionRouteOperationDisposition.Pending,
                    "Waiting for current world information before travel preparation.",
                    new Dictionary<string, string?>
                    {
                        ["preparationState"] = "CurrentWorldUnavailable",
                    });
                UpdateStatus(runner.RecordCurrentWorldUnavailable());
                state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
                return;
            }

            if (activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            {
                ObserveTravelPreparationOperation(
                    preparation,
                    MarketAcquisitionRouteOperationDisposition.Succeeded,
                    $"Already on {activeStop.WorldName}; travel preparation complete.",
                    new Dictionary<string, string?>
                    {
                        ["preparationState"] = "TargetWorldReached",
                    });
            }
            else
            {
                var preflight = uiAutomation.CheckTravelPreflight();
                if (!preflight.CanSendCommand)
                {
                    UpdateStatus(runner.RecordTravelBlockedByUi(preflight));
                    ObserveTravelPreparationOperation(
                        preparation,
                        MarketAcquisitionRouteOperationDisposition.Pending,
                        preflight.Message,
                        new Dictionary<string, string?>
                        {
                            ["preparationState"] = "UiBlocked",
                            ["blockingAddons"] = string.Join(", ", preflight.BlockingAddons),
                        });
                    state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
                    return;
                }

                ObserveTravelPreparationOperation(
                    preparation,
                    MarketAcquisitionRouteOperationDisposition.Succeeded,
                    $"Travel UI preflight passed for {activeStop.WorldName}.",
                    new Dictionary<string, string?>
                    {
                        ["preparationState"] = "ReadyToTravel",
                    });
            }
        }

        var needsWorldTravel = !activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase);
        var travelOperation = needsWorldTravel
            ? EnsureWorldTravelArrivalOperation(activeStop, currentWorld)
            : null;
        var travelResult = runner.PreparePendingStopForCurrentWorld(
            context.IsCurrentWorldAvailable,
            currentWorld,
            uiAutomation.ProcessCommand);
        if (travelOperation != null && travelResult.Success && runner.ActiveStop?.Status == "TravelCommandSent")
        {
            var lease = activeTravelLease ?? throw new InvalidOperationException("World travel command was accepted without a travel lease.");
            activeTravelLease = lease with { IsOwned = true };
            ObserveWorldTravelOperation(
                travelOperation,
                MarketAcquisitionRouteOperationDisposition.Pending,
                $"Lifestream command accepted for {activeStop.WorldName}; waiting for world arrival.",
                new Dictionary<string, string?>
                {
                    ["commandAccepted"] = "True",
                    ["leaseId"] = activeTravelLease.LeaseId,
                    ["leaseOwnership"] = "Owned",
                });
        }
        else if (travelOperation != null && !travelResult.Success)
        {
            ObserveWorldTravelOperation(
                travelOperation,
                MarketAcquisitionRouteOperationDisposition.Failed,
                travelResult.Message,
                new Dictionary<string, string?>
                {
                    ["commandAccepted"] = "False",
                    ["leaseId"] = activeTravelLease?.LeaseId,
                    ["leaseOwnership"] = "NotOwned",
                });
            activeTravelLease = null;
        }
        UpdateStatus(travelResult);
        if (!travelResult.Success && string.Equals(runner.State, "Failed", StringComparison.OrdinalIgnoreCase))
            UpdateStatus(FailRoute(travelResult.Message));
        state.NextRouteMonitorUtc = clock.UtcNow.AddSeconds(2);
    }

    private MarketAcquisitionRouteOperationSnapshot EnsureTravelPreparationOperation(
        MarketAcquisitionGuidedRouteStop activeStop)
    {
        if (operationExecutor.ActiveSnapshot is { } active)
        {
            if (active.Kind != MarketAcquisitionRouteOperationKind.TravelPreparation)
                throw new InvalidOperationException($"Cannot prepare travel while {active.Kind} operation {active.OperationId} is active.");

            return active;
        }

        var operation = operationExecutor.Begin(new MarketAcquisitionRouteOperationStart
        {
            OperationId = $"{state.ProgressNonce}:travel-preparation:{++operationSequence}",
            Kind = MarketAcquisitionRouteOperationKind.TravelPreparation,
            StartedAtUtc = clock.UtcNow,
            StartedAtMonotonicMilliseconds = clock.MonotonicMilliseconds,
            Timeout = TravelPreparationOperationTimeout,
            TimeoutDisposition = MarketAcquisitionRouteOperationDisposition.Failed,
            TimeoutMessage =
                $"Travel preparation timed out after {TravelPreparationOperationTimeout.TotalSeconds:N0}s while waiting to travel to {activeStop.WorldName}.",
            Context = new Dictionary<string, string?>
            {
                ["world"] = activeStop.WorldName,
                ["timeoutPolicySource"] = "NightmareToolsDefaultBoundProvisional",
            },
        });
        runner.RecordRouteOperationSnapshot(operation);
        return operation;
    }

    private MarketAcquisitionRouteOperationSnapshot ObserveTravelPreparationOperation(
        MarketAcquisitionRouteOperationSnapshot operation,
        MarketAcquisitionRouteOperationDisposition disposition,
        string message,
        IReadOnlyDictionary<string, string?> details)
    {
        var result = operationExecutor.Observe(
            new MarketAcquisitionRouteOperationObservation
            {
                OperationId = operation.OperationId,
                Disposition = disposition,
                Message = message,
                Details = details,
            },
            clock.UtcNow,
            clock.MonotonicMilliseconds);
        if (!result.Accepted || result.Snapshot == null)
            throw new InvalidOperationException(result.Message);

        runner.RecordRouteOperationSnapshot(result.Snapshot);
        return result.Snapshot;
    }

    private MarketAcquisitionRouteOperationSnapshot EnsureWorldTravelArrivalOperation(
        MarketAcquisitionGuidedRouteStop activeStop,
        string? currentWorld)
    {
        if (operationExecutor.ActiveSnapshot is { } active)
        {
            if (active.Kind != MarketAcquisitionRouteOperationKind.Travel)
                throw new InvalidOperationException($"Cannot start world travel while {active.Kind} operation {active.OperationId} is active.");

            return active;
        }

        var sourceWorld = currentWorld ??
            throw new InvalidOperationException("Current world is required before starting world travel.");
        var sourceDataCenter = MarketAcquisitionWorldCatalog.ResolveDataCenter(sourceWorld);
        var targetDataCenter = MarketAcquisitionWorldCatalog.ResolveDataCenter(activeStop.WorldName);
        var isDataCenterTravel = !sourceDataCenter.Equals(targetDataCenter, StringComparison.OrdinalIgnoreCase);
        var timeout = ResolveWorldTravelArrivalOperationTimeout(sourceWorld, activeStop.WorldName);
        var travelKind = isDataCenterTravel ? "Data center travel" : "World travel";
        var operation = operationExecutor.Begin(new MarketAcquisitionRouteOperationStart
        {
            OperationId = $"{state.ProgressNonce}:world-travel:{++operationSequence}",
            Kind = MarketAcquisitionRouteOperationKind.Travel,
            StartedAtUtc = clock.UtcNow,
            StartedAtMonotonicMilliseconds = clock.MonotonicMilliseconds,
            Timeout = timeout,
            TimeoutDisposition = MarketAcquisitionRouteOperationDisposition.Failed,
            TimeoutMessage =
                $"{travelKind} timed out after {timeout.TotalMinutes:N0} minutes while waiting to arrive on {activeStop.WorldName}.",
            Context = new Dictionary<string, string?>
            {
                ["world"] = activeStop.WorldName,
                ["sourceWorld"] = sourceWorld,
                ["sourceDataCenter"] = sourceDataCenter,
                ["targetDataCenter"] = targetDataCenter,
                ["travelScope"] = isDataCenterTravel ? "CrossDataCenter" : "SameDataCenter",
                ["timeoutSeconds"] = timeout.TotalSeconds.ToString("N0"),
                ["dependency"] = "Lifestream",
                ["timeoutPolicySource"] = "MarketAcquisitionTravelScope",
            },
        });
        activeTravelLease = new MarketAcquisitionTravelLease
        {
            LeaseId = $"{state.ProgressNonce}:lifestream:{operation.OperationId}",
            RouteRunId = state.ProgressNonce,
            OperationId = operation.OperationId,
            Dependency = "Lifestream",
            TargetWorld = activeStop.WorldName,
            IsOwned = false,
        };
        runner.RecordRouteOperationSnapshot(operation);
        runner.RecordRouteCleanup(
            "Travel lease created before Lifestream command dispatch.",
            CreateTravelCleanupDetails(activeTravelLease, "Start", "LeaseCreated", unresolvedExternalAutomation: false));
        return operation;
    }

    internal static TimeSpan ResolveWorldTravelArrivalOperationTimeout(string sourceWorld, string targetWorld)
    {
        var sourceDataCenter = MarketAcquisitionWorldCatalog.ResolveDataCenter(sourceWorld);
        var targetDataCenter = MarketAcquisitionWorldCatalog.ResolveDataCenter(targetWorld);
        return sourceDataCenter.Equals(targetDataCenter, StringComparison.OrdinalIgnoreCase)
            ? SameDataCenterWorldTravelArrivalOperationTimeout
            : DataCenterTravelArrivalOperationTimeout;
    }

    private MarketAcquisitionRouteOperationSnapshot ObserveWorldTravelOperation(
        MarketAcquisitionRouteOperationSnapshot operation,
        MarketAcquisitionRouteOperationDisposition disposition,
        string message,
        IReadOnlyDictionary<string, string?> details)
    {
        var result = operationExecutor.Observe(
            new MarketAcquisitionRouteOperationObservation
            {
                OperationId = operation.OperationId,
                Disposition = disposition,
                Message = message,
                Details = details,
            },
            clock.UtcNow,
            clock.MonotonicMilliseconds);
        if (!result.Accepted || result.Snapshot == null)
            throw new InvalidOperationException(result.Message);

        runner.RecordRouteOperationSnapshot(result.Snapshot);
        return result.Snapshot;
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
            if (operationExecutor.ActiveSnapshot is { Kind: MarketAcquisitionRouteOperationKind.Travel } travel)
            {
                ObserveWorldTravelOperation(
                    travel,
                    MarketAcquisitionRouteOperationDisposition.Pending,
                    $"Waiting for Lifestream arrival on {activeStop.WorldName}; current world is {currentWorld}.",
                    new Dictionary<string, string?>
                    {
                        ["currentWorld"] = currentWorld,
                        ["leaseId"] = activeTravelLease?.LeaseId,
                    });
            }
            UpdateStatus(runner.RecordCurrentWorld(currentWorld));
            return;
        }

        if (operationExecutor.ActiveSnapshot is { Kind: MarketAcquisitionRouteOperationKind.Travel } travelArrival)
        {
            ObserveWorldTravelOperation(
                travelArrival,
                MarketAcquisitionRouteOperationDisposition.Succeeded,
                $"Confirmed Lifestream arrival on {activeStop.WorldName}.",
                new Dictionary<string, string?>
                {
                    ["currentWorld"] = currentWorld,
                    ["leaseId"] = activeTravelLease?.LeaseId,
                    ["leaseOwnership"] = activeTravelLease?.IsOwned.ToString(),
                });
            activeTravelLease = null;
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
            if (approachResult.ActionKind == MarketBoardApproachActionKind.NavigationStarted)
            {
                activeApproachLease = new MarketAcquisitionApproachLease
                {
                    LeaseId = $"{state.ProgressNonce}:vnavmesh:{++operationSequence}",
                    RouteRunId = state.ProgressNonce,
                    OperationId = $"{state.ProgressNonce}:market-board-approach:{operationSequence}",
                    Dependency = "VNavmesh",
                };
                runner.RecordRouteCleanup(
                    "Route-owned vnavmesh approach started.",
                    new Dictionary<string, string?>
                    {
                        ["routeRunId"] = activeApproachLease.RouteRunId,
                        ["operationId"] = activeApproachLease.OperationId,
                        ["leaseId"] = activeApproachLease.LeaseId,
                        ["dependency"] = activeApproachLease.Dependency,
                        ["adapterCapability"] = "GlobalPathStopOnly",
                    });
            }
            else if (approachResult.ReadyToSearch)
            {
                activeApproachLease = null;
            }
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
            var operation = EnsureItemSearchOperation(activeLine, currentWorld);
            var deadline = operationExecutor.CheckDeadline(clock.UtcNow, clock.MonotonicMilliseconds);
            if (deadline.Snapshot is { IsTerminal: true } timedOut)
            {
                marketBoard.AbandonBrowse(timedOut.Message);
                runner.RecordRouteOperationSnapshot(timedOut);
                UpdateStatus(FailRoute(timedOut.Message));
                return;
            }

            var searchResult = marketBoard.SearchItem(activeLine.ItemId, activeLine.ItemName);
            UpdateStatus(runner.RecordSearchResult(searchResult, clock.UtcNow));
            var operationResult = ObserveItemSearchOperation(operation, searchResult);
            if (operationResult.Disposition == MarketAcquisitionRouteOperationDisposition.Pending)
            {
                state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
                return;
            }

            if (operationResult.Disposition != MarketAcquisitionRouteOperationDisposition.Succeeded)
            {
                UpdateStatus(FailRoute(operationResult.Message));
                return;
            }
        }

        state.NextRouteMonitorUtc = clock.UtcNow;
        UpdateStatus(runner.BeginProbe($"Arrived on {currentWorld}. Reading live listings for {FormatItem(GetActiveRouteLine(claimed))}."));
        state.ProbeRunning = true;
        ProbeLiveMarketBoard();
    }

    private MarketAcquisitionRouteOperationSnapshot EnsureItemSearchOperation(
        MarketAcquisitionRequestView activeLine,
        string currentWorld)
    {
        if (operationExecutor.ActiveSnapshot is { } active)
        {
            if (active.Kind != MarketAcquisitionRouteOperationKind.ItemSearch)
                throw new InvalidOperationException($"Cannot start item search while {active.Kind} operation {active.OperationId} is active.");

            return active;
        }

        var operation = operationExecutor.Begin(new MarketAcquisitionRouteOperationStart
        {
            OperationId = $"{state.ProgressNonce}:item-search:{++operationSequence}",
            Kind = MarketAcquisitionRouteOperationKind.ItemSearch,
            StartedAtUtc = clock.UtcNow,
            StartedAtMonotonicMilliseconds = clock.MonotonicMilliseconds,
            Timeout = MarketBoardItemSearchOperationTimeout,
            TimeoutDisposition = MarketAcquisitionRouteOperationDisposition.Failed,
            TimeoutMessage =
                $"Market board item search timed out after {MarketBoardItemSearchOperationTimeout.TotalSeconds:N0}s while waiting for listings for {FormatItem(activeLine)}.",
            Context = new Dictionary<string, string?>
            {
                ["world"] = currentWorld,
                ["lineId"] = runner.ActiveStop?.ActiveItemSubtask?.LineId,
                ["itemId"] = activeLine.ItemId.ToString(),
                ["itemName"] = activeLine.ItemName,
            },
        });
        runner.RecordRouteOperationSnapshot(operation);
        return operation;
    }

    private MarketAcquisitionRouteOperationSnapshot ObserveItemSearchOperation(
        MarketAcquisitionRouteOperationSnapshot operation,
        MarketBoardItemSearchResult searchResult)
    {
        var disposition = ClassifyItemSearchResult(searchResult);
        var message = disposition == MarketAcquisitionRouteOperationDisposition.Failed &&
                      !string.Equals(searchResult.Status, "SearchSubmitFailed", StringComparison.OrdinalIgnoreCase)
            ? $"Market board item search returned unsupported terminal status {searchResult.Status}. {searchResult.Message}"
            : searchResult.Message;
        var result = operationExecutor.Observe(
            new MarketAcquisitionRouteOperationObservation
            {
                OperationId = operation.OperationId,
                Disposition = disposition,
                Message = message,
                Details = searchResult.Details,
            },
            clock.UtcNow,
            clock.MonotonicMilliseconds);
        if (!result.Accepted || result.Snapshot == null)
            throw new InvalidOperationException(result.Message);

        runner.RecordRouteOperationSnapshot(result.Snapshot);
        return result.Snapshot;
    }

    internal static MarketAcquisitionRouteOperationDisposition ClassifyItemSearchResult(
        MarketBoardItemSearchResult searchResult)
    {
        ArgumentNullException.ThrowIfNull(searchResult);
        return searchResult.ReadyForListings
            ? MarketAcquisitionRouteOperationDisposition.Succeeded
            : searchResult.IsInProgress
                ? MarketAcquisitionRouteOperationDisposition.Pending
                : MarketAcquisitionRouteOperationDisposition.Failed;
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
        var candidateRead = activeSubtask is null
            ? state.MarketBoardReadResult
            : ExcludeSunkExactAcquisitionListings(state.MarketBoardReadResult);
        state.LiveCandidatePlan = canBuildLiveCandidatePlan
            ? activeSubtask == null
                ? MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(activeLine, plan, currentWorld, state.MarketBoardReadResult, totals.PurchasedQuantity, totals.SpentGil)
                : MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(activeLine, plan, activeSubtask, currentWorld, candidateRead, totals.PurchasedQuantity, totals.SpentGil)
            : null;
        if (state.LiveCandidatePlan != null &&
            TryContinueVisibleListingRead(
                currentWorld,
                state.MarketBoardReadResult,
                state.LiveCandidatePlan,
                requireRunningRoute: recordRouteResult))
            return;

        if (state.MarketBoardReadResult.IsFresh)
            ReportMarketObservation(claimed, activeLine, activeSubtask, currentWorld, state.MarketBoardReadResult);

        MarketAcquisitionRouteActionResult? authorityFailure = null;
        if (recordRouteResult && !state.EvidenceRefreshOnly && activeSubtask is not null && state.LiveCandidatePlan is not null && exactAcquisitionAuthority is not null)
        {
            authorityFailure = EnforceExactAcquisitionCandidateAuthority(activeSubtask, state.LiveCandidatePlan);
        }
        var probeResult = authorityFailure is null && recordRouteResult && runner.IsRunning && runner.ActiveStop is { Status: "Arrived" } && state.LiveCandidatePlan != null
            ? runner.RecordProbe(currentWorld, state.LiveCandidatePlan, allowPurchases: !state.EvidenceRefreshOnly)
            : null;
        if (probeResult?.Success == true && state.LiveCandidatePlan != null)
        {
            evidence.RecordProbeVisit(currentWorld, activeLine, activeSubtask, state.LiveCandidatePlan, claimed.Id, state.ProgressNonce);
            if (state.EvidenceRefreshOnly && runner.State.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                ReportRouteProgress(includeEvidenceRefresh: true);
        }
        EvaluateExactAcquisitionRouteCompletion();

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

        var freshRead = ReadPurchaseListings(activeLine, currentWorld);
        state.MarketBoardReadResult = freshRead;
        if (freshRead.Status is not ("Ready" or "NoListings"))
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
        var candidateRead = activeStop.ActiveItemSubtask is null
            ? freshRead
            : ExcludeSunkExactAcquisitionListings(freshRead);
        state.LiveCandidatePlan = activeStop.ActiveItemSubtask == null
            ? MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(activeLine, plan, currentWorld, freshRead, totals.PurchasedQuantity, totals.SpentGil)
            : MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(activeLine, plan, activeStop.ActiveItemSubtask, currentWorld, candidateRead, totals.PurchasedQuantity, totals.SpentGil);
        if (TryContinueVisibleListingRead(currentWorld, freshRead, state.LiveCandidatePlan))
            return;

        if (exactAcquisitionAuthority is not null && activeStop.ActiveItemSubtask is { } authoritySubtask)
        {
            if (EnforceExactAcquisitionCandidateAuthority(authoritySubtask, state.LiveCandidatePlan) is not null)
            {
                return;
            }
        }

        if (state.ExecutionMode == MarketAcquisitionExecutionMode.DryRun)
        {
            SimulateDryRunPurchase(currentWorld);
            return;
        }

        var selection = purchase.ExecuteFirstCandidate(state.LiveCandidatePlan, freshRead);
        var now = clock.UtcNow;
        purchaseAutomation.RecordPurchaseSelection(selection, now, MarketBoardPurchaseConfirmationWatchdog);
        if (selection.Status.Equals("PurchaseSelectionSent", StringComparison.OrdinalIgnoreCase))
            state.PurchaseRecoveryPreviousBrowseOperationId = freshRead.BrowseOperationId;
        runner.RecordAutomationSnapshot(CreatePurchaseSelectionSnapshot(selection));

        if (selection.Status.Equals("NoCandidate", StringComparison.OrdinalIgnoreCase))
        {
            if (ShouldFailWorldPurchaseBatchOnNoCandidate(state.LiveCandidatePlan))
            {
                UpdateStatus(FailRoute(state.LiveCandidatePlan.Message));
                ReportRouteProgress();
                return;
            }

            CompleteActiveWorldPurchaseBatch(currentWorld);
            return;
        }

        if (ClassifyPurchaseSelectionOutcome(selection.Status) == MarketBoardAutomationOutcome.Recoverable)
        {
            RequirePurchaseRecoveryRefresh(
                $"Purchase selection will be replanned from refreshed listings: {selection.Message}");
            state.AcquisitionStatus = $"Purchase: {selection.Status}. {selection.Message}";
            state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(250);
            return;
        }

        if (!selection.Status.Equals("PurchaseSelectionSent", StringComparison.OrdinalIgnoreCase) || selection.Candidate == null)
        {
            UpdateStatus(FailRoute($"World purchase batch stopped: {selection.Message}"));
            ReportRouteProgress();
            return;
        }

        purchaseAutomation.ScheduleNextMonitor(now, MarketBoardPurchaseInitialMonitorDelay);
        state.AcquisitionStatus = $"Purchase: {selection.Status}. {selection.Message}";
    }

    private MarketBoardReadResult ReadPurchaseListings(
        MarketAcquisitionRequestView activeLine,
        string currentWorld)
    {
        if (state.UseProjectedMarketBoardSnapshot &&
            state.MarketBoardReadResult is { } projected &&
            projected.ItemId == activeLine.ItemId &&
            projected.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
        {
            state.UseProjectedMarketBoardSnapshot = false;
            return projected;
        }

        state.UseProjectedMarketBoardSnapshot = false;
        if (!state.PurchaseRecoveryRefreshRequired)
            return listingReadAccumulator.Merge(marketBoard.ReadCurrentListings(currentWorld));

        var search = marketBoard.SearchItem(
            activeLine.ItemId,
            activeLine.ItemName,
            MarketBoardItemSearchIntent.RequireFreshBrowse,
            state.PurchaseRecoveryPreviousBrowseOperationId);
        if (!search.ReadyForListings)
            return CreatePurchaseRecoveryPendingRead(activeLine.ItemId, currentWorld, search);

        state.PurchaseRecoveryRefreshRequired = false;
        state.PurchaseRecoveryPreviousBrowseOperationId = null;
        listingReadAccumulator.Clear();
        return listingReadAccumulator.Merge(marketBoard.ReadCurrentListings(currentWorld));
    }

    private static MarketBoardReadResult CreatePurchaseRecoveryPendingRead(
        uint itemId,
        string currentWorld,
        MarketBoardItemSearchResult search)
    {
        var browse = search.BrowseEvidence;
        return new MarketBoardReadResult
        {
            Status = "PurchaseRecoveryRefreshPending",
            Message = $"Refreshing listings after a recoverable purchase failure ({search.Status}). {search.Message}",
            ReadState = MarketBoardListingReadState.Loading,
            ItemId = itemId,
            WorldName = currentWorld,
            ReportedListingCount = browse?.ExpectedListingCount ?? 0,
            CurrentRequestId = browse?.RequestId ?? 0,
            BrowseOperationId = browse?.OperationId ?? string.Empty,
            BrowseHeaderStatus = browse?.HeaderStatus ?? 0,
            BrowseExpectedPageCount = browse?.ExpectedPageCount ?? 0,
            BrowseObservedPageCount = browse?.PageCount ?? 0,
            BrowseHistoryItemId = browse?.HistoryItemId,
        };
    }

    private void RequirePurchaseRecoveryRefresh(string reason)
    {
        if (!state.PurchaseRecoveryRefreshRequired)
            state.PurchaseRecoveryPreviousBrowseOperationId = state.MarketBoardReadResult?.BrowseOperationId;
        state.PurchaseRecoveryRefreshRequired = true;
        state.UseProjectedMarketBoardSnapshot = false;
        listingReadAccumulator.Clear();
        runner.RecordAutomationSnapshot(MarketBoardAutomationSnapshot.Create(
            "BuyListing",
            "Recover",
            "RefreshListings",
            "RefreshRequired",
            MarketBoardAutomationOutcome.Recoverable,
            "RefreshAndReplan",
            new Dictionary<string, string?>
            {
                ["reason"] = reason,
                ["previousBrowseOperationId"] = state.PurchaseRecoveryPreviousBrowseOperationId,
            }));
    }

    private MarketBoardReadResult ExcludeSunkExactAcquisitionListings(MarketBoardReadResult readResult)
    {
        if (exactAcquisitionAuthority is null || exactAcquisitionAuthority.State.SunkPurchases.Count == 0)
            return readResult;

        var remaining = new List<MarketBoardLiveListing>(readResult.Listings.Count);
        foreach (var listing in readResult.Listings)
        {
            if (!exactAcquisitionAuthority.IsSunkListing(listing))
                remaining.Add(listing);
        }

        var excludedCount = readResult.Listings.Count - remaining.Count;
        return excludedCount == 0
            ? readResult
            : readResult with
            {
                ReportedListingCount = Math.Max(0, readResult.ReportedListingCount - excludedCount),
                Listings = remaining,
            };
    }

    public MarketAcquisitionRouteEngineTickResult MonitorMarketBoardPurchase()
    {
        var previousSession = purchaseAutomation.PurchaseSession;
        if (previousSession?.IsActive != true)
            return MarketAcquisitionRouteEngineTickResult.Idle();

        var now = clock.UtcNow;
        if (!purchaseAutomation.IsMonitorDue(now))
            return MarketAcquisitionRouteEngineTickResult.Idle("Waiting for purchase monitor tick.");

        if (previousSession.Phase == MarketBoardPurchaseSessionPhase.WaitingForOutcome &&
            purchase.PurchaseEvidenceState != null)
        {
            return MonitorServerPurchaseEvidence(previousSession, now);
        }

        try
        {
            var canUseServerEvidence = purchase.HasServerPurchaseEvidence;
            var requireServerEvidence = exactAcquisitionAuthority is not null;
            var tick = purchaseAutomation.MonitorPurchase(
                now,
                MarketAcquisitionRoutePacing.PurchaseEvidencePollInterval,
                MarketBoardPurchaseOutcomeWatchdog,
                candidate => canUseServerEvidence || requireServerEvidence
                    ? purchase.TryConfirmPendingPurchase(candidate, CreatePurchaseIntentContext())
                    : purchase.TryConfirmPendingPurchase(candidate),
                () => ReadFreshListingsForFallbackOutcomeVerification(previousSession),
                verifyOutcomeFromListings: !canUseServerEvidence && !requireServerEvidence);
            if (!tick.DidWork)
                return MarketAcquisitionRouteEngineTickResult.Idle("Purchase monitor had no due work.");

            ApplyPurchaseMonitorTick(tick, previousSession);
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, purchaseAutomation.NextMonitorUtc);
        }
        catch (Exception ex)
        {
            purchaseAutomation.RecordMonitorFailure("PurchaseMonitorFailed", ex.Message);
            state.AcquisitionStatus = $"Purchase monitor failed: {ex.Message}";
            FailRoute(state.AcquisitionStatus, ex);
            ReportRouteProgress();
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, purchaseAutomation.NextMonitorUtc);
        }
    }

    private MarketAcquisitionRouteEngineTickResult MonitorServerPurchaseEvidence(
        MarketBoardPurchaseSession session,
        DateTimeOffset nowUtc)
    {
        if (!purchase.HasServerPurchaseEvidence)
        {
            return StopForTerminalPurchaseEvidence(
                "Server purchase evidence became unavailable after confirmation submission; purchase outcome is indeterminate.");
        }

        var advance = purchase.AdvancePurchaseEvidence(nowUtc);
        if (advance.Status == MarketPurchaseEvidenceAdvanceStatus.PersistenceFailed)
            return StopForTerminalPurchaseEvidence(advance.Message);
        var evidenceState = advance.State ?? purchase.PurchaseEvidenceState;
        switch (evidenceState)
        {
            case PendingMarketPurchase:
                purchaseAutomation.ScheduleNextMonitor(nowUtc, MarketAcquisitionRoutePacing.PurchaseEvidencePollInterval);
                state.AcquisitionStatus = "Purchase: waiting for durable server confirmation evidence.";
                return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, purchaseAutomation.NextMonitorUtc);
            case ConfirmedMarketPurchase confirmed:
                return ApplyConfirmedServerPurchase(session, confirmed, nowUtc);
            case TimedOutIndeterminateMarketPurchase timedOut:
                return StopForTerminalPurchaseEvidence(
                    $"Purchase evidence timed out for intent {timedOut.Intent.IntentId}; reconcile outcome before any retry.");
            case ConflictingMarketPurchasePacket conflicting:
                return StopForTerminalPurchaseEvidence(
                    $"A conflicting purchase packet followed intent {conflicting.Intent.IntentId}; reconcile outcome before any retry.");
            default:
                return StopForTerminalPurchaseEvidence(
                    "Durable purchase intent disappeared before terminal server evidence was applied.");
        }
    }

    private MarketAcquisitionRouteEngineTickResult ApplyConfirmedServerPurchase(
        MarketBoardPurchaseSession session,
        ConfirmedMarketPurchase confirmed,
        DateTimeOffset nowUtc)
    {
        var candidate = session.Candidate;
        var intent = confirmed.Intent;
        var lineId = GetActiveRouteLineId(claimedRequest!);
        if (!intent.RouteId.Equals(claimedRequest!.Id, StringComparison.Ordinal) ||
            !intent.RouteRunId.Equals(state.ProgressNonce, StringComparison.Ordinal) ||
            !intent.AttemptId.Equals(state.ProgressNonce, StringComparison.Ordinal) ||
            !intent.LineId.Equals(lineId, StringComparison.Ordinal) ||
            intent.ItemId != candidate.ItemId || intent.IsHighQuality != candidate.IsHq ||
            intent.Quantity != candidate.Quantity || intent.ListingId != candidate.ListingId ||
            intent.RetainerId != candidate.RetainerId || intent.UnitPrice != candidate.UnitPrice ||
            intent.TotalGil != candidate.TotalGil ||
            !intent.WorldName.Equals(candidate.WorldName, StringComparison.OrdinalIgnoreCase))
        {
            return StopForTerminalPurchaseEvidence(
                "Confirmed server evidence does not match the active route, line, world, or exact listing intent.");
        }

        var evidenceResolved = false;
        try
        {
            var projectedRead = MarketBoardPurchaseSnapshotProjector.ApplyConfirmedPurchase(
                state.MarketBoardReadResult ??
                throw new InvalidOperationException("The authoritative purchase snapshot is unavailable."),
                candidate);
            var nextWorldQuantity = checked(state.ActiveWorldPurchasedQuantity + candidate.Quantity);
            var nextWorldGil = checked(state.ActiveWorldSpentGil + candidate.TotalGil);
            var nextLineQuantity = checked(state.ActiveLinePurchasedQuantity + candidate.Quantity);
            var nextLineGil = checked(state.ActiveLineSpentGil + candidate.TotalGil);
            if (exactAcquisitionAuthority is not null)
            {
                var activePlan = runner.ActivePlan ?? throw new InvalidOperationException("Active purchase plan is unavailable.");
                exactAcquisitionAuthority.RecordPurchase(lineId, candidate, activePlan);
            }
            var resolved = purchase.ResolvePurchaseEvidence(
                intent.IntentId,
                MarketPurchaseTerminalDisposition.AppliedExactlyOnce,
                nowUtc,
                $"Applied confirmed market purchase for listing {candidate.ListingId} exactly once.");
            if (!resolved.IsResolved)
                return StopForTerminalPurchaseEvidence(resolved.Message);
            evidenceResolved = true;

            state.MarketBoardReadResult = projectedRead;
            state.UseProjectedMarketBoardSnapshot = true;
            state.PurchaseRecoveryRefreshRequired = false;
            state.PurchaseRecoveryPreviousBrowseOperationId = null;
            state.ActiveWorldPurchasedQuantity = nextWorldQuantity;
            state.ActiveWorldSpentGil = nextWorldGil;
            state.ActiveLinePurchasedQuantity = nextLineQuantity;
            state.ActiveLineSpentGil = nextLineGil;
            state.AcquisitionStatus = "Purchase: confirmed by server packet; continuing from the remembered listing snapshot.";
            var checkpointRequested = ReportConfirmedPurchase(candidate, state.ActiveLinePurchasedQuantity, state.ActiveLineSpentGil);
            ClearMarketBoardAutomationState();
            if (checkpointRequested)
                PauseForShardCheckpoint();
            else
                BeginNextWorldPurchase();
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, state.NextRouteMonitorUtc);
        }
        catch (Exception exception)
        {
            if (evidenceResolved)
            {
                return StopAfterAppliedPurchaseFailure(
                    $"The purchase was confirmed and recorded, but route continuation failed: {exception.Message}");
            }
            return StopForTerminalPurchaseEvidence(
                $"Confirmed purchase could not be applied safely: {exception.Message}");
        }
    }

    private MarketAcquisitionRouteEngineTickResult StopAfterAppliedPurchaseFailure(string message)
    {
        ClearMarketBoardAutomationState();
        state.ManualRecoveryBlockedReason = null;
        state.MarketBoardReadResult = null;
        state.MarketBoardReconciliation = null;
        state.LiveCandidatePlan = null;
        state.UseProjectedMarketBoardSnapshot = false;
        state.PurchaseRecoveryRefreshRequired = false;
        state.PurchaseRecoveryPreviousBrowseOperationId = null;
        exactAcquisitionAuthority?.RequestRecovery(message);
        state.AcquisitionStatus = message;
        UpdateStatus(FailRoute(message));
        state.ManualRecoveryBlockedReason = null;
        ReportRouteProgress();
        return MarketAcquisitionRouteEngineTickResult.Worked(message, state.NextRouteMonitorUtc);
    }

    private MarketAcquisitionRouteEngineTickResult StopForTerminalPurchaseEvidence(string message)
    {
        exactAcquisitionAuthority?.Pause(message);
        state.AcquisitionStatus = message;
        UpdateStatus(FailRoute(message));
        ClearMarketBoardAutomationState();
        ReportRouteProgress();
        return MarketAcquisitionRouteEngineTickResult.Worked(message, state.NextRouteMonitorUtc);
    }

    private MarketPurchaseIntentContext CreatePurchaseIntentContext()
    {
        var claimed = claimedRequest ?? throw new InvalidOperationException("No claimed route can arm purchase evidence.");
        return new()
        {
            RouteId = claimed.Id,
            RouteRunId = state.ProgressNonce,
            AttemptId = state.ProgressNonce,
            LineId = GetActiveRouteLineId(claimed),
            EvidenceTimeout = MarketBoardPurchaseOutcomeWatchdog,
        };
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
            exactAcquisitionAuthority?.RecordPurchase(GetActiveRouteLineId(claimedRequest!), candidate, runner.ActivePlan);
            var checkpointRequested = ReportConfirmedPurchase(candidate, state.ActiveLinePurchasedQuantity, state.ActiveLineSpentGil);
            state.UseProjectedMarketBoardSnapshot =
                state.MarketBoardReadResult is { IsFresh: true, Status: "Ready" or "NoListings" };
            ClearMarketBoardAutomationState();
            if (checkpointRequested)
                PauseForShardCheckpoint();
            else if (state.MarketBoardReadResult?.Status is "MarketBoardNotOpen" or "NoListings")
                CompleteActiveWorldPurchaseBatch(context.GetCurrentWorldName());
            else
                BeginNextWorldPurchase();
        }
        else if (!session.IsActive)
        {
            if (!session.ConfirmationWasSubmitted &&
                session.Status.Equals("ConfirmationTimeout", StringComparison.OrdinalIgnoreCase))
            {
                RequirePurchaseRecoveryRefresh(session.Message);
                ClearMarketBoardAutomationState();
                state.AcquisitionStatus =
                    $"Purchase confirmation did not complete; refreshing the snapshot and retrying safely. {session.Message}";
                state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
                return;
            }

            var message = $"World purchase batch stopped: {session.Message}";
            exactAcquisitionAuthority?.Pause(message);
            UpdateStatus(FailRoute(message));
            ReportRouteProgress();
        }
    }

    private void CompleteActiveWorldPurchaseBatch(string currentWorld)
    {
        evidence.Flush();
        var activeSubtask = runner.ActiveStop?.ActiveItemSubtask;
        if (activeSubtask != null)
        {
            var lineStatus = ResolveZeroPurchaseLineStatus(state.LiveCandidatePlan, state.ActiveLinePurchasedQuantity, state.ActiveLineSpentGil);
            ReportAcquisitionLineProgress(activeSubtask, lineStatus, state.ActiveLinePurchasedQuantity, state.ActiveLineSpentGil,
                state.ExecutionMode == MarketAcquisitionExecutionMode.DryRun
                    ? $"Dry run for {FormatItem(GetActiveRouteLine(claimedRequest!))} on {currentWorld}: would purchase {state.ActiveLinePurchasedQuantity:N0}, would spend {state.ActiveLineSpentGil:N0} gil."
                    : $"Completed {FormatItem(GetActiveRouteLine(claimedRequest!))} on {currentWorld}: purchased {state.ActiveLinePurchasedQuantity:N0}, spent {state.ActiveLineSpentGil:N0} gil.");
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
        var shouldCloseMarketBoard =
            MarketAcquisitionRoutePacing.ShouldCloseMarketBoardForNextStop(currentWorld, nextStop);
        if (shouldCloseMarketBoard)
            uiAutomation.TryCloseMarketBoardWindows();
        if (shouldCloseMarketBoard)
        {
            state.ActiveWorldPurchasedQuantity = 0;
            state.ActiveWorldSpentGil = 0;
            state.ActiveWorldPurchaseBatchWorld = null;
            state.ActivePurchaseLineId = null;
            state.ActiveLinePurchasedQuantity = 0;
            state.ActiveLineSpentGil = 0;
        }
        else if (activeSubtask != null && nextStop is not null && nextStop.ActiveItemSubtask != null &&
                 !activeSubtask.LineId.Equals(nextStop.ActiveItemSubtask.LineId, StringComparison.Ordinal))
        {
            ResetMarketBoardStateForNextRouteItem();
            state.ActiveWorldPurchaseBatchWorld = nextStop.WorldName;
        }

        ReportRouteProgress();
        EvaluateExactAcquisitionRouteCompletion();
        if (runner.ActiveStop is null)
            shardCheckpoints?.RequestFinalCheckpoint();
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
        state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(250);
    }

    private void ClearMarketBoardAutomationState()
    {
        listingReadAccumulator.Clear();
        purchaseAutomation.Clear();
        if (!state.PurchaseRecoveryRefreshRequired)
            state.PurchaseRecoveryPreviousBrowseOperationId = null;
    }

    private MarketBoardReadResult ReadFreshListingsForFallbackOutcomeVerification(MarketBoardPurchaseSession session)
    {
        var candidate = session.Candidate;
        var itemName = runner.RetainedActiveStop?.ActiveItemSubtask?.ItemName ??
                       claimedRequest?.ItemName;
        var search = marketBoard.SearchItem(
            candidate.ItemId,
            itemName,
            MarketBoardItemSearchIntent.RequireFreshBrowse,
            state.PurchaseRecoveryPreviousBrowseOperationId);
        if (search.ReadyForListings)
            return marketBoard.ReadCurrentListings(context.GetCurrentWorldName());

        var browse = search.BrowseEvidence;
        return new MarketBoardReadResult
        {
            Status = "FallbackOutcomeRefreshPending",
            Message = $"Server purchase evidence is unavailable; refreshing listings to reconcile the outcome ({search.Status}). {search.Message}",
            ReadState = MarketBoardListingReadState.Loading,
            ItemId = candidate.ItemId,
            WorldName = context.GetCurrentWorldName(),
            ReportedListingCount = browse?.ExpectedListingCount ?? 0,
            CurrentRequestId = browse?.RequestId ?? 0,
            BrowseOperationId = browse?.OperationId ?? string.Empty,
            BrowseHeaderStatus = browse?.HeaderStatus ?? 0,
            BrowseExpectedPageCount = browse?.ExpectedPageCount ?? 0,
            BrowseObservedPageCount = browse?.PageCount ?? 0,
            BrowseHistoryItemId = browse?.HistoryItemId,
        };
    }

    private void SimulateDryRunPurchase(string currentWorld)
    {
        var candidates = new List<MarketBoardPurchaseCandidate>();
        foreach (var row in state.LiveCandidatePlan!.Rows)
        {
            if (row.Decision.Equals("WouldBuy", StringComparison.OrdinalIgnoreCase) &&
                MarketBoardListingIntegrity.IsRealListing(row.LiveListing))
                candidates.Add(MarketBoardPurchaseCandidate.FromLiveListing(row.LiveListing));
        }

        if (candidates.Count == 0)
        {
            if (ShouldFailWorldPurchaseBatchOnNoCandidate(state.LiveCandidatePlan!))
            {
                UpdateStatus(FailRoute(state.LiveCandidatePlan!.Message));
                return;
            }

            CompleteActiveWorldPurchaseBatch(currentWorld);
            return;
        }

        var activeSubtask = runner.ActiveStop?.ActiveItemSubtask;
        var lineId = activeSubtask?.LineId ?? GetActiveRouteLineId(claimedRequest!);
        foreach (var candidate in candidates)
        {
            state.ActiveWorldPurchasedQuantity = checked(state.ActiveWorldPurchasedQuantity + candidate.Quantity);
            state.ActiveWorldSpentGil = checked(state.ActiveWorldSpentGil + candidate.TotalGil);
            state.ActiveLinePurchasedQuantity = checked(state.ActiveLinePurchasedQuantity + candidate.Quantity);
            state.ActiveLineSpentGil = checked(state.ActiveLineSpentGil + candidate.TotalGil);
            exactAcquisitionAuthority?.RecordPurchase(lineId, candidate);
            runner.RecordPurchaseAudit(
                lineId,
                activeSubtask?.ItemName,
                currentWorld,
                candidate.ListingId,
                candidate.RetainerId,
                candidate.Quantity,
                candidate.TotalGil,
                "DryRunWouldPurchase",
                activeSubtask?.Source);
        }

        state.AcquisitionStatus = $"Dry run: would purchase {state.ActiveLinePurchasedQuantity:N0} for {state.ActiveLineSpentGil:N0} gil; no purchase UI was invoked.";
        CompleteActiveWorldPurchaseBatch(currentWorld);
    }

    private void EvaluateExactAcquisitionRouteCompletion()
    {
        if (runner.ActiveStop is not null || exactAcquisitionAuthority is null || runner.ActivePlan is not { } completedPlan)
            return;
        exactAcquisitionAuthority.EvaluateRouteEnd(completedPlan);
        state.AcquisitionStatus = exactAcquisitionAuthority.State.Message;
    }

    private bool ReportConfirmedPurchase(MarketBoardPurchaseCandidate candidate, uint linePurchasedQuantity, uint lineSpentGil)
    {
        var claimed = claimedRequest;
        var activeSubtask = runner.ActiveStop?.ActiveItemSubtask ??
                            runner.RetainedActiveStop?.ActiveItemSubtask;
        if (claimed == null || activeSubtask == null || string.IsNullOrWhiteSpace(claimed.ClaimToken))
            return shardCheckpoints?.RecordConfirmedPurchase(candidate) == true;

        var lineId = string.IsNullOrWhiteSpace(activeSubtask.LineId) ? GetActiveRouteLineId(claimed) : activeSubtask.LineId;
        var worldName = string.IsNullOrWhiteSpace(candidate.WorldName) ? context.GetCurrentWorldName() : candidate.WorldName;
        var message = $"Purchased {candidate.Quantity:N0} {FormatItem(GetActiveRouteLine(claimed))} on {worldName} for {candidate.TotalGil:N0} gil.";
        runner.RecordPurchaseAudit(lineId, activeSubtask.ItemName, worldName, candidate.ListingId, candidate.RetainerId, candidate.Quantity, candidate.TotalGil, "Purchased", activeSubtask.Source);
        runner.RecordLineProgress(lineId, activeSubtask.ItemName, "Running", linePurchasedQuantity, lineSpentGil, message, activeSubtask.Source);
        evidence.RecordPurchaseVisit(candidate, activeSubtask, worldName, claimed.Id, state.ProgressNonce);
        ReportPurchaseAudit(claimed, lineId, activeSubtask.ItemName, candidate, worldName, message);
        ReportLineProgress(claimed, lineId, activeSubtask.ItemName, "Running", linePurchasedQuantity, lineSpentGil, message, null);
        return shardCheckpoints?.RecordConfirmedPurchase(candidate) == true;
    }

    private void PauseForShardCheckpoint()
    {
        var pause = runner.Pause();
        state.AcquisitionStatus = pause.Success
            ? shardCheckpoints?.Snapshot.Message ?? "Shard storage checkpoint started."
            : pause.Message;
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

    private void ReportMarketObservation(
        MarketAcquisitionClaimView claimed,
        MarketAcquisitionRequestView activeLine,
        MarketAcquisitionWorldItemSubtask? activeSubtask,
        string worldName,
        MarketBoardReadResult readResult)
    {
        if (!reportDispatcher.CanReport || string.IsNullOrWhiteSpace(claimed.ClaimToken))
            return;

        var lineId = !string.IsNullOrWhiteSpace(activeSubtask?.LineId)
            ? activeSubtask.LineId
            : GetActiveRouteLineId(claimed);
        var itemId = activeSubtask?.ItemId ?? activeLine.ItemId;
        var itemName = activeSubtask?.ItemName ?? activeLine.ItemName;
        var dataCenter = !string.IsNullOrWhiteSpace(activeSubtask?.DataCenter)
            ? activeSubtask.DataCenter
            : MarketAcquisitionWorldCatalog.ResolveDataCenter(worldName);
        reportDispatcher.EnqueueMarketObservation(new MarketAcquisitionMarketObservationReport(
            claimed.Id,
            claimed.ClaimToken,
            state.ProgressNonce,
            ++state.ProgressReportSequence,
            lineId,
            itemId,
            itemName,
            dataCenter,
            worldName,
            clock.UtcNow,
            readResult));
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
                "ConfirmationSubmitted" => "AwaitPurchaseOutcome",
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
        "MarketBoardNotOpen" or
        "InfoProxyUnavailable" or
        "ListingMissing" or
        "ListingListUnavailable" or
        "ListingListNotReady" or
        "SetLastPurchasedFailed" => MarketBoardAutomationOutcome.Recoverable,
        _ => MarketBoardAutomationOutcome.Fatal,
    };

    private static string ChoosePurchaseSelectionNextAction(string status) => status switch
    {
        "PurchaseSelectionSent" => "WaitForConfirmation",
        "NoCandidate" => "CompleteWorldBatch",
        "MarketBoardNotOpen" => "ReopenMarketBoard",
        "InfoProxyUnavailable" or
        "ListingMissing" or
        "ListingListUnavailable" or
        "ListingListNotReady" or
        "SetLastPurchasedFailed" => "RefreshAndReplan",
        _ => "StopRoute",
    };

    public void ReportRouteProgress(bool includeEvidenceRefresh = false)
    {
        if (state.EvidenceRefreshOnly && !includeEvidenceRefresh)
            return;

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

    private void ClearExecutionState(bool preserveExecutionMode = false)
    {
        CleanupOwnedApproach("Replacement");
        CleanupOwnedTravel("Replacement");
        travelInterruptedByCleanup = false;
        CancelActiveOperation("Route execution state reset.");
        state.ResetRouteExecutionState(preserveExecutionMode);
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
        evidence.Flush();
        CleanupOwnedApproach("Dispose");
        CleanupOwnedTravel("Dispose");
        CancelActiveOperation("Route engine disposed.");
        purchaseAutomation.Dispose();
        reportDispatcher.Dispose();
        freshnessCancellation.Cancel();
        freshnessCancellation.Dispose();
        runner.Dispose();
    }

    private void CancelActiveOperation(string message)
    {
        marketBoard.AbandonBrowse(message);
        var cancellation = operationExecutor.Cancel(clock.UtcNow, clock.MonotonicMilliseconds, message);
        if (cancellation.Accepted && cancellation.Snapshot != null)
            runner.RecordRouteOperationSnapshot(cancellation.Snapshot);
    }

    private bool TryFailExpiredOperation()
    {
        if (operationExecutor.ActiveSnapshot == null)
            return false;

        var deadline = operationExecutor.CheckDeadline(clock.UtcNow, clock.MonotonicMilliseconds);
        if (deadline.Snapshot is not { IsTerminal: true } timedOut)
            return false;

        marketBoard.AbandonBrowse(timedOut.Message);
        runner.RecordRouteOperationSnapshot(timedOut);
        UpdateStatus(FailRoute(timedOut.Message));
        return true;
    }

    private MarketAcquisitionRouteActionResult FailRoute(string message, Exception? exception = null)
    {
        CaptureManualRecoverySafetyBlock();
        evidence.Flush();
        CleanupOwnedApproach("Failure");
        CleanupOwnedTravel("Failure");
        CancelActiveOperation($"Route failed; active operation cancelled. {message}");
        return runner.FailRoute(message, exception);
    }

    private void CaptureManualRecoverySafetyBlock()
    {
        var purchaseSession = purchaseAutomation.PurchaseSession;
        state.ManualRecoveryBlockedReason = purchaseSession?.ConfirmationWasSubmitted != true
            ? null
            : $"The retained route cannot resume automatically because listing {purchaseSession.Candidate.ListingId} may have been purchased. Reconcile that purchase outcome before retrying it.";
    }

    private void CleanupOwnedApproach(string terminalReason)
    {
        var lease = activeApproachLease;
        if (lease == null)
            return;

        activeApproachLease = null;
        MarketAcquisitionApproachCleanupResult result;
        try
        {
            result = marketBoard.StopOwnedApproach(lease);
        }
        catch (Exception ex)
        {
            result = new MarketAcquisitionApproachCleanupResult
            {
                Status = MarketAcquisitionTravelCleanupStatus.Failed,
                Message = $"vnavmesh cleanup adapter threw {ex.GetType().Name}: {ex.Message}",
                AdapterCapability = "AdapterException",
            };
        }

        runner.RecordRouteCleanup(
            result.Message,
            new Dictionary<string, string?>
            {
                ["routeRunId"] = lease.RouteRunId,
                ["operationId"] = lease.OperationId,
                ["leaseId"] = lease.LeaseId,
                ["dependency"] = lease.Dependency,
                ["terminalReason"] = terminalReason,
                ["cleanupStatus"] = result.Status.ToString(),
                ["adapterCapability"] = result.AdapterCapability,
            });
    }

    private void CleanupOwnedTravel(string terminalReason)
    {
        var lease = activeTravelLease;
        if (lease == null)
            return;

        var cleanupId = Guid.NewGuid().ToString("N");

        runner.RecordRouteCleanup(
            "Route cleanup requested for Lifestream travel.",
            CreateTravelCleanupDetails(lease, terminalReason, "Requested", unresolvedExternalAutomation: false, cleanupId: cleanupId));

        // Fence the local lease before calling an external dependency. Any observation after this point
        // is either rejected by the operation executor or belongs to a later lease.
        activeTravelLease = null;
        if (operationExecutor.ActiveSnapshot is { } active &&
            string.Equals(active.OperationId, lease.OperationId, StringComparison.Ordinal))
        {
            var cancellation = operationExecutor.Cancel(
                clock.UtcNow,
                clock.MonotonicMilliseconds,
                $"Route cleanup requested ({terminalReason}) for Lifestream travel to {lease.TargetWorld}.");
            if (cancellation.Accepted && cancellation.Snapshot != null)
                runner.RecordRouteOperationSnapshot(cancellation.Snapshot);
        }

        MarketAcquisitionTravelCleanupResult result;
        if (!lease.IsOwned)
        {
            result = new MarketAcquisitionTravelCleanupResult
            {
                Status = MarketAcquisitionTravelCleanupStatus.NothingOwned,
                Message = "Lifestream command was not accepted; no owned travel requires cancellation.",
                AdapterCapability = "LeaseNotOwned",
            };
        }
        else
        {
            try
            {
                result = travelCleanup.CancelOwnedTravel(lease);
            }
            catch (Exception ex)
            {
                result = new MarketAcquisitionTravelCleanupResult
                {
                    Status = MarketAcquisitionTravelCleanupStatus.Failed,
                    Message = $"Lifestream cleanup adapter threw {ex.GetType().Name}: {ex.Message}",
                    UnresolvedExternalAutomation = true,
                    AdapterCapability = "AdapterException",
                    ExceptionType = ex.GetType().FullName,
                };
            }
        }

        var unresolved = result.UnresolvedExternalAutomation ||
                         result.Status is MarketAcquisitionTravelCleanupStatus.Unsupported or MarketAcquisitionTravelCleanupStatus.Unavailable or MarketAcquisitionTravelCleanupStatus.Failed;
        if (unresolved)
            unresolvedTravelLease = lease;

        runner.RecordRouteCleanup(
            result.Message,
            CreateTravelCleanupDetails(lease, terminalReason, result.Status.ToString(), unresolved, result, cleanupId));
        runner.RecordRouteCleanup(
            unresolved
                ? "Route cleanup completed with unresolved external Lifestream automation."
                : "Route cleanup completed.",
            CreateTravelCleanupDetails(lease, terminalReason, "Aggregate", unresolved, result, cleanupId));
    }

    private bool TryReconcileUnresolvedTravelLease(out string message, bool allowIdleResolution = false)
    {
        var lease = unresolvedTravelLease;
        if (lease == null)
        {
            message = string.Empty;
            return true;
        }

        if (context.IsCurrentWorldAvailable &&
            lease.TargetWorld.Equals(context.GetCurrentWorldName(), StringComparison.OrdinalIgnoreCase))
        {
            unresolvedTravelLease = null;
            runner.RecordRouteCleanup(
                $"Resolved previous unsupported Lifestream travel lease after arrival on {lease.TargetWorld}.",
                CreateTravelCleanupDetails(lease, "Reconcile", "ResolvedByArrival", unresolvedExternalAutomation: false));
            message = string.Empty;
            return true;
        }

        var isBusy = false;
        var travelStateAvailable = allowIdleResolution && context.TryIsWorldTravelBusy(out isBusy);
        if (travelStateAvailable && !isBusy)
        {
            unresolvedTravelLease = null;
            runner.RecordRouteCleanup(
                $"Resolved previous unsupported Lifestream travel lease after Lifestream reported idle.",
                CreateTravelCleanupDetails(lease, "ManualRecovery", "ResolvedByIdleObservation", unresolvedExternalAutomation: false));
            message = string.Empty;
            return true;
        }

        if (allowIdleResolution)
        {
            message = travelStateAvailable && isBusy
                ? $"Cannot recover while the previous Lifestream travel to {lease.TargetWorld} is still running."
                : $"Cannot recover because Lifestream travel state is unavailable; the previous travel to {lease.TargetWorld} cannot yet be proven finished.";
            return false;
        }

        message = $"Cannot start a new route while previous Lifestream travel to {lease.TargetWorld} remains unresolved. Confirm arrival on that world before restarting.";
        return false;
    }

    private IReadOnlyDictionary<string, string?> CreateTravelCleanupDetails(
        MarketAcquisitionTravelLease lease,
        string terminalReason,
        string status,
        bool unresolvedExternalAutomation,
        MarketAcquisitionTravelCleanupResult? result = null,
        string? cleanupId = null) =>
        new Dictionary<string, string?>
        {
            ["cleanupId"] = cleanupId ?? Guid.NewGuid().ToString("N"),
            ["routeRunId"] = lease.RouteRunId,
            ["operationId"] = lease.OperationId,
            ["leaseId"] = lease.LeaseId,
            ["dependency"] = lease.Dependency,
            ["targetWorld"] = lease.TargetWorld,
            ["terminalReason"] = terminalReason,
            ["leaseOwnership"] = lease.IsOwned.ToString(),
            ["cleanupStatus"] = status,
            ["unresolvedExternalAutomation"] = unresolvedExternalAutomation.ToString(),
            ["adapterCapability"] = result?.AdapterCapability,
            ["exceptionType"] = result?.ExceptionType,
            ["cleanupRecordedAtUtc"] = clock.UtcNow.ToString("O"),
            ["cleanupRecordedAtMonotonicMilliseconds"] = clock.MonotonicMilliseconds.ToString(),
        };
}
