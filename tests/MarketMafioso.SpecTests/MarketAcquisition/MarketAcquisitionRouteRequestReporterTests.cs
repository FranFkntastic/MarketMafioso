using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteRequestReporterTests
{
    [Fact]
    public void ResolveRetainerName_PreservesObservedName()
    {
        var candidate = new MarketBoardPurchaseCandidate
        {
            RetainerId = "retainer-1",
            RetainerName = "Darkwinds",
        };

        Assert.Equal("Darkwinds", MarketAcquisitionRouteRequestReporter.ResolveRetainerName(candidate));
    }

    [Fact]
    public void ResolveRetainerName_FallsBackToStableRetainerId()
    {
        var candidate = new MarketBoardPurchaseCandidate
        {
            RetainerId = "retainer-1",
        };

        Assert.Equal("Retainer retainer-1", MarketAcquisitionRouteRequestReporter.ResolveRetainerName(candidate));
    }
}
