namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngineReportingTests
{
    [Fact]
    public void ReportRouteProgress_CoalescesDuplicateMessages()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);

        harness.Engine.ReportRouteProgress();
        harness.Engine.ReportRouteProgress();

        Assert.Single(harness.Reporter.RouteProgressReports);
    }

    [Fact]
    public void ReportRouteProgress_SkipsWhenReporterCannotReport()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Reporter.CanReport = false;
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);

        harness.Engine.ReportRouteProgress();

        Assert.Empty(harness.Reporter.RouteProgressReports);
    }
}
