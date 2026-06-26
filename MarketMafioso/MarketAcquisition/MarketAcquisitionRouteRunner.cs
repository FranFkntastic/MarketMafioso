using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteRunner : IDisposable
{
    private const string LocalMarketBoardCommand = "/li mb";

    private readonly string diagnosticsDirectory;
    private MarketAcquisitionGuidedRouteSession? session;
    private MarketAcquisitionRouteDiagnostics diagnostics = MarketAcquisitionRouteDiagnostics.Disabled;
    private bool diagnosticsRequested;

    public MarketAcquisitionRouteRunner(string diagnosticsDirectory)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsDirectory))
            throw new ArgumentException("Diagnostics directory is required.", nameof(diagnosticsDirectory));

        this.diagnosticsDirectory = diagnosticsDirectory;
    }

    public string State { get; private set; } = "Idle";

    public string StatusMessage { get; private set; } = "No route has started.";

    public string? LastDiagnosticFilePath { get; private set; }

    public bool SearchSubmitted { get; private set; }

    public MarketAcquisitionGuidedRouteStop? ActiveStop =>
        State is "Completed" or "Stopped" or "Failed"
            ? null
            : session?.ActiveStop;

    public IReadOnlyList<MarketAcquisitionGuidedRouteStop> Stops => session?.Stops ?? [];

    public bool IsRunning => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);

    public bool IsPaused => string.Equals(State, "Paused", StringComparison.OrdinalIgnoreCase);

    public bool CanRestart => session != null;

    public MarketAcquisitionRouteActionResult Start(MarketAcquisitionPlan plan, bool enableDiagnostics = false)
    {
        ArgumentNullException.ThrowIfNull(plan);

        CloseDiagnostics();
        diagnosticsRequested = enableDiagnostics;
        diagnostics = enableDiagnostics
            ? MarketAcquisitionRouteDiagnostics.CreateEnabled(diagnosticsDirectory, DateTimeOffset.Now)
            : MarketAcquisitionRouteDiagnostics.Disabled;
        LastDiagnosticFilePath = diagnostics.FilePath;
        session = MarketAcquisitionGuidedRouteSession.Start(plan);
        State = "Running";
        SearchSubmitted = false;
        StatusMessage = $"Route started. Next stop: {session.ActiveStop?.WorldName}.";
        diagnostics.Record(
            "route-start",
            StatusMessage,
            new Dictionary<string, string?>
            {
                ["requestId"] = plan.RequestId,
                ["worldCount"] = plan.WorldBatches.Count.ToString(),
                ["plannedQuantity"] = plan.PlannedQuantity.ToString(),
                ["plannedGil"] = plan.PlannedGil.ToString(),
            });
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public MarketAcquisitionRouteActionResult Restart(MarketAcquisitionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        diagnostics.Record("route-restart", "Restarting market acquisition route.");
        return Start(plan, diagnosticsRequested);
    }

    public MarketAcquisitionRouteActionResult Pause()
    {
        if (!IsRunning)
            return Fail($"Route cannot be paused while {State}.");

        State = "Paused";
        StatusMessage = "Route paused.";
        diagnostics.Record("paused", StatusMessage);
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public MarketAcquisitionRouteActionResult Resume()
    {
        if (!IsPaused)
            return Fail($"Route cannot be resumed while {State}.");

        State = "Running";
        StatusMessage = $"Route resumed. Next stop: {ActiveStop?.WorldName}.";
        diagnostics.Record("resumed", StatusMessage);
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public MarketAcquisitionRouteActionResult Stop()
    {
        if (State is "Idle" or "Completed" or "Stopped")
            return Fail($"Route cannot be stopped while {State}.");

        State = "Stopped";
        StatusMessage = "Route stopped.";
        SearchSubmitted = false;
        diagnostics.Record("stopped", StatusMessage);
        CloseDiagnostics();
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public void Reset(string statusMessage)
    {
        CloseDiagnostics();
        session = null;
        State = "Idle";
        StatusMessage = statusMessage;
        SearchSubmitted = false;
        diagnosticsRequested = false;
        LastDiagnosticFilePath = null;
    }

    public MarketAcquisitionRouteActionResult ExecutePendingTravelCommand(Func<string, bool> processCommand)
    {
        ArgumentNullException.ThrowIfNull(processCommand);

        if (!IsRunning)
            return Fail($"Route is {State}; no command was sent.");

        var activeStop = ActiveStop;
        if (activeStop == null)
            return Complete("Route complete. No purchases were executed.");

        if (!string.Equals(activeStop.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            return MarketAcquisitionRouteActionResult.Ok(StatusMessage);

        var result = session!.ExecuteActiveStop(processCommand);
        StatusMessage = result.Message;
        diagnostics.Record(
            "travel-command",
            result.Message,
            new Dictionary<string, string?>
            {
                ["world"] = activeStop.WorldName,
                ["command"] = activeStop.LifestreamCommand,
                ["success"] = result.Success.ToString(),
            });

        if (!result.Success)
            State = "Failed";

        return result.Success
            ? MarketAcquisitionRouteActionResult.Ok(result.Message)
            : Fail(result.Message);
    }

    public MarketAcquisitionRouteActionResult PreparePendingStopForCurrentWorld(
        bool currentWorldIsValid,
        string? currentWorld,
        Func<string, bool> processCommand)
    {
        ArgumentNullException.ThrowIfNull(processCommand);

        if (!IsRunning)
            return Fail($"Route is {State}; pending stop was not prepared.");

        var activeStop = ActiveStop;
        if (activeStop == null)
            return Complete("Route complete. No purchases were executed.");

        if (!string.Equals(activeStop.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            return MarketAcquisitionRouteActionResult.Ok(StatusMessage);

        if (currentWorldIsValid &&
            activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
        {
            return RecordCurrentWorld(currentWorld!);
        }

        if (!currentWorldIsValid)
            return RecordCurrentWorldUnavailable();

        return ExecutePendingTravelCommand(processCommand);
    }

    public MarketAcquisitionRouteActionResult RecordCurrentWorldUnavailable()
    {
        if (!IsRunning)
            return Fail($"Route is {State}; current world was not recorded.");

        var result = session?.RecordCurrentWorldUnavailable() ??
                     MarketAcquisitionGuidedRouteResult.Fail("No route has started.");
        StatusMessage = result.Message;
        diagnostics.Record("world-unavailable", result.Message);
        return MarketAcquisitionRouteActionResult.Fail(result.Message);
    }

    public MarketAcquisitionRouteActionResult RecordCurrentWorld(string currentWorld)
    {
        if (!IsRunning)
            return Fail($"Route is {State}; current world was not recorded.");

        var result = session?.RecordCurrentWorld(currentWorld) ??
                     MarketAcquisitionGuidedRouteResult.Fail("No route has started.");
        StatusMessage = result.Message;
        diagnostics.Record(
            "current-world",
            result.Message,
            new Dictionary<string, string?>
            {
                ["currentWorld"] = currentWorld,
                ["success"] = result.Success.ToString(),
            });
        return result.Success
            ? MarketAcquisitionRouteActionResult.Ok(result.Message)
            : MarketAcquisitionRouteActionResult.Fail(result.Message);
    }

    public MarketAcquisitionRouteActionResult RecordSearchResult(MarketBoardItemSearchResult searchResult)
    {
        ArgumentNullException.ThrowIfNull(searchResult);

        if (!IsRunning)
            return Fail($"Route is {State}; search result was not recorded.");

        StatusMessage = searchResult.Message;
        SearchSubmitted = searchResult.SearchSent;
        diagnostics.Record(
            "item-search",
            searchResult.Message,
            new Dictionary<string, string?>
            {
                ["status"] = searchResult.Status,
                ["searchSubmitted"] = SearchSubmitted.ToString(),
            });
        return searchResult.SearchSent
            ? MarketAcquisitionRouteActionResult.Ok(searchResult.Message)
            : MarketAcquisitionRouteActionResult.Fail(searchResult.Message);
    }

    public MarketAcquisitionRouteActionResult RecordMarketBoardApproach(MarketBoardApproachResult approachResult)
    {
        ArgumentNullException.ThrowIfNull(approachResult);

        if (!IsRunning)
            return Fail($"Route is {State}; market board approach was not recorded.");

        StatusMessage = approachResult.Message;
        diagnostics.Record(
            "market-board-approach",
            approachResult.Message,
            new Dictionary<string, string?>
            {
                ["status"] = approachResult.Status,
            }.Concat(approachResult.Details).ToDictionary(pair => pair.Key, pair => pair.Value));

        return approachResult.ReadyToSearch || approachResult.ActionTaken
            ? MarketAcquisitionRouteActionResult.Ok(approachResult.Message)
            : MarketAcquisitionRouteActionResult.Fail(approachResult.Message);
    }

    public MarketAcquisitionRouteActionResult ExecuteMarketBoardTravelCommand(Func<string, bool> processCommand)
    {
        ArgumentNullException.ThrowIfNull(processCommand);

        if (!IsRunning)
            return Fail($"Route is {State}; market board travel command was not sent.");

        var activeStop = ActiveStop;
        if (activeStop == null)
            return Complete("Route complete. No purchases were executed.");

        if (!string.Equals(activeStop.Status, "Arrived", StringComparison.OrdinalIgnoreCase))
            return Fail($"Cannot request market board travel while stop is {activeStop.Status}.");

        if (activeStop.MarketBoardTravelCommandSent)
        {
            StatusMessage = "Waiting for Lifestream market board travel to finish.";
            diagnostics.Record(
                "market-board-travel-wait",
                StatusMessage,
                new Dictionary<string, string?>
                {
                    ["world"] = activeStop.WorldName,
                    ["command"] = LocalMarketBoardCommand,
                });
            return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
        }

        if (!processCommand(LocalMarketBoardCommand))
        {
            StatusMessage = $"Lifestream command was not handled: {LocalMarketBoardCommand}";
            diagnostics.Record(
                "market-board-travel-command",
                StatusMessage,
                new Dictionary<string, string?>
                {
                    ["world"] = activeStop.WorldName,
                    ["command"] = LocalMarketBoardCommand,
                    ["success"] = false.ToString(),
                });
            return MarketAcquisitionRouteActionResult.Fail(StatusMessage);
        }

        activeStop.MarketBoardTravelCommandSent = true;
        StatusMessage = "Sent /li mb. Waiting for Lifestream market board travel to finish.";
        diagnostics.Record(
            "market-board-travel-command",
            StatusMessage,
            new Dictionary<string, string?>
            {
                ["world"] = activeStop.WorldName,
                ["command"] = LocalMarketBoardCommand,
                ["success"] = true.ToString(),
            });
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public void ClearSearchSubmission(string reason)
    {
        SearchSubmitted = false;
        diagnostics.Record("item-search-reset", reason);
    }

    public MarketAcquisitionRouteActionResult BeginProbe(string message)
    {
        if (!IsRunning)
            return Fail($"Route is {State}; probe was not started.");

        StatusMessage = message;
        diagnostics.Record("probe-start", message);
        return MarketAcquisitionRouteActionResult.Ok(message);
    }

    public MarketAcquisitionRouteActionResult RecordProbe(string currentWorld, MarketAcquisitionLiveDryRun dryRun)
    {
        ArgumentNullException.ThrowIfNull(dryRun);

        if (!IsRunning)
            return Fail($"Route is {State}; probe result was not recorded.");

        var result = session?.RecordProbe(currentWorld, dryRun) ??
                     MarketAcquisitionGuidedRouteResult.Fail("No route has started.");
        StatusMessage = result.Message;
        SearchSubmitted = false;
        diagnostics.Record(
            "probe-result",
            result.Message,
            new Dictionary<string, string?>
            {
                ["currentWorld"] = currentWorld,
                ["dryRunStatus"] = dryRun.Status,
                ["wouldBuyQuantity"] = dryRun.WouldBuyQuantity.ToString(),
                ["wouldSpendGil"] = dryRun.WouldSpendGil.ToString(),
                ["success"] = result.Success.ToString(),
            });

        if (result.Success && session?.ActiveStop == null)
            return Complete(result.Message);

        return result.Success
            ? MarketAcquisitionRouteActionResult.Ok(result.Message)
            : MarketAcquisitionRouteActionResult.Fail(result.Message);
    }

    public MarketAcquisitionRouteActionResult FailRoute(string message, Exception? exception = null)
    {
        State = "Failed";
        StatusMessage = message;
        SearchSubmitted = false;
        diagnostics.Fail(message, exception);
        CloseDiagnostics();
        return MarketAcquisitionRouteActionResult.Fail(message);
    }

    public void Dispose()
    {
        CloseDiagnostics();
    }

    private MarketAcquisitionRouteActionResult Complete(string message)
    {
        State = "Completed";
        StatusMessage = message;
        SearchSubmitted = false;
        diagnostics.Complete(message);
        CloseDiagnostics();
        return MarketAcquisitionRouteActionResult.Ok(message);
    }

    private MarketAcquisitionRouteActionResult Fail(string message)
    {
        StatusMessage = message;
        return MarketAcquisitionRouteActionResult.Fail(message);
    }

    private void CloseDiagnostics()
    {
        diagnostics.Dispose();
        diagnostics = MarketAcquisitionRouteDiagnostics.Disabled;
    }
}

public sealed record MarketAcquisitionRouteActionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;

    public static MarketAcquisitionRouteActionResult Ok(string message) => new()
    {
        Success = true,
        Message = message,
    };

    public static MarketAcquisitionRouteActionResult Fail(string message) => new()
    {
        Success = false,
        Message = message,
    };
}
