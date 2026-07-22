namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteDiagnosticsPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ShouldCreatePackage_UsesSettingOrExplicitDiagnosticStart(
        bool settingEnabled,
        bool explicitDiagnosticStart,
        bool expected)
    {
        Assert.Equal(
            expected,
            MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnosticsPolicy.ShouldCreatePackage(
                settingEnabled,
                explicitDiagnosticStart));
    }

}
