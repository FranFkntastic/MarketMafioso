namespace MarketMafioso.Tests;

public sealed class HttpReportResponseTests
{
    [Fact]
    public void ParseReportResponse_UsesServerProvidedDashboardAndReportUrls()
    {
        const string body = """
            {
                "id": "snapshot-1",
                "dashboardUrl": "https://dev.xivcraftarchitect.com/api/marketmafioso/",
                "reportUrl": "https://dev.xivcraftarchitect.com/api/marketmafioso/reports/snapshot-1"
            }
            """;

        var response = HttpReporter.ParseReportResponse(body);

        Assert.Equal("snapshot-1", response.ReportId);
        Assert.Equal("https://dev.xivcraftarchitect.com/api/marketmafioso/", response.DashboardUrl);
        Assert.Equal("https://dev.xivcraftarchitect.com/api/marketmafioso/reports/snapshot-1", response.ReportUrl);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/api/marketmafioso/reports/snapshot-1",
            response.ResolveReportUrl("https://dev.xivcraftarchitect.com/api/marketmafioso/inventory"));
    }

    [Fact]
    public void ParseReportResponse_DerivesReportUrlForOlderServerResponses()
    {
        const string body = """{"id":"snapshot-2"}""";

        var response = HttpReporter.ParseReportResponse(body);

        Assert.Equal("snapshot-2", response.ReportId);
        Assert.Null(response.DashboardUrl);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/api/marketmafioso/reports/snapshot-2",
            response.ResolveReportUrl("https://dev.xivcraftarchitect.com/api/marketmafioso/inventory"));
    }

    [Fact]
    public void ParseReportResponse_DerivesDashboardUrlForOlderServerResponses()
    {
        const string body = """{"id":"snapshot-3"}""";

        var response = HttpReporter.ParseReportResponse(body);

        Assert.Null(response.DashboardUrl);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/api/marketmafioso/",
            response.ResolveDashboardUrl("https://dev.xivcraftarchitect.com/api/marketmafioso/inventory"));
        Assert.Equal(
            "http://localhost:8080/",
            response.ResolveDashboardUrl("http://localhost:8080/inventory"));
    }

    [Fact]
    public void ResolveDashboardUrlForDisplay_DerivesFromConfiguredEndpointBeforeServerResponse()
    {
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/api/marketmafioso/",
            HttpReporter.ResolveDashboardUrlForDisplay(null, "https://dev.xivcraftarchitect.com/api/marketmafioso/inventory"));
        Assert.Equal(
            "http://localhost:8080/",
            HttpReporter.ResolveDashboardUrlForDisplay(null, "http://localhost:8080/inventory"));
    }
}
