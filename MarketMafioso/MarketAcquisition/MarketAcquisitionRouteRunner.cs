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
    private string? searchCaptureItemName;
    private uint searchCaptureItemId;
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

    public bool SearchCaptureEnabled { get; private set; }

    public MarketAcquisitionSearchCaptureStep SearchCaptureStep { get; private set; }

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
        bool enableDiagnostics = false,
        bool enableSearchCapture = false)
    {
        ArgumentNullException.ThrowIfNull(plan);

        CloseDiagnostics();
        diagnosticsRequested = enableDiagnostics || enableSearchCapture;
        diagnostics = diagnosticsRequested
            ? MarketAcquisitionRouteDiagnostics.CreateEnabled(diagnosticsDirectory, DateTimeOffset.Now)
            : MarketAcquisitionRouteDiagnostics.Disabled;
        LastDiagnosticFilePath = diagnostics.FilePath;
        session = MarketAcquisitionGuidedRouteSession.Start(plan);
        State = "Running";
        SearchSubmitted = false;
        MarketBoardCloseRequiredBeforeTravel = false;
        SearchCaptureEnabled = enableSearchCapture;
        SearchCaptureStep = MarketAcquisitionSearchCaptureStep.None;
        searchCaptureItemId = 0;
        searchCaptureItemName = null;
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
                ["searchCaptureEnabled"] = SearchCaptureEnabled.ToString(),
            });
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public MarketAcquisitionRouteActionResult Restart(MarketAcquisitionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var preserveSearchCapture = SearchCaptureEnabled;
        diagnostics.Record("route-restart", "Restarting market acquisition route.");
        return Start(plan, diagnosticsRequested, preserveSearchCapture);
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
        ResetSearchCapture();
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
        ResetSearchCapture();
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

    public MarketAcquisitionRouteActionResult RecordInputSnapshot(string label, MarketBoardItemSearchResult snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!diagnostics.IsEnabled)
        {
            diagnostics = MarketAcquisitionRouteDiagnostics.CreateEnabled(diagnosticsDirectory, DateTimeOffset.Now);
            LastDiagnosticFilePath = diagnostics.FilePath;
        }

        var details = new Dictionary<string, string?>
        {
            ["label"] = label,
            ["status"] = snapshot.Status,
        };
        foreach (var pair in snapshot.Details)
            details[pair.Key] = pair.Value;

        diagnostics.Record("input-snapshot", snapshot.Message, details);
        StatusMessage = $"Captured market board input snapshot: {label}.";
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

    public MarketAcquisitionRouteActionResult BeginSearchCapture(uint itemId, string itemName)
    {
        if (!IsRunning)
            return Fail($"Route is {State}; search capture was not started.");

        if (!SearchCaptureEnabled)
            return Fail("Search capture is not enabled for this route.");

        if (itemId == 0)
            return Fail("Search capture requires a planned item id.");

        if (string.IsNullOrWhiteSpace(itemName))
            return Fail("Search capture requires a planned item name.");

        if (SearchCaptureStep is MarketAcquisitionSearchCaptureStep.AwaitingManualSearch
            or MarketAcquisitionSearchCaptureStep.AwaitingManualItemSelection)
            return MarketAcquisitionRouteActionResult.Ok(StatusMessage);

        searchCaptureItemId = itemId;
        searchCaptureItemName = itemName.Trim();
        SearchCaptureStep = MarketAcquisitionSearchCaptureStep.AwaitingManualSearch;
        SearchSubmitted = false;
        StatusMessage = BuildSearchCaptureManualSearchPrompt();
        diagnostics.Record(
            "search-capture-step",
            StatusMessage,
            new Dictionary<string, string?>
            {
                ["step"] = SearchCaptureStep.ToString(),
                ["itemId"] = searchCaptureItemId.ToString(),
                ["itemName"] = searchCaptureItemName,
            });
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public MarketAcquisitionRouteActionResult RecordSearchCaptureObservation(MarketBoardItemSearchResult observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (!IsRunning)
            return Fail($"Route is {State}; search capture observation was not recorded.");

        if (!SearchCaptureEnabled)
            return Fail("Search capture is not enabled for this route.");

        var details = new Dictionary<string, string?>
        {
            ["step"] = SearchCaptureStep.ToString(),
            ["status"] = observation.Status,
            ["searchSubmitted"] = SearchSubmitted.ToString(),
        };
        foreach (var pair in observation.Details)
            details[pair.Key] = pair.Value;

        if (observation.ReadyForListings)
        {
            SearchCaptureStep = MarketAcquisitionSearchCaptureStep.Complete;
            SearchSubmitted = true;
            StatusMessage = observation.Message;
            details["step"] = SearchCaptureStep.ToString();
            details["searchSubmitted"] = SearchSubmitted.ToString();
            diagnostics.Record("search-capture-observation", observation.Message, details);
            return MarketAcquisitionRouteActionResult.Ok(observation.Message);
        }

        if (string.Equals(observation.Status, "ItemResultsReady", StringComparison.OrdinalIgnoreCase) &&
            SearchCaptureStep == MarketAcquisitionSearchCaptureStep.AwaitingManualSearch)
        {
            SearchCaptureStep = MarketAcquisitionSearchCaptureStep.AwaitingManualItemSelection;
            StatusMessage = BuildSearchCaptureManualSelectionPrompt();
            details["step"] = SearchCaptureStep.ToString();
            diagnostics.Record("search-capture-observation", observation.Message, details);
            diagnostics.Record(
                "search-capture-step",
                StatusMessage,
                new Dictionary<string, string?>
                {
                    ["step"] = SearchCaptureStep.ToString(),
                    ["itemId"] = searchCaptureItemId.ToString(),
                    ["itemName"] = searchCaptureItemName,
                });
            return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
        }

        if (SearchCaptureStep == MarketAcquisitionSearchCaptureStep.AwaitingManualItemSelection)
            StatusMessage = BuildSearchCaptureManualSelectionPrompt();
        else if (SearchCaptureStep == MarketAcquisitionSearchCaptureStep.AwaitingManualSearch)
            StatusMessage = BuildSearchCaptureManualSearchPrompt();
        else
            StatusMessage = observation.Message;

        diagnostics.Record("search-capture-observation", observation.Message, details);
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
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
        if (SearchCaptureEnabled && SearchCaptureStep == MarketAcquisitionSearchCaptureStep.Complete)
            SearchCaptureStep = MarketAcquisitionSearchCaptureStep.None;

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
        if (SearchCaptureEnabled)
        {
            SearchCaptureStep = MarketAcquisitionSearchCaptureStep.None;
            searchCaptureItemId = 0;
            searchCaptureItemName = null;
        }

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
        ResetSearchCapture();
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
        ResetSearchCapture();
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

    private string BuildSearchCaptureManualSearchPrompt()
    {
        var itemName = searchCaptureItemName ?? "the planned item";
        var itemId = searchCaptureItemId == 0 ? string.Empty : $" ({searchCaptureItemId})";
        return $"Capture step 1: Type {itemName}{itemId} into the market board search box, then press Search.";
    }

    private string BuildSearchCaptureManualSelectionPrompt()
    {
        var itemName = searchCaptureItemName ?? "the planned item";
        return $"Capture step 2: Select the exact item result: {itemName}.";
    }

    private void ResetSearchCapture()
    {
        SearchCaptureEnabled = false;
        SearchCaptureStep = MarketAcquisitionSearchCaptureStep.None;
        searchCaptureItemId = 0;
        searchCaptureItemName = null;
    }
}

public enum MarketAcquisitionSearchCaptureStep
{
    None,
    AwaitingManualSearch,
    AwaitingManualItemSelection,
    Complete,
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
