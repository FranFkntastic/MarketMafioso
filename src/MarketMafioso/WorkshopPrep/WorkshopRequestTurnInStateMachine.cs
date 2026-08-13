using System.Collections.Generic;

namespace MarketMafioso.WorkshopPrep;

internal enum WorkshopRequestTurnInPhase
{
    Idle,
    WaitingForRequest,
    WaitingForContextMenu,
    WaitingForHandOver,
    Submitted,
}

internal enum WorkshopRequestTurnInAction
{
    None,
    OpenItemSelector,
    SelectEligibleItem,
    HandOver,
}

internal sealed record WorkshopRequestTurnInObservation(
    bool RequestReady,
    int RequestEntryCount,
    bool ContextMenuReady,
    bool HandOverEnabled);

internal sealed record WorkshopRequestTurnInDecision(
    WorkshopRequestTurnInAction Action,
    string Message,
    bool IsSubmitted = false);

internal sealed class WorkshopRequestTurnInStateMachine
{
    public WorkshopRequestTurnInPhase Phase { get; private set; }

    public uint? ItemId { get; private set; }

    public void Begin(uint itemId)
    {
        ItemId = itemId;
        Phase = WorkshopRequestTurnInPhase.WaitingForRequest;
    }

    public void Reset()
    {
        ItemId = null;
        Phase = WorkshopRequestTurnInPhase.Idle;
    }

    public WorkshopRequestTurnInDecision Advance(WorkshopRequestTurnInObservation observation)
    {
        if (ItemId == null || Phase == WorkshopRequestTurnInPhase.Idle)
            return new(WorkshopRequestTurnInAction.None, "No workshop material request is active.");

        if (Phase == WorkshopRequestTurnInPhase.Submitted)
            return new(WorkshopRequestTurnInAction.None, $"Workshop material {ItemId} was handed over.", IsSubmitted: true);

        if (!observation.RequestReady)
            return new(WorkshopRequestTurnInAction.None, $"Waiting for the Request window for workshop material {ItemId}.");

        if (observation.RequestEntryCount != 1)
        {
            return new(
                WorkshopRequestTurnInAction.None,
                $"Workshop material Request has {observation.RequestEntryCount} entries; expected exactly one.");
        }

        switch (Phase)
        {
            case WorkshopRequestTurnInPhase.WaitingForRequest:
                Phase = WorkshopRequestTurnInPhase.WaitingForContextMenu;
                return new(WorkshopRequestTurnInAction.OpenItemSelector, $"Opened the item selector for workshop material {ItemId}.");

            case WorkshopRequestTurnInPhase.WaitingForContextMenu when !observation.ContextMenuReady:
                return new(WorkshopRequestTurnInAction.None, $"Waiting for the eligible-item menu for workshop material {ItemId}.");

            case WorkshopRequestTurnInPhase.WaitingForContextMenu:
                Phase = WorkshopRequestTurnInPhase.WaitingForHandOver;
                return new(WorkshopRequestTurnInAction.SelectEligibleItem, $"Selected the eligible inventory item for workshop material {ItemId}.");

            case WorkshopRequestTurnInPhase.WaitingForHandOver when !observation.HandOverEnabled:
                return new(WorkshopRequestTurnInAction.None, $"Waiting for Hand Over to enable for workshop material {ItemId}.");

            case WorkshopRequestTurnInPhase.WaitingForHandOver:
                Phase = WorkshopRequestTurnInPhase.Submitted;
                return new(WorkshopRequestTurnInAction.HandOver, $"Handed over workshop material {ItemId}.", IsSubmitted: true);

            default:
                return new(WorkshopRequestTurnInAction.None, $"Waiting for workshop material {ItemId} turn-in.");
        }
    }
}

internal static class WorkshopRequestTurnInProtocol
{
    public static IReadOnlyList<int> OpenItemSelectorPayload { get; } = new[] { 2, 0, 0, 0 };

    public static IReadOnlyList<int> SelectEligibleItemPayload { get; } = new[] { 0, 0, 1_021_003, 0, 0 };
}
