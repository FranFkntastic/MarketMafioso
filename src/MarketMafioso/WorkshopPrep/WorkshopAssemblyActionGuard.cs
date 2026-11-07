using System;

namespace MarketMafioso.WorkshopPrep;

// Every real UI action the runner takes passes through here. A healthy run makes
// steady progress; a run that repeats itself is driving a game UI that is not
// responding the way we expect, and continuing can wedge the character (Nov 6:
// Primary was left stuck after hundreds of unthrottled callbacks). Halt instead.
internal sealed class WorkshopAssemblyActionGuard
{
    internal const int MaxIdenticalActions = 3;
    internal const int MaxActionsPerState = 16;
    internal const int MaxStationReopensWithoutProgress = 6;

    private int actionsInState;
    private int identicalActions;
    private string? lastAction;
    private int stationReopensWithoutProgress;

    public void EnterState()
    {
        actionsInState = 0;
        identicalActions = 0;
        lastAction = null;
    }

    public void NoteAction(WorkshopAssemblyRunnerState state, string message)
    {
        actionsInState++;
        identicalActions = string.Equals(message, lastAction, StringComparison.Ordinal) ? identicalActions + 1 : 1;
        lastAction = message;

        if (identicalActions > MaxIdenticalActions)
        {
            throw new WorkshopAssemblyHaltException(
                $"Workshop assembly repeated the same UI action {identicalActions} times in {state} without the game responding ({message}). Halted before driving the game further.");
        }

        if (actionsInState > MaxActionsPerState)
        {
            throw new WorkshopAssemblyHaltException(
                $"Workshop assembly took {actionsInState} UI actions in {state} without moving on (last: {message}). Halted before driving the game further.");
        }
    }

    public void NoteStationReopen(string reason)
    {
        stationReopensWithoutProgress++;
        if (stationReopensWithoutProgress > MaxStationReopensWithoutProgress)
        {
            throw new WorkshopAssemblyHaltException(
                $"Workshop assembly returned to the fabrication station {stationReopensWithoutProgress} times without contributing anything (last: {reason}). Halted before driving the game further.");
        }
    }

    public void NoteProgress() => stationReopensWithoutProgress = 0;

    public void Reset()
    {
        EnterState();
        stationReopensWithoutProgress = 0;
    }
}

internal sealed class WorkshopAssemblyHaltException(string message) : InvalidOperationException(message);
