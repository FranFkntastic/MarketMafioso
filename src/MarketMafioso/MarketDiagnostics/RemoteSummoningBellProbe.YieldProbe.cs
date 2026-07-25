using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Franthropy.Dalamud.Automation.Retainers;

namespace MarketMafioso.MarketDiagnostics;

internal sealed record YieldEventSceneProbeView(
    bool Active,
    bool CanArmControl,
    bool CanReplaySessionFree,
    string Mode,
    string State,
    string Message,
    string Readiness,
    string? BellGameObjectId,
    string? RetainerId,
    string? Opcode,
    string? LastEvidencePath);

internal sealed partial class RemoteSummoningBellProbe
{
    private static readonly TimeSpan YieldControlWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan YieldDirectObservationWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan YieldSettleWindow = TimeSpan.FromSeconds(2);
    private const int MaximumYieldStateSamples = 128;

    private YieldProbeSession? yieldProbeSession;
    private YieldTemplateContext? yieldTemplateContext;
    private YieldEventSceneProbeView yieldProbeView = new(
        false,
        false,
        false,
        "None",
        "Idle",
        "No current-build YieldEventScene2 control has been confirmed.",
        "Run the in-session control beside a loaded bell first.",
        null,
        null,
        null,
        null);

    public YieldEventSceneProbeView GetYieldProbeView()
    {
        if (yieldProbeView.Active)
            return yieldProbeView;

        ValidateYieldTemplateContext();
        var observation = bell.ObserveLoadedBell();
        var anyRetainerUiOpen = IsAnyRetainerSessionUiOpen();
        var noOtherProbe = session is null && normalCaptureSession is null;
        var canArmControl =
            configuration.EnableMarketDiagnostics &&
            clientState.IsLoggedIn &&
            noOtherProbe &&
            !anyRetainerUiOpen &&
            observation.Available &&
            !observation.OutsideOrdinaryInteractionRange;
        var canReplay =
            configuration.EnableMarketDiagnostics &&
            clientState.IsLoggedIn &&
            noOtherProbe &&
            !anyRetainerUiOpen &&
            yieldTemplateContext is not null;

        var readiness = anyRetainerUiOpen
            ? "Close the current bell/retainer session before running either probe."
            : yieldTemplateContext is null
                ? observation.Available && observation.OutsideOrdinaryInteractionRange
                    ? $"Move inside ordinary bell range to run the control ({observation.Distance:F1}/{observation.OrdinaryInteractionDistance:F1} yalms)."
                    : observation.Available
                        ? "Arm the control, interact normally, and select any retainer."
                        : observation.Message
                : "The exact confirmed control packet is cached. Close every retainer window, move to the desired same-territory test position, then run the session-free replay.";

        return yieldProbeView with
        {
            CanArmControl = canArmControl,
            CanReplaySessionFree = canReplay,
            Readiness = readiness,
            BellGameObjectId =
                yieldTemplateContext?.BellGameObjectId ??
                FormatGameObjectId(observation.BellGameObjectId),
            RetainerId = yieldTemplateContext is null
                ? yieldProbeView.RetainerId
                : $"0x{yieldTemplateContext.RetainerId:X16}",
            Opcode = yieldTemplateContext is null
                ? yieldProbeView.Opcode
                : $"0x{yieldTemplateContext.Opcode:X}",
        };
    }

    public string BeginYieldControl()
    {
        var precondition = ValidateYieldProbeStart(requireTemplate: false);
        if (precondition is not null)
            return precondition;

        if (!AutoRetainerSuppressionLease.TryAcquire(autoRetainer, out var suppression, out var suppressionMessage))
            return suppressionMessage;
        autoRetainerSuppression = suppression;

        var arm = bell.TryArmYieldEventSceneControl();
        if (!arm.Armed)
        {
            ReleaseAutoRetainerSuppression();
            yieldProbeView = yieldProbeView with
            {
                State = "Not armed",
                Message = arm.Message,
                Readiness = arm.Message,
                BellGameObjectId = FormatGameObjectId(arm.BellGameObjectId),
            };
            return arm.Message;
        }

        yieldTemplateContext = null;
        var now = DateTimeOffset.UtcNow;
        var active = new YieldProbeSession(
            YieldEventSceneProbeMode.InSessionControl,
            now,
            now + YieldControlWindow,
            clientState.TerritoryType,
            CapturePosition(),
            objectTable.LocalPlayer?.Name.TextValue ?? string.Empty,
            arm.BellGameObjectId,
            arm.BellEventId,
            arm.BellEventIdSource,
            arm.Distance,
            arm.OrdinaryInteractionDistance);
        CaptureYieldStateTransition(active);
        yieldProbeSession = active;
        yieldProbeView = new(
            true,
            false,
            false,
            "In-session control",
            "Armed",
            "Waiting for one stock retainer-selection yield packet.",
            $"Interact with the bell normally and select any retainer. The stock yield will be replaced once with an exact clone. {suppressionMessage}",
            FormatGameObjectId(arm.BellGameObjectId),
            null,
            null,
            yieldProbeView.LastEvidencePath);
        log.Information(
            "[MarketMafioso] Armed YieldEventScene2 control for bell {BellGameObjectId:X}, event 0x{EventId:X}, territory {TerritoryId}.",
            arm.BellGameObjectId,
            arm.BellEventId,
            active.TerritoryId);
        return "YieldEventScene2 control armed. Interact normally and select any retainer.";
    }

    public string BeginYieldSessionFreeReplay()
    {
        var precondition = ValidateYieldProbeStart(requireTemplate: true);
        if (precondition is not null)
            return precondition;
        var template = yieldTemplateContext!;

        if (!AutoRetainerSuppressionLease.TryAcquire(autoRetainer, out var suppression, out var suppressionMessage))
            return suppressionMessage;
        autoRetainerSuppression = suppression;

        var now = DateTimeOffset.UtcNow;
        var active = new YieldProbeSession(
            YieldEventSceneProbeMode.SessionFreeReplay,
            now,
            now + YieldDirectObservationWindow,
            clientState.TerritoryType,
            CapturePosition(),
            objectTable.LocalPlayer?.Name.TextValue ?? string.Empty,
            template.BellGameObjectIdValue,
            template.BellEventId,
            template.BellEventIdSource,
            null,
            null);
        CaptureYieldStateTransition(active);
        var submission = bell.ReplayCapturedYieldEventScene();
        if (!submission.Sent)
        {
            yieldProbeSession = active;
            CompleteYieldProbe(
                active,
                "NotSubmitted",
                submission.Message,
                false);
            return submission.Message;
        }

        yieldProbeSession = active;
        yieldProbeView = new(
            true,
            false,
            false,
            "Session-free replay",
            "Observing",
            submission.Message,
            $"One packet was sent. No retry or inventory command will follow. {suppressionMessage}",
            template.BellGameObjectId,
            $"0x{submission.RetainerId:X16}",
            $"0x{submission.Opcode:X}",
            yieldProbeView.LastEvidencePath);
        log.Information(
            "[MarketMafioso] Submitted one session-free YieldEventScene2 opcode 0x{Opcode:X}, event 0x{EventId:X}, retainer 0x{RetainerId:X16}, territory {TerritoryId}.",
            submission.Opcode,
            submission.EventId,
            submission.RetainerId,
            active.TerritoryId);
        return submission.Message;
    }

    public string GetYieldProbeStatus()
    {
        var current = GetYieldProbeView();
        var evidence = current.LastEvidencePath is null
            ? string.Empty
            : $" Evidence: {current.LastEvidencePath}";
        return $"{current.Mode}/{current.State}: {current.Message}{evidence}";
    }

    public string CancelYieldProbe()
    {
        if (yieldProbeSession is not { } active)
            return "The YieldEventScene2 probe is not active.";

        CompleteYieldProbe(
            active,
            "Cancelled",
            "The YieldEventScene2 probe was cancelled by command.",
            IsAddonReady("SelectString"));
        return "YieldEventScene2 probe cancelled; bounded evidence was written.";
    }

    private void UpdateYieldProbe()
    {
        if (yieldProbeSession is not { } active)
            return;

        CaptureYieldStateTransition(active);
        if (clientState.TerritoryType != active.TerritoryId ||
            !string.Equals(
                objectTable.LocalPlayer?.Name.TextValue,
                active.CharacterName,
                StringComparison.Ordinal))
        {
            CompleteYieldProbe(
                active,
                "IdentityOrTerritoryChanged",
                "Character or territory changed before the YieldEventScene2 probe concluded.",
                false);
            DiscardYieldTemplate("YieldEventScene2 template discarded after character or territory change.");
            return;
        }

        var transport = bell.ObserveYieldEventSceneProbe();
        if (transport.State == YieldEventSceneProbeState.Failed)
        {
            CompleteYieldProbe(active, "TransportFailed", transport.Message, false);
            return;
        }

        if (transport.Sent && !active.OutboundObserved)
        {
            active.OutboundObserved = true;
            yieldProbeView = yieldProbeView with
            {
                State = "Observing",
                Message =
                    $"Sent opcode 0x{transport.Opcode:X} for retainer 0x{transport.RetainerId:X16}; waiting for matching scene 2.",
                RetainerId = $"0x{transport.RetainerId:X16}",
                Opcode = $"0x{transport.Opcode:X}",
            };
        }

        var commandMenuReady = IsAddonReady("SelectString");
        if (transport.MatchingEventPlayObserved && commandMenuReady)
        {
            active.SuccessObservedAtUtc ??= DateTimeOffset.UtcNow;
            yieldProbeView = yieldProbeView with
            {
                State = "Settling",
                Message = "Matching scene-2 EventPlay and retainer command menu observed.",
                Readiness = "Hold here; the recorder will stop automatically.",
            };
            if (DateTimeOffset.UtcNow - active.SuccessObservedAtUtc.Value >= YieldSettleWindow)
            {
                CompleteYieldProbe(
                    active,
                    "Confirmed",
                    active.Mode == YieldEventSceneProbeMode.InSessionControl
                        ? "The exact cloned YieldEventScene2 completed inside the accepted bell session."
                        : "The session-free YieldEventScene2 received scene 2 and opened the retainer command menu.",
                    true);
                return;
            }
        }

        if (DateTimeOffset.UtcNow < active.DeadlineUtc)
            return;

        if (!transport.Sent)
        {
            CompleteYieldProbe(
                active,
                "ControlPacketNotCaptured",
                "No matching stock YieldEventScene2 was captured before the control window expired.",
                commandMenuReady);
            return;
        }

        var verdict = transport.MatchingEventPlayObserved
            ? commandMenuReady
                ? "Confirmed"
                : "MatchingEventPlayWithoutCommandMenu"
            : commandMenuReady
                ? "CommandMenuWithoutMatchingEventPlay"
                : "NoMatchingEventPlay";
        var message = transport.MatchingEventPlayObserved
            ? "The server returned matching scene 2, but the retainer command menu did not appear."
            : commandMenuReady
                ? "The retainer command menu appeared without the expected matching scene-2 hook observation."
                : active.Mode == YieldEventSceneProbeMode.SessionFreeReplay
                    ? "The one session-free YieldEventScene2 produced no matching scene-2 EventPlay or command menu."
                    : "The cloned in-session YieldEventScene2 produced no matching scene-2 EventPlay or command menu.";
        CompleteYieldProbe(active, verdict, message, commandMenuReady);
    }

    private void CompleteYieldProbe(
        YieldProbeSession active,
        string verdict,
        string message,
        bool commandMenuObserved)
    {
        CaptureYieldStateTransition(active);
        var transport = bell.ObserveYieldEventSceneProbe();
        bell.CancelYieldEventSceneProbe("The YieldEventScene2 probe concluded.");
        CaptureYieldStateTransition(active);
        yieldProbeSession = null;

        var evidence = new YieldEventSceneProbeEvidence(
            active.StartedAtUtc,
            DateTimeOffset.UtcNow,
            active.TerritoryId,
            active.StartPosition,
            CapturePosition(),
            active.CharacterName,
            active.Mode.ToString(),
            FormatGameObjectId(active.BellGameObjectId),
            active.BellEventId,
            active.BellEventIdSource,
            active.Distance,
            active.OrdinaryInteractionDistance,
            verdict,
            message,
            commandMenuObserved,
            active.StateSamples.ToArray(),
            transport);
        var path = WriteYieldProbeEvidence(evidence);

        if (active.Mode == YieldEventSceneProbeMode.InSessionControl &&
            verdict == "Confirmed" &&
            transport.CachedTemplateAvailable)
        {
            yieldTemplateContext = new(
                active.TerritoryId,
                active.CharacterName,
                active.BellGameObjectId,
                FormatGameObjectId(active.BellGameObjectId)!,
                active.BellEventId,
                active.BellEventIdSource,
                transport.RetainerId,
                transport.Opcode,
                path);
        }

        if (IsAnyRetainerSessionUiOpen() || condition[ConditionFlag.OccupiedSummoningBell])
            releaseSuppressionWhenRetainerListCloses = autoRetainerSuppression is { Changed: true };
        else
            ReleaseAutoRetainerSuppression();

        yieldProbeView = new(
            false,
            false,
            false,
            active.Mode == YieldEventSceneProbeMode.InSessionControl
                ? "In-session control"
                : "Session-free replay",
            verdict,
            releaseSuppressionWhenRetainerListCloses
                ? $"{message} AutoRetainer remains suppressed until the retainer session closes."
                : message,
            yieldTemplateContext is null
                ? message
                : "The confirmed current-build control packet remains cached for one-shot same-territory replay.",
            FormatGameObjectId(active.BellGameObjectId),
            transport.RetainerId == 0 ? null : $"0x{transport.RetainerId:X16}",
            transport.Opcode == 0 ? null : $"0x{transport.Opcode:X}",
            path);

        if (verdict == "Confirmed")
            chatGui.Print($"[MMF] YieldEventScene2 probe: {message}");
        else
            chatGui.PrintError($"[MMF] YieldEventScene2 probe: {message}");
        log.Information(
            "[MarketMafioso] YieldEventScene2 probe concluded {Verdict}. Evidence: {EvidencePath}",
            verdict,
            path ?? "(write failed)");
    }

    private string? ValidateYieldProbeStart(bool requireTemplate)
    {
        if (disposed)
            return "The YieldEventScene2 probe is unavailable because it has been disposed.";
        if (!configuration.EnableMarketDiagnostics)
            return "Enable Market Diagnostics in settings before running the YieldEventScene2 probe.";
        if (!clientState.IsLoggedIn)
            return "The YieldEventScene2 probe requires a logged-in character.";
        if (session is not null)
            return "The remote bell interaction probe is already active.";
        if (normalCaptureSession is not null)
            return "The normal bell flight recorder is already armed.";
        if (yieldProbeSession is not null)
            return "A YieldEventScene2 probe is already active.";
        if (IsAnyRetainerSessionUiOpen())
            return "Close every bell and retainer window before starting this probe.";
        if (releaseSuppressionWhenRetainerListCloses)
            ReleaseAutoRetainerSuppression();

        ValidateYieldTemplateContext();
        if (requireTemplate && yieldTemplateContext is null)
            return "Run and confirm the in-session YieldEventScene2 control first.";
        return null;
    }

    private void ValidateYieldTemplateContext()
    {
        if (yieldTemplateContext is not { } template)
            return;

        var currentCharacter = objectTable.LocalPlayer?.Name.TextValue ?? string.Empty;
        if (clientState.TerritoryType == template.TerritoryId &&
            string.Equals(currentCharacter, template.CharacterName, StringComparison.Ordinal))
        {
            return;
        }

        DiscardYieldTemplate("YieldEventScene2 template discarded after character or territory change.");
    }

    private void DiscardYieldTemplate(string reason)
    {
        yieldTemplateContext = null;
        bell.DiscardYieldEventSceneTemplate(reason);
        yieldProbeView = yieldProbeView with
        {
            CanReplaySessionFree = false,
            Message = reason,
            Readiness = "Run the in-session control again before another replay.",
        };
    }

    private void CaptureYieldStateTransition(YieldProbeSession active)
    {
        if (active.StateSamples.Count >= MaximumYieldStateSamples)
            return;

        var state = CaptureNormalState(active.BellEventId);
        if (state == active.LastState)
            return;

        active.LastState = state;
        active.StateSamples.Add(new(DateTimeOffset.UtcNow, state));
    }

    private string? WriteYieldProbeEvidence(YieldEventSceneProbeEvidence evidence)
    {
        try
        {
            Directory.CreateDirectory(evidenceDirectory);
            var mode = evidence.Mode == nameof(YieldEventSceneProbeMode.InSessionControl)
                ? "yield-control"
                : "yield-session-free";
            var path = Path.Combine(
                evidenceDirectory,
                $"{mode}-{evidence.StartedAtUtc:yyyyMMdd-HHmmss-fff}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(evidence, JsonOptions));
            return path;
        }
        catch (Exception ex)
        {
            log.Error(ex, "[MarketMafioso] Unable to write YieldEventScene2 probe evidence.");
            return null;
        }
    }

    private sealed class YieldProbeSession
    {
        public YieldProbeSession(
            YieldEventSceneProbeMode mode,
            DateTimeOffset startedAtUtc,
            DateTimeOffset deadlineUtc,
            uint territoryId,
            ProbePosition? startPosition,
            string characterName,
            ulong bellGameObjectId,
            uint bellEventId,
            string bellEventIdSource,
            float? distance,
            float? ordinaryInteractionDistance)
        {
            Mode = mode;
            StartedAtUtc = startedAtUtc;
            DeadlineUtc = deadlineUtc;
            TerritoryId = territoryId;
            StartPosition = startPosition;
            CharacterName = characterName;
            BellGameObjectId = bellGameObjectId;
            BellEventId = bellEventId;
            BellEventIdSource = bellEventIdSource;
            Distance = distance;
            OrdinaryInteractionDistance = ordinaryInteractionDistance;
        }

        public YieldEventSceneProbeMode Mode { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public DateTimeOffset DeadlineUtc { get; }
        public uint TerritoryId { get; }
        public ProbePosition? StartPosition { get; }
        public string CharacterName { get; }
        public ulong BellGameObjectId { get; }
        public uint BellEventId { get; }
        public string BellEventIdSource { get; }
        public float? Distance { get; }
        public float? OrdinaryInteractionDistance { get; }
        public bool OutboundObserved { get; set; }
        public DateTimeOffset? SuccessObservedAtUtc { get; set; }
        public NormalBellClientState? LastState { get; set; }
        public List<NormalBellClientStateSample> StateSamples { get; } = [];
    }

    private sealed record YieldTemplateContext(
        uint TerritoryId,
        string CharacterName,
        ulong BellGameObjectIdValue,
        string BellGameObjectId,
        uint BellEventId,
        string BellEventIdSource,
        ulong RetainerId,
        uint Opcode,
        string? ControlEvidencePath);

    private sealed record YieldEventSceneProbeEvidence(
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        uint TerritoryId,
        ProbePosition? StartPosition,
        ProbePosition? ConclusionPosition,
        string CharacterName,
        string Mode,
        string? BellGameObjectId,
        uint BellEventId,
        string BellEventIdSource,
        float? Distance,
        float? OrdinaryInteractionDistance,
        string Verdict,
        string Message,
        bool CommandMenuObserved,
        NormalBellClientStateSample[] StateTransitions,
        YieldEventSceneProbeObservation Transport);
}
