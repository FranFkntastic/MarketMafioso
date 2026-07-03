using System.Numerics;
using MarketMafioso.MarketAcquisition;

using MarketMafioso.Automation.Travel;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketBoardApproachServiceTests
{
    [Fact]
    public void Decide_ReturnsReadyWhenMarketBoardUiIsOpen()
    {
        var decision = MarketBoardApproachService.Decide(
            marketBoardUiOpen: true,
            boardDistance: null,
            vnavmeshAvailable: false,
            vnavmeshRunning: false);

        Assert.Equal(MarketBoardApproachDecisionKind.ReadyToSearch, decision.Kind);
    }

    [Fact]
    public void Decide_ChoosesDirectInteractionInsideInteractionRange()
    {
        var decision = MarketBoardApproachService.Decide(
            marketBoardUiOpen: false,
            boardDistance: MarketBoardApproachService.DirectInteractionDistance,
            vnavmeshAvailable: true,
            vnavmeshRunning: false);

        Assert.Equal(MarketBoardApproachDecisionKind.InteractDirectly, decision.Kind);
    }

    [Fact]
    public void Decide_ContinuesVnavmeshMovementWhenAlreadyRunning()
    {
        var decision = MarketBoardApproachService.Decide(
            marketBoardUiOpen: false,
            boardDistance: MarketBoardApproachService.DirectInteractionDistance + 1,
            vnavmeshAvailable: true,
            vnavmeshRunning: true);

        Assert.Equal(MarketBoardApproachDecisionKind.WaitForMovement, decision.Kind);
    }

    [Fact]
    public void Decide_ChoosesVnavmeshOnlyOutsideDirectRange()
    {
        var decision = MarketBoardApproachService.Decide(
            marketBoardUiOpen: false,
            boardDistance: MarketBoardApproachService.DirectInteractionDistance + 1,
            vnavmeshAvailable: true,
            vnavmeshRunning: false);

        Assert.Equal(MarketBoardApproachDecisionKind.StartNavigation, decision.Kind);
    }

    [Fact]
    public void Decide_ContinuesApproachAtKnownTooFarDistance()
    {
        var decision = MarketBoardApproachService.Decide(
            marketBoardUiOpen: false,
            boardDistance: 5.49f,
            vnavmeshAvailable: true,
            vnavmeshRunning: false);

        Assert.Equal(MarketBoardApproachDecisionKind.StartNavigation, decision.Kind);
    }

    [Fact]
    public void NavigationStopDistance_IsInsideDirectInteractionDistance()
    {
        Assert.True(MarketBoardApproachService.NavigationStopDistance < MarketBoardApproachService.DirectInteractionDistance);
    }

    [Fact]
    public void Decide_RequestsMarketBoardTravelWhenVnavmeshUnavailableOutsideDirectRange()
    {
        var decision = MarketBoardApproachService.Decide(
            marketBoardUiOpen: false,
            boardDistance: MarketBoardApproachService.DirectInteractionDistance + 1,
            vnavmeshAvailable: false,
            vnavmeshRunning: false);

        Assert.Equal(MarketBoardApproachDecisionKind.RequestMarketBoardTravel, decision.Kind);
    }

    [Fact]
    public void Decide_WaitsForManualInteractionWhenNoNearbyBoardIsKnown()
    {
        var decision = MarketBoardApproachService.Decide(
            marketBoardUiOpen: false,
            boardDistance: null,
            vnavmeshAvailable: true,
            vnavmeshRunning: false);

        Assert.Equal(MarketBoardApproachDecisionKind.RequestMarketBoardTravel, decision.Kind);
    }

    [Fact]
    public void Decide_RequestsMarketBoardTravelWhenBoardIsOutsideApproachRange()
    {
        var decision = MarketBoardApproachService.Decide(
            marketBoardUiOpen: false,
            boardDistance: MarketBoardApproachService.MaximumApproachDistance + 1,
            vnavmeshAvailable: true,
            vnavmeshRunning: false);

        Assert.Equal(MarketBoardApproachDecisionKind.RequestMarketBoardTravel, decision.Kind);
    }

    [Fact]
    public void CalculateHorizontalDistance_UsesPlayerAndBoardPositions()
    {
        var distance = MarketBoardApproachService.CalculateHorizontalDistance(
            new Vector3(10, 20, 30),
            new Vector3(13, 99, 34));

        Assert.Equal(5, distance);
    }
}
