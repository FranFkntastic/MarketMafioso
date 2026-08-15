using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketAcquisitionTravelCompletionTests
{
    [Fact]
    public void MatchingWorldWhileLifestreamBusy_DoesNotCompleteTrip()
    {
        var result = MarketAcquisitionTravelCompletion.Evaluate(
            "Bahamut", "Bahamut", lifestreamStateAvailable: true, lifestreamBusy: true, marketBoardReady: false);

        Assert.Equal(MarketAcquisitionTravelCompletionState.LifestreamBusy, result.State);
        Assert.False(result.IsComplete);
        Assert.True(result.TargetWorldReached);
    }

    [Fact]
    public void IdleLifestreamWithoutMarketBoard_DoesNotCompleteTrip()
    {
        var result = MarketAcquisitionTravelCompletion.Evaluate(
            "Bahamut", "Bahamut", lifestreamStateAvailable: true, lifestreamBusy: false, marketBoardReady: false);

        Assert.Equal(MarketAcquisitionTravelCompletionState.WaitingForMarketBoard, result.State);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void IdleLifestreamWithMarketBoard_CompletesTrip()
    {
        var result = MarketAcquisitionTravelCompletion.Evaluate(
            "Bahamut", "Bahamut", lifestreamStateAvailable: true, lifestreamBusy: false, marketBoardReady: true);

        Assert.Equal(MarketAcquisitionTravelCompletionState.Ready, result.State);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void UnavailableLifestreamState_FailsClosedAfterWorldArrival()
    {
        var result = MarketAcquisitionTravelCompletion.Evaluate(
            "Bahamut", "Bahamut", lifestreamStateAvailable: false, lifestreamBusy: false, marketBoardReady: true);

        Assert.Equal(MarketAcquisitionTravelCompletionState.LifestreamStateUnavailable, result.State);
        Assert.False(result.IsComplete);
    }
}
