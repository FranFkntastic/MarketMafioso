using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteRunner : IDisposable
{
    private const string LocalMarketBoardCommand = "/li mb";
    private static readonly TimeSpan ItemSearchAutomationTimeout = TimeSpan.FromSeconds(15);

    private readonly string diagnosticsDirectory;
    private MarketAcquisitionGuidedRouteSession? session;
    private MarketAcquisitionRouteDiagnostics diagnostics = MarketAcquisitionRouteDiagnostics.Disabled;
    private bool diagnosticsRequested;
    private bool standaloneInputCaptureLogOpen;
    private DateTimeOffset? itemSearchAutomationStartedUtc;

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

    public bool MarketBoardCloseRequiredBeforeTravel { get; private set; }

    public bool CanFinalizeInputCaptureLog => standaloneInputCaptureLogOpen && diagnostics.IsEnabled;

    public MarketAcquisitionGuidedRouteStop? ActiveStop =>
        State is "Completed" or "Stopped" or "Failed"
            ? null
            : session?.ActiveStop;

    public IReadOnlyList<MarketAcquisitionGuidedRouteStop> Stops => session?.Stops ?? [];

    public bool IsRunning => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);

    public bool IsPaused => string.Equals(State, "Paused", StringComparison.OrdinalIgnoreCase);

    public bool CanRestart => session != null;

    public MarketAcquisitionRouteActionResult Start(
        MarketAcquisitionPlan plan,
        bool enableDiagnostics = false)
    {
        ArgumentNullException.ThrowIfNull(plan);

        CloseDiagnostics();
        diagnosticsRequested = enableDiagnostics;
        diagnostics = diagnosticsRequested
            ? MarketAcquisitionRouteDiagnostics.CreateEnabled(diagnosticsDirectory, DateTimeOffset.Now)
            : MarketAcquisitionRouteDiagnostics.Disabled;
        LastDiagnosticFilePath = diagnostics.FilePath;
        session = MarketAcquisitionGuidedRouteSession.Start(plan);
        State = "Running";
        SearchSubmitted = false;
        MarketBoardCloseRequiredBeforeTravel = false;
        standaloneInputCaptureLogOpen = false;
        itemSearchAutomationStartedUtc = null;
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
        MarketBoardCloseRequiredBeforeTravel = false;
        standaloneInputCaptureLogOpen = false;
        itemSearchAutomationStartedUtc = null;
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
        MarketBoardCloseRequiredBeforeTravel = false;
        standaloneInputCaptureLogOpen = false;
        itemSearchAutomationStartedUtc = null;
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

        if (MarketBoardCloseRequiredBeforeTravel)
        {
            StatusMessage = $"Waiting for market board windows to close before traveling to {activeStop.WorldName}.";
            return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
        }

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

    public MarketAcquisitionRouteActionResult RecordMarketBoardClosedBeforeTravel()
    {
        if (!IsRunning)
            return Fail($"Route is {State}; market board close was not recorded.");

        if (!MarketBoardCloseRequiredBeforeTravel)
            return MarketAcquisitionRouteActionResult.Ok(StatusMessage);

        MarketBoardCloseRequiredBeforeTravel = false;
        var activeStop = ActiveStop;
        StatusMessage = activeStop == null
            ? "Market board windows closed."
            : $"Market board windows closed. Next stop: {activeStop.WorldName}.";
        diagnostics.Record(
            "market-board-closed",
            StatusMessage,
            new Dictionary<string, string?>
            {
                ["nextWorld"] = activeStop?.WorldName,
            });
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
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
        return RecordSearchResult(searchResult, DateTimeOffset.UtcNow);
    }

    public MarketAcquisitionRouteActionResult RecordInputCapture(string label, MarketBoardInputCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        if (!diagnostics.IsEnabled)
        {
            diagnostics = MarketAcquisitionRouteDiagnostics.CreateEnabled(diagnosticsDirectory, DateTimeOffset.Now);
            LastDiagnosticFilePath = diagnostics.FilePath;
            standaloneInputCaptureLogOpen = true;
        }

        var details = new Dictionary<string, string?>
        {
            ["label"] = label,
            ["status"] = capture.Status,
        };
        foreach (var pair in capture.Details)
            details[pair.Key] = pair.Value;

        diagnostics.Record("input-capture", capture.Message, details);
        StatusMessage = $"Captured market board input state: {label}.";
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public MarketAcquisitionRouteActionResult FinalizeInputCaptureLog()
    {
        if (diagnosticsRequested && IsRunning)
        {
            StatusMessage = "Active route diagnostics are still running; stop or complete the route to finalize that log.";
            return MarketAcquisitionRouteActionResult.Fail(StatusMessage);
        }

        if (!CanFinalizeInputCaptureLog)
        {
            StatusMessage = "No standalone input capture log is open.";
            return MarketAcquisitionRouteActionResult.Fail(StatusMessage);
        }

        StatusMessage = "Standalone input capture log finalized.";
        diagnostics.Record("input-capture-finalized", StatusMessage);
        standaloneInputCaptureLogOpen = false;
        CloseDiagnostics();
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public MarketAcquisitionRouteActionResult RecordSearchResult(MarketBoardItemSearchResult searchResult, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(searchResult);

        if (!IsRunning)
            return Fail($"Route is {State}; search result was not recorded.");

        if (searchResult.ReadyForListings)
            itemSearchAutomationStartedUtc = null;
        else if (searchResult.IsInProgress)
            itemSearchAutomationStartedUtc ??= nowUtc;

        StatusMessage = searchResult.ReadyForListings
            ? searchResult.Message
            : $"Waiting for market board listings. {searchResult.Message}";
        SearchSubmitted = searchResult.ReadyForListings;
        var details = new Dictionary<string, string?>
        {
            ["status"] = searchResult.Status,
            ["searchSubmitted"] = SearchSubmitted.ToString(),
        };
        if (itemSearchAutomationStartedUtc is { } startedAt)
        {
            var elapsed = nowUtc - startedAt;
            details["automationElapsedSeconds"] = Math.Max(0, elapsed.TotalSeconds).ToString("F1");
        }

        foreach (var pair in searchResult.Details)
            details[pair.Key] = pair.Value;

        diagnostics.Record(
            "item-search",
            searchResult.Message,
            details);

        if (searchResult.IsInProgress &&
            itemSearchAutomationStartedUtc is { } started &&
            nowUtc - started > ItemSearchAutomationTimeout)
        {
            var timeoutMessage =
                $"Market board item search automation timed out after {ItemSearchAutomationTimeout.TotalSeconds:N0}s while waiting for listings. Last status: {searchResult.Status}.";
            return FailRoute(timeoutMessage);
        }

        return searchResult.IsInProgress || searchResult.ReadyForListings
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
        itemSearchAutomationStartedUtc = null;

        diagnostics.Record("item-search-reset", reason);
    }

    public MarketAcquisitionRouteActionResult BeginProbe(string message)
    {
        if (!IsRunning)
            return Fail($"Route is {State}; probe was not started.");

        StatusMessage = message;
        diagnostics.Record("probe-start", message);
        itemSearchAutomationStartedUtc = null;
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
        itemSearchAutomationStartedUtc = null;
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

        if (result.Success)
        {
            MarketBoardCloseRequiredBeforeTravel = true;
            StatusMessage = $"{result.Message} Closing market board windows before next travel.";
            diagnostics.Record(
                "market-board-close-required",
                StatusMessage,
                new Dictionary<string, string?>
                {
                    ["nextWorld"] = session?.ActiveStop?.WorldName,
                });
        }

        return result.Success
            ? MarketAcquisitionRouteActionResult.Ok(result.Message)
            : MarketAcquisitionRouteActionResult.Fail(result.Message);
    }

    public MarketAcquisitionRouteActionResult FailRoute(string message, Exception? exception = null)
    {
        State = "Failed";
        StatusMessage = message;
        SearchSubmitted = false;
        MarketBoardCloseRequiredBeforeTravel = false;
        standaloneInputCaptureLogOpen = false;
        itemSearchAutomationStartedUtc = null;
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
        MarketBoardCloseRequiredBeforeTravel = false;
        standaloneInputCaptureLogOpen = false;
        itemSearchAutomationStartedUtc = null;
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
