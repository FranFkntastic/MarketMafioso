using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteRunner : IDisposable
{
    private const string LocalMarketBoardCommand = "/li mb";
    private static readonly TimeSpan ItemSearchAutomationTimeout = TimeSpan.FromSeconds(15);

    private readonly string diagnosticsDirectory;
    private readonly UniversalisFreshnessVerifierDelegate? universalisFreshnessVerifier;
    private readonly Dictionary<FreshnessObservationKey, FreshnessObservation> freshnessObservations = [];
    private readonly HashSet<FreshnessObservationKey> verifiedFreshnessObservations = [];
    private MarketAcquisitionGuidedRouteSession? session;
    private MarketAcquisitionRouteDiagnostics diagnostics = MarketAcquisitionRouteDiagnostics.Disabled;
    private bool diagnosticsRequested;
    private bool includeOpportunisticChecksRequested;
    private bool standaloneInputCaptureLogOpen;
    private DateTimeOffset? itemSearchAutomationStartedUtc;
    private string? lastWorldSummarySignature;

    public MarketAcquisitionRouteRunner(string diagnosticsDirectory)
        : this(diagnosticsDirectory, null)
    {
    }

    public MarketAcquisitionRouteRunner(
        string diagnosticsDirectory,
        UniversalisFreshnessVerifierDelegate? universalisFreshnessVerifier)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsDirectory))
            throw new ArgumentException("Diagnostics directory is required.", nameof(diagnosticsDirectory));

        this.diagnosticsDirectory = diagnosticsDirectory;
        this.universalisFreshnessVerifier = universalisFreshnessVerifier;
    }

    public string State { get; private set; } = "Idle";

    public string StatusMessage { get; private set; } = "No route has started.";

    public string? LastDiagnosticFilePath { get; private set; }

    public MarketAcquisitionWorldCompletionSummary? LatestWorldCompletionSummary { get; private set; }

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
        bool enableDiagnostics = false,
        bool includeOpportunisticChecks = false)
    {
        ArgumentNullException.ThrowIfNull(plan);

        CloseDiagnostics();
        diagnosticsRequested = enableDiagnostics;
        includeOpportunisticChecksRequested = includeOpportunisticChecks;
        diagnostics = diagnosticsRequested
            ? MarketAcquisitionRouteDiagnostics.CreateEnabled(diagnosticsDirectory, DateTimeOffset.Now)
            : MarketAcquisitionRouteDiagnostics.Disabled;
        LastDiagnosticFilePath = diagnostics.FilePath;
        session = MarketAcquisitionGuidedRouteSession.Start(plan, includeOpportunisticChecks);
        State = "Running";
        SearchSubmitted = false;
        MarketBoardCloseRequiredBeforeTravel = false;
        standaloneInputCaptureLogOpen = false;
        itemSearchAutomationStartedUtc = null;
        LatestWorldCompletionSummary = null;
        lastWorldSummarySignature = null;
        freshnessObservations.Clear();
        verifiedFreshnessObservations.Clear();
        StatusMessage = $"Route started. Next stop: {session.ActiveStop?.WorldName}.";
        diagnostics.Record(
            "route-start",
            StatusMessage,
            new Dictionary<string, string?>
            {
                ["requestId"] = plan.RequestId,
                ["worldMode"] = plan.WorldMode,
                ["lineCount"] = plan.Lines.Count.ToString(),
                ["worldCount"] = plan.WorldBatches.Count.ToString(),
                ["plannedQuantity"] = plan.PlannedQuantity.ToString(),
                ["plannedGil"] = plan.PlannedGil.ToString(),
                ["firstStop"] = session.ActiveStop?.WorldName,
                ["firstItem"] = FormatRouteItem(session.ActiveStop?.ActiveItemSubtask),
                ["firstItemSource"] = session.ActiveStop?.ActiveItemSubtask?.Source,
                ["sourceListingCount"] = plan.Diagnostics.SourceListingCount.ToString(),
                ["plannedListingCount"] = plan.Diagnostics.PlannedListingCount.ToString(),
                ["opportunisticChecks"] = includeOpportunisticChecks.ToString(),
            });
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public MarketAcquisitionRouteActionResult Restart(MarketAcquisitionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        diagnostics.Record("route-restart", "Restarting market acquisition route.");
        return Start(plan, diagnosticsRequested, includeOpportunisticChecksRequested);
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
        includeOpportunisticChecksRequested = false;
        LastDiagnosticFilePath = null;
        LatestWorldCompletionSummary = null;
        lastWorldSummarySignature = null;
        freshnessObservations.Clear();
        verifiedFreshnessObservations.Clear();
    }

    public MarketAcquisitionRouteActionResult ExecutePendingTravelCommand(Func<string, bool> processCommand)
    {
        ArgumentNullException.ThrowIfNull(processCommand);

        if (!IsRunning)
            return Fail($"Route is {State}; no command was sent.");

        var activeStop = ActiveStop;
        if (activeStop == null)
            return Complete("Route complete.");

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

    public MarketAcquisitionRouteActionResult RecordTravelBlockedByUi(MarketAcquisitionRouteTravelPreflightResult preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);

        if (!IsRunning)
            return Fail($"Route is {State}; travel preflight was not recorded.");

        StatusMessage = preflight.Message;
        diagnostics.Record(
            "travel-ui-blocked",
            preflight.Message,
            new Dictionary<string, string?>
            {
                ["blockingAddons"] = string.Join(", ", preflight.BlockingAddons),
            });
        return MarketAcquisitionRouteActionResult.Ok(preflight.Message);
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
            return Complete("Route complete.");

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
            diagnostics = MarketAcquisitionRouteDiagnostics.CreateInputCapture(diagnosticsDirectory, DateTimeOffset.Now);
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

    public MarketAcquisitionRouteActionResult RecordAutomationSnapshot(MarketBoardAutomationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!diagnostics.IsEnabled)
            return MarketAcquisitionRouteActionResult.Ok(StatusMessage);

        diagnostics.RecordAutomationSnapshot(snapshot);
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
            ["subtaskSource"] = session?.ActiveStop?.ActiveItemSubtask?.Source,
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

        var snapshot = CreateSearchAutomationSnapshot(searchResult, "Observed", details);
        diagnostics.RecordAutomationSnapshot(snapshot);

        if (searchResult.IsInProgress &&
            itemSearchAutomationStartedUtc is { } started &&
            nowUtc - started > ItemSearchAutomationTimeout)
        {
            var timeoutMessage =
                $"Market board item search automation timed out after {ItemSearchAutomationTimeout.TotalSeconds:N0}s while waiting for listings. Last status: {searchResult.Status}.";
            diagnostics.RecordAutomationSnapshot(CreateSearchAutomationSnapshot(searchResult, "TimedOut", details));
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
            return Complete("Route complete.");

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

    public MarketAcquisitionRouteActionResult RecordProbe(string currentWorld, MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        ArgumentNullException.ThrowIfNull(candidatePlan);

        if (!IsRunning)
            return Fail($"Route is {State}; probe result was not recorded.");

        var result = session?.RecordProbe(currentWorld, candidatePlan) ??
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
                ["liveCandidateStatus"] = candidatePlan.Status,
                ["wouldBuyQuantity"] = candidatePlan.WouldBuyQuantity.ToString(),
                ["wouldSpendGil"] = candidatePlan.WouldSpendGil.ToString(),
                ["subtaskSource"] = session?.ActiveStop?.ActiveItemSubtask?.Source,
                ["success"] = result.Success.ToString(),
            });

        if (result.Success)
            RecordLatestWorldSummary();

        if (result.Success && session?.ActiveStop == null)
            return Complete(result.Message);

        if (result.Success &&
            string.Equals(session?.ActiveStop?.Status, "Pending", StringComparison.OrdinalIgnoreCase))
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

    public void RecordLineProgress(
        string lineId,
        string? itemName,
        string status,
        uint purchasedQuantity,
        uint spentGil,
        string message,
        string? source = null)
    {
        diagnostics.Record(
            "line-progress",
            message,
            new Dictionary<string, string?>
            {
                ["lineId"] = lineId,
                ["itemName"] = itemName,
                ["status"] = status,
                ["source"] = source,
                ["purchasedQuantity"] = purchasedQuantity.ToString(),
                ["spentGil"] = spentGil.ToString(),
            });
    }

    public void RecordPurchaseAudit(
        string lineId,
        string? itemName,
        string worldName,
        string listingId,
        string retainerId,
        uint quantity,
        uint totalGil,
        string result,
        string? source = null)
    {
        diagnostics.Record(
            "purchase-audit",
            $"Purchase audit {result}: {itemName ?? lineId} on {worldName}, listing {listingId}.",
            new Dictionary<string, string?>
            {
                ["lineId"] = lineId,
                ["itemName"] = itemName,
                ["world"] = worldName,
                ["listingId"] = listingId,
                ["retainerId"] = retainerId,
                ["source"] = source,
                ["quantity"] = quantity.ToString(),
                ["totalGil"] = totalGil.ToString(),
                ["result"] = result,
            });

        if (result.Equals("Purchased", StringComparison.OrdinalIgnoreCase))
            RecordFreshnessObservation(lineId, itemName, worldName, listingId);
    }

    public void RecordWorldSummary(MarketAcquisitionWorldCompletionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        LatestWorldCompletionSummary = summary;
        diagnostics.Record(
            "world-summary",
            summary.Message,
            new Dictionary<string, string?>
            {
                ["world"] = summary.WorldName,
                ["dataCenter"] = summary.DataCenter,
                ["purchasedQuantity"] = summary.PurchasedQuantity.ToString(),
                ["spentGil"] = summary.SpentGil.ToString(),
                ["completedLineCount"] = summary.CompletedLineCount.ToString(),
                ["skippedLineCount"] = summary.SkippedLineCount.ToString(),
                ["failedLineCount"] = summary.FailedLineCount.ToString(),
            });
    }

    public MarketAcquisitionRouteActionResult RecordWorldPurchaseBatchComplete(
        string currentWorld,
        uint purchasedQuantity,
        uint spentGil)
    {
        if (!IsRunning)
            return Fail($"Route is {State}; world purchase result was not recorded.");

        var result = session?.RecordWorldPurchaseBatchComplete(currentWorld, purchasedQuantity, spentGil) ??
                     MarketAcquisitionGuidedRouteResult.Fail("No route has started.");
        StatusMessage = result.Message;
        SearchSubmitted = false;
        itemSearchAutomationStartedUtc = null;
        diagnostics.Record(
            "world-purchase-complete",
            result.Message,
            new Dictionary<string, string?>
            {
                ["currentWorld"] = currentWorld,
                ["purchasedQuantity"] = purchasedQuantity.ToString(),
                ["spentGil"] = spentGil.ToString(),
                ["subtaskSource"] = session?.ActiveStop?.ActiveItemSubtask?.Source,
                ["success"] = result.Success.ToString(),
            });

        if (result.Success)
            RecordLatestWorldSummary();

        if (result.Success && session?.ActiveStop == null)
            return Complete(result.Message);

        if (result.Success &&
            string.Equals(session?.ActiveStop?.Status, "Pending", StringComparison.OrdinalIgnoreCase))
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

    public async Task<MarketAcquisitionRouteActionResult> VerifyLatestWorldFreshnessAsync(
        CancellationToken cancellationToken)
    {
        if (universalisFreshnessVerifier == null)
            return MarketAcquisitionRouteActionResult.Ok("Universalis freshness verification is not configured.");

        var summary = LatestWorldCompletionSummary;
        if (summary == null)
            return MarketAcquisitionRouteActionResult.Ok("No completed world is available for Universalis freshness verification.");

        var observations = freshnessObservations
            .Where(pair => pair.Key.WorldName.Equals(summary.WorldName, StringComparison.OrdinalIgnoreCase))
            .Where(pair => !verifiedFreshnessObservations.Contains(pair.Key))
            .Select(pair => pair.Value)
            .ToList();

        if (observations.Count == 0)
            return MarketAcquisitionRouteActionResult.Ok($"No purchased listings require Universalis freshness verification for {summary.WorldName}.");

        foreach (var observation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            UniversalisFreshnessResult result;
            try
            {
                result = await universalisFreshnessVerifier(
                    observation.WorldName,
                    observation.ItemId,
                    observation.ObservedAtUtc,
                    observation.ListingIds,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = UniversalisFreshnessResult.Unavailable(ex.Message);
            }

            diagnostics.Record(
                "universalis-freshness",
                $"{result.Status}: {FormatFreshnessItem(observation)} on {observation.WorldName}. {result.Message}",
                new Dictionary<string, string?>
                {
                    ["world"] = observation.WorldName,
                    ["itemId"] = observation.ItemId.ToString(),
                    ["itemName"] = observation.ItemName,
                    ["status"] = result.Status,
                    ["message"] = result.Message,
                    ["observedAtUtc"] = observation.ObservedAtUtc.ToString("O"),
                    ["listingIds"] = string.Join(", ", observation.ListingIds),
                });
            verifiedFreshnessObservations.Add(new FreshnessObservationKey(observation.WorldName, observation.ItemId));
        }

        return MarketAcquisitionRouteActionResult.Ok(
            $"Recorded Universalis freshness for {observations.Count:N0} item(s) on {summary.WorldName}.");
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

    private void RecordLatestWorldSummary()
    {
        var summary = session?.LastWorldCompletionSummary;
        if (summary == null)
            return;

        var signature = $"{summary.DataCenter}:{summary.WorldName}:{summary.PurchasedQuantity}:{summary.SpentGil}:{summary.CompletedLineCount}:{summary.SkippedLineCount}:{summary.FailedLineCount}";
        if (string.Equals(lastWorldSummarySignature, signature, StringComparison.Ordinal))
            return;

        lastWorldSummarySignature = signature;
        RecordWorldSummary(summary);
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

    private static MarketBoardAutomationSnapshot CreateSearchAutomationSnapshot(
        MarketBoardItemSearchResult searchResult,
        string phase,
        IReadOnlyDictionary<string, string?> details)
    {
        var outcome = phase.Equals("TimedOut", StringComparison.OrdinalIgnoreCase)
            ? MarketBoardAutomationOutcome.Fatal
            : ClassifySearchOutcome(searchResult);
        return MarketBoardAutomationSnapshot.Create(
            "SearchItem",
            phase,
            "ItemSearchResultReady",
            searchResult.Status,
            outcome,
            ChooseSearchNextAction(searchResult, outcome),
            details);
    }

    private static MarketBoardAutomationOutcome ClassifySearchOutcome(MarketBoardItemSearchResult searchResult)
    {
        if (searchResult.ReadyForListings)
            return MarketBoardAutomationOutcome.Success;

        if (searchResult.IsInProgress)
            return MarketBoardAutomationOutcome.InProgress;

        return MarketBoardAutomationOutcome.Recoverable;
    }

    private static string ChooseSearchNextAction(
        MarketBoardItemSearchResult searchResult,
        MarketBoardAutomationOutcome outcome)
    {
        if (outcome == MarketBoardAutomationOutcome.Fatal)
            return "CaptureInputState";

        if (searchResult.ReadyForListings)
            return "ReadLiveListings";

        if (searchResult.IsInProgress)
            return "ContinuePolling";

        return "TryAlternateInputPath";
    }

    private static string? FormatRouteItem(MarketAcquisitionWorldItemSubtask? subtask)
    {
        if (subtask == null)
            return null;

        return string.IsNullOrWhiteSpace(subtask.ItemName)
            ? subtask.ItemId.ToString()
            : $"{subtask.ItemName} ({subtask.ItemId})";
    }

    private void RecordFreshnessObservation(
        string lineId,
        string? itemName,
        string worldName,
        string listingId)
    {
        if (string.IsNullOrWhiteSpace(worldName) ||
            string.IsNullOrWhiteSpace(listingId))
        {
            return;
        }

        var itemId = ResolveLineItemId(lineId);
        if (itemId == 0)
            return;

        var key = new FreshnessObservationKey(worldName, itemId);
        if (!freshnessObservations.TryGetValue(key, out var observation))
        {
            observation = new FreshnessObservation(worldName, itemId, itemName);
            freshnessObservations.Add(key, observation);
        }

        observation.ObservedAtUtc = DateTimeOffset.UtcNow;
        observation.ListingIds.Add(listingId);
    }

    private uint ResolveLineItemId(string lineId)
    {
        if (string.IsNullOrWhiteSpace(lineId))
            return session?.ActiveStop?.ActiveItemSubtask?.ItemId ?? 0;

        return session?.ActiveStop?.LineStates.FirstOrDefault(line =>
                   line.LineId.Equals(lineId, StringComparison.Ordinal))?.ItemId ??
               session?.ActiveStop?.ActiveItemSubtask?.ItemId ??
               0;
    }

    private static string FormatFreshnessItem(FreshnessObservation observation) =>
        string.IsNullOrWhiteSpace(observation.ItemName)
            ? $"item {observation.ItemId}"
            : $"{observation.ItemName} ({observation.ItemId})";

    private sealed record FreshnessObservationKey(string WorldName, uint ItemId);

    private sealed class FreshnessObservation
    {
        public FreshnessObservation(string worldName, uint itemId, string? itemName)
        {
            WorldName = worldName;
            ItemId = itemId;
            ItemName = itemName;
        }

        public string WorldName { get; }
        public uint ItemId { get; }
        public string? ItemName { get; }
        public DateTimeOffset ObservedAtUtc { get; set; }
        public HashSet<string> ListingIds { get; } = new(StringComparer.Ordinal);
    }
}

public delegate Task<UniversalisFreshnessResult> UniversalisFreshnessVerifierDelegate(
    string worldName,
    uint itemId,
    DateTimeOffset observedAtUtc,
    IReadOnlyCollection<string> purchasedListingIds,
    CancellationToken cancellationToken);

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
