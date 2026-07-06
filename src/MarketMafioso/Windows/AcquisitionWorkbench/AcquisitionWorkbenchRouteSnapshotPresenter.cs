using System;
using System.Collections.Generic;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.AcquisitionWorkbench;

public static class AcquisitionWorkbenchRouteSnapshotPresenter
{
    public static AcquisitionWorkbenchRouteSnapshot Build(
        MarketAcquisitionPlan? preparedPlan,
        MarketAcquisitionRouteRunner routeRunner,
        bool isBusy,
        bool canPrepareRoute = false)
    {
        ArgumentNullException.ThrowIfNull(routeRunner);

        var hasReadyPlan = preparedPlan is { Status: "Ready" } &&
                           preparedPlan.WorldBatches.Count > 0;
        var canStart = !isBusy &&
                       hasReadyPlan &&
                       !routeRunner.IsRunning &&
                       !routeRunner.IsPaused;
        var activeStop = routeRunner.ActiveStop;
        var routeRows = MarketAcquisitionRouteTablePresenter.BuildRows(routeRunner.Stops);
        var completedOrProbedWorldCount = routeRunner.CompletedOrProbedStops.Count;

        return new AcquisitionWorkbenchRouteSnapshot
        {
            State = routeRunner.State,
            StatusMessage = routeRunner.StatusMessage,
            HasPreparedPlan = preparedPlan is not null,
            PreparedPlanStatus = preparedPlan?.Status,
            PreparedWorldCount = preparedPlan?.WorldBatches.Count ?? 0,
            PreparedQuantity = preparedPlan?.PlannedQuantity ?? 0,
            PreparedGil = preparedPlan?.PlannedGil ?? 0,
            CanPrepare = !isBusy && canPrepareRoute,
            CanStart = canStart,
            CanStartWithDiagnostics = canStart,
            CanPause = routeRunner.IsRunning,
            CanResume = routeRunner.IsPaused,
            CanStop = routeRunner.IsRunning || routeRunner.IsPaused,
            CanRestart = canStart && routeRunner.CanRestart,
            CanReprepare = canStart &&
                           routeRunner.CanRestart &&
                           completedOrProbedWorldCount > 0,
            ActiveWorld = activeStop?.WorldName,
            ActiveWorldPlannedQuantity = activeStop?.PlannedQuantity ?? 0,
            ActiveWorldPlannedGil = activeStop?.PlannedGil ?? 0,
            RouteRows = routeRows,
            CompletedOrProbedWorldCount = completedOrProbedWorldCount,
            LastDiagnosticFilePath = routeRunner.LastDiagnosticFilePath,
            LastRunSummary = routeRunner.LastRunSummary,
            LatestWorldCompletionSummary = routeRunner.LatestWorldCompletionSummary,
        };
    }
}

public sealed record AcquisitionWorkbenchRouteSnapshot
{
    public string State { get; init; } = "Idle";
    public string StatusMessage { get; init; } = string.Empty;
    public bool HasPreparedPlan { get; init; }
    public string? PreparedPlanStatus { get; init; }
    public int PreparedWorldCount { get; init; }
    public uint PreparedQuantity { get; init; }
    public uint PreparedGil { get; init; }
    public bool CanPrepare { get; init; }
    public bool CanStart { get; init; }
    public bool CanStartWithDiagnostics { get; init; }
    public bool CanPause { get; init; }
    public bool CanResume { get; init; }
    public bool CanStop { get; init; }
    public bool CanRestart { get; init; }
    public bool CanReprepare { get; init; }
    public string? ActiveWorld { get; init; }
    public uint ActiveWorldPlannedQuantity { get; init; }
    public uint ActiveWorldPlannedGil { get; init; }
    public IReadOnlyList<MarketAcquisitionRouteStopRow> RouteRows { get; init; } = [];
    public int CompletedOrProbedWorldCount { get; init; }
    public string? LastDiagnosticFilePath { get; init; }
    public MarketAcquisitionRouteRunSummary? LastRunSummary { get; init; }
    public MarketAcquisitionWorldCompletionSummary? LatestWorldCompletionSummary { get; init; }
}
