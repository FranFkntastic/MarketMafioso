using System;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ClientGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MarketMafioso.MarketDiagnostics;

internal sealed partial class RemoteSummoningBellProbe
{
    private const float BoundaryNavigationTriggerDistance = 4.60f;
    private const float BoundaryNavigationMaximumInteractionDistance = 4.75f;
    private const float BoundaryNavigationStagingDistance = 4.35f;
    private const float BoundaryNavigationStagingAcceptanceDistance = 4.55f;
    private const float BoundaryNavigationTravelDistance = 2f;
    private const float BoundaryNavigationStopRange = 0.05f;
    private const float BoundaryNavigationObservedMovement = 0.02f;
    private const int BoundaryNavigationMaximumPostInteractionFrames = 10;

    private BoundaryMotionTriggerSession? boundaryMotionTriggerSession;
    private BoundaryNavigationTriggerSession? boundaryNavigationTriggerSession;
    private DateTimeOffset? boundaryRetainerListAutoCloseDeadlineUtc;

    public string BeginBoundaryMotionCapture()
    {
        if (keyState[VirtualKey.S])
            return "Release S before arming the one-shot boundary trigger.";

        var observation = bell.ObserveLoadedBell();
        var focusTarget = targetManager.FocusTarget;
        if (!observation.Available)
            return observation.Message;
        if (focusTarget is null || focusTarget.GameObjectId != observation.BellGameObjectId)
            return "Set the loaded summoning bell as focus target before arming the S-edge trigger.";

        var captureMessage = BeginNormalCapture();
        if (normalCaptureSession is not { } active)
            return captureMessage;

        boundaryMotionTriggerSession = new(active.Arm.BellGameObjectId);
        normalCaptureView = normalCaptureView with
        {
            State = "Boundary trigger armed",
            Message = "Waiting for the first S-key down edge; the focus-target interaction will run once on the game thread.",
            Readiness = "Press and hold S. The trigger invokes ordinary focus-target interaction once, then permanently disarms.",
        };
        log.Information(
            "[MarketMafioso] Armed one-shot S-edge boundary trigger for focus-target bell {BellGameObjectId:X}.",
            active.Arm.BellGameObjectId);
        return "S-edge boundary capture armed. Press and hold S once.";
    }

    private unsafe void UpdateBoundaryMotionTrigger()
    {
        if (boundaryMotionTriggerSession is not { } trigger)
            return;
        if (normalCaptureSession is not { } active)
        {
            boundaryMotionTriggerSession = null;
            return;
        }

        var sDown = keyState[VirtualKey.S];
        if (!sDown || trigger.PreviousSDown)
        {
            trigger.PreviousSDown = sDown;
            return;
        }

        // Disarm before touching the game so no failure path or held key can retry.
        boundaryMotionTriggerSession = null;
        var observedAtUtc = DateTimeOffset.UtcNow;
        var position = CapturePosition();
        var observation = bell.ObserveLoadedBell();
        var focusTarget = targetManager.FocusTarget;
        var focusTargetId = focusTarget?.GameObjectId ?? 0;
        if (focusTarget is null || focusTargetId != trigger.BellGameObjectId)
        {
            active.BoundaryTrigger = new(
                observedAtUtc,
                position,
                observation.Distance,
                FormatGameObjectId(focusTargetId) ?? "none",
                0,
                null,
                null,
                null,
                "Focus target changed before the S-key edge; no interaction was attempted.");
            CompleteNormalCapture(
                active,
                "BoundaryTriggerTargetChanged",
                "Focus target changed before the S-key edge; no interaction was attempted.",
                false);
            return;
        }

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
        {
            active.BoundaryTrigger = new(
                observedAtUtc,
                position,
                observation.Distance,
                FormatGameObjectId(focusTargetId)!,
                0,
                null,
                null,
                null,
                "TargetSystem was unavailable; no interaction was attempted.");
            CompleteNormalCapture(
                active,
                "BoundaryTriggerUnavailable",
                "TargetSystem was unavailable; no interaction was attempted.",
                false);
            return;
        }

        targetManager.Target = focusTarget;
        var rawResult = targetSystem->InteractWithObject((ClientGameObject*)focusTarget.Address, false);
        var message =
            $"One ordinary focus-target interaction ran at {observation.Distance:F6} yalms on the first S-key edge.";
        active.BoundaryTrigger = new(
            observedAtUtc,
            position,
            observation.Distance,
            FormatGameObjectId(focusTargetId)!,
            rawResult,
            null,
            null,
            null,
            message);
        normalCaptureView = normalCaptureView with
        {
            State = "Boundary trigger fired",
            Message = message,
            Readiness = "If RetainerList opens, select one retainer and stop at its command menu; otherwise release S and wait.",
        };
        log.Information(
            "[MarketMafioso] S-edge boundary trigger invoked stock focus-target interaction for bell {BellGameObjectId:X} at {Distance:F6} yalms; result={Result} (raw={RawResult}).",
            focusTargetId,
            observation.Distance,
            rawResult != 0,
            rawResult);
    }

    public bool CanBeginBoundaryNavigationCapture()
    {
        var observation = bell.ObserveLoadedBell();
        return
            !disposed &&
            configuration.EnableMarketDiagnostics &&
            clientState.IsLoggedIn &&
            observation.Available &&
            session is null &&
            normalCaptureSession is null &&
            yieldProbeSession is null &&
            warmSessionProbeSession is null &&
            retainerRpcProbeSession is null &&
            boundaryNavigationTriggerSession is null &&
            !IsAnyRetainerSessionUiOpen() &&
            vnavmesh.IsReady &&
            !vnavmesh.IsRunning;
    }

    public string BeginBoundaryNavigationCapture()
    {
        if (boundaryNavigationTriggerSession is not null)
            return "The one-tick boundary navigation is already active.";
        if (session is not null ||
            normalCaptureSession is not null ||
            yieldProbeSession is not null ||
            warmSessionProbeSession is not null ||
            retainerRpcProbeSession is not null)
        {
            return "Another remote-bell diagnostic is already active.";
        }
        if (IsAnyRetainerSessionUiOpen())
            return "Close the current retainer interaction before running boundary navigation.";
        if (!vnavmesh.IsReady)
            return "vnavmesh is unavailable or not ready.";
        if (vnavmesh.IsRunning)
            return "vnavmesh is already running; stop its current path before this bounded test.";

        var observation = bell.ObserveLoadedBell();
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (!observation.Available || playerPosition is null)
            return observation.Message;
        if (!TryFindGameObjectPosition(observation.BellGameObjectId, out var bellPosition))
            return "The loaded bell position could not be resolved.";

        if (observation.Distance > BoundaryNavigationStagingAcceptanceDistance)
        {
            if (!TryComputeBellRadiusPosition(
                    playerPosition.Value,
                    bellPosition,
                    BoundaryNavigationStagingDistance,
                    out var stagingDestination))
            {
                return "A safe inside-range staging position could not be derived.";
            }

            boundaryNavigationTriggerSession = new(
                observation.BellGameObjectId,
                stagingDestination,
                DateTimeOffset.UtcNow,
                BoundaryNavigationStage.StagingInside);
            var stagingMove = vnavmesh.MoveCloseTo(stagingDestination, BoundaryNavigationStopRange);
            if (!stagingMove.Success)
            {
                boundaryNavigationTriggerSession = null;
                return stagingMove.Message;
            }

            normalCaptureView = new(
                true,
                false,
                "Boundary staging",
                $"vnavmesh is returning to a truthful {BoundaryNavigationStagingDistance:F2}-yalm staging point.",
                "No input is required. The recorder will arm only after the character is safely inside range.",
                FormatGameObjectId(observation.BellGameObjectId),
                observation.Distance,
                observation.OrdinaryInteractionDistance,
                normalCaptureView.LastEvidencePath);
            return "Autonomous boundary test started; returning to the inside-range staging point first.";
        }

        boundaryNavigationTriggerSession = new(
            observation.BellGameObjectId,
            playerPosition.Value,
            DateTimeOffset.UtcNow,
            BoundaryNavigationStage.StagingInside);
        return StartBoundaryOutboundCapture(boundaryNavigationTriggerSession, observation, playerPosition.Value, bellPosition);
    }

    public bool CanCloseBoundaryRetainerList() => IsAddonReady("RetainerList");

    public unsafe string CloseBoundaryRetainerList()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("RetainerList", 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible)
            return "The RetainerList is not open.";

        addon->Close(true);
        boundaryRetainerListAutoCloseDeadlineUtc = null;
        return "Requested ordinary RetainerList closure.";
    }

    private unsafe void UpdateBoundaryRetainerListAutoClose()
    {
        if (boundaryRetainerListAutoCloseDeadlineUtc is not { } deadline)
            return;

        var addon = gameGui.GetAddonByName<AtkUnitBase>("RetainerList", 1);
        if (addon != null && addon->IsReady && addon->IsVisible)
        {
            addon->Close(true);
            boundaryRetainerListAutoCloseDeadlineUtc = null;
            if (normalCaptureSession is { } active)
            {
                CompleteNormalCapture(
                    active,
                    "BoundaryAcceptedNoMovement",
                    "The stock bell scene was accepted, but the preloaded direct path produced no position delta before the event locked movement.",
                    false);
            }
            return;
        }

        if (DateTimeOffset.UtcNow >= deadline)
        {
            boundaryRetainerListAutoCloseDeadlineUtc = null;
            log.Warning("[MarketMafioso] Boundary test cleanup timed out before RetainerList became ready.");
        }
    }

    private unsafe void UpdateBoundaryNavigationTrigger()
    {
        if (boundaryNavigationTriggerSession is not { } trigger)
            return;

        var observation = bell.ObserveLoadedBell();
        if (!observation.Available)
        {
            FailBoundaryNavigation("The loaded bell disappeared during bounded navigation.");
            return;
        }

        if (trigger.Stage == BoundaryNavigationStage.StagingInside)
        {
            if (observation.Distance > BoundaryNavigationStagingAcceptanceDistance)
            {
                if (DateTimeOffset.UtcNow - trigger.StageStartedAtUtc >= TimeSpan.FromSeconds(15))
                {
                    FailBoundaryNavigation(
                        $"vnavmesh did not reach the inside staging envelope ({observation.Distance:F6}/{BoundaryNavigationStagingAcceptanceDistance:F2} yalms).");
                }
                return;
            }

            StopBoundaryNavigationPath();
            var playerPosition = objectTable.LocalPlayer?.Position;
            if (playerPosition is null ||
                !TryFindGameObjectPosition(trigger.BellGameObjectId, out var bellPosition))
            {
                FailBoundaryNavigation("The player or bell position disappeared at the staging point.");
                return;
            }

            StartBoundaryOutboundCapture(trigger, observation, playerPosition.Value, bellPosition);
            return;
        }

        if (normalCaptureSession is not { } active)
        {
            FailBoundaryNavigation("The passive recorder ended before boundary navigation completed.");
            return;
        }

        if (!trigger.NavigationObservedRunning)
        {
            if (vnavmesh.IsRunning)
            {
                trigger.NavigationObservedRunning = true;
            }
            else if (DateTimeOffset.UtcNow - trigger.StageStartedAtUtc >= TimeSpan.FromSeconds(3))
            {
                CompleteNormalCapture(
                    active,
                    "BoundaryNavigationNeverStarted",
                    "vnavmesh accepted the outward path but never entered its running state.",
                    false);
            }
            return;
        }

        if (trigger.Stage == BoundaryNavigationStage.InteractionFired)
        {
            trigger.FramesAfterInteraction++;
            var moved = MathF.Abs(observation.Distance - trigger.InteractionDistance);
            if (moved < BoundaryNavigationObservedMovement &&
                trigger.FramesAfterInteraction < BoundaryNavigationMaximumPostInteractionFrames)
            {
                return;
            }

            var stop = StopBoundaryNavigation();
            if (active.BoundaryTrigger is { } evidence)
            {
                active.BoundaryTrigger = evidence with
                {
                    PositionAfterMovement = CapturePosition(),
                    DistanceAfterMovement = observation.Distance,
                    NavigationStopMessage = stop,
                };
            }
            normalCaptureView = normalCaptureView with
            {
                State = "Boundary movement stopped",
                Message = $"The rig stopped after observing {moved:F6} yalms of post-interaction movement at {observation.Distance:F6} yalms.",
                Readiness = "No input is required. Wait for the passive recorder to classify the response.",
            };
            boundaryRetainerListAutoCloseDeadlineUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            return;
        }

        if (observation.Distance < BoundaryNavigationTriggerDistance)
        {
            if (!vnavmesh.IsRunning)
            {
                CompleteNormalCapture(
                    active,
                    "BoundaryNavigationStoppedEarly",
                    $"vnavmesh stopped before reaching the trigger distance ({observation.Distance:F6}/{BoundaryNavigationTriggerDistance:F3} yalms).",
                    false);
            }
            return;
        }

        if (observation.Distance > BoundaryNavigationMaximumInteractionDistance)
        {
            CompleteNormalCapture(
                active,
                "BoundaryNavigationOvershot",
                $"The truthful position crossed ordinary range before interaction ({observation.Distance:F6}/{BoundaryNavigationMaximumInteractionDistance:F3} yalms); no interaction was attempted.",
                false);
            return;
        }

        ClientGameObject* bellObject = null;
        foreach (var gameObject in objectTable)
        {
            if (gameObject.GameObjectId != trigger.BellGameObjectId)
                continue;

            targetManager.Target = gameObject;
            bellObject = (ClientGameObject*)gameObject.Address;
            break;
        }

        var targetSystem = TargetSystem.Instance();
        if (bellObject == null || targetSystem == null)
        {
            CompleteNormalCapture(
                active,
                "BoundaryNavigationTargetUnavailable",
                "The loaded bell or TargetSystem was unavailable at the interaction boundary.",
                false);
            return;
        }

        trigger.Stage = BoundaryNavigationStage.InteractionFired;
        trigger.InteractionDistance = observation.Distance;
        var observedAtUtc = DateTimeOffset.UtcNow;
        var position = CapturePosition();
        var rawResult = targetSystem->InteractWithObject(bellObject, false);
        var message =
            $"One ordinary interaction ran at {observation.Distance:F6} yalms during bounded outward navigation.";
        active.BoundaryTrigger = new(
            observedAtUtc,
            position,
            observation.Distance,
            FormatGameObjectId(trigger.BellGameObjectId)!,
            rawResult,
            null,
            null,
            null,
            message);
        normalCaptureView = normalCaptureView with
        {
            State = "Boundary interaction fired",
            Message = message,
            Readiness = "No input is required; one outward movement tick remains before the rig stops vnavmesh.",
        };
        log.Information(
            "[MarketMafioso] Boundary navigation invoked one stock interaction for bell {BellGameObjectId:X} at {Distance:F6} yalms; raw result={RawResult}.",
            trigger.BellGameObjectId,
            observation.Distance,
            rawResult);
    }

    private unsafe string StartBoundaryOutboundCapture(
        BoundaryNavigationTriggerSession trigger,
        Franthropy.Dalamud.Automation.Retainers.RemoteSummoningBellObservation observation,
        System.Numerics.Vector3 playerPosition,
        System.Numerics.Vector3 bellPosition)
    {
        if (!TryComputeOutwardDestination(
                playerPosition,
                bellPosition,
                BoundaryNavigationTravelDistance,
                out var destination))
        {
            FailBoundaryNavigation("A safe outward navigation direction could not be derived.");
            return "A safe outward navigation direction could not be derived.";
        }

        ClientGameObject* bellObject = null;
        foreach (var gameObject in objectTable)
        {
            if (gameObject.GameObjectId != trigger.BellGameObjectId)
                continue;

            targetManager.Target = gameObject;
            bellObject = (ClientGameObject*)gameObject.Address;
            break;
        }

        var targetSystem = TargetSystem.Instance();
        if (bellObject == null || targetSystem == null)
        {
            FailBoundaryNavigation("The loaded bell or TargetSystem was unavailable at the staging point.");
            return "The loaded bell or TargetSystem was unavailable at the staging point.";
        }

        var captureMessage = BeginNormalCapture();
        if (normalCaptureSession is not { } active)
        {
            FailBoundaryNavigation(captureMessage);
            return captureMessage;
        }

        if (!vnavmesh.SetMovementAllowed(false))
        {
            CompleteNormalCapture(
                active,
                "BoundaryNavigationMovementGateUnavailable",
                "vnavmesh could not pause movement while the direct path was prepared; no interaction was attempted.",
                false);
            return "vnavmesh could not pause movement while the direct path was prepared.";
        }

        trigger.Destination = destination;
        trigger.StageStartedAtUtc = DateTimeOffset.UtcNow;
        trigger.NavigationObservedRunning = false;
        var move = vnavmesh.MoveDirect(playerPosition, destination);
        if (!move.Success)
        {
            vnavmesh.SetMovementAllowed(true);
            CompleteNormalCapture(
                active,
                "BoundaryNavigationRejected",
                $"vnavmesh rejected the prepared direct path: {move.Message}",
                false);
            return move.Message;
        }

        trigger.Stage = BoundaryNavigationStage.InteractionFired;
        trigger.InteractionDistance = observation.Distance;
        var observedAtUtc = DateTimeOffset.UtcNow;
        var position = CapturePosition();
        var rawResult = targetSystem->InteractWithObject(bellObject, false);
        var movementReleased = vnavmesh.SetMovementAllowed(true);
        var message =
            $"One ordinary interaction ran while stationary at {observation.Distance:F6} yalms; the preloaded outward path was released immediately afterward.";
        active.BoundaryTrigger = new(
            observedAtUtc,
            position,
            observation.Distance,
            FormatGameObjectId(trigger.BellGameObjectId)!,
            rawResult,
            null,
            null,
            movementReleased
                ? null
                : "vnavmesh did not confirm that its movement gate was released.",
            message);
        normalCaptureView = normalCaptureView with
        {
            State = "Boundary navigation armed",
            Message = message,
            Readiness = "No input is required. The rig will stop after one observed movement delta.",
        };
        log.Information(
            "[MarketMafioso] Invoked one stationary stock interaction for bell {BellGameObjectId:X} at {Distance:F6} yalms, then released the preloaded direct path toward ({X:F3},{Y:F3},{Z:F3}); raw result={RawResult}, movement released={MovementReleased}.",
            active.Arm.BellGameObjectId,
            observation.Distance,
            destination.X,
            destination.Y,
            destination.Z,
            rawResult,
            movementReleased);
        return "Autonomous stationary-interaction boundary capture armed. No input is required.";
    }

    private static bool TryComputeBellRadiusPosition(
        System.Numerics.Vector3 playerPosition,
        System.Numerics.Vector3 bellPosition,
        float radius,
        out System.Numerics.Vector3 destination)
    {
        var x = playerPosition.X - bellPosition.X;
        var z = playerPosition.Z - bellPosition.Z;
        var horizontalLength = MathF.Sqrt((x * x) + (z * z));
        if (horizontalLength < 0.01f || radius <= 0)
        {
            destination = default;
            return false;
        }

        destination = new(
            bellPosition.X + ((x / horizontalLength) * radius),
            playerPosition.Y,
            bellPosition.Z + ((z / horizontalLength) * radius));
        return true;
    }

    private void FailBoundaryNavigation(string message)
    {
        StopBoundaryNavigation();
        normalCaptureView = normalCaptureView with
        {
            Active = false,
            CanArm = false,
            State = "Boundary navigation failed",
            Message = message,
            Readiness = message,
        };
        log.Warning("[MarketMafioso] Boundary navigation failed: {Message}", message);
    }

    private string StopBoundaryNavigation()
    {
        if (boundaryNavigationTriggerSession is null)
            return "No boundary navigation was active.";

        boundaryNavigationTriggerSession = null;
        return StopBoundaryNavigationPath();
    }

    private string StopBoundaryNavigationPath()
    {
        vnavmesh.SetMovementAllowed(true);
        return vnavmesh.IsReady
            ? vnavmesh.Stop().Message
            : "vnavmesh became unavailable before the owned path could be stopped.";
    }

    private sealed class BoundaryMotionTriggerSession(ulong bellGameObjectId)
    {
        public ulong BellGameObjectId { get; } = bellGameObjectId;
        public bool PreviousSDown { get; set; }
    }

    private enum BoundaryNavigationStage
    {
        StagingInside,
        MovingOutward,
        InteractionFired,
    }

    private sealed class BoundaryNavigationTriggerSession(
        ulong bellGameObjectId,
        System.Numerics.Vector3 destination,
        DateTimeOffset stageStartedAtUtc,
        BoundaryNavigationStage stage)
    {
        public ulong BellGameObjectId { get; } = bellGameObjectId;
        public System.Numerics.Vector3 Destination { get; set; } = destination;
        public DateTimeOffset StageStartedAtUtc { get; set; } = stageStartedAtUtc;
        public BoundaryNavigationStage Stage { get; set; } = stage;
        public bool NavigationObservedRunning { get; set; }
        public float InteractionDistance { get; set; }
        public int FramesAfterInteraction { get; set; }
    }
}
