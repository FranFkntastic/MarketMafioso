using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopAssemblyRunner : IDisposable
{
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly WorkshopAssemblyUiAutomation uiAutomation;
    private readonly string diagnosticsDirectory;
    private WorkshopAssemblyPlan? activePlan;
    private WorkshopAssemblyDiagnostics diagnostics = WorkshopAssemblyDiagnostics.Disabled;
    private DateTimeOffset continueAt = DateTimeOffset.MinValue;
    private DateTimeOffset stateStartedAt = DateTimeOffset.MinValue;
    private int activeEntryIndex;
    private int activeEntryCompletedQuantity;
    private uint? activeMaterialItemId;

    public WorkshopAssemblyRunner(
        IFramework framework,
        IPluginLog log,
        WorkshopAssemblyUiAutomation uiAutomation,
        string diagnosticsDirectory)
    {
        this.framework = framework;
        this.log = log;
        this.uiAutomation = uiAutomation;
        this.diagnosticsDirectory = diagnosticsDirectory;
        Progress = BuildProgress(WorkshopAssemblyRunnerState.Idle, "Workshop assembly has not run.");
    }

    public WorkshopAssemblyProgress Progress { get; private set; }
    public string? LastDiagnosticFilePath { get; private set; }
    public bool IsRunning => Progress.State is not WorkshopAssemblyRunnerState.Idle
        and not WorkshopAssemblyRunnerState.Complete
        and not WorkshopAssemblyRunnerState.Stopped
        and not WorkshopAssemblyRunnerState.Failed;

    public WorkshopAssemblyActionResult Start(WorkshopAssemblyPlan plan, bool enableDiagnostics = false)
    {
        if (IsRunning)
            return new(false, "Workshop assembly is already running.");

        diagnostics.Dispose();
        diagnostics = enableDiagnostics
            ? WorkshopAssemblyDiagnostics.CreateEnabled(diagnosticsDirectory, DateTimeOffset.Now)
            : WorkshopAssemblyDiagnostics.Disabled;
        uiAutomation.Diagnostics = diagnostics;
        LastDiagnosticFilePath = diagnostics.FilePath;
        activePlan = plan;
        activeEntryIndex = 0;
        activeEntryCompletedQuantity = 0;
        activeMaterialItemId = null;
        continueAt = DateTimeOffset.MinValue;
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

    public void Stop()
    {
        if (!IsRunning)
            return;

        framework.Update -= OnFrameworkUpdate;
        SetState(WorkshopAssemblyRunnerState.Stopped, "Workshop assembly stopped by user.");
        diagnostics.Record("stopped", "Workshop assembly stopped by user.");
        CloseDiagnostics();
        log.Information("[MarketMafioso] Native workshop assembly stopped by user.");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!IsRunning || activePlan == null || DateTimeOffset.Now < continueAt)
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
                diagnostics.Record(
                    "lockout-end",
                    "Post-contribution lockout elapsed.",
                    BuildActiveDetails(entry));
                SetState(WorkshopAssemblyRunnerState.SubmittingMaterial, $"Continuing material contribution for {entry.ProjectName}.");
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

        Progress = BuildProgress(
            WorkshopAssemblyRunnerState.WaitingForFabricationStation,
            $"Waiting for fabrication station UI. {uiAutomation.DescribeUiState()}");
    }

    private void TickOpeningProject(WorkshopAssemblyQueueEntry entry)
    {
        var result = uiAutomation.TryOpenProject(entry);
        activeMaterialItemId = result.ActiveMaterialItemId;
        RecordActionResult("open-project", entry, result);
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
        RecordActionResult("submit-material", entry, result);
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

        if (result.Success)
        {
            SetState(WorkshopAssemblyRunnerState.ConfirmingContribution, result.Message);
            return;
        }

        HandlePendingActionOrTimeout(WorkshopAssemblyRunnerState.SubmittingMaterial, result);
    }

    private void TickConfirmingContribution(WorkshopAssemblyQueueEntry entry)
    {
        var result = uiAutomation.TryConfirmContribution();
        activeMaterialItemId = result.ActiveMaterialItemId;
        RecordActionResult("confirm-contribution", entry, result);
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
        continueAt = DateTimeOffset.Now + WorkshopAssemblyTiming.PostContributionLockout;
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

    private void HandlePendingActionOrTimeout(
        WorkshopAssemblyRunnerState state,
        WorkshopAssemblyActionResult result)
    {
        if (result.ActionTaken)
        {
            SetState(state, result.Message);
            return;
        }

        if (DateTimeOffset.Now - stateStartedAt > WorkshopAssemblyTiming.AddonTimeout)
            throw new InvalidOperationException(result.Message);

        Progress = BuildProgress(state, result.Message);
    }

    private void CompleteActiveProject(WorkshopAssemblyQueueEntry entry, string message)
    {
        activeEntryCompletedQuantity++;
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
        SetState(WorkshopAssemblyRunnerState.Complete, "Workshop assembly complete.");
        diagnostics.Complete("Workshop assembly complete.");
        CloseDiagnostics();
        log.Information("[MarketMafioso] Native workshop assembly complete.");
    }

    private void Fail(string message, Exception ex)
    {
        framework.Update -= OnFrameworkUpdate;
        SetState(WorkshopAssemblyRunnerState.Failed, message);
        diagnostics.Fail(message, ex);
        CloseDiagnostics();
        log.Error(ex, "[MarketMafioso] Native workshop assembly failed.");
    }

    private void SetState(WorkshopAssemblyRunnerState state, string message)
    {
        if (Progress.State != state)
        {
            stateStartedAt = DateTimeOffset.Now;
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
        details["isContributionConfirmed"] = result.IsContributionConfirmed.ToString();
        details["isProjectComplete"] = result.IsProjectComplete.ToString();
        details["activeMaterialItemId"] = result.ActiveMaterialItemId?.ToString();

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
            DateTimeOffset.Now);
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        uiAutomation.Dispose();
    }
}
