using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionGuidedRouteSession
{
    private int activeStopIndex;

    private MarketAcquisitionGuidedRouteSession(IReadOnlyList<MarketAcquisitionGuidedRouteStop> stops)
    {
        Stops = stops;
        Status = stops.Count == 0 ? "Complete" : "Active";
    }

    public string Status { get; private set; }
    public IReadOnlyList<MarketAcquisitionGuidedRouteStop> Stops { get; }
    public MarketAcquisitionGuidedRouteStop? ActiveStop =>
        Status == "Complete" || activeStopIndex >= Stops.Count
            ? null
            : Stops[activeStopIndex];
    public bool ShouldMonitorActiveStop =>
        ActiveStop?.Status is "TravelCommandSent" or "Arrived" or "Purchasing";

    public static MarketAcquisitionGuidedRouteSession Start(MarketAcquisitionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!string.Equals(plan.Status, "Ready", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A ready market acquisition plan is required before starting a guided route.");

        var stops = plan.WorldBatches
            .Where(batch => !string.IsNullOrWhiteSpace(batch.WorldName))
            .Select(batch => new MarketAcquisitionGuidedRouteStop
            {
                WorldName = batch.WorldName,
                DataCenter = string.IsNullOrWhiteSpace(batch.DataCenter)
                    ? MarketAcquisitionPlanner.ResolveNorthAmericaDataCenter(batch.WorldName)
                    : batch.DataCenter,
                PlannedQuantity = batch.PlannedQuantity,
                PlannedGil = batch.PlannedGil,
                LifestreamCommand = BuildLifestreamCommand(batch.WorldName),
                Status = "Pending",
            })
            .ToList();

        if (stops.Count == 0)
            throw new InvalidOperationException("A guided route requires at least one planned world batch.");

        return new MarketAcquisitionGuidedRouteSession(stops);
    }

    public MarketAcquisitionGuidedRouteResult RecordCurrentWorld(string currentWorld)
    {
        var stop = ActiveStop;
        if (stop == null)
            return MarketAcquisitionGuidedRouteResult.Fail("Guided route is already complete.");

        if (!stop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            return MarketAcquisitionGuidedRouteResult.Fail($"Waiting for {stop.WorldName}; current world is {currentWorld}.");

        stop.Status = "Arrived";
        return MarketAcquisitionGuidedRouteResult.Ok($"Arrived on {stop.WorldName}. Searching the market board item when the market board is ready.");
    }

    public MarketAcquisitionGuidedRouteResult RecordCurrentWorldUnavailable()
    {
        var stop = ActiveStop;
        if (stop == null)
            return MarketAcquisitionGuidedRouteResult.Fail("Guided route is already complete.");

        return MarketAcquisitionGuidedRouteResult.Fail(
            $"Waiting for {stop.WorldName}; current world is unavailable during world travel.");
    }

    public MarketAcquisitionGuidedRouteResult ExecuteActiveStop(Func<string, bool> processCommand)
    {
        ArgumentNullException.ThrowIfNull(processCommand);

        var stop = ActiveStop;
        if (stop == null)
            return MarketAcquisitionGuidedRouteResult.Fail("Guided route is already complete.");

        if (!processCommand(stop.LifestreamCommand))
            return MarketAcquisitionGuidedRouteResult.Fail($"Lifestream command was not handled: {stop.LifestreamCommand}");

        stop.Status = "TravelCommandSent";
        return MarketAcquisitionGuidedRouteResult.Ok($"Sent {stop.LifestreamCommand}. Waiting for arrival on {stop.WorldName}.");
    }

    public MarketAcquisitionGuidedRouteResult RecordProbe(string currentWorld, MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        ArgumentNullException.ThrowIfNull(candidatePlan);

        var stop = ActiveStop;
        if (stop == null)
            return MarketAcquisitionGuidedRouteResult.Fail("Guided route is already complete.");

        if (!stop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            return MarketAcquisitionGuidedRouteResult.Fail($"Cannot record probe for {currentWorld}; active stop is {stop.WorldName}.");

        stop.LiveCandidateStatus = candidatePlan.Status;
        stop.WouldBuyQuantity = candidatePlan.WouldBuyQuantity;
        stop.WouldSpendGil = candidatePlan.WouldSpendGil;

        if (candidatePlan.WouldBuyQuantity > 0)
        {
            stop.Status = "AwaitingPurchaseConfirmation";
            return MarketAcquisitionGuidedRouteResult.Ok(
                $"Approve purchases on {stop.WorldName}: {candidatePlan.WouldBuyQuantity:N0} item(s), {candidatePlan.WouldSpendGil:N0} gil.");
        }

        CompleteActiveStop(0, 0);
        if (Status == "Complete")
            return MarketAcquisitionGuidedRouteResult.Ok("Guided route complete. No safe live candidates remained.");

        return MarketAcquisitionGuidedRouteResult.Ok($"Recorded {currentWorld}. Next stop: {ActiveStop?.WorldName}.");
    }

    public MarketAcquisitionGuidedRouteResult ConfirmActiveWorldPurchaseBatch()
    {
        var stop = ActiveStop;
        if (stop == null)
            return MarketAcquisitionGuidedRouteResult.Fail("Guided route is already complete.");

        if (!stop.Status.Equals("AwaitingPurchaseConfirmation", StringComparison.OrdinalIgnoreCase))
            return MarketAcquisitionGuidedRouteResult.Fail($"Cannot start purchases while stop is {stop.Status}.");

        stop.Status = "Purchasing";
        return MarketAcquisitionGuidedRouteResult.Ok($"Purchasing approved for {stop.WorldName}.");
    }

    public MarketAcquisitionGuidedRouteResult RecordWorldPurchaseBatchComplete(
        string currentWorld,
        uint purchasedQuantity,
        uint spentGil)
    {
        var stop = ActiveStop;
        if (stop == null)
            return MarketAcquisitionGuidedRouteResult.Fail("Guided route is already complete.");

        if (!stop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            return MarketAcquisitionGuidedRouteResult.Fail($"Cannot complete purchases for {currentWorld}; active stop is {stop.WorldName}.");

        if (!stop.Status.Equals("Purchasing", StringComparison.OrdinalIgnoreCase))
            return MarketAcquisitionGuidedRouteResult.Fail($"Cannot complete purchases while stop is {stop.Status}.");

        CompleteActiveStop(purchasedQuantity, spentGil);
        if (Status == "Complete")
            return MarketAcquisitionGuidedRouteResult.Ok(
                $"Guided route complete. Purchased {purchasedQuantity:N0} item(s), spent {spentGil:N0} gil on {currentWorld}.");

        return MarketAcquisitionGuidedRouteResult.Ok(
            $"Completed {currentWorld}: purchased {purchasedQuantity:N0} item(s), spent {spentGil:N0} gil. Next stop: {ActiveStop?.WorldName}.");
    }

    private void CompleteActiveStop(uint purchasedQuantity, uint spentGil)
    {
        var stop = ActiveStop;
        if (stop == null)
            return;

        stop.Status = "Complete";
        stop.PurchasedQuantity = purchasedQuantity;
        stop.SpentGil = spentGil;
        activeStopIndex++;
        if (activeStopIndex >= Stops.Count)
            Status = "Complete";
    }

    private static string BuildLifestreamCommand(string worldName) => $"/li {worldName} mb";
}

public sealed record MarketAcquisitionGuidedRouteStop
{
    public string WorldName { get; init; } = string.Empty;
    public string DataCenter { get; init; } = string.Empty;
    public string LifestreamCommand { get; init; } = string.Empty;
    public uint PlannedQuantity { get; init; }
    public uint PlannedGil { get; init; }
    public string Status { get; set; } = string.Empty;
    public string? LiveCandidateStatus { get; set; }
    public uint WouldBuyQuantity { get; set; }
    public uint WouldSpendGil { get; set; }
    public uint PurchasedQuantity { get; set; }
    public uint SpentGil { get; set; }
    public bool MarketBoardTravelCommandSent { get; set; }
}

public sealed record MarketAcquisitionGuidedRouteResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;

    public static MarketAcquisitionGuidedRouteResult Ok(string message) => new()
    {
        Success = true,
        Message = message,
    };

    public static MarketAcquisitionGuidedRouteResult Fail(string message) => new()
    {
        Success = false,
        Message = message,
    };
}
