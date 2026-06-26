using System;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionRouteProgressReporter
{
    public const string ProgressAction = "progress";
    public const string CompleteAction = "complete";
    public const string FailAction = "fail";

    public static string ResolveAction(string runnerState)
    {
        if (runnerState.Equals("Completed", StringComparison.OrdinalIgnoreCase))
            return CompleteAction;

        return runnerState.Equals("Failed", StringComparison.OrdinalIgnoreCase)
            ? FailAction
            : ProgressAction;
    }
}
