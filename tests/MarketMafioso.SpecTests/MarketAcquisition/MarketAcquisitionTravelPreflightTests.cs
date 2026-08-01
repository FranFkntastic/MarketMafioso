using MarketMafioso.Automation.Travel;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketAcquisitionTravelPreflightTests
{
    [Fact]
    public void BusyLifestream_BlocksTheNextTravelCommand()
    {
        var result = MarketAcquisitionTravelPreflight.Evaluate(
            ReadyUi(),
            lifestreamStateAvailable: true,
            lifestreamBusy: true,
            operation: "travel to Alexander");

        Assert.Equal(MarketAcquisitionTravelPreflightState.LifestreamBusy, result.State);
        Assert.False(result.CanSendCommand);
        Assert.Contains("already handling travel", result.Message);
    }

    [Fact]
    public void IdleLifestream_AllowsTheNextTravelCommandToProceed()
    {
        var result = MarketAcquisitionTravelPreflight.Evaluate(
            ReadyUi(),
            lifestreamStateAvailable: true,
            lifestreamBusy: false,
            operation: "market-board travel");

        Assert.Equal(MarketAcquisitionTravelPreflightState.Ready, result.State);
        Assert.True(result.CanSendCommand);
        Assert.Equal("market-board travel", result.Operation);
    }

    [Fact]
    public void UnavailableLifestreamState_FailsClosedBeforeTravel()
    {
        var result = MarketAcquisitionTravelPreflight.Evaluate(
            ReadyUi(),
            lifestreamStateAvailable: false,
            lifestreamBusy: false,
            operation: "travel to Bahamut");

        Assert.Equal(MarketAcquisitionTravelPreflightState.LifestreamStateUnavailable, result.State);
        Assert.False(result.CanSendCommand);
        Assert.Contains("state is unavailable", result.Message);
    }

    [Fact]
    public void BlockingUi_RemainsTheFirstFailureReason()
    {
        var result = MarketAcquisitionTravelPreflight.Evaluate(
            AutomationTravelPreflight.Check(["ItemSearch"]),
            lifestreamStateAvailable: true,
            lifestreamBusy: true,
            operation: "travel to Jenova");

        Assert.Equal(MarketAcquisitionTravelPreflightState.UiBlocked, result.State);
        Assert.False(result.CanSendCommand);
        Assert.Contains("ItemSearch", result.Message);
    }

    private static AutomationTravelPreflightResult ReadyUi() => AutomationTravelPreflight.Check([]);
}
