using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopAssemblyRunner : IDisposable
{
    private const int MaxStalledMaterialRequestRecoveries = 3;
    private const int RetainedDiagnosticLogs = 30;
    private static readonly TimeSpan PostMortemDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NativeFaultPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly IWorkshopAssemblyUiAutomation uiAutomation;
    private readonly string diagnosticsDirectory;
    private readonly Action<WorkshopAssemblyQueueEntry> onProjectRetrieved;
    private readonly TimeProvider timeProvider;
    private WorkshopAssemblyPlan? activePlan;
    private WorkshopAssemblyDiagnostics diagnostics = WorkshopAssemblyDiagnostics.Disabled;
    private DateTimeOffset continueAt = DateTimeOffset.MinValue;
    private DateTimeOffset stateStartedAt = DateTimeOffset.MinValue;
    private int activeEntryIndex;
    private int activeEntryCompletedQuantity;
    private uint? activeMaterialItemId;
    private uint? pendingProgressMaterialItemId;
    private uint? pendingProgressStepsComplete;
    private int stalledMaterialRequestRecoveryCount;
    private WorkshopAssemblyRunnerState stateBeforePause = WorkshopAssemblyRunnerState.Idle;
    private readonly WorkshopAssemblyActionGuard actionGuard = new();
    private DateTimeOffset lastStationInteractAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastNativeFaultPollAt = DateTimeOffset.MinValue;
    private DateTimeOffset? postMortemAt;
    private string? postMortemReason;

    public WorkshopAssemblyRunner(
        IFramework framework,
        IPluginLog log,
        IWorkshopAssemblyUiAutomation uiAutomation,
        string diagnosticsDirectory,
        Action<WorkshopAssemblyQueueEntry>? onProjectRetrieved = null,
        TimeProvider? timeProvider = null)
    {
        this.framework = framework;
        this.log = log;
        this.uiAutomation = uiAutomation;
        this.diagnosticsDirectory = diagnosticsDirectory;
        this.onProjectRetrieved = onProjectRetrieved ?? (_ => { });
        this.timeProvider = timeProvider ?? TimeProvider.System;
        Progress = BuildProgress(WorkshopAssemblyRunnerState.Idle, "Workshop assembly has not run.");
    }

    public WorkshopAssemblyProgress Progress { get; private set; }
    public string? LastDiagnosticFilePath { get; private set; }
    public bool IsRunning => Progress.State is not WorkshopAssemblyRunnerState.Idle
        and not WorkshopAssemblyRunnerState.Paused
        and not WorkshopAssemblyRunnerState.Complete
        and not WorkshopAssemblyRunnerState.Stopped
        and not WorkshopAssemblyRunnerState.Failed;
    public bool IsPaused => Progress.State == WorkshopAssemblyRunnerState.Paused;
    public bool HasActiveRun => IsRunning || IsPaused;

    public WorkshopAssemblyActionResult Start(WorkshopAssemblyPlan plan, bool enableDiagnostics = false)
    {
        if (HasActiveRun)
            return new(false, "Workshop assembly is already running.");

        CancelPostMortem();
        CloseDiagnostics();

        var blocker = uiAutomation.CheckPreflight();
        if (blocker != null)
        {
            log.Warning("[MarketMafioso] Workshop assembly refused to start. {Reason} Forensics: {Forensics}", blocker, CaptureForensicsSafely());
            Progress = BuildProgress(WorkshopAssemblyRunnerState.Failed, blocker);
            return new(false, blocker);
        }

        // Always on: the runs that wedge the character are never the ones somebody
        // remembered to start with diagnostics. Old logs are pruned instead.
        _ = enableDiagnostics;
        diagnostics = CreateDiagnostics();
        uiAutomation.Diagnostics = diagnostics;
        LastDiagnosticFilePath = diagnostics.FilePath;
        activePlan = plan;
        activeEntryIndex = 0;
        activeEntryCompletedQuantity = 0;
        activeMaterialItemId = null;
        pendingProgressMaterialItemId = null;
        pendingProgressStepsComplete = null;
        stalledMaterialRequestRecoveryCount = 0;
        stateBeforePause = WorkshopAssemblyRunnerState.Idle;
        continueAt = DateTimeOffset.MinValue;
        lastStationInteractAt = DateTimeOffset.MinValue;
        lastNativeFaultPollAt = DateTimeOffset.MinValue;
        actionGuard.Reset();
        uiAutomation.ResetState();
        uiAutomation.BeginRun();
        SetState(WorkshopAssemblyRunnerState.WaitingForFabricationStation, "Waiting for fabrication station UI.");
        diagnostics.Record(
            "plan",
            "Workshop assembly plan loaded.",
            new Dictionary<string, string?>
            {
                ["entryCount"] = plan.Entries.Count.ToString(),
                ["totalProjects"] = plan.Entries.Sum(x => x.Quantity).ToString(),
                ["totalMaterialRows"] = plan.TotalMaterials.Count.ToString(),
            });
        framework.Update += OnFrameworkUpdate;
        log.Information("[MarketMafioso] Native workshop assembly started.");
        return new(true, "Native workshop assembly started.");
    }

    public WorkshopAssemblyActionResult Pause()
    {
        if (!IsRunning)
            return new(false, "Workshop assembly is not running.");

        stateBeforePause = Progress.State;
        framework.Update -= OnFrameworkUpdate;
        uiAutomation.EndRun();
        SetState(WorkshopAssemblyRunnerState.Paused, "Workshop assembly paused.");
        diagnostics.Record("paused", "Workshop assembly paused by user.");
        log.Information("[MarketMafioso] Native workshop assembly paused by user.");
        return new(true, "Workshop assembly paused.");
    }

    public WorkshopAssemblyActionResult Resume()
    {
        if (!IsPaused)
            return new(false, "Workshop assembly is not paused.");

        var resumeState = stateBeforePause == WorkshopAssemblyRunnerState.Paused ||
            stateBeforePause == WorkshopAssemblyRunnerState.Idle
            ? WorkshopAssemblyRunnerState.WaitingForFabricationStation
            : stateBeforePause;
        stateBeforePause = WorkshopAssemblyRunnerState.Idle;
        SetState(resumeState, "Workshop assembly resumed.");
        diagnostics.Record("resumed", "Workshop assembly resumed by user.");
        uiAutomation.BeginRun();
        framework.Update += OnFrameworkUpdate;
        log.Information("[MarketMafioso] Native workshop assembly resumed by user.");
        return new(true, "Workshop assembly resumed.");
    }

    public void Stop()
    {
        if (!HasActiveRun)
            return;

        framework.Update -= OnFrameworkUpdate;
        stateBeforePause = WorkshopAssemblyRunnerState.Idle;
        uiAutomation.ResetState();
        SetState(WorkshopAssemblyRunnerState.Stopped, "Workshop assembly stopped by user.");
        diagnostics.Record("stopped", "Workshop assembly stopped by user.");
        log.Information("[MarketMafioso] Native workshop assembly stopped by user.");
        RecordForensicsAndSchedulePostMortem("stopped by user");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!IsRunning || activePlan == null || Now < continueAt)
            return;

        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            Fail($"Workshop assembly failed. {ex.Message}", ex);
        }
    }

    private void Tick()
    {
        if (activePlan == null)
            throw new InvalidOperationException("Workshop assembly plan is unavailable.");

        // Paced actions are at least 250 ms apart, so a 100 ms throttle on the log
        // tail still guarantees a crash check between any two actions.
        if (Now - lastNativeFaultPollAt >= NativeFaultPollInterval)
        {
            lastNativeFaultPollAt = Now;
            var fault = uiAutomation.PollNativeFault();
            if (fault != null)
                throw new WorkshopAssemblyHaltException(fault);
        }

        var cutsceneResult = uiAutomation.TrySkipCutscene();
        if (cutsceneResult.ActionTaken)
        {
            diagnostics.Record(
                "cutscene-skip",
                cutsceneResult.Message,
                new Dictionary<string, string?>
                {
                    ["requiresWorkshopReopen"] = cutsceneResult.RequiresWorkshopReopen.ToString(),
                });
            NoteAction(cutsceneResult);
            if (cutsceneResult.RequiresWorkshopReopen)
                SetState(WorkshopAssemblyRunnerState.WaitingForFabricationStation, cutsceneResult.Message);

            return;
        }

        if (activeEntryIndex >= activePlan.Entries.Count)
        {
            Complete();
            return;
        }

        var entry = activePlan.Entries[activeEntryIndex];
        switch (Progress.State)
        {
            case WorkshopAssemblyRunnerState.WaitingForFabricationStation:
                TickWaitingForFabricationStation();
                break;

            case WorkshopAssemblyRunnerState.OpeningProject:
                TickOpeningProject(entry);
                break;

            case WorkshopAssemblyRunnerState.SubmittingMaterial:
                TickSubmittingMaterial(entry);
                break;

            case WorkshopAssemblyRunnerState.ConfirmingContribution:
                TickConfirmingContribution(entry);
                break;

            case WorkshopAssemblyRunnerState.WaitingForContributionLockout:
                TickWaitingForContributionLockout(entry);
                break;

            default:
                SetState(WorkshopAssemblyRunnerState.WaitingForFabricationStation, "Waiting for fabrication station UI.");
                break;
        }
    }

    private void TickWaitingForFabricationStation()
    {
        if (uiAutomation.IsFabricationStationUiReady())
        {
            SetState(WorkshopAssemblyRunnerState.OpeningProject, "Fabrication station UI is ready.");
            return;
        }

        // Keep checking readiness every tick, but give the game time to answer an
        // interact before trying again (the old loop re-interacted every 250 ms forever).
        if (Now - lastStationInteractAt < WorkshopAssemblyTiming.StationInteractRetryDelay)
        {
            if (Now - stateStartedAt > WorkshopAssemblyTiming.AddonTimeout)
                throw new InvalidOperationException("The fabrication station did not open after interacting with it.");
            return;
        }

        var openResult = uiAutomation.TryOpenFabricationStation();
        if (openResult.ActionTaken)
        {
            lastStationInteractAt = Now;
            NoteAction(openResult);
            Progress = BuildProgress(WorkshopAssemblyRunnerState.WaitingForFabricationStation, openResult.Message);
            return;
        }

        if (Now - stateStartedAt > WorkshopAssemblyTiming.AddonTimeout)
            throw new InvalidOperationException(openResult.Message);

        Progress = BuildProgress(
            WorkshopAssemblyRunnerState.WaitingForFabricationStation,
            openResult.Message);
    }

    private void TickOpeningProject(WorkshopAssemblyQueueEntry entry)
    {
        var result = uiAutomation.TryOpenProject(entry);
        activeMaterialItemId = result.ActiveMaterialItemId;
        RecordActionResult("open-project", entry, result);
        NoteAction(result);
        if (result.RequiresWorkshopReopen)
        {
            activeMaterialItemId = null;
            continueAt = Now + WorkshopAssemblyTiming.UiInteractionDelay;
            SetState(WorkshopAssemblyRunnerState.WaitingForFabricationStation, result.Message);
            return;
        }

        if (result.Success)
        {
            SetState(WorkshopAssemblyRunnerState.SubmittingMaterial, result.Message);
            return;
        }

        HandlePendingActionOrTimeout(WorkshopAssemblyRunnerState.OpeningProject, result);
    }

    private void TickSubmittingMaterial(WorkshopAssemblyQueueEntry entry)
    {
        var result = uiAutomation.TrySubmitNextMaterial(entry);
        activeMaterialItemId = result.ActiveMaterialItemId;
        if (result.ActionTaken && result.ActiveMaterialItemId != null && result.ActiveMaterialStepsComplete != null)
        {
            pendingProgressMaterialItemId = result.ActiveMaterialItemId;
            pendingProgressStepsComplete = result.ActiveMaterialStepsComplete;
        }

        RecordActionResult("submit-material", entry, result);
        NoteAction(result);
        if (result.IsProjectComplete)
        {
            CompleteActiveProject(entry, result.Message);
            return;
        }

        if (result.IsContributionConfirmed)
        {
            StartContributionLockout(result);
            return;
        }

        if (result.RequiresWorkshopReopen)
        {
            SetState(WorkshopAssemblyRunnerState.WaitingForFabricationStation, result.Message);
            return;
        }

        if (result.Success)
        {
            SetState(WorkshopAssemblyRunnerState.ConfirmingContribution, result.Message);
            return;
        }

        if (result.HasPendingMaterialRequest &&
            !result.ActionTaken &&
            Now - stateStartedAt > WorkshopAssemblyTiming.AddonTimeout)
        {
            RecoverStalledMaterialRequest(entry, result);
            return;
        }

        HandlePendingActionOrTimeout(WorkshopAssemblyRunnerState.SubmittingMaterial, result);
    }

    private void RecoverStalledMaterialRequest(
        WorkshopAssemblyQueueEntry entry,
        WorkshopAssemblyActionResult stalledResult)
    {
        if (stalledMaterialRequestRecoveryCount >= MaxStalledMaterialRequestRecoveries)
        {
            throw new InvalidOperationException(
                $"Workshop material request for {entry.ProjectName} remained stuck after {MaxStalledMaterialRequestRecoveries} recovery attempts. {stalledResult.Message}");
        }

        stalledMaterialRequestRecoveryCount++;
        var recovery = uiAutomation.RecoverStalledMaterialRequest();
        diagnostics.Record(
            "request-recovery",
            recovery.Message,
            new Dictionary<string, string?>
            {
                ["attempt"] = stalledMaterialRequestRecoveryCount.ToString(),
                ["maximumAttempts"] = MaxStalledMaterialRequestRecoveries.ToString(),
                ["project"] = entry.ProjectName,
                ["workshopItemId"] = entry.WorkshopItemId.ToString(),
                ["materialItemId"] = stalledResult.ActiveMaterialItemId?.ToString(),
                ["stalledMessage"] = stalledResult.Message,
            });
        activeMaterialItemId = null;
        pendingProgressMaterialItemId = null;
        pendingProgressStepsComplete = null;
        SetState(WorkshopAssemblyRunnerState.WaitingForFabricationStation, recovery.Message);
    }

    private void TickConfirmingContribution(WorkshopAssemblyQueueEntry entry)
    {
        var result = uiAutomation.TryConfirmContribution();
        activeMaterialItemId = result.ActiveMaterialItemId;
        RecordActionResult("confirm-contribution", entry, result);
        NoteAction(result);
        if (result.IsProjectComplete)
        {
            CompleteActiveProject(entry, result.Message);
            return;
        }

        if (result.Success || result.IsContributionConfirmed)
        {
            StartContributionLockout(result);
            return;
        }

        HandlePendingActionOrTimeout(WorkshopAssemblyRunnerState.ConfirmingContribution, result);
    }

    private void StartContributionLockout(WorkshopAssemblyActionResult result)
    {
        stalledMaterialRequestRecoveryCount = 0;
        continueAt = Now + WorkshopAssemblyTiming.PostContributionLockout;
        diagnostics.Record(
            "lockout-start",
            "Post-contribution lockout started.",
            new Dictionary<string, string?>
            {
                ["durationMs"] = WorkshopAssemblyTiming.PostContributionLockout.TotalMilliseconds.ToString("F0"),
                ["activeMaterialItemId"] = result.ActiveMaterialItemId?.ToString(),
            });
        SetState(WorkshopAssemblyRunnerState.WaitingForContributionLockout, result.Message);
    }

    private void TickWaitingForContributionLockout(WorkshopAssemblyQueueEntry entry)
    {
        if (pendingProgressMaterialItemId == null || pendingProgressStepsComplete == null)
        {
            diagnostics.Record(
                "lockout-end",
                "Post-contribution lockout elapsed with no tracked material progress.",
                BuildActiveDetails(entry));
            SetState(WorkshopAssemblyRunnerState.SubmittingMaterial, $"Continuing material contribution for {entry.ProjectName}.");
            return;
        }

        var result = uiAutomation.TryWaitForContributionProgress(
            entry,
            pendingProgressMaterialItemId.Value,
            pendingProgressStepsComplete.Value);
        activeMaterialItemId = result.ActiveMaterialItemId;
        RecordActionResult("lockout-wait", entry, result);
        if (result.RequiresWorkshopReopen)
        {
            pendingProgressMaterialItemId = null;
            pendingProgressStepsComplete = null;
            activeMaterialItemId = null;
            SetState(WorkshopAssemblyRunnerState.WaitingForFabricationStation, result.Message);
            return;
        }

        if (!result.Success)
        {
            if (Now - stateStartedAt > WorkshopAssemblyTiming.AddonTimeout)
            {
                diagnostics.Record(
                    "progress-reconcile",
                    "Workshop material progress remained unchanged; reopening the fabrication station to reconcile authoritative state.",
                    BuildActiveDetails(entry));
                pendingProgressMaterialItemId = null;
                pendingProgressStepsComplete = null;
                activeMaterialItemId = null;
                uiAutomation.ResetState();
                SetState(
                    WorkshopAssemblyRunnerState.WaitingForFabricationStation,
                    $"Workshop material progress remained unchanged for {entry.ProjectName}; reopening the fabrication station.");
                return;
            }

            Progress = BuildProgress(WorkshopAssemblyRunnerState.WaitingForContributionLockout, result.Message);
            return;
        }

        pendingProgressMaterialItemId = null;
        pendingProgressStepsComplete = null;
        actionGuard.NoteProgress();
        diagnostics.Record(
            "lockout-end",
            "Post-contribution lockout elapsed and material progress was observed.",
            BuildActiveDetails(entry));
        SetState(WorkshopAssemblyRunnerState.SubmittingMaterial, $"Continuing material contribution for {entry.ProjectName}.");
    }

    private void HandlePendingActionOrTimeout(
        WorkshopAssemblyRunnerState state,
        WorkshopAssemblyActionResult result)
    {
        if (result.ActionTaken)
        {
            // One material request spans several callbacks inside the same runner state.
            // Each real sub-action is fresh progress and earns a fresh bounded wait window.
            stateStartedAt = Now;
            Progress = BuildProgress(state, result.Message);
            return;
        }

        if (Now - stateStartedAt > WorkshopAssemblyTiming.AddonTimeout)
            throw new InvalidOperationException(result.Message);

        Progress = BuildProgress(state, result.Message);
    }

    private void CompleteActiveProject(WorkshopAssemblyQueueEntry entry, string message)
    {
        onProjectRetrieved(entry);
        actionGuard.NoteProgress();
        activeEntryCompletedQuantity++;
        stalledMaterialRequestRecoveryCount = 0;
        activeMaterialItemId = null;
        if (activeEntryCompletedQuantity >= entry.Quantity)
        {
            activeEntryIndex++;
            activeEntryCompletedQuantity = 0;
        }

        if (activePlan != null && activeEntryIndex >= activePlan.Entries.Count)
        {
            Complete();
            return;
        }

        diagnostics.Record(
            "project-complete",
            message,
            BuildActiveDetails(entry));
        SetState(WorkshopAssemblyRunnerState.OpeningProject, message);
    }

    private void Complete()
    {
        framework.Update -= OnFrameworkUpdate;
        uiAutomation.EndRun();
        uiAutomation.ResetState();
        SetState(WorkshopAssemblyRunnerState.Complete, "Workshop assembly complete.");
        diagnostics.Complete("Workshop assembly complete.");
        CloseDiagnostics();
        log.Information("[MarketMafioso] Native workshop assembly complete.");
    }

    private void Fail(string message, Exception ex)
    {
        framework.Update -= OnFrameworkUpdate;
        uiAutomation.ResetState();
        SetState(WorkshopAssemblyRunnerState.Failed, message);
        diagnostics.Fail(message, ex);
        log.Error(ex, "[MarketMafioso] Native workshop assembly failed. {Message}", message);
        RecordForensicsAndSchedulePostMortem("failed");
    }

    // Only real UI actions count: pace the next tick so one action can never
    // re-fire every frame, and feed the repeat guard.
    private void NoteAction(WorkshopAssemblyActionResult result)
    {
        if (!result.ActionTaken)
            return;

        continueAt = Now + WorkshopAssemblyTiming.UiInteractionDelay;
        actionGuard.NoteAction(Progress.State, result.Message);
    }

    // Snapshot the game state when a run ends badly, then again a few seconds
    // later: the second capture shows whether the character is left wedged.
    private void RecordForensicsAndSchedulePostMortem(string reason)
    {
        uiAutomation.EndRun();
        var forensics = CaptureForensicsSafely();
        diagnostics.Record("forensics", $"Game state when the run {reason}.", new Dictionary<string, string?> { ["state"] = forensics });
        log.Warning("[MarketMafioso] Workshop assembly {Reason}; game state: {Forensics}", reason, forensics);

        CancelPostMortem();
        postMortemReason = reason;
        postMortemAt = Now + PostMortemDelay;
        framework.Update += OnPostMortemUpdate;
    }

    private void OnPostMortemUpdate(IFramework _)
    {
        if (postMortemAt == null || Now < postMortemAt)
            return;

        var forensics = CaptureForensicsSafely();
        var reason = postMortemReason ?? "ended";
        diagnostics.Record("forensics-followup", $"Game state {PostMortemDelay.TotalSeconds:F0}s after the run {reason}.", new Dictionary<string, string?> { ["state"] = forensics });
        log.Warning("[MarketMafioso] Workshop assembly follow-up ({Reason}, +{Seconds}s); game state: {Forensics}", reason, PostMortemDelay.TotalSeconds, forensics);
        CancelPostMortem();
        CloseDiagnostics();
    }

    private void CancelPostMortem()
    {
        framework.Update -= OnPostMortemUpdate;
        postMortemAt = null;
        postMortemReason = null;
    }

    private string CaptureForensicsSafely()
    {
        try
        {
            return uiAutomation.CaptureForensics();
        }
        catch (Exception ex)
        {
            return $"forensics unavailable: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private WorkshopAssemblyDiagnostics CreateDiagnostics()
    {
        try
        {
            PruneDiagnosticLogs();
            return WorkshopAssemblyDiagnostics.CreateEnabled(diagnosticsDirectory, Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning(ex, "[MarketMafioso] Workshop assembly diagnostics could not be opened; running without a log file.");
            return WorkshopAssemblyDiagnostics.Disabled;
        }
    }

    private void PruneDiagnosticLogs()
    {
        if (!Directory.Exists(diagnosticsDirectory))
            return;

        var stale = new DirectoryInfo(diagnosticsDirectory)
            .GetFiles("assembly-*.log")
            .OrderByDescending(x => x.LastWriteTimeUtc)
            .Skip(RetainedDiagnosticLogs - 1);
        foreach (var file in stale)
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
            }
        }
    }

    private void SetState(WorkshopAssemblyRunnerState state, string message)
    {
        if (Progress.State != state)
        {
            if (state == WorkshopAssemblyRunnerState.WaitingForFabricationStation && IsRunning)
                actionGuard.NoteStationReopen(message);
            actionGuard.EnterState();
            stateStartedAt = Now;
            diagnostics.Record(
                "state",
                $"Entered {state}.",
                new Dictionary<string, string?>
                {
                    ["previousState"] = Progress.State.ToString(),
                    ["message"] = message,
                    ["activeProjectIndex"] = activeEntryIndex.ToString(),
                    ["activeCompletedQuantity"] = activeEntryCompletedQuantity.ToString(),
                    ["activeMaterialItemId"] = activeMaterialItemId?.ToString(),
                });
        }

        Progress = BuildProgress(state, message);
    }

    private void RecordActionResult(
        string phase,
        WorkshopAssemblyQueueEntry entry,
        WorkshopAssemblyActionResult result)
    {
        var details = BuildActiveDetails(entry);
        details["success"] = result.Success.ToString();
        details["actionTaken"] = result.ActionTaken.ToString();
        details["requiresWorkshopReopen"] = result.RequiresWorkshopReopen.ToString();
        details["isContributionConfirmed"] = result.IsContributionConfirmed.ToString();
        details["isProjectComplete"] = result.IsProjectComplete.ToString();
        details["hasPendingMaterialRequest"] = result.HasPendingMaterialRequest.ToString();
        details["activeMaterialItemId"] = result.ActiveMaterialItemId?.ToString();
        details["activeMaterialStepsComplete"] = result.ActiveMaterialStepsComplete?.ToString();

        diagnostics.Record(phase, result.Message, details);
    }

    private Dictionary<string, string?> BuildActiveDetails(WorkshopAssemblyQueueEntry entry)
    {
        return new Dictionary<string, string?>
        {
            ["project"] = entry.ProjectName,
            ["workshopItemId"] = entry.WorkshopItemId.ToString(),
            ["resultItemId"] = entry.ResultItemId.ToString(),
            ["activeEntryIndex"] = activeEntryIndex.ToString(),
            ["activeEntryCompletedQuantity"] = activeEntryCompletedQuantity.ToString(),
            ["entryQuantity"] = entry.Quantity.ToString(),
            ["activeMaterialItemId"] = activeMaterialItemId?.ToString(),
        };
    }

    private void CloseDiagnostics()
    {
        diagnostics.Dispose();
        diagnostics = WorkshopAssemblyDiagnostics.Disabled;
        uiAutomation.Diagnostics = WorkshopAssemblyDiagnostics.Disabled;
    }

    private WorkshopAssemblyProgress BuildProgress(WorkshopAssemblyRunnerState state, string message)
    {
        var entry = activePlan?.Entries.ElementAtOrDefault(activeEntryIndex);
        var completedProjects = activePlan == null
            ? 0
            : activePlan.Entries.Take(activeEntryIndex).Sum(x => x.Quantity) + activeEntryCompletedQuantity;
        var totalProjects = activePlan?.Entries.Sum(x => x.Quantity) ?? 0;

        return new WorkshopAssemblyProgress(
            state,
            message,
            entry?.ProjectName,
            entry?.WorkshopItemId,
            activeMaterialItemId,
            completedProjects,
            totalProjects,
            Now);
    }

    private DateTimeOffset Now => timeProvider.GetLocalNow();

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        CancelPostMortem();
        uiAutomation.EndRun();
        CloseDiagnostics();
        uiAutomation.Dispose();
    }
}
