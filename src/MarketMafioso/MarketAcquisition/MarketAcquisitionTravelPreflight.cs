using System;
using System.Collections.Generic;
using MarketMafioso.Automation.Travel;

namespace MarketMafioso.MarketAcquisition;

public enum MarketAcquisitionTravelPreflightState
{
    Ready,
    UiBlocked,
    LifestreamBusy,
    LifestreamStateUnavailable,
}

public sealed record MarketAcquisitionTravelPreflightResult
{
    public required MarketAcquisitionTravelPreflightState State { get; init; }
    public required string Operation { get; init; }
    public required string Message { get; init; }
    public bool BusyStateAvailable { get; init; }
    public bool LifestreamBusy { get; init; }
    public IReadOnlyList<string> BlockingAddons { get; init; } = [];

    public bool CanSendCommand => State == MarketAcquisitionTravelPreflightState.Ready;
}

public static class MarketAcquisitionTravelPreflight
{
    public static MarketAcquisitionTravelPreflightResult Evaluate(
        AutomationTravelPreflightResult uiPreflight,
        bool lifestreamStateAvailable,
        bool lifestreamBusy,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(uiPreflight);
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("Travel operation is required.", nameof(operation));

        var normalizedOperation = operation.Trim();
        if (!uiPreflight.CanSendCommand)
        {
            return new MarketAcquisitionTravelPreflightResult
            {
                State = MarketAcquisitionTravelPreflightState.UiBlocked,
                Operation = normalizedOperation,
                Message = uiPreflight.Message,
                BusyStateAvailable = lifestreamStateAvailable,
                LifestreamBusy = lifestreamBusy,
                BlockingAddons = uiPreflight.BlockingAddons,
            };
        }

        if (!lifestreamStateAvailable)
        {
            return new MarketAcquisitionTravelPreflightResult
            {
                State = MarketAcquisitionTravelPreflightState.LifestreamStateUnavailable,
                Operation = normalizedOperation,
                Message = $"Lifestream travel state is unavailable; waiting before {normalizedOperation}.",
                BusyStateAvailable = false,
                LifestreamBusy = false,
            };
        }

        if (lifestreamBusy)
        {
            return new MarketAcquisitionTravelPreflightResult
            {
                State = MarketAcquisitionTravelPreflightState.LifestreamBusy,
                Operation = normalizedOperation,
                Message = $"Lifestream is already handling travel; waiting before {normalizedOperation}.",
                BusyStateAvailable = true,
                LifestreamBusy = true,
            };
        }

        return new MarketAcquisitionTravelPreflightResult
        {
            State = MarketAcquisitionTravelPreflightState.Ready,
            Operation = normalizedOperation,
            Message = $"Lifestream travel preflight passed for {normalizedOperation}.",
            BusyStateAvailable = true,
            LifestreamBusy = false,
        };
    }
}

public enum MarketAcquisitionTravelCompletionState
{
    WaitingForWorld,
    LifestreamStateUnavailable,
    LifestreamBusy,
    WaitingForMarketBoard,
    Ready,
}

public sealed record MarketAcquisitionTravelCompletionResult
{
    public required MarketAcquisitionTravelCompletionState State { get; init; }
    public required string Message { get; init; }
    public bool TargetWorldReached { get; init; }
    public bool BusyStateAvailable { get; init; }
    public bool LifestreamBusy { get; init; }
    public bool MarketBoardReady { get; init; }
    public bool IsComplete => State == MarketAcquisitionTravelCompletionState.Ready;
}

public static class MarketAcquisitionTravelCompletion
{
    public static MarketAcquisitionTravelCompletionResult Evaluate(
        string targetWorld,
        string currentWorld,
        bool lifestreamStateAvailable,
        bool lifestreamBusy,
        bool marketBoardReady)
    {
        if (string.IsNullOrWhiteSpace(targetWorld))
            throw new ArgumentException("Target world is required.", nameof(targetWorld));
        if (string.IsNullOrWhiteSpace(currentWorld))
            throw new ArgumentException("Current world is required.", nameof(currentWorld));

        var targetWorldReached = targetWorld.Equals(currentWorld, StringComparison.OrdinalIgnoreCase);
        if (!targetWorldReached)
        {
            return Create(
                MarketAcquisitionTravelCompletionState.WaitingForWorld,
                $"Waiting for Lifestream arrival on {targetWorld}; current world is {currentWorld}.",
                false,
                lifestreamStateAvailable,
                lifestreamBusy,
                marketBoardReady);
        }

        if (!lifestreamStateAvailable)
        {
            return Create(
                MarketAcquisitionTravelCompletionState.LifestreamStateUnavailable,
                $"Arrived on {targetWorld}; waiting for authoritative Lifestream travel state.",
                true,
                false,
                false,
                marketBoardReady);
        }

        if (lifestreamBusy)
        {
            return Create(
                MarketAcquisitionTravelCompletionState.LifestreamBusy,
                $"Arrived on {targetWorld}; waiting for Lifestream to finish the market-board trip.",
                true,
                true,
                true,
                marketBoardReady);
        }

        if (!marketBoardReady)
        {
            return Create(
                MarketAcquisitionTravelCompletionState.WaitingForMarketBoard,
                $"Lifestream is idle on {targetWorld}; waiting for the market board UI from the completed trip.",
                true,
                true,
                false,
                false);
        }

        return Create(
            MarketAcquisitionTravelCompletionState.Ready,
            $"Lifestream completed the market-board trip on {targetWorld}.",
            true,
            true,
            false,
            true);
    }

    private static MarketAcquisitionTravelCompletionResult Create(
        MarketAcquisitionTravelCompletionState state,
        string message,
        bool targetWorldReached,
        bool busyStateAvailable,
        bool lifestreamBusy,
        bool marketBoardReady) => new()
        {
            State = state,
            Message = message,
            TargetWorldReached = targetWorldReached,
            BusyStateAvailable = busyStateAvailable,
            LifestreamBusy = lifestreamBusy,
            MarketBoardReady = marketBoardReady,
        };
}
