using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Travel;
using MarketMafioso.MarketAcquisition.ExactAuthority;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngine : IDisposable
{
    private static readonly TimeSpan RouteMonitorInterval = TimeSpan.FromMilliseconds(500);
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
    private readonly IMarketAcquisitionIntelligenceReporter? intelligenceReporter;
    private readonly IMarketAcquisitionInventoryObserver? inventoryObserver;
    private readonly MarketAcquisitionAethernetDeparture? aethernetDeparture;
    private string? lastDepartureStage;
    private readonly IMarketAcquisitionRouteClock clock;
    private readonly string reportPluginInstanceId;
    private readonly string? reportPluginVersion;
    private readonly Func<bool> exhaustiveResearchModeProvider;
    private readonly MarketBoardListingReadAccumulator listingReadAccumulator = new();
    private readonly MarketBoardAutomationController purchaseAutomation = new();
    private readonly MarketAcquisitionRouteOperationExecutor operationExecutor = new();
    private readonly MarketAcquisitionRouteEngineState state = new();
    private CancellationTokenSource freshnessCancellation = new();
    private MarketAcquisitionClaimView? claimedRequest;
    private MarketAcquisitionTravelLease? activeTravelLease;
    private MarketAcquisitionTravelLease? unresolvedTravelLease;
    private bool travelInterruptedByCleanup;
    private long operationSequence;
    private readonly IExactAcquisitionRouteExecutionStateStore exactAcquisitionStateStore;
    private readonly IShardAcquisitionCheckpointCoordinator? shardCheckpoints;
    private ExactAcquisitionRouteAuthoritySession? exactAcquisitionAuthority;
    private ExactAcquisitionDryRunScenario exactAcquisitionDryRunScenario;
    private bool exactAcquisitionDryRunFaultEligible;
    private bool exactAcquisitionDryRunFaultInjected;
    private bool exactAcquisitionDryRunNoViableConsumed;
    private bool purchaseEvidenceRouteBlocked;
    private readonly Dictionary<string, uint> initialOnHandByLine = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> inventoryCoveredPurchases = new(StringComparer.Ordinal);
    private string? reconciledDepartureWorld;
    private string? reconciledSearch;
    private DateTimeOffset? inventoryReflectionWaitStartedUtc;
    private static readonly TimeSpan InventoryReflectionWait = TimeSpan.FromSeconds(10);
    private string? activeLinePurchaseWorld;
    private bool activeSearchAllowsPriceCutoff = true;
    private bool ownsLocalApproachNavigation;
    private string? retainedRecoveryBrowseOperationId;

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
        IShardAcquisitionCheckpointCoordinator? shardCheckpoints = null,
        IMarketAcquisitionReportOutbox? reportDeadLetter = null,
        string? reportPluginInstanceId = null,
        string? reportPluginVersion = null,
        Func<bool>? exhaustiveResearchModeProvider = null,
        IMarketAcquisitionIntelligenceReporter? intelligenceReporter = null,
        IMarketAcquisitionInventoryObserver? inventoryObserver = null,
        MarketAcquisitionAethernetDeparture? aethernetDeparture = null)
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
            reportOutbox,
            deadLetterOutbox: reportDeadLetter);
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.reportPluginInstanceId = reportPluginInstanceId ?? string.Empty;
        this.reportPluginVersion = reportPluginVersion;
        this.exhaustiveResearchModeProvider = exhaustiveResearchModeProvider ?? (() => false);
        this.intelligenceReporter = intelligenceReporter;
        this.inventoryObserver = inventoryObserver;
        this.aethernetDeparture = aethernetDeparture;
        this.exactAcquisitionStateStore = exactAcquisitionStateStore ?? throw new ArgumentNullException(nameof(exactAcquisitionStateStore));
        this.shardCheckpoints = shardCheckpoints;
    }

    public bool IsRouteRunning => runner.IsRunning || state.ProbeRunning;

    public bool IsPurchaseAutomationBusy => purchaseAutomation.IsBusy;

    public bool IsShardCheckpointActive => shardCheckpoints?.IsActive == true;

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
        StatusMessage = state.EvidenceRefreshOnly
            ? $"Evidence-only scan (no purchasing): {runner.StatusMessage}"
            : runner.StatusMessage,
        VisibleAcquisitionStatus = state.EvidenceRefreshOnly
            ? $"Evidence-only scan (no purchasing): {state.AcquisitionStatus}"
            : state.AcquisitionStatus,
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
        CompletionOutcome = runner.CompletionOutcome,
        LatestWorldCompletionSummary = runner.LatestWorldCompletionSummary,
        LastRunDiagnosticSummary = runner.LastRunDiagnosticSummary,
        ExactAcquisitionExecution = exactAcquisitionAuthority?.State,
        ShardCheckpoint = shardCheckpoints?.Snapshot,
        ReportBacklog = reportDispatcher.GetBacklogSnapshot(),
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
        if (!TryPreflightRouteAction(out var evidenceBlockReason))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(evidenceBlockReason));
        if (!TryReconcileUnresolvedTravelLease(out var reconciliationFailure, allowIdleResolution: true))
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
        if (!TryApplyInventoryBaselines(routePlan, exactAcquisitionContract is not null, out routePlan, out var inventoryFailure))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(inventoryFailure));
        if (routePlan.WorldBatches.Count == 0)
        {
            if (exactAcquisitionContract is not null || routePlan.Lines.Count == 0 ||
                !routePlan.Lines.All(line => line.QuantityMode == "TargetQuantity" &&
                    MarketAcquisitionTargetBases.Normalize(line.TargetBasis) == MarketAcquisitionTargetBases.OnHandTotal &&
                    line.InitialOnHandQuantity >= line.RequestedQuantity))
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail("No useful world tasks remain after inventory reconciliation, but targets are not all met."));

            var alreadyMet = runner.CompleteAlreadyMet(routePlan);
            if (executionMode == MarketAcquisitionExecutionMode.Live)
            {
                BeginHostedReportingSession(claimed);
                foreach (var line in routePlan.Lines)
                    ReportLineProgress(claimed, line.LineId, line.ItemName, "Complete", 0, 0, alreadyMet.Message, null);
                ReportRouteProgress();
            }
            return UpdateStatus(alreadyMet);
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
            BeginHostedReportingSession(claimed);
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
        if (!TryPreflightRouteAction(out var evidenceBlockReason))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(evidenceBlockReason));
        try
        {
            exactAcquisitionAuthority.ValidateCurrentDocument(workbenchDocument);
            claimedRequest = remainingClaim;
            ClearExecutionState(preserveExecutionMode: true);
            if (!TryApplyInventoryBaselines(plan, exactAcquisition: true, out plan, out var inventoryFailure))
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(inventoryFailure));
            exactAcquisitionAuthority.BeginRecovery(plan);
            if (state.ExecutionMode == MarketAcquisitionExecutionMode.Live)
                BeginHostedReportingSession(remainingClaim);
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
        if (!TryPreflightRouteAction(out var evidenceBlockReason))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(evidenceBlockReason));
        if (!TryReconcileUnresolvedTravelLease(out var reconciliationFailure, allowIdleResolution: true))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(reconciliationFailure));

        claimedRequest = claimed;
        ClearExecutionState();
        state.EvidenceRefreshOnly = true;
        BeginHostedReportingSession(claimed);
        var result = runner.Start(
            plan,
            enableDiagnostics,
            includeOpportunisticChecks: false,
            diagnosticsLevel: enableDiagnostics ? diagnosticsLevel : MarketAcquisitionRouteDiagnosticsLevel.Off,
            evaluateTargetFulfillment: false);
        state.AcquisitionStatus = result.Success
            ? $"Evidence-only scan started (travel, no purchasing). {result.Message}"
            : result.Message;
        return result;
    }

    public MarketAcquisitionRouteActionResult Pause()
    {
        inventoryReflectionWaitStartedUtc = null;
        travelInterruptedByCleanup = activeTravelLease != null;
        CleanupOwnedTravel("Pause");
        CancelActiveOperation("Route paused; active operation cancelled.");
        var result = runner.Pause();
        if (result.Success)
            exactAcquisitionAuthority?.Pause("External plan route paused; no purchase authority is active.");
        return UpdateStatus(result);
    }

    public MarketAcquisitionRouteActionResult Resume()
    {
        if (!TryPreflightRouteAction(out var evidenceBlockReason))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(evidenceBlockReason));
        if (shardCheckpoints?.IsActive == true)
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(
                $"Route resume is locked while purchased shards are being reconciled: {shardCheckpoints.Snapshot.Message}"));
        if (!TryReconcileUnresolvedTravelLease(out var reconciliationFailure, allowIdleResolution: true))
        {
            var blocked = runner.RecordTravelRecoveryBlocked(reconciliationFailure);
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(blocked.Message));
        }

        if (travelInterruptedByCleanup)
        {
            if (!context.IsCurrentWorldAvailable)
            {
                const string message = "Route resume is waiting for the current world to become available after interrupted travel.";
                var blocked = runner.RecordTravelRecoveryBlocked(message);
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(blocked.Message));
            }

            var reconciled = runner.ReconcileInterruptedTravel(context.GetCurrentWorldName());
            if (!reconciled.Success)
                return UpdateStatus(reconciled);

            travelInterruptedByCleanup = false;
        }

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
        if (!TryPreflightRouteAction(out var evidenceBlockReason))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(evidenceBlockReason));
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
        {
            var blocked = runner.RecordTravelRecoveryBlocked(reconciliationFailure);
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(blocked.Message));
        }

        CleanupOwnedTravel("Recovery");
        reconciledDepartureWorld = null;
        reconciledSearch = null;
        inventoryReflectionWaitStartedUtc = null;
        var retainedBrowse = marketBoard.BrowseSnapshot;
        CancelActiveOperation("Recovering retained route progress.");
        retainedRecoveryBrowseOperationId = string.IsNullOrWhiteSpace(retainedBrowse.OperationId)
            ? null
            : retainedBrowse.OperationId;
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
        BeginHostedReportingSession(claimed);
        freshnessCancellation.Cancel();
        freshnessCancellation.Dispose();
        freshnessCancellation = new CancellationTokenSource();

        var result = runner.Recover(context.GetCurrentWorldName());
        if (result.Success)
        {
            state.ManualRecoveryBlockedReason = null;
            travelInterruptedByCleanup = false;
        }
        return UpdateStatus(result);
    }

    public MarketAcquisitionRouteActionResult Restart(
        MarketAcquisitionPlan plan,
        MarketAcquisitionClaimView claimed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(claimed);
        if (!TryPreflightRouteAction(out var evidenceBlockReason))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(evidenceBlockReason));
        if (state.ManualRecoveryBlockedReason is { } blockedReason)
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(blockedReason));
        CleanupOwnedTravel("Replacement");
        if (!TryReconcileUnresolvedTravelLease(out var reconciliationFailure))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(reconciliationFailure));
        claimedRequest = claimed;
        ClearExecutionState(preserveExecutionMode: true);
        if (!TryApplyInventoryBaselines(plan, exactAcquisitionAuthority is not null, out plan, out var inventoryFailure))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(inventoryFailure));
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
            BeginHostedReportingSession(claimed);
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
        if (!TryPreflightRouteAction(out var evidenceBlockReason))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(evidenceBlockReason));
        if (state.ManualRecoveryBlockedReason is { } blockedReason)
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(blockedReason));
        CleanupOwnedTravel("Replacement");
        if (!TryReconcileUnresolvedTravelLease(out var reconciliationFailure))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(reconciliationFailure));
        claimedRequest = claimed;
        ClearExecutionState(preserveExecutionMode: true);
        if (!TryApplyInventoryBaselines(plan, exactAcquisitionAuthority is not null, out plan, out var inventoryFailure))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(inventoryFailure));
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
            BeginHostedReportingSession(claimed);
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
                    $"Purchase reconciliation could not preserve the reconciled purchase: {exception.Message}");
            }

            var applied = purchase.ResolvePurchaseEvidence(
                intent.IntentId,
                terminal is ConfirmedMarketPurchase
                    ? MarketPurchaseTerminalDisposition.AppliedExactlyOnce
                    : MarketPurchaseTerminalDisposition.ManuallyReconciled,
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
        purchaseEvidenceRouteBlocked = false;
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
        // Stop/Pause may race an async pathfind. Keep draining only the owned
        // leg at monitor cadence, even while ordinary route execution is stopped.
        if (aethernetDeparture?.HasPendingCleanup == true && clock.UtcNow >= state.NextRouteMonitorUtc)
        {
            aethernetDeparture.ReconcileCleanup();
            state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
        }
        if (!TryAdvanceAndPreflightPurchaseEvidence(allowActivePurchaseSession: true))
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, state.NextRouteMonitorUtc);

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
        if (WaitForNativeBrowseCompletion())
            return;
        if (reconciledDepartureWorld != activeStop.WorldName)
        {
            if (!ReconcileInventory(activeStop)) return;
            reconciledDepartureWorld = activeStop.WorldName;
            if (activeStop.ItemSubtasks.Count > 0 && activeStop.ItemSubtasks.All(IsInventorySatisfied))
            {
                while (runner.IsRunning && ReferenceEquals(runner.ActiveStop, activeStop) && activeStop.ActiveItemSubtask is not null)
                    SkipInventorySatisfiedLine(activeStop);
                reconciledDepartureWorld = null;
                return;
            }
        }
        var currentWorld = context.IsCurrentWorldAvailable ? context.GetCurrentWorldName() : null;
        if (aethernetDeparture?.ReconcileCleanup() == false)
        {
            state.AcquisitionStatus = "Waiting for interrupted aethernet departure navigation and Lifestream transfer to become idle.";
            state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
            return;
        }
        // Start and recovery alike: already on the stop's world with a board open,
        // in interaction range, or within vnavmesh approach range means walk to it
        // locally instead of re-running Lifestream's whole market-board trip.
        if (activeTravelLease is null &&
            activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase) &&
            marketBoard.CanApproachMarketBoardLocally())
        {
            if (!context.TryIsWorldTravelBusy(out var travelBusy) || travelBusy)
            {
                state.AcquisitionStatus = "Waiting for confirmed idle Lifestream before the local market-board approach.";
                return;
            }
            var approach = marketBoard.OpenOrApproachMarketBoard();
            if (approach.ActionKind == MarketBoardApproachActionKind.NavigationStarted)
                ownsLocalApproachNavigation = true;
            UpdateStatus(runner.RecordMarketBoardApproach(approach));
            if (!approach.ReadyToSearch)
                return;

            if (!StopOwnedLocalApproach())
                return;
            UpdateStatus(runner.RecordCurrentWorld(currentWorld!));
            HandleArrivedStop(currentWorld!);
            return;
        }
        if (!StopOwnedLocalApproach())
            return;
        // Other worlds, or no board within approach range, delegate the complete
        // trip to `/li <world> mb`.
        const bool requiresTravelPreparation = true;
        MarketAcquisitionRouteOperationSnapshot? preparation = null;
        if (requiresTravelPreparation ||
            operationExecutor.ActiveSnapshot?.Kind == MarketAcquisitionRouteOperationKind.TravelPreparation)
        {
            preparation = EnsureTravelPreparationOperation(activeStop);
        }

        if (requiresTravelPreparation && uiAutomation.TryCloseMarketBoardWindows())
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

        if (runner.MarketBoardCloseRequiredBeforeTravel)
            UpdateStatus(runner.RecordMarketBoardClosedBeforeTravel());

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

            if (aethernetDeparture != null)
            {
                var ready = aethernetDeparture.Tick(activeStop.WorldName, currentWorld!);
                if (lastDepartureStage != aethernetDeparture.Stage)
                {
                    lastDepartureStage = aethernetDeparture.Stage;
                    runner.RecordRouteCleanup(aethernetDeparture.Message, new Dictionary<string, string?>
                    {
                        ["preparationState"] = aethernetDeparture.Stage,
                        ["world"] = activeStop.WorldName,
                        ["networkIdentity"] = aethernetDeparture.NetworkIdentity,
                    });
                }
                if (!ready)
                {
                    ObserveTravelPreparationOperation(preparation,
                        MarketAcquisitionRouteOperationDisposition.Pending,
                        aethernetDeparture.Message,
                        new Dictionary<string, string?> { ["preparationState"] = aethernetDeparture.Stage });
                    state.AcquisitionStatus = aethernetDeparture.Message;
                    state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
                    return;
                }
            }

            var preflight = CheckLifestreamCommandPreflight($"travel to {activeStop.WorldName} market board");
            if (!preflight.CanSendCommand)
            {
                ObserveTravelPreparationOperation(
                    preparation,
                    MarketAcquisitionRouteOperationDisposition.Pending,
                    preflight.Message,
                    new Dictionary<string, string?>
                    {
                        ["preparationState"] = "PreflightBlocked",
                        ["preflightState"] = preflight.State.ToString(),
                        ["busyStateAvailable"] = preflight.BusyStateAvailable.ToString(),
                        ["lifestreamBusy"] = preflight.LifestreamBusy.ToString(),
                        ["blockingAddons"] = string.Join(", ", preflight.BlockingAddons),
                    });
                UpdateStatus(runner.RecordTravelPreflightBlocked(preflight));
                state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
                return;
            }

            ObserveTravelPreparationOperation(
                preparation,
                MarketAcquisitionRouteOperationDisposition.Succeeded,
                $"Travel preflight passed for {activeStop.WorldName} market board.",
                new Dictionary<string, string?>
                {
                    ["preparationState"] = "ReadyToTravel",
                });
        }

        var travelOperation = EnsureWorldTravelArrivalOperation(activeStop, currentWorld);
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
        // The next stop (or recovery) must derive its local leg from observations.
        aethernetDeparture?.Cleanup();
        lastDepartureStage = null;
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

    private MarketAcquisitionTravelPreflightResult CheckLifestreamCommandPreflight(string operation)
    {
        var lifestreamStateAvailable = context.TryIsWorldTravelBusy(out var lifestreamBusy);
        return MarketAcquisitionTravelPreflight.Evaluate(
            uiAutomation.CheckTravelPreflight(),
            lifestreamStateAvailable,
            lifestreamBusy,
            operation);
    }

    private void HandleWorldScopedStop(MarketAcquisitionGuidedRouteStop activeStop, string currentWorld)
    {
        if (string.Equals(activeStop.Status, "TravelCommandSent", StringComparison.OrdinalIgnoreCase))
        {
            var lifestreamStateAvailable = context.TryIsWorldTravelBusy(out var lifestreamBusy);
            var completion = MarketAcquisitionTravelCompletion.Evaluate(
                activeStop.WorldName,
                currentWorld,
                lifestreamStateAvailable,
                lifestreamBusy,
                marketBoard.IsMarketBoardReady());

            if (!completion.IsComplete)
            {
                if (operationExecutor.ActiveSnapshot is { Kind: MarketAcquisitionRouteOperationKind.Travel } pendingTravel)
                {
                    ObserveWorldTravelOperation(
                        pendingTravel,
                        MarketAcquisitionRouteOperationDisposition.Pending,
                        completion.Message,
                        new Dictionary<string, string?>
                        {
                            ["currentWorld"] = currentWorld,
                            ["leaseId"] = activeTravelLease?.LeaseId,
                            ["completionState"] = completion.State.ToString(),
                            ["busyStateAvailable"] = completion.BusyStateAvailable.ToString(),
                            ["lifestreamBusy"] = completion.LifestreamBusy.ToString(),
                            ["marketBoardReady"] = completion.MarketBoardReady.ToString(),
                        });
                }
                UpdateStatus(runner.RecordTravelCompletion(completion));
                state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
                return;
            }

            if (operationExecutor.ActiveSnapshot is { Kind: MarketAcquisitionRouteOperationKind.Travel } travelArrival)
            {
                ObserveWorldTravelOperation(
                    travelArrival,
                    MarketAcquisitionRouteOperationDisposition.Succeeded,
                    completion.Message,
                    new Dictionary<string, string?>
                    {
                        ["currentWorld"] = currentWorld,
                        ["leaseId"] = activeTravelLease?.LeaseId,
                        ["leaseOwnership"] = activeTravelLease?.IsOwned.ToString(),
                        ["marketBoardReady"] = completion.MarketBoardReady.ToString(),
                    });
                activeTravelLease = null;
            }
            UpdateStatus(runner.RecordCurrentWorld(currentWorld));
        }

        if (runner.ActiveStop?.Status == "Arrived")
            HandleArrivedStop(currentWorld);
    }

    private void HandleArrivedStop(string currentWorld)
    {
        var claimed = claimedRequest ?? throw new InvalidOperationException("No dashboard request is accepted.");
        if (!runner.SearchSubmitted)
        {
            var stop = runner.ActiveStop!;
            var searchKey = $"{stop.WorldName}:{stop.CompletedItemSubtaskCount}";
            if (reconciledSearch != searchKey)
            {
                if (!ReconcileInventory(stop)) return;
                reconciledSearch = searchKey;
                if (stop.ActiveItemSubtask is { } task && IsInventorySatisfied(task))
                {
                    SkipInventorySatisfiedLine(stop);
                    return;
                }
            }
            if (!marketBoard.IsMarketBoardReady())
            {
                UpdateStatus(runner.RecordTravelCompletion(
                    MarketAcquisitionTravelCompletion.Evaluate(
                        currentWorld,
                        currentWorld,
                        lifestreamStateAvailable: true,
                        lifestreamBusy: false,
                        marketBoardReady: false)));
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

            var searchResult = marketBoard.SearchItem(activeLine.ItemId, activeLine.ItemName,
                retainedRecoveryBrowseOperationId == null
                    ? MarketBoardItemSearchIntent.PresentOrBrowse
                    : MarketBoardItemSearchIntent.RequireFreshBrowse,
                retainedRecoveryBrowseOperationId);
            if (searchResult.BrowseEvidence is { Owner: MarketBoardBrowseOwner.MarketAcquisition } recoveredBrowse &&
                recoveredBrowse.ItemId == activeLine.ItemId &&
                recoveredBrowse.OperationId != retainedRecoveryBrowseOperationId)
                retainedRecoveryBrowseOperationId = null;
            UpdateStatus(runner.RecordSearchResult(searchResult, clock.UtcNow));
            var operationResult = ObserveItemSearchOperation(operation, searchResult);
            if (operationResult.Disposition == MarketAcquisitionRouteOperationDisposition.Pending)
            {
                if (TryObserveConclusiveListingPrefix(operation, activeLine, currentWorld, searchResult.BrowseEvidence))
                    return;

                state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
                return;
            }

            if (operationResult.Disposition != MarketAcquisitionRouteOperationDisposition.Succeeded)
            {
                if (ShouldPauseForExpiredMarketSession(searchResult))
                {
                    runner.ClearSearchSubmission(operationResult.Message);
                    exactAcquisitionAuthority?.Pause(operationResult.Message);
                    UpdateStatus(runner.Pause(operationResult.Message));
                    return;
                }

                UpdateStatus(FailRoute(operationResult.Message));
                return;
            }
        }

        state.NextRouteMonitorUtc = clock.UtcNow;
        UpdateStatus(runner.BeginProbe($"Arrived on {currentWorld}. Reading live listings for {FormatItem(GetActiveRouteLine(claimed))}."));
        state.ProbeRunning = true;
        ProbeLiveMarketBoard();
    }

    private bool TryObserveConclusiveListingPrefix(
        MarketAcquisitionRouteOperationSnapshot operation,
        MarketAcquisitionRequestView activeLine,
        string currentWorld,
        MarketBoardBrowseSnapshot? browse)
    {
        if (!MarketAcquisitionResearchModePolicy.AllowsConclusivePrefixDeparture(operation.Context))
            return false;

        var plan = runner.ActivePlan;
        var activeSubtask = runner.ActiveStop?.ActiveItemSubtask;
        if (plan == null)
            return false;

        var prefixRead = marketBoard.ReadCurrentListings(currentWorld);
        if (prefixRead.ReadState != MarketBoardListingReadState.FreshPartial)
            return false;

        var totals = ResolveActiveRouteLinePurchaseTotals(activeSubtask);
        if (!MarketAcquisitionLiveCandidatePlanner.TryBuildConclusiveSortedPrefixPlan(
                activeLine,
                plan,
                activeSubtask,
                currentWorld,
                prefixRead,
                totals.PurchasedQuantity,
                totals.SpentGil,
                browse,
                out var prefixPlan))
        {
            return false;
        }

        // Preserve the early decision for diagnostics, not as permission to end
        // transport. Search remains pending until native pages and history finish.
        state.MarketBoardReadResult = prefixRead;
        state.LiveCandidatePlan = prefixPlan;
        state.AcquisitionStatus = $"Eligible price range observed. Waiting for native listings and history. {prefixPlan.Message}";
        state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
        return true;
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

        activeSearchAllowsPriceCutoff = !exhaustiveResearchModeProvider();
        var operation = operationExecutor.Begin(new MarketAcquisitionRouteOperationStart
        {
            OperationId = $"{state.ProgressNonce}:item-search:{++operationSequence}",
            Kind = MarketAcquisitionRouteOperationKind.ItemSearch,
            StartedAtUtc = clock.UtcNow,
            StartedAtMonotonicMilliseconds = clock.MonotonicMilliseconds,
            Timeout = null,
            TimeoutDisposition = MarketAcquisitionRouteOperationDisposition.Failed,
            TimeoutMessage = "Market board item-search liveness is owned by its progress-aware browse gate.",
            Context = new Dictionary<string, string?>
            {
                ["world"] = currentWorld,
                ["lineId"] = runner.ActiveStop?.ActiveItemSubtask?.LineId,
                ["itemId"] = activeLine.ItemId.ToString(),
                ["itemName"] = activeLine.ItemName,
                [MarketAcquisitionResearchModePolicy.ContextKey] =
                    MarketAcquisitionResearchModePolicy.Capture(!activeSearchAllowsPriceCutoff),
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
                      !string.Equals(searchResult.Status, "SearchSubmitFailed", StringComparison.OrdinalIgnoreCase) &&
                      !ShouldPauseForExpiredMarketSession(searchResult)
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

    // A market-board lockout is cleared only by a relog, so the route keeps its stop and waits for the user.
    internal static bool ShouldPauseForExpiredMarketSession(MarketBoardItemSearchResult searchResult) =>
        searchResult.Status is "MarketBoardSessionExpired" or "MarketBoardRateLimited";

    private void ProbeLiveMarketBoardCore(
        MarketAcquisitionPlan plan,
        MarketAcquisitionClaimView claimed,
        bool recordRouteResult)
    {
        var activeLine = GetActiveRouteLine(claimed);
        var activeSubtask = recordRouteResult ? runner.ActiveStop?.ActiveItemSubtask : null;
        var currentWorld = context.GetCurrentWorldName();
        if (recordRouteResult)
            EnsureActiveLinePurchaseAccumulator(activeSubtask, currentWorld);

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
        if (candidatePlan.PriceRangeExhausted || (requireRunningRoute && !runner.IsRunning) ||
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
                TargetBasis = activeSubtask.TargetBasis,
                MaximumOverage = activeSubtask.MaximumOverage,
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
        var purchased = checked(completed.PurchasedQuantity + state.ActiveLinePurchasedQuantity);
        var covered = inventoryCoveredPurchases.GetValueOrDefault(activeSubtask.LineId);
        return new MarketAcquisitionRouteLinePurchaseTotals(
            checked(initialOnHandByLine.GetValueOrDefault(activeSubtask.LineId) + (purchased > covered ? purchased - covered : 0u)),
            checked(completed.SpentGil + state.ActiveLineSpentGil));
    }

    private void EnsureActiveLinePurchaseAccumulator(MarketAcquisitionWorldItemSubtask? activeSubtask, string currentWorld)
    {
        if (activeSubtask == null)
            return;
        if (!ShouldResetLinePurchaseAccumulator(
                state.ActivePurchaseLineId,
                activeLinePurchaseWorld,
                activeSubtask.LineId,
                currentWorld))
            return;

        state.ActivePurchaseLineId = activeSubtask.LineId;
        activeLinePurchaseWorld = currentWorld;
        state.ActiveLinePurchasedQuantity = 0;
        state.ActiveLineSpentGil = 0;
        // Recovery can discard active counters after their stock was folded into bags.
        // Do not let that obsolete coverage swallow receipts from subsequent purchases.
        var covered = Math.Min(inventoryCoveredPurchases.GetValueOrDefault(activeSubtask.LineId),
            runner.GetLinePurchaseTotals(activeSubtask.LineId).PurchasedQuantity);
        inventoryCoveredPurchases[activeSubtask.LineId] = covered;
        runner.RecordReconciledInventory(activeSubtask.LineId,
            initialOnHandByLine.GetValueOrDefault(activeSubtask.LineId), covered);
    }

    internal static bool ShouldResetLinePurchaseAccumulator(
        string? activeLineId,
        string? activeWorld,
        string nextLineId,
        string nextWorld) =>
        !string.Equals(activeLineId, nextLineId, StringComparison.Ordinal) ||
        !string.Equals(activeWorld, nextWorld, StringComparison.OrdinalIgnoreCase);

    private static string FormatItem(MarketAcquisitionRequestView line) =>
        string.IsNullOrWhiteSpace(line.ItemName) ? line.ItemId.ToString() : $"{line.ItemName} ({line.ItemId})";

    public void BeginNextWorldPurchase()
    {
        var failureStage = "purchase-continuation.prepare";
        try
        {
            if (!runner.IsRunning)
                return;
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
            var startingLineOrWorld = !string.Equals(state.ActivePurchaseLineId, activeLineId, StringComparison.Ordinal) ||
                                      !string.Equals(activeLinePurchaseWorld, currentWorld, StringComparison.OrdinalIgnoreCase);
            EnsureActiveLinePurchaseAccumulator(activeStop.ActiveItemSubtask, currentWorld);
            if (startingLineOrWorld)
            {
                if (activeStop.ActiveItemSubtask != null)
                    ReportAcquisitionLineProgress(activeStop.ActiveItemSubtask, "Running", 0, 0,
                        $"Started purchasing {FormatItem(activeLine)} on {activeStop.WorldName}.");
            }

            failureStage = "purchase-continuation.read-listings";
            var freshRead = ReadPurchaseListings(activeLine, currentWorld);
            state.MarketBoardReadResult = freshRead;
            var totals = ResolveActiveRouteLinePurchaseTotals(activeStop.ActiveItemSubtask);
            var candidateRead = activeStop.ActiveItemSubtask is null
                ? freshRead
                : ExcludeSunkExactAcquisitionListings(freshRead);
            failureStage = "purchase-continuation.candidate-planning";
            var priceComplete = TryBuildPriceCompletePlan(activeLine, plan, activeStop.ActiveItemSubtask,
                currentWorld, candidateRead, totals, out var prefixPlan);
            if (!priceComplete && freshRead.Status is not ("Ready" or "NoListings"))
            {
                if (!freshRead.IsFresh || freshRead.Status == "VerifiedListingPrefix")
                {
                    runner.RecordListingReadPending(currentWorld, freshRead);
                    state.AcquisitionStatus = $"Waiting for fresh market listings. {freshRead.Message}";
                    state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
                    return;
                }

                throw new InvalidOperationException(freshRead.Message);
            }

            state.LiveCandidatePlan = priceComplete ? prefixPlan : activeStop.ActiveItemSubtask == null
                ? MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(activeLine, plan, currentWorld, freshRead, totals.PurchasedQuantity, totals.SpentGil)
                : MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(activeLine, plan, activeStop.ActiveItemSubtask, currentWorld, candidateRead, totals.PurchasedQuantity, totals.SpentGil);
            failureStage = "purchase-continuation.visible-listing-continuation";
            if (TryContinueVisibleListingRead(currentWorld, freshRead, state.LiveCandidatePlan))
                return;

            failureStage = "purchase-continuation.candidate-authority";
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

            failureStage = "purchase-continuation.select-candidate";
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
        catch (Exception exception)
        {
            MarketAcquisitionRouteFailure.Annotate(exception, failureStage);
            CaptureFailure(exception);
            throw;
        }
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
        if (search.BrowseEvidence?.IsFailed == true)
            throw new InvalidOperationException(search.Message);
        var recoveredRead = marketBoard.ReadCurrentListings(currentWorld);
        var recoveredPriceComplete = search.BrowseEvidence?.OperationId != state.PurchaseRecoveryPreviousBrowseOperationId &&
            TryBuildPriceCompletePlan(activeLine, runner.ActivePlan!, runner.ActiveStop?.ActiveItemSubtask,
                currentWorld, recoveredRead, ResolveActiveRouteLinePurchaseTotals(runner.ActiveStop?.ActiveItemSubtask), out _);
        if (!search.ReadyForListings && !recoveredPriceComplete)
            return CreatePurchaseRecoveryPendingRead(activeLine.ItemId, currentWorld, search);

        state.PurchaseRecoveryRefreshRequired = false;
        state.PurchaseRecoveryPreviousBrowseOperationId = null;
        listingReadAccumulator.Clear();
        return listingReadAccumulator.Merge(recoveredRead);
    }

    private bool TryBuildPriceCompletePlan(
        MarketAcquisitionRequestView line, MarketAcquisitionPlan plan,
        MarketAcquisitionWorldItemSubtask? subtask, string world, MarketBoardReadResult read,
        MarketAcquisitionRouteLinePurchaseTotals totals, out MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        candidatePlan = new();
        return activeSearchAllowsPriceCutoff && marketBoard.BrowseSnapshot.IsComplete &&
            MarketAcquisitionLiveCandidatePlanner.TryBuildConclusiveSortedPrefixPlan(
                line, plan, subtask, world, read, totals.PurchasedQuantity, totals.SpentGil,
                marketBoard.BrowseSnapshot, out candidatePlan);
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
        if (!TryAdvanceAndPreflightPurchaseEvidence(allowActivePurchaseSession: true))
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, purchaseAutomation.NextMonitorUtc);

        var previousSession = purchaseAutomation.PurchaseSession;
        if (previousSession?.IsActive != true)
            return MarketAcquisitionRouteEngineTickResult.Idle();

        var now = clock.UtcNow;
        if (runner.IsPaused && previousSession.Phase != MarketBoardPurchaseSessionPhase.WaitingForOutcome)
        {
            RequirePurchaseRecoveryRefresh("Route paused before purchase confirmation; the pending selection was fenced and will be replanned after resume.");
            purchaseAutomation.Clear();
            state.AcquisitionStatus = "Route paused; no purchase confirmation authority is active.";
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, state.NextRouteMonitorUtc);
        }
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
        var failureStage = "confirmed-purchase.projection";
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
                failureStage = "confirmed-purchase.authority-checkpoint";
                var activePlan = runner.ActivePlan ?? throw new InvalidOperationException("Active purchase plan is unavailable.");
                exactAcquisitionAuthority.RecordPurchase(lineId, candidate, activePlan);
            }
            failureStage = "confirmed-purchase.evidence-resolution";
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
            failureStage = "confirmed-purchase.receipt-reporting-checkpoint";
            var checkpointRequested = ReportConfirmedPurchase(candidate, state.ActiveLinePurchasedQuantity, state.ActiveLineSpentGil);
            failureStage = "confirmed-purchase.clear-automation";
            ClearMarketBoardAutomationState();
            failureStage = "confirmed-purchase.close-or-next-item-transition";
            if (checkpointRequested)
                PauseForShardCheckpoint();
            else
                BeginNextWorldPurchase();
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, state.NextRouteMonitorUtc);
        }
        catch (Exception exception)
        {
            MarketAcquisitionRouteFailure.Annotate(exception, failureStage, details: new Dictionary<string, string?>
            {
                ["requestId"] = intent.RouteId,
                ["runId"] = intent.RouteRunId,
                ["lineId"] = intent.LineId,
                ["itemId"] = candidate.ItemId.ToString(),
                ["world"] = candidate.WorldName,
                ["listingId"] = candidate.ListingId.ToString(),
                ["intentId"] = intent.IntentId,
                ["evidenceResolved"] = evidenceResolved.ToString(),
                ["purchaseEvidenceApplied"] = evidenceResolved.ToString(),
            });
            CaptureFailure(exception);
            runner.RecordFailure("Confirmed purchase continuation failed.", exception);
            if (evidenceResolved)
            {
                return StopAfterAppliedPurchaseFailure(
                    $"The purchase was confirmed and recorded, but route continuation failed: {exception.Message}", exception);
            }
            return StopForTerminalPurchaseEvidence(
                $"Confirmed purchase could not be applied safely: {exception.Message}", exception);
        }
    }

    private MarketAcquisitionRouteEngineTickResult StopAfterAppliedPurchaseFailure(string message, Exception exception)
    {
        FailureBookkeeping(ClearMarketBoardAutomationState, "failure.clear-automation", exception);
        state.ManualRecoveryBlockedReason = null;
        state.MarketBoardReadResult = null;
        state.MarketBoardReconciliation = null;
        state.LiveCandidatePlan = null;
        state.UseProjectedMarketBoardSnapshot = false;
        state.PurchaseRecoveryRefreshRequired = false;
        state.PurchaseRecoveryPreviousBrowseOperationId = null;
        FailureBookkeeping(() => exactAcquisitionAuthority?.RequestRecovery(message), "failure.request-recovery", exception);
        state.AcquisitionStatus = message;
        UpdateStatus(FailRoute(message, exception));
        state.ManualRecoveryBlockedReason = null;
        FailureBookkeeping(() => ReportRouteProgress(), "failure.report-progress", exception);
        return MarketAcquisitionRouteEngineTickResult.Worked(message, state.NextRouteMonitorUtc);
    }

    private MarketAcquisitionRouteEngineTickResult StopForTerminalPurchaseEvidence(string message, Exception? exception = null)
    {
        FailureBookkeeping(() => exactAcquisitionAuthority?.Pause(message), "failure.pause-authority", exception);
        state.AcquisitionStatus = message;
        UpdateStatus(FailRoute(message, exception));
        FailureBookkeeping(ClearMarketBoardAutomationState, "failure.clear-automation", exception);
        FailureBookkeeping(() => ReportRouteProgress(), "failure.report-progress", exception);
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
            ExpectedItemName = GetActiveRouteLine(claimed).ItemName,
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
                state.MarketBoardReadResult is { IsFresh: true, Status: "Ready" or "NoListings" or "VerifiedListingPrefix" };
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
        var failureStage = "batch.native-browse-completion";
        var failureLine = runner.ActiveStop?.ActiveItemSubtask;
        var failureItemId = failureLine?.ItemId ?? claimedRequest?.ItemId;
        var failureLineId = failureLine?.LineId ?? state.ActivePurchaseLineId;
        try
        {
            if (WaitForNativeBrowseCompletion())
                return;
            var activeSubtask = runner.ActiveStop?.ActiveItemSubtask;
            if (state.MarketBoardReadResult is { IsFresh: true, Status: "NoListings" } emptyRead &&
                claimedRequest is { } emptyClaim && runner.ActivePlan is { } activePlan)
            {
                var line = GetActiveRouteLine(emptyClaim);
                var totals = ResolveActiveRouteLinePurchaseTotals(activeSubtask);
                failureStage = "batch.candidate-planning";
                state.LiveCandidatePlan = activeSubtask is null
                    ? MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(line, activePlan, currentWorld, emptyRead, totals.PurchasedQuantity, totals.SpentGil)
                    : MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(line, activePlan, activeSubtask, currentWorld, emptyRead, totals.PurchasedQuantity, totals.SpentGil);
            }
            // A receipt proves one purchase, not that the remaining price/HQ envelope
            // is empty. Persist the final reconciled observation before advancing.
            failureStage = "batch.final-cleared-world-checkpoint";
            if (state.ExecutionMode == MarketAcquisitionExecutionMode.Live && state.LiveCandidatePlan is { } finalPlan && claimedRequest is { } claimed)
                evidence.RecordProbeVisit(currentWorld, GetActiveRouteLine(claimed), activeSubtask, finalPlan, claimed.Id, state.ProgressNonce);
            failureStage = "batch.final-evidence-flush";
            evidence.Flush();
            failureStage = "batch.line-reporting";
            if (activeSubtask != null)
            {
                var lineStatus = ResolveZeroPurchaseLineStatus(state.LiveCandidatePlan, state.ActiveLinePurchasedQuantity, state.ActiveLineSpentGil);
                ReportAcquisitionLineProgress(activeSubtask, lineStatus, state.ActiveLinePurchasedQuantity, state.ActiveLineSpentGil,
                    state.ExecutionMode == MarketAcquisitionExecutionMode.DryRun
                        ? $"Dry run for {FormatItem(GetActiveRouteLine(claimedRequest!))} on {currentWorld}: would purchase {state.ActiveLinePurchasedQuantity:N0}, would spend {state.ActiveLineSpentGil:N0} gil."
                        : $"Completed {FormatItem(GetActiveRouteLine(claimedRequest!))} on {currentWorld}: purchased {state.ActiveLinePurchasedQuantity:N0}, spent {state.ActiveLineSpentGil:N0} gil.");
            }

            failureStage = "batch.advance";
            if (state.LiveCandidatePlan is { } finalCandidates && claimedRequest is { } finalClaim)
            {
                var line = GetActiveRouteLine(finalClaim);
                var totals = ResolveActiveRouteLinePurchaseTotals(activeSubtask);
                state.LiveCandidatePlan = finalCandidates with
                {
                    RemainingNeed = line.Quantity > totals.PurchasedQuantity ? line.Quantity - totals.PurchasedQuantity : 0u,
                };
            }
            var result = runner.RecordWorldPurchaseBatchComplete(
                currentWorld,
                activeSubtask == null ? state.ActiveWorldPurchasedQuantity : state.ActiveLinePurchasedQuantity,
                activeSubtask == null ? state.ActiveWorldSpentGil : state.ActiveLineSpentGil,
                state.ActiveLinePurchasedQuantity == 0 && state.ActiveLineSpentGil == 0
                    ? ResolveZeroPurchaseLineStatus(state.LiveCandidatePlan, state.ActiveLinePurchasedQuantity, state.ActiveLineSpentGil)
                    : null,
                state.ActiveLinePurchasedQuantity == 0 && state.ActiveLineSpentGil == 0 ? state.LiveCandidatePlan?.Message : null,
                state.LiveCandidatePlan);
            state.AcquisitionStatus = result.Message;
            ClearMarketBoardAutomationState();
            state.ActiveLinePurchasedQuantity = 0;
            state.ActiveLineSpentGil = 0;
            state.ActivePurchaseLineId = null;
            activeLinePurchaseWorld = null;

            failureStage = "batch.close-or-next-item-transition";
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
            }
            else if (activeSubtask != null && nextStop is not null && nextStop.ActiveItemSubtask != null &&
                     !activeSubtask.LineId.Equals(nextStop.ActiveItemSubtask.LineId, StringComparison.Ordinal))
            {
                ResetMarketBoardStateForNextRouteItem();
                state.ActiveWorldPurchaseBatchWorld = nextStop.WorldName;
            }

            failureStage = "batch.progress-reporting-final-checkpoint";
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
        catch (Exception exception)
        {
            MarketAcquisitionRouteFailure.Annotate(exception, failureStage, details: new Dictionary<string, string?>
            {
                ["world"] = currentWorld,
                ["lineId"] = failureLineId,
                ["itemId"] = failureItemId?.ToString(),
            });
            CaptureFailure(exception);
            throw;
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
        var failureStage = "confirmed-purchase.receipt-reporting";
        try
        {
            var claimed = claimedRequest;
            var activeSubtask = runner.ActiveStop?.ActiveItemSubtask ??
                                runner.RetainedActiveStop?.ActiveItemSubtask;
            if (claimed == null || activeSubtask == null || string.IsNullOrWhiteSpace(claimed.ClaimToken))
            {
                failureStage = "confirmed-purchase.shard-checkpoint";
                return shardCheckpoints?.RecordConfirmedPurchase(candidate) == true;
            }

            var lineId = string.IsNullOrWhiteSpace(activeSubtask.LineId) ? GetActiveRouteLineId(claimed) : activeSubtask.LineId;
            var worldName = string.IsNullOrWhiteSpace(candidate.WorldName) ? context.GetCurrentWorldName() : candidate.WorldName;
            var message = $"Purchased {candidate.Quantity:N0} {FormatItem(GetActiveRouteLine(claimed))} on {worldName} for {candidate.TotalGil:N0} gil.";
            runner.RecordPurchaseAudit(lineId, activeSubtask.ItemName, worldName, candidate.ListingId, candidate.RetainerId, candidate.Quantity, candidate.TotalGil, "Purchased", activeSubtask.Source);
            runner.RecordLineProgress(lineId, activeSubtask.ItemName, "Running", linePurchasedQuantity, lineSpentGil, message, activeSubtask.Source);
            failureStage = "confirmed-purchase.visit-checkpoint";
            evidence.RecordPurchaseVisit(candidate, activeSubtask, worldName, claimed.Id, state.ProgressNonce);
            failureStage = "confirmed-purchase.receipt-reporting";
            ReportPurchaseAudit(claimed, lineId, activeSubtask.ItemName, candidate, worldName, message);
            ReportLineProgress(claimed, lineId, activeSubtask.ItemName, "Running", linePurchasedQuantity, lineSpentGil, message, null);
            failureStage = "confirmed-purchase.shard-checkpoint";
            return shardCheckpoints?.RecordConfirmedPurchase(candidate) == true;
        }
        catch (Exception exception)
        {
            MarketAcquisitionRouteFailure.Annotate(exception, failureStage);
            throw;
        }
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
        var lineId = !string.IsNullOrWhiteSpace(activeSubtask?.LineId)
            ? activeSubtask.LineId
            : GetActiveRouteLineId(claimed);
        var itemId = activeSubtask?.ItemId ?? activeLine.ItemId;
        var itemName = activeSubtask?.ItemName ?? activeLine.ItemName;
        var dataCenter = !string.IsNullOrWhiteSpace(activeSubtask?.DataCenter)
            ? activeSubtask.DataCenter
            : MarketAcquisitionWorldCatalog.ResolveDataCenter(worldName);
        var report = new MarketAcquisitionMarketObservationReport(
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
            readResult);

        switch (MarketAcquisitionObservationDeliveryPolicy.Resolve(
                    reportDispatcher.CanReport,
                    claimed.ClaimToken,
                    intelligenceReporter != null))
        {
            case MarketAcquisitionObservationDelivery.HostedLifecycle:
                reportDispatcher.EnqueueMarketObservation(report);
                break;
            case MarketAcquisitionObservationDelivery.DirectEvidence:
                intelligenceReporter!.EnqueueRouteObservation(report);
                break;
        }
    }

    private void BeginHostedReportingSession(MarketAcquisitionClaimView request)
    {
        if (reportDispatcher.CanReport && !string.IsNullOrWhiteSpace(request.ClaimToken))
            reportDispatcher.BeginSession(request);
        else
            reportDispatcher.ResetSession();
    }

    private bool TryPreflightRouteAction(out string blockReason)
    {
        var preflight = MarketPurchaseEvidenceRoutePreflight.Evaluate(
            purchase.PurchaseEvidenceState,
            purchaseSessionActive: false,
            nowUtc: clock.UtcNow);
        if (preflight.CanExecute)
        {
            if (purchaseEvidenceRouteBlocked)
            {
                blockReason = state.ManualRecoveryBlockedReason ??
                              "Purchase evidence preflight is still recovering; route execution is paused.";
                return false;
            }

            blockReason = string.Empty;
            return true;
        }

        blockReason = preflight.BlockReason!;
        BlockRouteForPurchaseEvidence(blockReason);
        return false;
    }

    private bool TryAdvanceAndPreflightPurchaseEvidence(bool allowActivePurchaseSession)
    {
        MarketPurchaseEvidenceAdvanceResult advance;
        try
        {
            advance = purchase.AdvancePurchaseEvidence(clock.UtcNow);
        }
        catch (Exception exception)
        {
            BlockRouteForPurchaseEvidence($"Purchase evidence preflight failed: {exception.Message}");
            return false;
        }

        if (advance.Status == MarketPurchaseEvidenceAdvanceStatus.PersistenceFailed)
        {
            BlockRouteForPurchaseEvidence(advance.Message);
            return false;
        }

        var preflight = MarketPurchaseEvidenceRoutePreflight.Evaluate(
            purchase.PurchaseEvidenceState ?? advance.State,
            purchaseSessionActive: allowActivePurchaseSession && purchaseAutomation.PurchaseSession?.IsActive == true,
            nowUtc: clock.UtcNow);
        if (preflight.CanExecute)
        {
            if (purchaseEvidenceRouteBlocked && purchase.PurchaseEvidenceState is null && advance.State is null)
            {
                purchaseEvidenceRouteBlocked = false;
                state.ManualRecoveryBlockedReason = null;
            }
            return true;
        }

        BlockRouteForPurchaseEvidence(preflight.BlockReason!);
        return false;
    }

    private void BlockRouteForPurchaseEvidence(string message)
    {
        purchaseEvidenceRouteBlocked = true;
        state.ManualRecoveryBlockedReason = message;
        state.AcquisitionStatus = message;
        exactAcquisitionAuthority?.Pause(message);
        if (runner.IsRunning || runner.IsPaused)
        {
            var result = FailRoute(message);
            state.AcquisitionStatus = result.Message;
        }

        ClearMarketBoardAutomationState();
        state.ManualRecoveryBlockedReason = message;
        state.AcquisitionStatus = message;
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
                : candidatePlan?.Status.Equals(MarketAcquisitionLiveCandidateStatuses.OverageLimit, StringComparison.OrdinalIgnoreCase) == true
                    ? "SkippedOverageLimit"
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
            details["candidateRetainerNameSource"] = result.Candidate.RetainerNameSource;
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
                ["candidateRetainerNameSource"] = candidate.RetainerNameSource,
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
            message,
            reportPluginInstanceId,
            reportPluginVersion,
            clock.UtcNow);
        reportDispatcher.EnqueueRouteProgress(report);
    }

    private void ClearExecutionState(bool preserveExecutionMode = false)
    {
        var purchaseEvidenceState = purchase.PurchaseEvidenceState;
        CleanupOwnedTravel("Replacement");
        travelInterruptedByCleanup = false;
        CancelActiveOperation("Route execution state reset.");
        state.ResetRouteExecutionState(preserveExecutionMode);
        var preflight = MarketPurchaseEvidenceRoutePreflight.Evaluate(
            purchaseEvidenceState,
            purchaseSessionActive: false,
            nowUtc: clock.UtcNow);
        if (!preflight.CanExecute)
            state.ManualRecoveryBlockedReason = preflight.BlockReason;
        listingReadAccumulator.Clear();
        purchaseAutomation.Clear();
        initialOnHandByLine.Clear();
        activeLinePurchaseWorld = null;
        reportDispatcher.ResetSession();
        freshnessCancellation.Cancel();
        freshnessCancellation.Dispose();
        freshnessCancellation = new CancellationTokenSource();
    }

    private bool TryApplyInventoryBaselines(
        MarketAcquisitionPlan plan,
        bool exactAcquisition,
        out MarketAcquisitionPlan adjustedPlan,
        out string failure)
    {
        initialOnHandByLine.Clear();
        var adjustedLines = new List<MarketAcquisitionPlanLine>(plan.Lines.Count);
        inventoryCoveredPurchases.Clear();
        reconciledDepartureWorld = null;
        reconciledSearch = null;
        inventoryReflectionWaitStartedUtc = null;
        foreach (var line in plan.Lines)
        {
            var basis = MarketAcquisitionTargetBases.Normalize(line.TargetBasis, exactAcquisition);
            var initialOnHand = 0u;
            if (line.QuantityMode.Equals("TargetQuantity", StringComparison.OrdinalIgnoreCase) &&
                basis == MarketAcquisitionTargetBases.OnHandTotal)
            {
                if (inventoryObserver is null)
                {
                    adjustedPlan = plan;
                    failure = $"Cannot start acquisition safely because current inventory is unavailable for {line.ItemName ?? line.ItemId.ToString()}.";
                    return false;
                }

                var observation = inventoryObserver.Observe(line.ItemId, line.HqPolicy);
                if (!observation.IsAvailable)
                {
                    adjustedPlan = plan;
                    failure = $"Cannot start acquisition safely for {line.ItemName ?? line.ItemId.ToString()}: {observation.Message}";
                    return false;
                }
                initialOnHand = observation.Quantity;
            }

            initialOnHandByLine[line.LineId] = initialOnHand;
            adjustedLines.Add(line with { TargetBasis = basis, InitialOnHandQuantity = initialOnHand });
        }

        adjustedPlan = plan with { Lines = adjustedLines };
        if (!exactAcquisition && plan.AdvisoryListings is { } advisory && claimedRequest is { } request &&
            plan.Lines.Any(line => line.InitialOnHandQuantity != initialOnHandByLine.GetValueOrDefault(line.LineId)))
            adjustedPlan = MarketAcquisitionPlanner.BuildPlan(request, advisory, plan.PreparedAtUtc,
                context.GetCurrentWorldName(), plan.SweepWorldExclusions, initialOnHandByLine);
        failure = string.Empty;
        return true;
    }

    private bool IsInventorySatisfied(MarketAcquisitionWorldItemSubtask task) =>
        exactAcquisitionAuthority is null && task.QuantityMode == "TargetQuantity" &&
        MarketAcquisitionTargetBases.Normalize(task.TargetBasis) == MarketAcquisitionTargetBases.OnHandTotal &&
        (ulong)initialOnHandByLine.GetValueOrDefault(task.LineId) +
        (state.ExecutionMode == MarketAcquisitionExecutionMode.DryRun ? runner.GetLinePurchaseTotals(task.LineId).PurchasedQuantity : 0u) >= task.RequestedQuantity;

    private bool ReconcileInventory(MarketAcquisitionGuidedRouteStop stop)
    {
        if (exactAcquisitionAuthority is not null) return true;
        foreach (var task in stop.ItemSubtasks.Where(task => task.QuantityMode == "TargetQuantity" &&
                     MarketAcquisitionTargetBases.Normalize(task.TargetBasis) == MarketAcquisitionTargetBases.OnHandTotal))
        {
            var observed = inventoryObserver?.Observe(task.ItemId, task.HqPolicy);
            if (observed?.IsAvailable != true || purchase.PurchaseEvidenceState is PendingMarketPurchase)
            {
                var now = clock.UtcNow;
                inventoryReflectionWaitStartedUtc ??= now;
                var reason = purchase.PurchaseEvidenceState is PendingMarketPurchase
                    ? "purchase receipt pending"
                    : observed?.Message ?? "Inventory unavailable";
                if (now - inventoryReflectionWaitStartedUtc.Value < InventoryReflectionWait)
                {
                    state.AcquisitionStatus = $"Waiting to reconcile inventory for {task.ItemName}: {reason}.";
                    state.NextRouteMonitorUtc = now.AddMilliseconds(250);
                    return false;
                }
                UpdateStatus(FailRoute($"Inventory reconciliation timed out for {task.ItemName}: {reason}."));
                return false;
            }
            var receipts = runner.GetLinePurchaseTotals(task.LineId).PurchasedQuantity;
            if (stop.Status == "Purchasing" && stop.ActiveItemSubtask?.LineId == task.LineId &&
                state.ActivePurchaseLineId == task.LineId && activeLinePurchaseWorld == stop.WorldName)
                receipts = checked(receipts + state.ActiveLinePurchasedQuantity);
            var covered = inventoryCoveredPurchases.GetValueOrDefault(task.LineId);
            // A receipt is not a second copy of stock already visible in bags. Until its
            // delta is reflected, keep the previous baseline and refuse another purchase.
            if (state.ExecutionMode != MarketAcquisitionExecutionMode.DryRun && receipts > covered &&
                observed.Value.Quantity < (ulong)initialOnHandByLine.GetValueOrDefault(task.LineId) + receipts - covered)
            {
                // Give the inventory update a bounded chance to land, then trust the bags:
                // a persistent shortfall means stock was used or moved, not a missing receipt.
                var now = clock.UtcNow;
                inventoryReflectionWaitStartedUtc ??= now;
                if (now - inventoryReflectionWaitStartedUtc.Value < InventoryReflectionWait)
                {
                    state.AcquisitionStatus = $"Waiting for purchased {task.ItemName} to appear in main bags/crystal pouch before re-checking need.";
                    state.NextRouteMonitorUtc = now.AddMilliseconds(250);
                    return false;
                }

                state.AcquisitionStatus = $"{task.ItemName}: bags hold {observed.Value.Quantity} after confirmed purchases; using the observed count (stock may have been used or moved).";
            }
            initialOnHandByLine[task.LineId] = observed.Value.Quantity;
            inventoryCoveredPurchases[task.LineId] = state.ExecutionMode == MarketAcquisitionExecutionMode.DryRun ? 0u : receipts;
            runner.RecordReconciledInventory(task.LineId, observed.Value.Quantity, inventoryCoveredPurchases[task.LineId]);
        }
        inventoryReflectionWaitStartedUtc = null;
        return true;
    }

    private void SkipInventorySatisfiedLine(MarketAcquisitionGuidedRouteStop stop)
    {
        if (stop.ActiveItemSubtask is { } task)
            ReportAcquisitionLineProgress(task, "Complete", 0, 0, "Target already met by current inventory; no purchase needed.");
        UpdateStatus(runner.SkipInventorySatisfiedLine());
    }

    private MarketAcquisitionRouteActionResult UpdateStatus(MarketAcquisitionRouteActionResult result)
    {
        state.AcquisitionStatus = result.Message;
        return result;
    }

    public void Dispose()
    {
        evidence.Flush();
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
        // Cancelling route intent does not cancel an accepted native request.
        // Keep observing it while stopped so recovery cannot overlap its pages.
        var browse = marketBoard.BrowseSnapshot;
        if (!(browse.IsActive && browse.Owner == MarketBoardBrowseOwner.MarketAcquisition && browse.RequestAccepted))
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

        if (timedOut.Kind == MarketAcquisitionRouteOperationKind.TravelPreparation &&
            aethernetDeparture?.IsInProgress == true)
        {
            // A local optimization may time out, but must not fail the route or
            // dispatch /li over an active path/transfer. A fresh preparation
            // window continues the drain and then the existing direct path.
            runner.RecordRouteOperationSnapshot(timedOut);
            aethernetDeparture.FallBack("Local aethernet departure preparation timed out.");
            state.AcquisitionStatus = aethernetDeparture.Message;
            return false;
        }

        marketBoard.AbandonBrowse(timedOut.Message);
        runner.RecordRouteOperationSnapshot(timedOut);
        UpdateStatus(FailRoute(timedOut.Message));
        return true;
    }

    private MarketAcquisitionRouteActionResult FailRoute(string message, Exception? exception = null)
    {
        if (exception != null)
        {
            CaptureFailure(exception);
            runner.RecordFailure(message, exception);
        }
        FailureBookkeeping(CaptureManualRecoverySafetyBlock, "failure.safety-capture", exception);
        FailureBookkeeping(evidence.Flush, "failure.evidence-flush", exception);
        FailureBookkeeping(() => CleanupOwnedTravel("Failure"), "failure.travel-cleanup", exception);
        FailureBookkeeping(() => CancelActiveOperation($"Route failed; active operation cancelled. {message}"), "failure.cancel-operation", exception);
        return runner.FailRoute(message, exception);
    }

    private void FailureBookkeeping(Action action, string stage, Exception? original)
    {
        try { action(); }
        catch (Exception secondary) when (original != null)
        {
            MarketAcquisitionRouteFailure.Annotate(secondary, stage);
            CaptureFailure(secondary);
            runner.RecordFailure("Additional failure while handling the original route exception.", secondary);
        }
    }

    private void CaptureFailure(Exception exception)
    {
        try
        {
            var candidate = purchaseAutomation.PurchaseSession?.Candidate;
            MarketAcquisitionRouteFailure.Annotate(exception, "route.failure", details: new Dictionary<string, string?>
            {
                ["source"] = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                    typeof(MarketAcquisitionRouteEngine).Assembly)?.InformationalVersion,
                ["requestId"] = claimedRequest?.Id,
                ["runId"] = state.ProgressNonce,
                ["operationId"] = operationExecutor.ActiveSnapshot?.OperationId,
                ["browseOperationId"] = state.MarketBoardReadResult?.BrowseOperationId,
                ["lineId"] = state.ActivePurchaseLineId ?? runner.RetainedActiveStop?.ActiveItemSubtask?.LineId,
                ["itemId"] = (candidate?.ItemId ?? runner.RetainedActiveStop?.ActiveItemSubtask?.ItemId ?? claimedRequest?.ItemId)?.ToString(),
                ["world"] = candidate?.WorldName ?? runner.RetainedActiveStop?.WorldName,
                ["listingId"] = candidate?.ListingId.ToString(),
            });
        }
        catch { /* Capture before cleanup, without replacing the original exception. */ }
    }

    private void CaptureManualRecoverySafetyBlock()
    {
        var evidencePreflight = MarketPurchaseEvidenceRoutePreflight.Evaluate(
            purchase.PurchaseEvidenceState,
            purchaseSessionActive: false,
            nowUtc: clock.UtcNow);
        if (!evidencePreflight.CanExecute)
        {
            state.ManualRecoveryBlockedReason = evidencePreflight.BlockReason;
            return;
        }

        var purchaseSession = purchaseAutomation.PurchaseSession;
        state.ManualRecoveryBlockedReason = purchaseSession?.ConfirmationWasSubmitted != true
            ? null
            : $"The retained route cannot resume automatically because listing {purchaseSession.Candidate.ListingId} may have been purchased. Reconcile that purchase outcome before retrying it.";
    }

    private void CleanupOwnedTravel(string terminalReason)
    {
        if (aethernetDeparture != null)
        {
            var stopped = aethernetDeparture.Cleanup();
            runner.RecordRouteCleanup("Local aethernet departure cleanup requested.", new Dictionary<string, string?>
            {
                ["dependency"] = "vnavmesh / Lifestream aethernet",
                ["terminalReason"] = terminalReason,
                ["navigationReleased"] = stopped.ToString(),
                ["preparationState"] = aethernetDeparture.Stage,
            });
        }
        lastDepartureStage = null;
        retainedRecoveryBrowseOperationId = null;
        StopOwnedLocalApproach(releasing: true);
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

    private bool WaitForNativeBrowseCompletion()
    {
        var browse = marketBoard.BrowseSnapshot;
        if (!browse.IsActive)
            return false;
        state.AcquisitionStatus = $"Waiting for native listings and history before route handoff. {browse.Message}";
        state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
        return true;
    }

    private bool StopOwnedLocalApproach(bool releasing = false)
    {
        if (!ownsLocalApproachNavigation)
            return true;
        if (releasing)
            ownsLocalApproachNavigation = false;
        var result = marketBoard.StopMarketBoardApproach();
        runner.RecordRouteCleanup(result.Message, new Dictionary<string, string?>
        {
            ["dependency"] = "vnavmesh",
            ["success"] = result.Success.ToString(),
        });
        if (result.Success)
            ownsLocalApproachNavigation = false;
        else if (!releasing)
            throw new InvalidOperationException(result.Message);
        return result.Success || releasing;
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
