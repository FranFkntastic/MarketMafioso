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

    [Fact]
    public void Restart_ReportsWithCurrentClaimToken()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        var plan = MarketAcquisitionRouteEngineTestData.Plan("Maduin");
        harness.Engine.Start(plan, MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);
        var refreshedClaim = MarketAcquisitionRouteEngineTestData.AcceptedClaim() with
        {
            ClaimToken = "refreshed-claim-token",
        };

        harness.Engine.Restart(plan, refreshedClaim);
        harness.Engine.ReportRouteProgress();

        Assert.Equal("refreshed-claim-token", Assert.Single(harness.Reporter.RouteProgressReports).ClaimToken);
    }

    [Fact]
    public void ReprepareAndRestart_ReportsWithCurrentClaimToken()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        var plan = MarketAcquisitionRouteEngineTestData.Plan("Maduin");
        harness.Engine.Start(plan, MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);
        var refreshedClaim = MarketAcquisitionRouteEngineTestData.AcceptedClaim() with
        {
            ClaimToken = "refreshed-claim-token",
        };

        harness.Engine.ReprepareAndRestart(plan, DateTimeOffset.UtcNow, refreshedClaim);
        harness.Engine.ReportRouteProgress();

        Assert.Equal("refreshed-claim-token", Assert.Single(harness.Reporter.RouteProgressReports).ClaimToken);
    }
}
