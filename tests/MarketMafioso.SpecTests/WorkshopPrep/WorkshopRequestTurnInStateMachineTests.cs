using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopRequestTurnInStateMachineTests
{
    [Fact]
    public void Delayed_addons_and_hand_over_advance_once_in_native_order()
    {
        var machine = new WorkshopRequestTurnInStateMachine();
        machine.Begin(77);

        AssertDecision(machine.Advance(new(false, 0, false, false)), WorkshopRequestTurnInAction.None);
        AssertDecision(machine.Advance(new(true, 1, false, false)), WorkshopRequestTurnInAction.OpenItemSelector);
        AssertDecision(machine.Advance(new(true, 1, false, false)), WorkshopRequestTurnInAction.None);
        AssertDecision(machine.Advance(new(true, 1, true, false)), WorkshopRequestTurnInAction.SelectEligibleItem);
        AssertDecision(machine.Advance(new(true, 1, false, false)), WorkshopRequestTurnInAction.None);
        var submitted = machine.Advance(new(true, 1, false, true));
        AssertDecision(submitted, WorkshopRequestTurnInAction.HandOver);
        Assert.True(submitted.IsSubmitted);

        var duplicate = machine.Advance(new(true, 1, false, true));
        AssertDecision(duplicate, WorkshopRequestTurnInAction.None);
        Assert.True(duplicate.IsSubmitted);
    }

    [Fact]
    public void Unexpected_request_shape_refuses_to_select_or_submit()
    {
        var machine = new WorkshopRequestTurnInStateMachine();
        machine.Begin(77);

        var decision = machine.Advance(new(true, 2, true, true));

        AssertDecision(decision, WorkshopRequestTurnInAction.None);
        Assert.Equal(WorkshopRequestTurnInPhase.WaitingForRequest, machine.Phase);
        Assert.Contains("expected exactly one", decision.Message);
    }

    [Fact]
    public void Reset_discards_in_flight_request_idempotently()
    {
        var machine = new WorkshopRequestTurnInStateMachine();
        machine.Begin(77);
        machine.Advance(new(true, 1, false, false));

        machine.Reset();
        machine.Reset();

        Assert.Null(machine.ItemId);
        Assert.Equal(WorkshopRequestTurnInPhase.Idle, machine.Phase);
        AssertDecision(
            machine.Advance(new(true, 1, true, true)),
            WorkshopRequestTurnInAction.None);
    }

    [Fact]
    public void Native_callback_payloads_match_the_observed_request_protocol()
    {
        Assert.Equal([2, 0, 0, 0], WorkshopRequestTurnInProtocol.OpenItemSelectorPayload);
        Assert.Equal([0, 0, 1_021_003, 0, 0], WorkshopRequestTurnInProtocol.SelectEligibleItemPayload);
    }

    private static void AssertDecision(
        WorkshopRequestTurnInDecision decision,
        WorkshopRequestTurnInAction expectedAction)
    {
        Assert.Equal(expectedAction, decision.Action);
    }
}
