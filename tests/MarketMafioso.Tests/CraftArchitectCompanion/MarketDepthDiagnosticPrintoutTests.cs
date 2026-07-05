using System.Net;
using MarketMafioso.CraftArchitectCompanion;

namespace MarketMafioso.Tests.CraftArchitectCompanion;

public sealed class MarketDepthDiagnosticPrintoutTests
{
    [Fact]
    public void Write_IncludesRequestStepsAndEndpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mmf-market-depth-diagnostics", Guid.NewGuid().ToString("N"));
        var writtenAt = DateTimeOffset.Parse("2026-07-05T20:30:00+00:00");

        var path = MarketDepthDiagnosticPrintout.Write(
            directory,
            new MarketDepthDiagnosticReport
            {
                StartedAtUtc = writtenAt.AddSeconds(-2),
                FinishedAtUtc = writtenAt,
                Outcome = "Failure",
                Summary = "Market preview failed during Universalis listings.",
                Request = new MarketAppraisalRequest
                {
                    ItemId = 7017,
                    ItemName = "Varnish",
                    Quantity = 999,
                    HqPolicy = "Either",
                    BuyThresholdUnitPrice = 1636,
                    Region = "North America",
                    WorldMode = "Recommended",
                    SweepScope = "Region",
                },
                Steps =
                [
                    new MarketDepthDiagnosticStep
                    {
                        Name = "Universalis listings",
                        StartedAtUtc = writtenAt.AddSeconds(-1),
                        DurationMs = 1000,
                        Outcome = "Failure",
                        HttpStatusCode = HttpStatusCode.GatewayTimeout,
                        RequestUri = "https://universalis.app/api/v2/North-America/7017?listings=100",
                        ExceptionType = "System.Net.Http.HttpRequestException",
                        ExceptionMessage = "504 Gateway Timeout",
                    },
                ],
            });

        var text = File.ReadAllText(path);
        Assert.Contains("Request: Varnish (7017) x999", text, StringComparison.Ordinal);
        Assert.Contains("Threshold: 1,636 gil/unit", text, StringComparison.Ordinal);
        Assert.Contains("Universalis listings: Failure", text, StringComparison.Ordinal);
        Assert.Contains("HTTP: 504 GatewayTimeout", text, StringComparison.Ordinal);
        Assert.Contains("https://universalis.app/api/v2/North-America/7017?listings=100", text, StringComparison.Ordinal);
    }
}
