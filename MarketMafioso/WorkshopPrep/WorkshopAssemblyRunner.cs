using System;
using System.Linq;
using Dalamud.Plugin.Services;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopAssemblyRunner : IDisposable
{
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly WorkshopAssemblyUiAutomation uiAutomation;
    private WorkshopAssemblyPlan? activePlan;
    private DateTimeOffset continueAt = DateTimeOffset.MinValue;
    private int activeEntryIndex;
    private int activeEntryCompletedQuantity;

    public WorkshopAssemblyRunner(
        IFramework framework,
        IPluginLog log,
        WorkshopAssemblyUiAutomation uiAutomation)
    {
        this.framework = framework;
        this.log = log;
        this.uiAutomation = uiAutomation;
        Progress = BuildProgress(WorkshopAssemblyRunnerState.Idle, "Workshop assembly has not run.");
    }

    public WorkshopAssemblyProgress Progress { get; private set; }
    public bool IsRunning => Progress.State is not WorkshopAssemblyRunnerState.Idle
        and not WorkshopAssemblyRunnerState.Complete
        and not WorkshopAssemblyRunnerState.Stopped
        and not WorkshopAssemblyRunnerState.Failed;

    public WorkshopAssemblyActionResult Start(WorkshopAssemblyPlan plan)
    {
        if (IsRunning)
            return new(false, "Workshop assembly is already running.");

        activePlan = plan;
        activeEntryIndex = 0;
        activeEntryCompletedQuantity = 0;
        continueAt = DateTimeOffset.MinValue;
        Progress = BuildProgress(WorkshopAssemblyRunnerState.WaitingForFabricationStation, "Waiting for fabrication station UI.");
        framework.Update += OnFrameworkUpdate;
        log.Information("[MarketMafioso] Native workshop assembly started.");
        return new(true, "Native workshop assembly started.");
    }

    public void Stop()
    {
        if (!IsRunning)
            return;

        framework.Update -= OnFrameworkUpdate;
        Progress = BuildProgress(WorkshopAssemblyRunnerState.Stopped, "Workshop assembly stopped by user.");
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
            framework.Update -= OnFrameworkUpdate;
            Progress = BuildProgress(WorkshopAssemblyRunnerState.Failed, $"Workshop assembly failed. {ex.Message}");
            log.Error(ex, "[MarketMafioso] Native workshop assembly failed.");
        }
    }

    private void Tick()
    {
        if (activePlan == null)
            throw new InvalidOperationException("Workshop assembly plan is unavailable.");

        if (activeEntryIndex >= activePlan.Entries.Count)
        {
            framework.Update -= OnFrameworkUpdate;
            Progress = BuildProgress(WorkshopAssemblyRunnerState.Complete, "Workshop assembly complete.");
            log.Information("[MarketMafioso] Native workshop assembly complete.");
            return;
        }

        var entry = activePlan.Entries[activeEntryIndex];
        if (!uiAutomation.IsFabricationStationUiReady())
        {
            Progress = BuildProgress(
                WorkshopAssemblyRunnerState.WaitingForFabricationStation,
                $"Waiting for fabrication station UI. {uiAutomation.DescribeUiState()}");
            return;
        }

        Progress = BuildProgress(
            WorkshopAssemblyRunnerState.OpeningProject,
            $"Ready to assemble {entry.ProjectName}; project selection automation is next.");
        Stop();
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
            null,
            completedProjects,
            totalProjects,
            DateTimeOffset.Now);
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
    }
}
