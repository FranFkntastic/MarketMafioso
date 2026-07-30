using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketAcquisitionRouteTravelTimeoutPolicyTests
{
    [Theory]
    [InlineData("Siren", "Jenova", 3)]
    [InlineData("Siren", "Rafflesia", 6)]
    public void ResolveWorldTravelArrivalOperationTimeout_UsesTravelScope(
        string sourceWorld,
        string targetWorld,
        int expectedMinutes)
    {
        var timeout = MarketAcquisitionRouteEngine.ResolveWorldTravelArrivalOperationTimeout(
            sourceWorld,
            targetWorld);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), timeout);
    }
}
