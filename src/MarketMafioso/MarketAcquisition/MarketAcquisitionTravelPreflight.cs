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
