namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteDiagnosticsPolicyTests
{
    [Theory]
    [InlineData(MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnosticsLevel.Off)]
    [InlineData(MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnosticsLevel.Summary)]
    [InlineData(MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnosticsLevel.FullTrace)]
    public void Resolve_UsesConfiguredLevelForLiveRoutes(
        MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnosticsLevel configured)
    {
        Assert.Equal(
            configured,
            MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnosticsPolicy.Resolve(configured));
    }

    [Fact]
    public void Resolve_UpgradesOffToSummaryForDryRun()
    {
        Assert.Equal(
            MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnosticsLevel.Summary,
            MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnosticsPolicy.Resolve(
                MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnosticsLevel.Off,
                MarketMafioso.MarketAcquisition.MarketAcquisitionExecutionMode.DryRun));
    }
}
