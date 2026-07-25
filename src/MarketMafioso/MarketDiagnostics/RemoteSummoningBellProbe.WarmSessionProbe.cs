using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Franthropy.Dalamud.Automation.Retainers;

namespace MarketMafioso.MarketDiagnostics;

internal sealed record WarmSessionRetentionProbeView(
    bool Active,
    bool CanArm,
    string State,
    string Message,
    string Readiness,
    string? BellGameObjectId,
    string? RetainerId,
    string? Opcode,
    string? LastEvidencePath);

internal sealed partial class RemoteSummoningBellProbe
{
    private static readonly TimeSpan WarmSessionWorkflowWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WarmSessionReplayDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WarmSessionReplayWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WarmSessionSuccessSettleWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WarmSessionCleanupWindow = TimeSpan.FromSeconds(5);
    private const int MaximumWarmSessionStateSamples = 256;

    private WarmSessionProbeSession? warmSessionProbeSession;
    private WarmSessionRetentionProbeView warmSessionProbeView = new(
        false,
        false,
        "Idle",
        "Warm-session retention has not been tested.",
        "Stand inside ordinary range of a loaded summoning bell with every retainer window closed.",
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

    public string BeginWarmSessionRetentionProbe()
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
            arm);
        CaptureWarmSessionStateTransition(active);
        warmSessionProbeSession = active;
        warmSessionProbeView = new(
            true,
            false,
            "Armed",
            "Waiting to learn a real scene-1 retainer selection.",
            "Interact normally: select a retainer, choose Quit, select a retainer again from the reopened list, choose Quit again, then close the reopened retainer list. MMF will suppress that one close and replay the exact second selection automatically.",
            FormatGameObjectId(arm.BellGameObjectId),
            null,
            null,
            warmSessionProbeView.LastEvidencePath);
        log.Information(
            "[MarketMafioso] Armed warm-session retention for bell {BellGameObjectId:X}, event 0x{EventId:X}, territory {TerritoryId}.",
            arm.BellGameObjectId,
            arm.BellEventId,
            active.TerritoryId);
        return $"Warm-session retention armed. {warmSessionProbeView.Readiness} {suppressionMessage}";
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
        if (transport.SelectionCaptured && !active.SelectionObserved)
        {
            active.SelectionObserved = true;
            warmSessionProbeView = warmSessionProbeView with
            {
                State = transport.SelectionSceneId == 1 ? "Reusable selection learned" : "Initial selection learned",
                Message = transport.Message,
                Readiness = transport.SelectionSceneId == 1
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
                Readiness = "Choose Quit, then close the reopened retainer list. MMF will hold that teardown and replay once.",
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
            active.TeardownSuppressedAtUtc = now;
            warmSessionProbeView = warmSessionProbeView with
            {
                State = "Session held",
                Message = transport.Message,
                Readiness = "Waiting for the stock windows to finish closing before the single replay.",
            };
        }

        if (transport.TeardownSuppressed &&
            !transport.ReplaySent &&
            active.CleanupStartedAtUtc is null &&
            active.TeardownSuppressedAtUtc is { } heldAt &&
            now - heldAt >= WarmSessionReplayDelay &&
            !IsAnyRetainerAddonOpen())
        {
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
                    active.CancelRequested ? "CancelledAndCleanedUp" : "NoMatchingScene2",
                    active.CancelRequested
                        ? "The warm-session probe was cancelled and the held stock teardown was acknowledged."
                        : "The retained-session selection produced no matching scene 2; the held stock teardown was released and acknowledged.",
                    false);
                return;
            }

            if (now >= active.DeadlineUtc)
            {
                CompleteWarmSessionProbe(
                    active,
                    active.CancelRequested ? "CancelledCleanupUnconfirmed" : "CleanupUnconfirmed",
                    "The held stock teardown was released, but its acknowledgement was not confirmed inside the cleanup window.",
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

    private void BeginWarmSessionCleanup(WarmSessionProbeSession active, string reason)
    {
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
            verdict,
            message,
            commandMenuObserved,
            active.StateSamples.ToArray(),
            transport);
        var path = WriteWarmSessionEvidence(evidence);

        if (IsAnyRetainerSessionUiOpen())
            releaseSuppressionWhenRetainerListCloses = autoRetainerSuppression is { Changed: true };
        else
            ReleaseAutoRetainerSuppression();

        warmSessionProbeView = new(
            false,
            false,
            verdict,
            releaseSuppressionWhenRetainerListCloses
                ? $"{message} AutoRetainer remains suppressed until the retainer session closes."
                : message,
            verdict == "Confirmed"
                ? "Choose Quit and close the returned retainer list normally; this probe will not intercept anything else."
                : message,
            FormatGameObjectId(active.Arm.BellGameObjectId),
            transport.RetainerId == 0 ? null : $"0x{transport.RetainerId:X16}",
            transport.Opcode == 0 ? null : $"0x{transport.Opcode:X}",
            path);

        if (verdict is "Confirmed" or "SessionRetainedWithoutCommandMenu")
            chatGui.Print($"[MMF] Warm-session retention: {message}");
        else
            chatGui.PrintError($"[MMF] Warm-session retention: {message}");
        log.Information(
            "[MarketMafioso] Warm-session retention concluded {Verdict}. Evidence: {EvidencePath}",
            verdict,
            path ?? "(write failed)");
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
            WarmSessionRetentionArmResult arm)
        {
            StartedAtUtc = startedAtUtc;
            DeadlineUtc = deadlineUtc;
            TerritoryId = territoryId;
            StartPosition = startPosition;
            CharacterName = characterName;
            Arm = arm;
        }

        public DateTimeOffset StartedAtUtc { get; }
        public DateTimeOffset DeadlineUtc { get; set; }
        public uint TerritoryId { get; }
        public ProbePosition? StartPosition { get; }
        public string CharacterName { get; }
        public WarmSessionRetentionArmResult Arm { get; }
        public bool SelectionObserved { get; set; }
        public bool Scene1SelectionObserved { get; set; }
        public bool CancelRequested { get; set; }
        public DateTimeOffset? TeardownSuppressedAtUtc { get; set; }
        public DateTimeOffset? ReplayStartedAtUtc { get; set; }
        public DateTimeOffset? Scene2ObservedAtUtc { get; set; }
        public DateTimeOffset? CleanupStartedAtUtc { get; set; }
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
        string Verdict,
        string Message,
        bool CommandMenuObserved,
        NormalBellClientStateSample[] StateTransitions,
        WarmSessionRetentionProbeObservation Transport);
}
