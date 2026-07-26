namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionRouteDiagnosticsPolicy
{
    public static MarketAcquisitionRouteDiagnosticsLevel Resolve(
        MarketAcquisitionRouteDiagnosticsLevel configuredLevel,
        MarketAcquisitionExecutionMode executionMode = MarketAcquisitionExecutionMode.Live) =>
        executionMode == MarketAcquisitionExecutionMode.DryRun &&
        configuredLevel == MarketAcquisitionRouteDiagnosticsLevel.Off
            ? MarketAcquisitionRouteDiagnosticsLevel.Summary
            : configuredLevel;
}
