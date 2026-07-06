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
        var recovery = BuildRecoveryStatus(routeRunner, completedOrProbedWorldCount);

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
            RecoverySummary = recovery.Summary,
            RecoveryDetail = recovery.Detail,
        };
    }

    private static AcquisitionWorkbenchRecoveryStatus BuildRecoveryStatus(
        MarketAcquisitionRouteRunner routeRunner,
        int completedOrProbedWorldCount)
    {
        if (!routeRunner.CanRestart)
        {
            return new AcquisitionWorkbenchRecoveryStatus(
                "No route is available to recover.",
                "Sync and prepare a route before recovery controls are useful.");
        }

        if (routeRunner.IsPaused)
        {
            return new AcquisitionWorkbenchRecoveryStatus(
                "Route is paused.",
                "Resume to continue from the current stop, or stop the route before restarting or re-preparing it.");
        }

        if (routeRunner.IsRunning)
        {
            return new AcquisitionWorkbenchRecoveryStatus(
                "Route is running.",
                "Pause to hold the current stop, or stop the route if the current run should be abandoned.");
        }

        if (completedOrProbedWorldCount > 0)
        {
            return new AcquisitionWorkbenchRecoveryStatus(
                $"{FormatWorldCount(completedOrProbedWorldCount)} already been completed or probed.",
                "Re-prepare Route skips those worlds and starts the remaining plan. Restart starts the full prepared route again.");
        }

        return new AcquisitionWorkbenchRecoveryStatus(
            "A prepared route can be restarted.",
            "Restart starts the full prepared route again. Re-prepare becomes available after at least one world has been completed or probed.");
    }

    private static string FormatWorldCount(int count) =>
        count == 1
            ? "1 world has"
            : $"{count:N0} worlds have";
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
    public string RecoverySummary { get; init; } = string.Empty;
    public string RecoveryDetail { get; init; } = string.Empty;
}

public sealed record AcquisitionWorkbenchRecoveryStatus(string Summary, string Detail);
