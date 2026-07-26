using System;
using System.Numerics;
using System.Threading.Tasks;
using Franthropy.Dalamud.Automation.Retainers;

namespace MarketMafioso.MarketDiagnostics;

internal enum Scene2OddballStage
{
    None,
    AwaitingLocalHide,
    AwaitingLocalShow,
    AwaitingMovementHide,
    MovingAway,
    AwaitingRemoteShow,
    AwaitingInventory,
    AwaitingInventoryClose,
    Returning,
    AwaitingCleanupShow,
    CleaningUp,
}

internal sealed record Scene2UiStateSample(
    DateTimeOffset CapturedAtUtc,
    string Stage,
    RetainerLocalUiObservation Ui,
    int PostReplayContinuationCount,
    RemoteSummoningBellProbe.ProbePosition? Position,
    float? DistanceFromBell);

internal sealed partial class RemoteSummoningBellProbe
{
    private static readonly TimeSpan Scene2UiTransitionWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Scene2ActionWindow = TimeSpan.FromSeconds(12);

    private void UpdateScene2OddballExperiment(
        WarmSessionProbeSession active,
        DateTimeOffset now,
        WarmSessionRetentionProbeObservation transport)
    {
        if (active.ReplayMode == WarmSessionReplayMode.Scene2UiResurrection)
            UpdateScene2UiResurrection(active, now, transport);
        else
            UpdateScene2DistanceContinuation(active, now, transport);
    }

    private void UpdateScene2UiResurrection(
        WarmSessionProbeSession active,
        DateTimeOffset now,
        WarmSessionRetentionProbeObservation transport)
    {
        var ui = CaptureScene2UiSample(active, transport);
        switch (active.Scene2Stage)
        {
            case Scene2OddballStage.None:
            {
                active.Scene2RetainerObjectId = ui.Ui.RetainerObjectId;
                active.Scene2ContinuationCountBeforeAction = transport.PostReplayContinuationCount;
                var hidden = retainerAutomation.HideCurrentRetainerAddonLocally();
                RecordScene2Step(active, "Hide command addon locally", hidden);
                if (!hidden.Success)
                {
                    FinishScene2UiExperiment(
                        active,
                        "Scene2UiHideFailed",
                        $"The retained scene-2 command addon could not be hidden locally ({hidden.Code}): {hidden.Message}");
                    return;
                }

                active.Scene2Stage = Scene2OddballStage.AwaitingLocalHide;
                active.Scene2StageStartedAtUtc = now;
                active.DeadlineUtc = now + Scene2UiTransitionWindow;
                warmSessionProbeView = warmSessionProbeView with
                {
                    State = "Scene 2: addon hidden locally",
                    Message = hidden.Message,
                    Readiness = "Verifying that the agent, opener, runtime retainer, and server scene stayed intact.",
                };
                return;
            }

            case Scene2OddballStage.AwaitingLocalHide:
                if (!ui.Ui.AddonVisible)
                {
                    var noContinuation =
                        transport.PostReplayContinuationCount == active.Scene2ContinuationCountBeforeAction;
                    var statePreserved =
                        ui.Ui.AgentActive &&
                        ui.Ui.OpenerAvailable &&
                        ui.Ui.RetainerObjectId == active.Scene2RetainerObjectId &&
                        ui.Ui.RetainerObjectId != 0xE0000000;
                    var verified = noContinuation && statePreserved
                        ? RetainerAutomationResult.Succeeded(
                            "RetainerAddonHideVerified",
                            "The addon disappeared without an event continuation while the accepted scene-2 state remained intact.")
                        : RetainerAutomationResult.Failed(
                            "RetainerAddonHideChangedScene",
                            $"Local hide changed protected state (continuations={transport.PostReplayContinuationCount}, agent={ui.Ui.AgentActive}, opener={ui.Ui.OpenerAvailable}, runtime=0x{ui.Ui.RetainerObjectId:X8}).");
                    RecordScene2Step(active, "Verify hidden scene-2 state", verified);

                    var shown = retainerAutomation.ShowCurrentRetainerAddonLocally();
                    RecordScene2Step(active, "Show command addon locally", shown);
                    if (!shown.Success)
                    {
                        FinishScene2UiExperiment(
                            active,
                            "Scene2UiShowFailed",
                            $"The addon hid locally, but the retained agent could not show it again ({shown.Code}): {shown.Message}");
                        return;
                    }

                    active.Scene2Stage = Scene2OddballStage.AwaitingLocalShow;
                    active.Scene2StageStartedAtUtc = now;
                    active.DeadlineUtc = now + Scene2UiTransitionWindow;
                    warmSessionProbeView = warmSessionProbeView with
                    {
                        State = "Scene 2: reopening addon",
                        Message = shown.Message,
                        Readiness = "Waiting for the same retained command UI to become ready again.",
                    };
                    return;
                }

                if (now >= active.DeadlineUtc)
                {
                    FinishScene2UiExperiment(
                        active,
                        "Scene2UiHideTimedOut",
                        "The local hide request did not make the retainer command addon disappear.");
                }
                return;

            case Scene2OddballStage.AwaitingLocalShow:
                if (ui.Ui.AddonVisible)
                {
                    active.Scene2ContinuationCountAfterAction = transport.PostReplayContinuationCount;
                    var noContinuation =
                        active.Scene2ContinuationCountAfterAction == active.Scene2ContinuationCountBeforeAction;
                    var sameRuntime =
                        ui.Ui.RetainerObjectId == active.Scene2RetainerObjectId &&
                        ui.Ui.RetainerObjectId != 0xE0000000;
                    var success = noContinuation && sameRuntime && ui.Ui.AgentActive && ui.Ui.OpenerAvailable;
                    FinishScene2UiExperiment(
                        active,
                        success ? "ConfirmedScene2UiResurrection" : "Scene2UiResurrectionChangedState",
                        success
                            ? "Confirmed: the accepted scene-2 command addon hid and reopened locally with no outbound event continuation, while the same agent opener and runtime retainer remained active."
                            : $"The command addon reopened, but protected scene-2 state changed (continuations {active.Scene2ContinuationCountBeforeAction}->{active.Scene2ContinuationCountAfterAction}, runtime 0x{active.Scene2RetainerObjectId:X8}->0x{ui.Ui.RetainerObjectId:X8}).");
                    return;
                }

                if (now >= active.DeadlineUtc)
                {
                    FinishScene2UiExperiment(
                        active,
                        "Scene2UiShowTimedOut",
                        "The retained retainer agent did not recreate its command addon inside the bounded window.");
                }
                return;
        }
    }

    private void FinishScene2UiExperiment(
        WarmSessionProbeSession active,
        string verdict,
        string message)
    {
        active.Scene2ExperimentVerdict = verdict;
        active.Scene2ExperimentMessage = message;
        active.CommandMenuObservedAfterReplay = IsAddonReady("SelectString");
        if (!active.CommandMenuObservedAfterReplay)
            retainerAutomation.ShowCurrentRetainerAddonLocally();

        active.FinalCleanupTask = RunWarmSessionFinalCleanupAsync(
            active,
            active.BootstrapCancellation.Token);
        active.Scene2Stage = Scene2OddballStage.CleaningUp;
        active.DeadlineUtc = DateTimeOffset.UtcNow + WarmSessionFinalCleanupWindow;
        warmSessionProbeView = warmSessionProbeView with
        {
            State = "Scene 2 UI probe complete; cleaning up",
            Message = message,
            Readiness = "Closing the retainer and bell session normally before restoring AutoRetainer.",
        };
    }

    private void UpdateScene2DistanceContinuation(
        WarmSessionProbeSession active,
        DateTimeOffset now,
        WarmSessionRetentionProbeObservation transport)
    {
        var ui = CaptureScene2UiSample(active, transport);
        switch (active.Scene2Stage)
        {
            case Scene2OddballStage.None:
            {
                active.Scene2RetainerObjectId = ui.Ui.RetainerObjectId;
                var hidden = retainerAutomation.HideCurrentRetainerAddonLocally();
                RecordScene2Step(active, "Hide command addon before movement", hidden);
                if (!hidden.Success)
                {
                    BeginScene2Return(
                        active,
                        "Scene2MovementHideFailed",
                        $"The accepted scene-2 addon could not be hidden before movement ({hidden.Code}): {hidden.Message}");
                    return;
                }

                active.Scene2Stage = Scene2OddballStage.AwaitingMovementHide;
                active.Scene2StageStartedAtUtc = now;
                active.DeadlineUtc = now + Scene2UiTransitionWindow;
                warmSessionProbeView = warmSessionProbeView with
                {
                    State = "Scene 2: preparing movement",
                    Message = hidden.Message,
                    Readiness = "Waiting for the addon to hide before clearing the local bell movement lock.",
                };
                return;
            }

            case Scene2OddballStage.AwaitingMovementHide:
                if (!ui.Ui.AddonVisible)
                {
                    StartScene2OutwardMovement(active, now);
                    return;
                }

                if (now >= active.DeadlineUtc)
                {
                    BeginScene2Return(
                        active,
                        "Scene2MovementHideTimedOut",
                        "The retainer command addon did not hide before the bounded movement phase.");
                }
                return;

            case Scene2OddballStage.MovingAway:
                UpdateScene2OutwardMovement(active, now);
                return;

            case Scene2OddballStage.AwaitingRemoteShow:
                if (ui.Ui.AddonVisible)
                {
                    active.ReplayPosition = CapturePosition();
                    active.ReplayDistanceFromBell = DistanceToGameObject(
                        active.Arm.BellGameObjectId,
                        active.ReplayPosition);
                    active.Scene2ContinuationCountBeforeAction = transport.PostReplayContinuationCount;
                    active.Scene2ActionTask = retainerAutomation.OpenInventoryAsync(
                        active.BootstrapCancellation.Token);
                    active.Scene2Stage = Scene2OddballStage.AwaitingInventory;
                    active.Scene2StageStartedAtUtc = now;
                    active.DeadlineUtc = now + Scene2ActionWindow;
                    warmSessionProbeView = warmSessionProbeView with
                    {
                        State = "Scene 2: remote continuation sent",
                        Message = $"Choosing Entrust or withdraw items at {active.ReplayDistanceFromBell:0.0} yalms from the bell.",
                        Readiness = "No inventory item will be moved; waiting only for the retainer inventory window.",
                        DistanceMoved = DistanceBetween(active.HeldPosition, active.ReplayPosition),
                    };
                    return;
                }

                if (now >= active.DeadlineUtc)
                {
                    BeginScene2Return(
                        active,
                        "Scene2RemoteShowTimedOut",
                        "The retained command addon did not reopen after movement.");
                }
                return;

            case Scene2OddballStage.AwaitingInventory:
                if (active.Scene2ActionTask is { IsCompleted: true } inventoryTask)
                {
                    active.Scene2InventoryResult = ReadScene2TaskResult(inventoryTask, "Scene2InventoryException");
                    active.Scene2ContinuationCountAfterAction = transport.PostReplayContinuationCount;
                    RecordScene2Step(active, "Open retainer inventory beyond bell range", active.Scene2InventoryResult);
                    if (active.Scene2InventoryResult.Success)
                    {
                        active.Scene2ExperimentVerdict = "ConfirmedScene2ContinuationBeyondRange";
                        active.Scene2ExperimentMessage =
                            $"Confirmed: an accepted scene-2 session opened retainer inventory {active.ReplayDistanceFromBell:0.0} yalms from the bell without moving an item.";
                        active.Scene2ActionTask = retainerAutomation.CloseInventoryAsync(
                            active.BootstrapCancellation.Token);
                        active.Scene2Stage = Scene2OddballStage.AwaitingInventoryClose;
                        active.Scene2StageStartedAtUtc = now;
                        active.DeadlineUtc = now + Scene2ActionWindow;
                        warmSessionProbeView = warmSessionProbeView with
                        {
                            State = "Scene 2 continuation confirmed",
                            Message = active.Scene2ExperimentMessage,
                            Readiness = "Closing the inventory locally, then returning to the bell before stock session teardown.",
                        };
                    }
                    else
                    {
                        BeginScene2Return(
                            active,
                            "Scene2ContinuationRejectedBeyondRange",
                            $"The scene-2 inventory continuation failed at {active.ReplayDistanceFromBell:0.0} yalms ({active.Scene2InventoryResult.Code}): {active.Scene2InventoryResult.Message}");
                    }
                    return;
                }

                if (now >= active.DeadlineUtc)
                {
                    BeginScene2Return(
                        active,
                        "Scene2ContinuationTimedOutBeyondRange",
                        $"The scene-2 inventory continuation did not complete at {active.ReplayDistanceFromBell:0.0} yalms.");
                }
                return;

            case Scene2OddballStage.AwaitingInventoryClose:
                if (active.Scene2ActionTask is { IsCompleted: true } closeTask)
                {
                    var closed = ReadScene2TaskResult(closeTask, "Scene2InventoryCloseException");
                    RecordScene2Step(active, "Close remote retainer inventory", closed);
                    if (IsAddonReady("SelectString"))
                        RecordScene2Step(active, "Hide command addon before return", retainerAutomation.HideCurrentRetainerAddonLocally());
                    BeginScene2Return(
                        active,
                        active.Scene2ExperimentVerdict ?? "ConfirmedScene2ContinuationBeyondRange",
                        active.Scene2ExperimentMessage ?? "The scene-2 continuation was accepted beyond bell range.");
                    return;
                }

                if (now >= active.DeadlineUtc)
                {
                    BeginScene2Return(
                        active,
                        active.Scene2ExperimentVerdict ?? "Scene2InventoryCloseTimedOut",
                        $"{active.Scene2ExperimentMessage} The inventory window did not close inside the bounded window.");
                }
                return;

            case Scene2OddballStage.Returning:
                UpdateScene2ReturnBeforeCleanup(active, now);
                return;

            case Scene2OddballStage.AwaitingCleanupShow:
                if (ui.Ui.AddonVisible)
                {
                    active.CommandMenuObservedAfterReplay = true;
                    active.FinalCleanupTask = RunWarmSessionFinalCleanupAsync(
                        active,
                        active.BootstrapCancellation.Token);
                    active.Scene2Stage = Scene2OddballStage.CleaningUp;
                    active.DeadlineUtc = now + WarmSessionFinalCleanupWindow;
                    warmSessionProbeView = warmSessionProbeView with
                    {
                        State = "Scene 2 probe complete; cleaning up",
                        Message = active.Scene2ExperimentMessage ?? "The scene-2 distance probe completed.",
                        Readiness = "Closing the stock retainer and bell session beside the bell.",
                    };
                    return;
                }

                if (IsAddonReady("RetainerList"))
                {
                    active.FinalCleanupTask = retainerAutomation.CloseRetainerListAsync(
                        active.BootstrapCancellation.Token);
                    active.Scene2Stage = Scene2OddballStage.CleaningUp;
                    active.DeadlineUtc = now + WarmSessionFinalCleanupWindow;
                    return;
                }

                if (now >= active.DeadlineUtc)
                {
                    active.Scene2ExperimentVerdict = "Scene2CleanupUiUnavailable";
                    active.Scene2ExperimentMessage =
                        $"{active.Scene2ExperimentMessage} The command UI could not be restored beside the bell for stock cleanup.";
                    CompleteWarmSessionProbe(
                        active,
                        active.Scene2ExperimentVerdict,
                        active.Scene2ExperimentMessage,
                        false);
                }
                return;
        }
    }

    private void StartScene2OutwardMovement(WarmSessionProbeSession active, DateTimeOffset now)
    {
        if (active.RequestedMovementDistance is not { } requestedDistance ||
            active.HeldPosition is not { } heldPosition ||
            !TryFindGameObjectPosition(active.Arm.BellGameObjectId, out var bellPosition) ||
            !TryComputeOutwardDestination(
                new Vector3(heldPosition.X, heldPosition.Y, heldPosition.Z),
                bellPosition,
                requestedDistance,
                out var destination))
        {
            BeginScene2Return(
                active,
                "Scene2MovementTargetUnavailable",
                "The scene-2 probe could not derive a bounded outward movement target.");
            return;
        }

        if (!vnavmesh.IsReady)
        {
            BeginScene2Return(
                active,
                "Scene2MovementUnavailable",
                "vnavmesh was unavailable for the scene-2 distance probe.");
            return;
        }

        if (!TryAcquireLocalBellConditionOverride(active, out var overrideFailure))
        {
            BeginScene2Return(
                active,
                "Scene2MovementUnavailable",
                overrideFailure);
            return;
        }

        active.NavigationTarget = new(destination.X, destination.Y, destination.Z);
        active.NavigationStartedAtUtc = now;
        var move = vnavmesh.MoveCloseTo(destination, WarmSessionNavigationStopDistance);
        active.NavigationMessage = move.Message;
        RecordScene2Step(
            active,
            "Move outward with accepted scene 2 retained",
            move.Success
                ? RetainerAutomationResult.Succeeded("Scene2MovementStarted", move.Message)
                : RetainerAutomationResult.Failed("Scene2MovementRejected", move.Message));
        if (!move.Success)
        {
            BeginScene2Return(
                active,
                "Scene2MovementRejected",
                $"vnavmesh rejected the outward scene-2 movement: {move.Message}");
            return;
        }

        active.NavigationOwned = true;
        active.Scene2Stage = Scene2OddballStage.MovingAway;
        active.Scene2StageStartedAtUtc = now;
        active.DeadlineUtc = now + WarmSessionMovementWindow;
        warmSessionProbeView = warmSessionProbeView with
        {
            State = "Scene 2: moving away",
            Message = move.Message,
            Readiness = $"Moving {requestedDistance:0.#} yalms outward while the accepted scene-2 session remains active.",
        };
    }

    private void UpdateScene2OutwardMovement(WarmSessionProbeSession active, DateTimeOffset now)
    {
        HoldLocalBellConditionClear(active);
        var moved = DistanceBetween(active.HeldPosition, CapturePosition());
        warmSessionProbeView = warmSessionProbeView with
        {
            DistanceMoved = moved,
            Readiness = $"Moving with accepted scene 2 held: {moved:0.0}/{active.RequestedMovementDistance:0.0} yalms.",
        };

        if (active.RequestedMovementDistance is { } requested &&
            moved >= requested - WarmSessionMovementTolerance)
        {
            StopOwnedWarmSessionNavigation(active);
            RestoreLocalBellCondition(active);
            active.DistanceTargetReached = true;
            active.ReplayPosition = CapturePosition();
            var shown = retainerAutomation.ShowCurrentRetainerAddonLocally();
            RecordScene2Step(active, "Reopen command addon after movement", shown);
            if (!shown.Success)
            {
                BeginScene2Return(
                    active,
                    "Scene2RemoteShowFailed",
                    $"The retained command addon could not be shown after movement ({shown.Code}): {shown.Message}");
                return;
            }

            active.Scene2Stage = Scene2OddballStage.AwaitingRemoteShow;
            active.Scene2StageStartedAtUtc = now;
            active.DeadlineUtc = now + Scene2UiTransitionWindow;
            return;
        }

        if (now >= active.DeadlineUtc ||
            (now - active.NavigationStartedAtUtc!.Value >= TimeSpan.FromSeconds(1) && !vnavmesh.IsRunning))
        {
            BeginScene2Return(
                active,
                "Scene2MovementIncomplete",
                $"The scene-2 movement stopped after {moved:0.0}/{active.RequestedMovementDistance:0.0} yalms.");
        }
    }

    private void BeginScene2Return(
        WarmSessionProbeSession active,
        string verdict,
        string message)
    {
        active.Scene2ExperimentVerdict = verdict;
        active.Scene2ExperimentMessage = message;
        active.Scene2ActionTask = null;
        StopOwnedWarmSessionNavigation(active);
        if (IsAddonReady("SelectString"))
            RecordScene2Step(active, "Hide command addon before return", retainerAutomation.HideCurrentRetainerAddonLocally());

        if (!active.LocalBellConditionOverrideActive &&
            condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedSummoningBell] &&
            !TryAcquireLocalBellConditionOverride(active, out var overrideFailure))
        {
            active.Scene2ExperimentVerdict = "Scene2ReturnUnlockFailed";
            active.Scene2ExperimentMessage = $"{message} Return unlock failed: {overrideFailure}";
            CompleteWarmSessionProbe(
                active,
                active.Scene2ExperimentVerdict,
                active.Scene2ExperimentMessage,
                IsAddonReady("SelectString"));
            return;
        }

        active.Scene2Stage = Scene2OddballStage.Returning;
        active.Scene2StageStartedAtUtc = DateTimeOffset.UtcNow;
        active.ReturnNavigationStartedAtUtc = null;
        active.DeadlineUtc = DateTimeOffset.UtcNow + WarmSessionReturnWindow;
        warmSessionProbeView = warmSessionProbeView with
        {
            State = "Scene 2: returning to bell",
            Message = message,
            Readiness = "Returning to the held in-range position before stock session cleanup.",
        };
    }

    private void UpdateScene2ReturnBeforeCleanup(WarmSessionProbeSession active, DateTimeOffset now)
    {
        HoldLocalBellConditionClear(active);
        var current = CapturePosition();
        var remaining = DistanceBetween(active.HeldPosition, current);
        if (remaining <= WarmSessionMovementTolerance)
        {
            StopOwnedWarmSessionNavigation(active);
            RestoreLocalBellCondition(active);
            active.ReturnedToHeldPosition = true;
            var shown = retainerAutomation.ShowCurrentRetainerAddonLocally();
            RecordScene2Step(active, "Restore command addon beside bell", shown);
            active.Scene2Stage = Scene2OddballStage.AwaitingCleanupShow;
            active.Scene2StageStartedAtUtc = now;
            active.DeadlineUtc = now + Scene2UiTransitionWindow;
            return;
        }

        if (now >= active.DeadlineUtc)
        {
            RestoreLocalBellCondition(active);
            StopOwnedWarmSessionNavigation(active);
            active.Scene2ExperimentVerdict = "Scene2ReturnTimedOut";
            active.Scene2ExperimentMessage =
                $"{active.Scene2ExperimentMessage} Automatic return timed out {remaining:0.0} yalms from the held position.";
            CompleteWarmSessionProbe(
                active,
                active.Scene2ExperimentVerdict,
                active.Scene2ExperimentMessage,
                IsAddonReady("SelectString"));
            return;
        }

        if (active.ReturnNavigationStartedAtUtc is null)
        {
            if (active.HeldPosition is not { } held || !vnavmesh.IsReady)
                return;
            var move = vnavmesh.MoveCloseTo(
                new Vector3(held.X, held.Y, held.Z),
                WarmSessionNavigationStopDistance);
            active.NavigationMessage = $"{active.NavigationMessage} Return: {move.Message}".Trim();
            if (!move.Success)
            {
                active.DeadlineUtc = now;
                return;
            }
            active.NavigationOwned = true;
            active.ReturnNavigationStartedAtUtc = now;
        }

        warmSessionProbeView = warmSessionProbeView with
        {
            State = "Scene 2: returning to bell",
            DistanceMoved = DistanceBetween(active.HeldPosition, current),
            Readiness = $"Returning to the in-range position ({remaining:0.0} yalms remaining).",
        };
    }

    private Scene2UiStateSample CaptureScene2UiSample(
        WarmSessionProbeSession active,
        WarmSessionRetentionProbeObservation transport)
    {
        var position = CapturePosition();
        var sample = new Scene2UiStateSample(
            DateTimeOffset.UtcNow,
            active.Scene2Stage.ToString(),
            retainerAutomation.ObserveCurrentRetainerUi(),
            transport.PostReplayContinuationCount,
            position,
            DistanceToGameObject(active.Arm.BellGameObjectId, position));
        if (active.Scene2UiSamples.Count < MaximumWarmSessionStateSamples &&
            (active.Scene2UiSamples.Count == 0 ||
             active.Scene2UiSamples[^1] with { CapturedAtUtc = sample.CapturedAtUtc } != sample))
        {
            active.Scene2UiSamples.Add(sample);
        }
        return sample;
    }

    private static RetainerAutomationResult ReadScene2TaskResult(
        Task<RetainerAutomationResult> task,
        string exceptionCode)
    {
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return RetainerAutomationResult.Failed(exceptionCode, ex.Message);
        }
    }

    private static void RecordScene2Step(
        WarmSessionProbeSession active,
        string name,
        RetainerAutomationResult result) =>
        active.Scene2ExperimentSteps.Enqueue(
            new(name, result.Success, result.Code, result.Message, DateTimeOffset.UtcNow));
}
