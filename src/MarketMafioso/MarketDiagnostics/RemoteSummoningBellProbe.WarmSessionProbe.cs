using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Franthropy.Dalamud.Automation.Retainers;

namespace MarketMafioso.MarketDiagnostics;

internal sealed record WarmSessionRetentionProbeView(
    bool Active,
    bool CanArm,
    bool CanReplayHeldSession,
    string Mode,
    string State,
    string Message,
    string Readiness,
    double? HoldSeconds,
    float? DistanceMoved,
    string? BellGameObjectId,
    string? RetainerId,
    string? Opcode,
    string? LastEvidencePath);

internal enum WarmSessionReplayMode
{
    Immediate,
    Delayed,
    Manual,
    Distance,
}

internal sealed record WarmSessionBootstrapStep(
    string Name,
    bool Success,
    string Code,
    string Message,
    DateTimeOffset CompletedAtUtc);

internal sealed record WarmSessionBootstrapResult(
    bool Success,
    string Code,
    string Message,
    RetainerAutomationTarget? Target);

internal sealed partial class RemoteSummoningBellProbe
{
    private static readonly TimeSpan WarmSessionWorkflowWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WarmSessionReplayDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WarmSessionReplayWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WarmSessionSuccessSettleWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WarmSessionFinalCleanupWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WarmSessionCleanupWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WarmSessionManualHoldWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan WarmSessionMovementWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WarmSessionReturnWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumWarmSessionDelay = TimeSpan.FromMinutes(5);
    private const float MinimumWarmSessionMovementDistance = 5f;
    private const float MaximumWarmSessionMovementDistance = 100f;
    private const float WarmSessionMovementTolerance = 0.75f;
    private const float WarmSessionNavigationStopDistance = 0.5f;
    private const int MaximumWarmSessionStateSamples = 256;

    private WarmSessionProbeSession? warmSessionProbeSession;
    private WarmSessionRetentionProbeView warmSessionProbeView = new(
        false,
        false,
        false,
        "Immediate",
        "Idle",
        "Warm-session retention has not been tested.",
        "Stand inside ordinary range of a loaded summoning bell with every retainer window closed.",
        null,
        null,
        null,
        null,
        null,
        null);

    public WarmSessionRetentionProbeView GetWarmSessionProbeView()
    {
        if (warmSessionProbeView.Active)
            return warmSessionProbeView;

        var observation = bell.ObserveLoadedBell();
        var anyRetainerUiOpen = IsAnyRetainerSessionUiOpen();
        return warmSessionProbeView with
        {
            CanArm =
                configuration.EnableMarketDiagnostics &&
                clientState.IsLoggedIn &&
                session is null &&
                normalCaptureSession is null &&
                yieldProbeSession is null &&
                !anyRetainerUiOpen &&
                observation.Available &&
                !observation.OutsideOrdinaryInteractionRange,
            Readiness = anyRetainerUiOpen
                ? "Close the current bell/retainer session before arming retention."
                : observation.Available && observation.OutsideOrdinaryInteractionRange
                    ? $"Move inside ordinary bell range ({observation.Distance:F1}/{observation.OrdinaryInteractionDistance:F1} yalms)."
                    : observation.Available
                        ? "Ready. The proof stays beside this bell and sends one replay."
                        : observation.Message,
            BellGameObjectId = FormatGameObjectId(observation.BellGameObjectId),
        };
    }

    public string BeginWarmSessionRetentionProbe() =>
        BeginWarmSessionRetentionProbe(WarmSessionReplayMode.Immediate, WarmSessionReplayDelay, automateBootstrap: true);

    public string BeginDelayedWarmSessionRetentionProbe(TimeSpan delay)
    {
        if (delay < TimeSpan.FromSeconds(1) || delay > MaximumWarmSessionDelay)
            return "Choose a warm-session delay from 1 through 300 seconds.";

        return BeginWarmSessionRetentionProbe(WarmSessionReplayMode.Delayed, delay, automateBootstrap: true);
    }

    public string BeginManualWarmSessionRetentionProbe() =>
        BeginWarmSessionRetentionProbe(WarmSessionReplayMode.Manual, null, automateBootstrap: true);

    public string BeginDistanceWarmSessionRetentionProbe(float movementDistance)
    {
        if (movementDistance < MinimumWarmSessionMovementDistance ||
            movementDistance > MaximumWarmSessionMovementDistance)
        {
            return $"Choose a warm-session movement distance from " +
                   $"{MinimumWarmSessionMovementDistance:0} through {MaximumWarmSessionMovementDistance:0} yalms.";
        }

        return BeginWarmSessionRetentionProbe(
            WarmSessionReplayMode.Distance,
            null,
            automateBootstrap: true,
            requestedMovementDistance: movementDistance);
    }

    public string BeginManualUiWarmSessionRetentionProbe() =>
        BeginWarmSessionRetentionProbe(WarmSessionReplayMode.Immediate, WarmSessionReplayDelay, automateBootstrap: false);

    private string BeginWarmSessionRetentionProbe(
        WarmSessionReplayMode replayMode,
        TimeSpan? replayDelay,
        bool automateBootstrap,
        float? requestedMovementDistance = null)
    {
        var precondition = ValidateWarmSessionProbeStart();
        if (precondition is not null)
            return precondition;

        if (releaseSuppressionWhenRetainerListCloses)
            ReleaseAutoRetainerSuppression();
        if (!AutoRetainerSuppressionLease.TryAcquire(autoRetainer, out var suppression, out var suppressionMessage))
            return suppressionMessage;
        autoRetainerSuppression = suppression;

        var arm = bell.TryArmWarmSessionRetention();
        if (!arm.Armed)
        {
            ReleaseAutoRetainerSuppression();
            warmSessionProbeView = warmSessionProbeView with
            {
                State = "Not armed",
                Message = arm.Message,
                Readiness = arm.Message,
                BellGameObjectId = FormatGameObjectId(arm.BellGameObjectId),
            };
            return arm.Message;
        }

        var now = DateTimeOffset.UtcNow;
        var active = new WarmSessionProbeSession(
            now,
            now + WarmSessionWorkflowWindow,
            clientState.TerritoryType,
            CapturePosition(),
            objectTable.LocalPlayer?.Name.TextValue ?? string.Empty,
            arm,
            replayMode,
            replayDelay,
            automateBootstrap,
            requestedMovementDistance);
        CaptureWarmSessionStateTransition(active);
        warmSessionProbeSession = active;
        if (automateBootstrap)
            active.BootstrapTask = RunWarmSessionBootstrapAsync(active, active.BootstrapCancellation.Token);
        warmSessionProbeView = new(
            true,
            false,
            false,
            replayMode.ToString(),
            automateBootstrap ? "Automating bootstrap" : "Armed",
            automateBootstrap
                ? "Opening the bell and driving two bounded select/Quit cycles."
                : "Waiting to learn a real scene-1 retainer selection.",
            automateBootstrap
                ? "No input needed. Keep the character beside this bell while MMF reaches the held-session state."
                : replayMode switch
            {
                WarmSessionReplayMode.Delayed =>
                    $"Interact normally through the two select/Quit cycles, then close the returned list. MMF will hold the session for {replayDelay!.Value.TotalSeconds:0.#} seconds before one replay.",
                WarmSessionReplayMode.Manual =>
                    "Interact normally through the two select/Quit cycles, then close the returned list. MMF will hold the session until /mmf probe-bell-warm-replay.",
                WarmSessionReplayMode.Distance =>
                    $"MMF will move {requestedMovementDistance:0.#} yalms away after retaining the session, replay once, then return to the bell.",
                _ =>
                    "Interact normally: select a retainer, choose Quit, select a retainer again from the reopened list, choose Quit again, then close the reopened retainer list. MMF will suppress that one close and replay the exact second selection automatically.",
            },
            null,
            null,
            FormatGameObjectId(arm.BellGameObjectId),
            null,
            null,
            warmSessionProbeView.LastEvidencePath);
        log.Information(
            "[MarketMafioso] Armed warm-session retention for bell {BellGameObjectId:X}, event 0x{EventId:X}, territory {TerritoryId}.",
            arm.BellGameObjectId,
            arm.BellEventId,
            active.TerritoryId);
        return $"Warm-session {replayMode.ToString().ToLowerInvariant()} replay armed with " +
               $"{(automateBootstrap ? "automated" : "manual UI")} bootstrap. " +
               $"{warmSessionProbeView.Readiness} {suppressionMessage}";
    }

    private async Task<WarmSessionBootstrapResult> RunWarmSessionBootstrapAsync(
        WarmSessionProbeSession active,
        CancellationToken cancellationToken)
    {
        var list = await retainerAutomation.EnsureRetainerListAsync(cancellationToken).ConfigureAwait(false);
        RecordBootstrapStep(active, "Open retainer list", list);
        if (!list.Success)
            return BootstrapFailed(list);

        var firstOpen = await retainerAutomation.OpenFirstAvailableRetainerAsync(cancellationToken).ConfigureAwait(false);
        RecordBootstrapStep(
            active,
            "Select first available retainer",
            new(firstOpen.Success, firstOpen.Code, firstOpen.Message));
        if (!firstOpen.Success || firstOpen.Target is null)
            return new(false, firstOpen.Code, firstOpen.Message, null);

        var firstQuit = await retainerAutomation.CloseRetainerAsync(cancellationToken).ConfigureAwait(false);
        RecordBootstrapStep(active, "Quit first retainer visit", firstQuit);
        if (!firstQuit.Success)
            return BootstrapFailed(firstQuit, firstOpen.Target);

        var secondOpen = await retainerAutomation.OpenRetainerAsync(firstOpen.Target, cancellationToken).ConfigureAwait(false);
        RecordBootstrapStep(active, "Reselect the same retainer", secondOpen);
        if (!secondOpen.Success)
            return BootstrapFailed(secondOpen, firstOpen.Target);

        var secondQuit = await retainerAutomation.CloseRetainerAsync(cancellationToken).ConfigureAwait(false);
        RecordBootstrapStep(active, "Quit second retainer visit", secondQuit);
        if (!secondQuit.Success)
            return BootstrapFailed(secondQuit, firstOpen.Target);

        var finalClose = await retainerAutomation.CloseRetainerListAsync(cancellationToken).ConfigureAwait(false);
        RecordBootstrapStep(active, "Close returned retainer list", finalClose);
        return finalClose.Success
            ? new(true, "BootstrapComplete", "Automated select/Quit bootstrap completed.", firstOpen.Target)
            : BootstrapFailed(finalClose, firstOpen.Target);
    }

    private static WarmSessionBootstrapResult BootstrapFailed(
        RetainerAutomationResult result,
        RetainerAutomationTarget? target = null) =>
        new(false, result.Code, result.Message, target);

    private static void RecordBootstrapStep(
        WarmSessionProbeSession active,
        string name,
        RetainerAutomationResult result) =>
        active.BootstrapSteps.Enqueue(
            new(name, result.Success, result.Code, result.Message, DateTimeOffset.UtcNow));

    private async Task<RetainerAutomationResult> RunWarmSessionFinalCleanupAsync(
        WarmSessionProbeSession active,
        CancellationToken cancellationToken)
    {
        var quit = await retainerAutomation.CloseRetainerAsync(cancellationToken).ConfigureAwait(false);
        active.FinalCleanupSteps.Enqueue(
            new("Quit replayed retainer visit", quit.Success, quit.Code, quit.Message, DateTimeOffset.UtcNow));
        if (!quit.Success)
            return quit;

        var close = await retainerAutomation.CloseRetainerListAsync(cancellationToken).ConfigureAwait(false);
        active.FinalCleanupSteps.Enqueue(
            new("Close final retainer list", close.Success, close.Code, close.Message, DateTimeOffset.UtcNow));
        return close;
    }

    public string ReplayHeldWarmSession()
    {
        if (warmSessionProbeSession is not { } active)
            return "The warm-session retention probe is not active.";
        if (active.ReplayMode != WarmSessionReplayMode.Manual)
            return $"The active warm-session probe is in {active.ReplayMode.ToString().ToLowerInvariant()} mode.";

        var transport = bell.ObserveWarmSessionRetention();
        if (!transport.TeardownSuppressed)
            return "The warm session is not held yet. Finish the two select/Quit cycles and close the returned retainer list first.";
        if (transport.ReplaySent || active.ManualReplayRequested)
            return "The held warm-session replay has already been released.";
        if (IsAnyRetainerAddonOpen())
            return "Wait for every retainer addon to close before releasing the held replay.";

        var now = DateTimeOffset.UtcNow;
        active.ManualReplayRequested = true;
        active.ReplayNotBeforeUtc = now;
        active.DeadlineUtc = now + WarmSessionReplayWindow;
        warmSessionProbeView = warmSessionProbeView with
        {
            CanReplayHeldSession = false,
            State = "Replay released",
            Message = "Manual release accepted; the exact held scene-1 selection will be sent on the framework thread.",
            Readiness = "Waiting for the matching scene-2 response and retainer command menu.",
        };
        return "Manual warm-session replay released.";
    }

    public string GetWarmSessionProbeStatus()
    {
        var current = GetWarmSessionProbeView();
        var evidence = current.LastEvidencePath is null
            ? string.Empty
            : $" Evidence: {current.LastEvidencePath}";
        return $"{current.State}: {current.Message} {current.Readiness}{evidence}";
    }

    public string CancelWarmSessionProbe()
    {
        if (warmSessionProbeSession is not { } active)
            return "The warm-session retention probe is not active.";

        active.BootstrapCancellation.Cancel();
        var transport = bell.ObserveWarmSessionRetention();
        if (transport.TeardownSuppressed &&
            !transport.MatchingScene2Observed &&
            !transport.TeardownReleaseSent)
        {
            active.CancelRequested = true;
            active.CleanupStartedAtUtc = DateTimeOffset.UtcNow;
            active.DeadlineUtc = active.CleanupStartedAtUtc.Value + WarmSessionCleanupWindow;
            var release = bell.ReleaseWarmSession();
            warmSessionProbeView = warmSessionProbeView with
            {
                State = "Cleaning up",
                Message = release.Message,
                Readiness = "The exact held teardown was released; waiting briefly for the server acknowledgement.",
            };
            return "Cancellation requested; the held stock teardown was released for cleanup.";
        }

        CompleteWarmSessionProbe(
            active,
            transport.MatchingScene2Observed ? "CancelledAfterRetentionConfirmed" : "Cancelled",
            transport.MatchingScene2Observed
                ? "The probe was cancelled after the server had already accepted the retained-session replay."
                : "The warm-session retention probe was cancelled before it held a teardown.",
            IsAddonReady("SelectString"));
        return "Warm-session retention probe cancelled; bounded evidence was written.";
    }

    private void UpdateWarmSessionProbe()
    {
        if (warmSessionProbeSession is not { } active)
            return;

        CaptureWarmSessionStateTransition(active);
        if (clientState.TerritoryType != active.TerritoryId ||
            !string.Equals(
                objectTable.LocalPlayer?.Name.TextValue,
                active.CharacterName,
                StringComparison.Ordinal))
        {
            bell.StopWarmSessionRetention("Character or territory changed; no cached packet was sent.");
            CompleteWarmSessionProbe(
                active,
                "IdentityOrTerritoryChanged",
                "Character or territory changed before the warm-session proof concluded.",
                false,
                stopTransport: false);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var transport = bell.ObserveWarmSessionRetention();
        if (!UpdateWarmSessionBootstrap(active, transport))
            return;
        if (!UpdateWarmSessionFinalCleanup(active))
            return;

        if (transport.SelectionCaptured && !active.SelectionObserved)
        {
            active.SelectionObserved = true;
            warmSessionProbeView = warmSessionProbeView with
            {
                State = transport.SelectionSceneId == 1 ? "Reusable selection learned" : "Initial selection learned",
                Message = transport.Message,
                Readiness = active.AutomateBootstrap
                    ? "The automated bootstrap is continuing through the remaining stock UI transitions."
                    : transport.SelectionSceneId == 1
                        ? "Choose Quit, then close the reopened retainer list. MMF will hold that teardown and replay once."
                        : "Choose Quit, then select a retainer again from the reopened list.",
                RetainerId = $"0x{transport.RetainerId:X16}",
                Opcode = $"0x{transport.Opcode:X}",
            };
        }
        else if (transport.SelectionSceneId == 1 && !active.Scene1SelectionObserved)
        {
            active.Scene1SelectionObserved = true;
            warmSessionProbeView = warmSessionProbeView with
            {
                State = "Reusable selection learned",
                Message = transport.Message,
                Readiness = active.AutomateBootstrap
                    ? "The automated bootstrap is finishing the second Quit and closing the returned list."
                    : "Choose Quit, then close the reopened retainer list. MMF will hold that teardown and replay once.",
                RetainerId = $"0x{transport.RetainerId:X16}",
                Opcode = $"0x{transport.Opcode:X}",
            };
        }

        if (transport.State == WarmSessionRetentionProbeState.Failed)
        {
            CompleteWarmSessionProbe(active, "TransportFailed", transport.Message, false);
            return;
        }

        if (transport.TeardownSuppressed && active.TeardownSuppressedAtUtc is null)
        {
            var configuredDelay = active.ReplayDelay ?? WarmSessionReplayDelay;
            active.TeardownSuppressedAtUtc = now;
            active.HeldPosition = CapturePosition();
            active.ReplayNotBeforeUtc = active.ReplayMode is WarmSessionReplayMode.Manual or WarmSessionReplayMode.Distance
                ? null
                : now + configuredDelay;
            active.DeadlineUtc = active.ReplayMode switch
            {
                WarmSessionReplayMode.Manual => now + WarmSessionManualHoldWindow,
                WarmSessionReplayMode.Distance => now + WarmSessionMovementWindow,
                _ => now + configuredDelay + WarmSessionReplayWindow,
            };
            warmSessionProbeView = warmSessionProbeView with
            {
                State = "Session held",
                Message = transport.Message,
                CanReplayHeldSession = active.ReplayMode == WarmSessionReplayMode.Manual,
                HoldSeconds = 0,
                DistanceMoved = 0,
                Readiness = active.ReplayMode switch
                {
                    WarmSessionReplayMode.Delayed =>
                        $"Holding stationary for {configuredDelay.TotalSeconds:0.#} seconds before the single replay.",
                    WarmSessionReplayMode.Manual =>
                        "Session held. Move or wait as intended, then run /mmf probe-bell-warm-replay.",
                    WarmSessionReplayMode.Distance =>
                        $"Session held. Waiting for the stock addons to close before moving {active.RequestedMovementDistance:0.#} yalms.",
                    _ =>
                        "Waiting for the stock windows to finish closing before the single replay.",
                },
            };
        }

        if (active.TeardownSuppressedAtUtc is { } suppressionAt &&
            !transport.ReplaySent &&
            active.CleanupStartedAtUtc is null)
        {
            var heldFor = now - suppressionAt;
            var moved = DistanceBetween(active.HeldPosition, CapturePosition());
            warmSessionProbeView = warmSessionProbeView with
            {
                CanReplayHeldSession =
                    active.ReplayMode == WarmSessionReplayMode.Manual &&
                    !active.ManualReplayRequested &&
                    !IsAnyRetainerAddonOpen(),
                HoldSeconds = heldFor.TotalSeconds,
                DistanceMoved = moved,
                Readiness = active.ReplayMode switch
                {
                    WarmSessionReplayMode.Manual when !active.ManualReplayRequested =>
                        $"Held for {heldFor.TotalSeconds:0.0}s; moved {moved:0.0}y. Run /mmf probe-bell-warm-replay when ready.",
                    WarmSessionReplayMode.Delayed when active.ReplayNotBeforeUtc is { } replayAt =>
                        $"Held for {heldFor.TotalSeconds:0.0}s; replay in {Math.Max(0, (replayAt - now).TotalSeconds):0.0}s.",
                    _ => warmSessionProbeView.Readiness,
                },
            };
        }

        if (active.ReplayMode == WarmSessionReplayMode.Distance &&
            transport.TeardownSuppressed &&
            !transport.ReplaySent &&
            active.CleanupStartedAtUtc is null &&
            !IsAnyRetainerAddonOpen() &&
            !UpdateWarmSessionDistanceMovement(active, now))
        {
            return;
        }

        var replayAuthorized =
            active.ReplayMode switch
            {
                WarmSessionReplayMode.Manual => active.ManualReplayRequested,
                WarmSessionReplayMode.Distance => active.DistanceTargetReached,
                _ => true,
            };
        if (transport.TeardownSuppressed &&
            !transport.ReplaySent &&
            active.CleanupStartedAtUtc is null &&
            replayAuthorized &&
            active.ReplayNotBeforeUtc is { } replayNotBefore &&
            now >= replayNotBefore &&
            !IsAnyRetainerAddonOpen())
        {
            active.ReplayRequestedAtUtc ??= now;
            active.ReplayPosition = CapturePosition();
            var replay = bell.ReplayWarmSessionSelection();
            active.ReplayStartedAtUtc = now;
            active.DeadlineUtc = now + WarmSessionReplayWindow;
            if (!replay.ReplaySent)
            {
                BeginWarmSessionCleanup(active, replay.Message);
                return;
            }

            warmSessionProbeView = warmSessionProbeView with
            {
                State = "Replay sent",
                Message = replay.Message,
                Readiness = "Waiting for the exact bell/event scene-2 response and the retainer command menu.",
                CanReplayHeldSession = false,
                HoldSeconds = active.TeardownSuppressedAtUtc is { } heldAt
                    ? (now - heldAt).TotalSeconds
                    : null,
                DistanceMoved = DistanceBetween(active.HeldPosition, active.ReplayPosition),
            };
            transport = replay;
        }

        var commandMenuReady = IsAddonReady("SelectString");
        if (transport.MatchingScene2Observed)
        {
            active.Scene2ObservedAtUtc ??= now;
            warmSessionProbeView = warmSessionProbeView with
            {
                State = commandMenuReady ? "Confirmed; settling" : "Server accepted replay",
                Message = transport.Message,
                Readiness = commandMenuReady
                    ? "Warm-session retention worked. Hold for one second while evidence is sealed."
                    : "The server accepted the retained-session replay; waiting for the command menu.",
            };

            if (commandMenuReady &&
                now - active.Scene2ObservedAtUtc.Value >= WarmSessionSuccessSettleWindow)
            {
                if (active.AutomateBootstrap)
                {
                    active.CommandMenuObservedAfterReplay = true;
                    active.FinalCleanupTask = RunWarmSessionFinalCleanupAsync(
                        active,
                        active.BootstrapCancellation.Token);
                    active.DeadlineUtc = now + WarmSessionFinalCleanupWindow;
                    warmSessionProbeView = warmSessionProbeView with
                    {
                        State = "Confirmed; cleaning up",
                        Message = "Warm-session retention worked. Choosing Quit and closing the returned list.",
                        Readiness = "No input needed; AutoRetainer remains suppressed until the stock session is closed.",
                    };
                    return;
                }

                CompleteWarmSessionProbe(
                    active,
                    "Confirmed",
                    "Confirmed: after one stock scene-1 teardown was suppressed, the exact cached scene-1 selection received matching scene 2 and reopened the retainer command menu.",
                    true);
                return;
            }
        }

        if (active.CleanupStartedAtUtc is not null)
        {
            if (transport.CleanupAcknowledged ||
                (!condition[ConditionFlag.OccupiedSummoningBell] && !IsAnyRetainerAddonOpen()))
            {
                CompleteWarmSessionProbe(
                    active,
                    active.CancelRequested
                        ? "CancelledAndCleanedUp"
                        : active.BootstrapFailure is not null
                            ? "BootstrapFailedAndCleanedUp"
                            : "NoMatchingScene2",
                    active.CancelRequested
                        ? "The warm-session probe was cancelled and the held stock teardown was acknowledged."
                        : active.BootstrapFailure is { } bootstrapFailure
                            ? $"Automated bootstrap failed ({bootstrapFailure.Code}); the held stock teardown was released and acknowledged."
                            : "The retained-session selection produced no matching scene 2; the held stock teardown was released and acknowledged.",
                    false);
                return;
            }

            if (now >= active.DeadlineUtc)
            {
                CompleteWarmSessionProbe(
                    active,
                    active.CancelRequested
                        ? "CancelledCleanupUnconfirmed"
                        : active.BootstrapFailure is not null
                            ? "BootstrapFailureCleanupUnconfirmed"
                            : "CleanupUnconfirmed",
                    active.BootstrapFailure is { } bootstrapFailure
                        ? $"Automated bootstrap failed ({bootstrapFailure.Code}); the held stock teardown was released, but its acknowledgement was not confirmed inside the cleanup window."
                        : "The held stock teardown was released, but its acknowledgement was not confirmed inside the cleanup window.",
                    false);
            }
            return;
        }

        if (now < active.DeadlineUtc)
            return;

        if (transport.MatchingScene2Observed)
        {
            CompleteWarmSessionProbe(
                active,
                "SessionRetainedWithoutCommandMenu",
                "The server accepted the retained-session replay with matching scene 2, but the retainer command menu did not become ready.",
                false);
            return;
        }

        if (transport.TeardownSuppressed)
        {
            BeginWarmSessionCleanup(
                active,
                transport.ReplaySent
                    ? "No matching scene 2 arrived within the replay window."
                    : "The retained session never reached the replay point.");
            return;
        }

        CompleteWarmSessionProbe(
            active,
            transport.SelectionSceneId == 1
                ? "TeardownNotObserved"
                : "ReusableSelectionNotCaptured",
            transport.SelectionSceneId == 1
                ? "A reusable scene-1 selection was captured, but no final scene-1 teardown appeared before the workflow window expired."
                : "The workflow window expired before a scene-1 retainer selection was captured.",
            commandMenuReady);
    }

    private bool UpdateWarmSessionDistanceMovement(
        WarmSessionProbeSession active,
        DateTimeOffset now)
    {
        if (active.RequestedMovementDistance is not { } requestedDistance)
        {
            BeginWarmSessionCleanup(active, "The distance probe had no requested movement distance.");
            return false;
        }

        if (active.NavigationStartedAtUtc is null)
        {
            if (active.HeldPosition is not { } heldPosition ||
                !TryFindGameObjectPosition(active.Arm.BellGameObjectId, out var bellPosition) ||
                !TryComputeOutwardDestination(
                    new(heldPosition.X, heldPosition.Y, heldPosition.Z),
                    bellPosition,
                    requestedDistance,
                    out var destination))
            {
                BeginWarmSessionCleanup(
                    active,
                    "The probe could not derive a safe outward movement target from the held player and bell positions.");
                return false;
            }

            if (!vnavmesh.IsReady)
            {
                BeginWarmSessionCleanup(active, "vnavmesh was unavailable or not ready for the bounded movement probe.");
                return false;
            }

            active.NavigationTarget = new(destination.X, destination.Y, destination.Z);
            active.NavigationStartedAtUtc = now;
            var move = vnavmesh.MoveCloseTo(destination, WarmSessionNavigationStopDistance);
            active.NavigationMessage = move.Message;
            if (!move.Success)
            {
                BeginWarmSessionCleanup(active, $"vnavmesh rejected the bounded movement probe: {move.Message}");
                return false;
            }

            active.NavigationOwned = true;
            active.DeadlineUtc = now + WarmSessionMovementWindow;
            warmSessionProbeView = warmSessionProbeView with
            {
                State = "Moving with session held",
                Message = move.Message,
                Readiness =
                    $"Moving {requestedDistance:0.#} yalms outward from the bell before the single retained-session replay.",
            };
            return true;
        }

        var moved = DistanceBetween(active.HeldPosition, CapturePosition());
        if (moved >= requestedDistance - WarmSessionMovementTolerance)
        {
            StopOwnedWarmSessionNavigation(active);
            active.DistanceTargetReached = true;
            active.ReplayNotBeforeUtc = now;
            active.DeadlineUtc = now + WarmSessionReplayWindow;
            warmSessionProbeView = warmSessionProbeView with
            {
                State = "Movement target reached",
                DistanceMoved = moved,
                Readiness = $"Moved {moved:0.0} yalms with the session held; sending the one retained-session replay.",
            };
            return true;
        }

        warmSessionProbeView = warmSessionProbeView with
        {
            DistanceMoved = moved,
            Readiness = $"Moving with the session held: {moved:0.0}/{requestedDistance:0.0} yalms.",
        };

        if (now >= active.DeadlineUtc)
        {
            active.NavigationFailure =
                $"Movement timed out after reaching {moved:0.0}/{requestedDistance:0.0} yalms.";
            BeginWarmSessionCleanup(active, active.NavigationFailure);
            return false;
        }

        if (now - active.NavigationStartedAtUtc.Value >= TimeSpan.FromSeconds(1) &&
            !vnavmesh.IsRunning)
        {
            active.NavigationFailure =
                $"vnavmesh stopped after reaching only {moved:0.0}/{requestedDistance:0.0} yalms.";
            BeginWarmSessionCleanup(active, active.NavigationFailure);
            return false;
        }

        return true;
    }

    internal static bool TryComputeOutwardDestination(
        Vector3 playerPosition,
        Vector3 bellPosition,
        float movementDistance,
        out Vector3 destination)
    {
        var x = playerPosition.X - bellPosition.X;
        var z = playerPosition.Z - bellPosition.Z;
        var horizontalLength = MathF.Sqrt((x * x) + (z * z));
        if (horizontalLength < 0.01f || movementDistance <= 0)
        {
            destination = default;
            return false;
        }

        destination = new(
            playerPosition.X + ((x / horizontalLength) * movementDistance),
            playerPosition.Y,
            playerPosition.Z + ((z / horizontalLength) * movementDistance));
        return true;
    }

    private bool TryFindGameObjectPosition(ulong gameObjectId, out Vector3 position)
    {
        foreach (var gameObject in objectTable)
        {
            if (gameObject.GameObjectId != gameObjectId)
                continue;

            position = gameObject.Position;
            return true;
        }

        position = default;
        return false;
    }

    private bool UpdateWarmSessionBootstrap(
        WarmSessionProbeSession active,
        WarmSessionRetentionProbeObservation transport)
    {
        if (!active.AutomateBootstrap || active.BootstrapTask is null)
            return true;

        var steps = active.BootstrapSteps.ToArray();
        if (steps.Length > active.ObservedBootstrapStepCount)
        {
            active.ObservedBootstrapStepCount = steps.Length;
            var last = steps[^1];
            if (!transport.TeardownSuppressed)
            {
                warmSessionProbeView = warmSessionProbeView with
                {
                    State = last.Success ? "Automating bootstrap" : "Bootstrap step failed",
                    Message = last.Message,
                    Readiness = last.Success
                        ? $"Completed {steps.Length}/6 bootstrap actions."
                        : $"Stopped at bootstrap action {steps.Length}/6 ({last.Code}).",
                };
            }
        }

        if (!active.BootstrapTask.IsCompleted || active.BootstrapResult is not null)
            return true;

        try
        {
            active.BootstrapResult = active.BootstrapTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            active.BootstrapResult = new(false, "BootstrapCancelled", "Automated retainer bootstrap was cancelled.", null);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[MarketMafioso] Automated warm-session bootstrap failed unexpectedly.");
            active.BootstrapResult = new(false, "BootstrapException", ex.Message, null);
        }

        if (!active.BootstrapResult.Success)
        {
            if (transport.TeardownSuppressed &&
                !transport.MatchingScene2Observed &&
                !transport.TeardownReleaseSent)
            {
                active.BootstrapFailure = active.BootstrapResult;
                BeginWarmSessionCleanup(
                    active,
                    $"Automated bootstrap failed ({active.BootstrapResult.Code}): {active.BootstrapResult.Message}");
            }
            else
            {
                bell.StopWarmSessionRetention("The automated retainer bootstrap failed before a teardown was held.");
                CompleteWarmSessionProbe(
                    active,
                    "BootstrapFailed",
                    $"Automated bootstrap failed ({active.BootstrapResult.Code}): {active.BootstrapResult.Message}",
                    IsAddonReady("SelectString"),
                    stopTransport: false);
            }
            return false;
        }

        if (!transport.TeardownSuppressed)
        {
            warmSessionProbeView = warmSessionProbeView with
            {
                State = "Bootstrap complete",
                Message = active.BootstrapResult.Message,
                Readiness = "Waiting for the final stock teardown to enter the held-session state.",
            };
        }

        return true;
    }

    private bool UpdateWarmSessionFinalCleanup(WarmSessionProbeSession active)
    {
        if (active.FinalCleanupTask is null)
            return true;

        if (!active.FinalCleanupTask.IsCompleted)
        {
            if (DateTimeOffset.UtcNow < active.DeadlineUtc)
                return false;

            active.BootstrapCancellation.Cancel();
            CompleteWarmSessionProbe(
                active,
                "ConfirmedCleanupTimedOut",
                "Warm-session retention was confirmed, but automatic stock-session cleanup timed out.",
                active.CommandMenuObservedAfterReplay);
            return false;
        }

        if (active.FinalCleanupResult is null)
        {
            try
            {
                active.FinalCleanupResult = active.FinalCleanupTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                active.FinalCleanupResult = RetainerAutomationResult.Failed(
                    "FinalCleanupCancelled",
                    "Automatic stock-session cleanup was cancelled.");
            }
            catch (Exception ex)
            {
                log.Error(ex, "[MarketMafioso] Automatic post-proof retainer cleanup failed unexpectedly.");
                active.FinalCleanupResult = RetainerAutomationResult.Failed("FinalCleanupException", ex.Message);
            }

            if (active.FinalCleanupResult.Success &&
                active.ReplayMode == WarmSessionReplayMode.Distance)
            {
                active.DeadlineUtc = DateTimeOffset.UtcNow + WarmSessionReturnWindow;
            }
        }

        var cleanup = active.FinalCleanupResult!;
        if (cleanup.Success &&
            active.ReplayMode == WarmSessionReplayMode.Distance)
        {
            return UpdateWarmSessionReturn(active);
        }

        CompleteWarmSessionProbe(
            active,
            cleanup.Success ? "Confirmed" : "ConfirmedCleanupFailed",
            cleanup.Success
                ? "Confirmed: the retained scene-1 selection received matching scene 2, reopened the command menu, then MMF chose Quit and closed the returned retainer list normally."
                : $"Warm-session retention was confirmed, but automatic stock-session cleanup failed ({cleanup.Code}): {cleanup.Message}",
            active.CommandMenuObservedAfterReplay);
        return false;
    }

    private bool UpdateWarmSessionReturn(WarmSessionProbeSession active)
    {
        var currentPosition = CapturePosition();
        var distanceFromHeldPosition = DistanceBetween(active.HeldPosition, currentPosition);
        if (distanceFromHeldPosition <= WarmSessionMovementTolerance)
        {
            StopOwnedWarmSessionNavigation(active);
            active.ReturnedToHeldPosition = true;
            CompleteWarmSessionProbe(
                active,
                "Confirmed",
                "Confirmed: the retained scene-1 selection reopened the command menu after movement; MMF then closed the stock session and returned to the bell.",
                active.CommandMenuObservedAfterReplay);
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (now >= active.DeadlineUtc)
        {
            active.NavigationFailure =
                $"Return movement timed out {distanceFromHeldPosition:0.0} yalms from the held position.";
            CompleteWarmSessionProbe(
                active,
                "ConfirmedReturnFailed",
                $"Warm-session retention after movement was confirmed and cleaned up, but {active.NavigationFailure}",
                active.CommandMenuObservedAfterReplay);
            return false;
        }

        if (condition[ConditionFlag.OccupiedSummoningBell])
        {
            warmSessionProbeView = warmSessionProbeView with
            {
                State = "Confirmed; waiting to return",
                Readiness = "The proof is sealed. Waiting for the stock bell condition to clear before returning.",
            };
            return false;
        }

        if (active.ReturnNavigationStartedAtUtc is null)
        {
            if (active.HeldPosition is not { } heldPosition || !vnavmesh.IsReady)
            {
                active.NavigationFailure = "vnavmesh was unavailable for the automatic return to the bell.";
                CompleteWarmSessionProbe(
                    active,
                    "ConfirmedReturnFailed",
                    $"Warm-session retention after movement was confirmed and cleaned up, but {active.NavigationFailure}",
                    active.CommandMenuObservedAfterReplay);
                return false;
            }

            var destination = new Vector3(heldPosition.X, heldPosition.Y, heldPosition.Z);
            var move = vnavmesh.MoveCloseTo(destination, WarmSessionNavigationStopDistance);
            active.NavigationMessage = $"{active.NavigationMessage} Return: {move.Message}".Trim();
            if (!move.Success)
            {
                active.NavigationFailure = $"vnavmesh rejected the automatic return: {move.Message}";
                CompleteWarmSessionProbe(
                    active,
                    "ConfirmedReturnFailed",
                    $"Warm-session retention after movement was confirmed and cleaned up, but {active.NavigationFailure}",
                    active.CommandMenuObservedAfterReplay);
                return false;
            }

            active.NavigationOwned = true;
            active.ReturnNavigationStartedAtUtc = now;
            warmSessionProbeView = warmSessionProbeView with
            {
                State = "Confirmed; returning",
                Readiness = $"Returning to the held position ({distanceFromHeldPosition:0.0} yalms).",
            };
            return false;
        }

        warmSessionProbeView = warmSessionProbeView with
        {
            State = "Confirmed; returning",
            Readiness = $"Returning to the held position ({distanceFromHeldPosition:0.0} yalms remaining).",
        };

        if (now - active.ReturnNavigationStartedAtUtc.Value >= TimeSpan.FromSeconds(1) &&
            !vnavmesh.IsRunning)
        {
            active.NavigationFailure =
                $"vnavmesh stopped with {distanceFromHeldPosition:0.0} yalms left in the automatic return.";
            CompleteWarmSessionProbe(
                active,
                "ConfirmedReturnFailed",
                $"Warm-session retention after movement was confirmed and cleaned up, but {active.NavigationFailure}",
                active.CommandMenuObservedAfterReplay);
        }

        return false;
    }

    private void BeginWarmSessionCleanup(WarmSessionProbeSession active, string reason)
    {
        StopOwnedWarmSessionNavigation(active);
        active.CleanupStartedAtUtc = DateTimeOffset.UtcNow;
        active.DeadlineUtc = active.CleanupStartedAtUtc.Value + WarmSessionCleanupWindow;
        var release = bell.ReleaseWarmSession();
        warmSessionProbeView = warmSessionProbeView with
        {
            State = "Cleaning up",
            Message = $"{reason} {release.Message}",
            Readiness = "Waiting briefly for the server to acknowledge the exact held teardown.",
        };
    }

    private void CompleteWarmSessionProbe(
        WarmSessionProbeSession active,
        string verdict,
        string message,
        bool commandMenuObserved,
        bool stopTransport = true)
    {
        active.BootstrapCancellation.Cancel();
        StopOwnedWarmSessionNavigation(active);
        CaptureWarmSessionStateTransition(active);
        var transport = bell.ObserveWarmSessionRetention();
        if (stopTransport)
            bell.StopWarmSessionRetention("The warm-session retention probe concluded.");
        CaptureWarmSessionStateTransition(active);
        warmSessionProbeSession = null;

        var evidence = new WarmSessionRetentionProbeEvidence(
            active.StartedAtUtc,
            DateTimeOffset.UtcNow,
            active.TerritoryId,
            active.StartPosition,
            CapturePosition(),
            active.CharacterName,
            FormatGameObjectId(active.Arm.BellGameObjectId),
            active.Arm.BellEventId,
            active.Arm.BellEventIdSource,
            active.Arm.Distance,
            active.Arm.OrdinaryInteractionDistance,
            active.AutomateBootstrap ? "Automated" : "ManualUi",
            active.BootstrapSteps.ToArray(),
            active.FinalCleanupSteps.ToArray(),
            active.ReplayMode.ToString(),
            active.ReplayDelay?.TotalSeconds,
            active.RequestedMovementDistance,
            active.NavigationTarget,
            active.NavigationStartedAtUtc,
            active.NavigationMessage,
            active.NavigationStopMessage,
            active.NavigationFailure,
            active.DistanceTargetReached,
            active.ReturnedToHeldPosition,
            active.TeardownSuppressedAtUtc,
            active.ReplayRequestedAtUtc,
            active.ReplayStartedAtUtc,
            active.HeldPosition,
            active.ReplayPosition,
            active.TeardownSuppressedAtUtc is { } heldAt &&
            active.ReplayStartedAtUtc is { } replayAt
                ? (replayAt - heldAt).TotalMilliseconds
                : null,
            DistanceBetween(active.HeldPosition, active.ReplayPosition),
            verdict,
            message,
            commandMenuObserved,
            active.StateSamples.ToArray(),
            transport);
        var path = WriteWarmSessionEvidence(evidence);
        active.BootstrapCancellation.Dispose();

        if (IsAnyRetainerSessionUiOpen())
            releaseSuppressionWhenRetainerListCloses = autoRetainerSuppression is { Changed: true };
        else
            ReleaseAutoRetainerSuppression();

        warmSessionProbeView = new(
            false,
            false,
            false,
            active.ReplayMode.ToString(),
            verdict,
            releaseSuppressionWhenRetainerListCloses
                ? $"{message} AutoRetainer remains suppressed until the retainer session closes."
                : message,
            verdict == "Confirmed"
                ? "The retained session was confirmed and closed cleanly; the next probe may be armed."
                : message,
            active.TeardownSuppressedAtUtc is { } viewHeldAt &&
            active.ReplayStartedAtUtc is { } viewReplayAt
                ? (viewReplayAt - viewHeldAt).TotalSeconds
                : null,
            DistanceBetween(active.HeldPosition, active.ReplayPosition),
            FormatGameObjectId(active.Arm.BellGameObjectId),
            transport.RetainerId == 0 ? null : $"0x{transport.RetainerId:X16}",
            transport.Opcode == 0 ? null : $"0x{transport.Opcode:X}",
            path);

        if (verdict.StartsWith("Confirmed", StringComparison.Ordinal) ||
            verdict == "SessionRetainedWithoutCommandMenu")
            chatGui.Print($"[MMF] Warm-session retention: {message}");
        else
            chatGui.PrintError($"[MMF] Warm-session retention: {message}");
        log.Information(
            "[MarketMafioso] Warm-session retention concluded {Verdict}. Evidence: {EvidencePath}",
            verdict,
            path ?? "(write failed)");
    }

    private void StopOwnedWarmSessionNavigation(WarmSessionProbeSession active)
    {
        if (!active.NavigationOwned)
            return;

        active.NavigationOwned = false;
        if (!vnavmesh.IsRunning)
            return;

        var stop = vnavmesh.Stop();
        active.NavigationStopMessage = stop.Message;
    }

    private string? ValidateWarmSessionProbeStart()
    {
        if (disposed)
            return "The warm-session retention probe is unavailable because it has been disposed.";
        if (!configuration.EnableMarketDiagnostics)
            return "Enable Market Diagnostics in settings before running the warm-session retention probe.";
        if (!clientState.IsLoggedIn)
            return "The warm-session retention probe requires a logged-in character.";
        if (session is not null)
            return "The remote bell interaction probe is already active.";
        if (normalCaptureSession is not null)
            return "The normal bell recorder is already active.";
        if (yieldProbeSession is not null)
            return "The YieldEventScene2 probe is already active.";
        if (warmSessionProbeSession is not null)
            return "The warm-session retention probe is already active.";
        if (IsAnyRetainerSessionUiOpen())
            return "Close every bell and retainer window before starting the warm-session retention probe.";
        return null;
    }

    private void CaptureWarmSessionStateTransition(WarmSessionProbeSession active)
    {
        if (active.StateSamples.Count >= MaximumWarmSessionStateSamples)
            return;

        var state = CaptureNormalState(active.Arm.BellEventId);
        if (state == active.LastState)
            return;

        active.LastState = state;
        active.StateSamples.Add(new(DateTimeOffset.UtcNow, state));
    }

    private string? WriteWarmSessionEvidence(WarmSessionRetentionProbeEvidence evidence)
    {
        try
        {
            Directory.CreateDirectory(evidenceDirectory);
            var path = Path.Combine(
                evidenceDirectory,
                $"warm-session-retention-{evidence.StartedAtUtc:yyyyMMdd-HHmmss-fff}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(evidence, JsonOptions));
            return path;
        }
        catch (Exception ex)
        {
            log.Error(ex, "[MarketMafioso] Unable to write warm-session retention evidence.");
            return null;
        }
    }

    private sealed class WarmSessionProbeSession
    {
        public WarmSessionProbeSession(
            DateTimeOffset startedAtUtc,
            DateTimeOffset deadlineUtc,
            uint territoryId,
            ProbePosition? startPosition,
            string characterName,
            WarmSessionRetentionArmResult arm,
            WarmSessionReplayMode replayMode,
            TimeSpan? replayDelay,
            bool automateBootstrap,
            float? requestedMovementDistance)
        {
            StartedAtUtc = startedAtUtc;
            DeadlineUtc = deadlineUtc;
            TerritoryId = territoryId;
            StartPosition = startPosition;
            CharacterName = characterName;
            Arm = arm;
            ReplayMode = replayMode;
            ReplayDelay = replayDelay;
            AutomateBootstrap = automateBootstrap;
            RequestedMovementDistance = requestedMovementDistance;
        }

        public DateTimeOffset StartedAtUtc { get; }
        public DateTimeOffset DeadlineUtc { get; set; }
        public uint TerritoryId { get; }
        public ProbePosition? StartPosition { get; }
        public string CharacterName { get; }
        public WarmSessionRetentionArmResult Arm { get; }
        public WarmSessionReplayMode ReplayMode { get; }
        public TimeSpan? ReplayDelay { get; }
        public bool AutomateBootstrap { get; }
        public float? RequestedMovementDistance { get; }
        public CancellationTokenSource BootstrapCancellation { get; } = new();
        public ConcurrentQueue<WarmSessionBootstrapStep> BootstrapSteps { get; } = new();
        public ConcurrentQueue<WarmSessionBootstrapStep> FinalCleanupSteps { get; } = new();
        public Task<WarmSessionBootstrapResult>? BootstrapTask { get; set; }
        public WarmSessionBootstrapResult? BootstrapResult { get; set; }
        public WarmSessionBootstrapResult? BootstrapFailure { get; set; }
        public Task<RetainerAutomationResult>? FinalCleanupTask { get; set; }
        public RetainerAutomationResult? FinalCleanupResult { get; set; }
        public bool CommandMenuObservedAfterReplay { get; set; }
        public int ObservedBootstrapStepCount { get; set; }
        public bool SelectionObserved { get; set; }
        public bool Scene1SelectionObserved { get; set; }
        public bool CancelRequested { get; set; }
        public bool ManualReplayRequested { get; set; }
        public bool DistanceTargetReached { get; set; }
        public bool NavigationOwned { get; set; }
        public bool ReturnedToHeldPosition { get; set; }
        public DateTimeOffset? TeardownSuppressedAtUtc { get; set; }
        public DateTimeOffset? NavigationStartedAtUtc { get; set; }
        public DateTimeOffset? ReturnNavigationStartedAtUtc { get; set; }
        public DateTimeOffset? ReplayNotBeforeUtc { get; set; }
        public DateTimeOffset? ReplayRequestedAtUtc { get; set; }
        public DateTimeOffset? ReplayStartedAtUtc { get; set; }
        public DateTimeOffset? Scene2ObservedAtUtc { get; set; }
        public DateTimeOffset? CleanupStartedAtUtc { get; set; }
        public ProbePosition? HeldPosition { get; set; }
        public ProbePosition? NavigationTarget { get; set; }
        public ProbePosition? ReplayPosition { get; set; }
        public string? NavigationMessage { get; set; }
        public string? NavigationStopMessage { get; set; }
        public string? NavigationFailure { get; set; }
        public NormalBellClientState? LastState { get; set; }
        public List<NormalBellClientStateSample> StateSamples { get; } = [];
    }

    private sealed record WarmSessionRetentionProbeEvidence(
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        uint TerritoryId,
        ProbePosition? StartPosition,
        ProbePosition? ConclusionPosition,
        string CharacterName,
        string? BellGameObjectId,
        uint BellEventId,
        string BellEventIdSource,
        float Distance,
        float OrdinaryInteractionDistance,
        string BootstrapMode,
        WarmSessionBootstrapStep[] BootstrapSteps,
        WarmSessionBootstrapStep[] FinalCleanupSteps,
        string ReplayMode,
        double? RequestedHoldSeconds,
        float? RequestedMovementDistance,
        ProbePosition? NavigationTarget,
        DateTimeOffset? NavigationStartedAtUtc,
        string? NavigationMessage,
        string? NavigationStopMessage,
        string? NavigationFailure,
        bool DistanceTargetReached,
        bool ReturnedToHeldPosition,
        DateTimeOffset? TeardownSuppressedAtUtc,
        DateTimeOffset? ReplayRequestedAtUtc,
        DateTimeOffset? ReplayStartedAtUtc,
        ProbePosition? HeldPosition,
        ProbePosition? ReplayPosition,
        double? ActualHoldMilliseconds,
        float DistanceMovedBeforeReplay,
        string Verdict,
        string Message,
        bool CommandMenuObserved,
        NormalBellClientStateSample[] StateTransitions,
        WarmSessionRetentionProbeObservation Transport);

    private static float DistanceBetween(ProbePosition? left, ProbePosition? right)
    {
        if (left is null || right is null)
            return 0;

        var x = right.X - left.X;
        var y = right.Y - left.Y;
        var z = right.Z - left.Z;
        return MathF.Sqrt((x * x) + (y * y) + (z * z));
    }
}
