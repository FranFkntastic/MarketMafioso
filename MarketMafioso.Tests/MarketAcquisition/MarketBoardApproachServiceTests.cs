using MarketMafioso.MarketAcquisition;

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
    public void Decide_WaitsForManualInteractionWhenVnavmeshUnavailableOutsideDirectRange()
    {
        var decision = MarketBoardApproachService.Decide(
            marketBoardUiOpen: false,
            boardDistance: MarketBoardApproachService.DirectInteractionDistance + 1,
            vnavmeshAvailable: false,
            vnavmeshRunning: false);

        Assert.Equal(MarketBoardApproachDecisionKind.WaitForManualOpen, decision.Kind);
    }

    [Fact]
    public void Decide_WaitsForManualInteractionWhenNoNearbyBoardIsKnown()
    {
        var decision = MarketBoardApproachService.Decide(
            marketBoardUiOpen: false,
            boardDistance: null,
            vnavmeshAvailable: true,
            vnavmeshRunning: false);

        Assert.Equal(MarketBoardApproachDecisionKind.WaitForManualOpen, decision.Kind);
    }

    [Fact]
    public void Decide_WaitsForManualInteractionWhenBoardIsOutsideApproachRange()
    {
        var decision = MarketBoardApproachService.Decide(
            marketBoardUiOpen: false,
            boardDistance: MarketBoardApproachService.MaximumApproachDistance + 1,
            vnavmeshAvailable: true,
            vnavmeshRunning: false);

        Assert.Equal(MarketBoardApproachDecisionKind.WaitForManualOpen, decision.Kind);
    }
}
