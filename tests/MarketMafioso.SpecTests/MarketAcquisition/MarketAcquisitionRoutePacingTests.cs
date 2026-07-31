using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketAcquisitionRoutePacingTests
{
    [Fact]
    public void SuccessfulPurchaseEvidencePollingUsesTwoHundredMillisecondCadence()
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(200),
            MarketAcquisitionRoutePacing.PurchaseEvidencePollInterval);
    }

    [Fact]
    public void SameWorldItemTransitionKeepsMarketBoardOpen()
    {
        var nextStop = Stop("Siren");

        Assert.False(MarketAcquisitionRoutePacing.ShouldCloseMarketBoardForNextStop("Siren", nextStop));
    }

    [Fact]
    public void WorldTransitionClosesMarketBoardBeforeTravel()
    {
        Assert.True(MarketAcquisitionRoutePacing.ShouldCloseMarketBoardForNextStop("Siren", Stop("Jenova")));
        Assert.True(MarketAcquisitionRoutePacing.ShouldCloseMarketBoardForNextStop("Siren", null));
    }

    private static MarketAcquisitionGuidedRouteStop Stop(string worldName) =>
        new()
        {
            WorldName = worldName,
            DataCenter = "Aether",
            Status = "Pending",
        };
}
