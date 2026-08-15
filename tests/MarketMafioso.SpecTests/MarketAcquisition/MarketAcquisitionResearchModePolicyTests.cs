using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionResearchModePolicyTests
{
    [Fact]
    public void DefaultAndLegacySearchesPreserveDecisionReadyDeparture()
    {
        Assert.False(new Configuration().MarketAcquisitionExhaustiveResearchMode);
        Assert.Equal(
            MarketAcquisitionResearchModePolicy.DecisionReady,
            MarketAcquisitionResearchModePolicy.Capture(exhaustiveResearchMode: false));
        Assert.True(MarketAcquisitionResearchModePolicy.AllowsConclusivePrefixDeparture(
            new Dictionary<string, string?>()));
    }

    [Fact]
    public void UnknownCapturedModeRefusesEarlyDeparture()
    {
        Assert.False(MarketAcquisitionResearchModePolicy.AllowsConclusivePrefixDeparture(
            new Dictionary<string, string?>
            {
                [MarketAcquisitionResearchModePolicy.ContextKey] = "FutureMode",
            }));
    }

    [Fact]
    public void ExhaustiveSearchRefusesConclusivePrefixDeparture()
    {
        var context = new Dictionary<string, string?>
        {
            [MarketAcquisitionResearchModePolicy.ContextKey] =
                MarketAcquisitionResearchModePolicy.Capture(exhaustiveResearchMode: true),
        };

        Assert.False(MarketAcquisitionResearchModePolicy.AllowsConclusivePrefixDeparture(context));
    }

    [Fact]
    public void CapturedSearchModeDoesNotChangeWithLaterSettingValue()
    {
        var exhaustiveSetting = true;
        var context = new Dictionary<string, string?>
        {
            [MarketAcquisitionResearchModePolicy.ContextKey] =
                MarketAcquisitionResearchModePolicy.Capture(exhaustiveSetting),
        };
        exhaustiveSetting = false;

        Assert.False(exhaustiveSetting);
        Assert.False(MarketAcquisitionResearchModePolicy.AllowsConclusivePrefixDeparture(context));
    }
}
