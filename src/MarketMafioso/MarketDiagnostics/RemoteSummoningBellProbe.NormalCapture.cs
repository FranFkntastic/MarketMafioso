using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Retainers;

namespace MarketMafioso.MarketDiagnostics;

internal sealed record NormalSummoningBellCaptureView(
    bool Active,
    bool CanArm,
    string State,
    string Message,
    string Readiness,
    string? BellGameObjectId,
    float? Distance,
    float? OrdinaryInteractionDistance,
    string? LastEvidencePath);

internal sealed partial class RemoteSummoningBellProbe
{
    private static readonly TimeSpan NormalCaptureArmWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NormalLifecycleCaptureWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan NormalCaptureSettleWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NormalLifecycleClosureSettleWindow = TimeSpan.FromSeconds(3);
    private const int MaximumNormalStateSamples = 128;

    private NormalCaptureSession? normalCaptureSession;
    private NormalSummoningBellCaptureView normalCaptureView = new(
        false,
        false,
        "Idle",
        "The passive recorder has not been armed.",
        "Stand inside ordinary range of a loaded summoning bell with all retainer windows closed.",
        null,
        null,
        null,
        null);

    public NormalSummoningBellCaptureView GetNormalCaptureView()
    {
        if (normalCaptureView.Active)
            return normalCaptureView;

        var observation = bell.ObserveLoadedBell();
        var anyRetainerUiOpen = IsAnyRetainerSessionUiOpen();
        return normalCaptureView with
        {
            CanArm =
                configuration.EnableMarketDiagnostics &&
                clientState.IsLoggedIn &&
                observation.Available &&
                !observation.OutsideOrdinaryInteractionRange &&
                session is null &&
                yieldProbeSession is null &&
                warmSessionProbeSession is null &&
                !anyRetainerUiOpen,
            Readiness = anyRetainerUiOpen
                ? "Close the current retainer interaction before arming the recorder."
                : observation.Available && observation.OutsideOrdinaryInteractionRange
                    ? $"Move inside ordinary interaction range ({observation.Distance:F1}/{observation.OrdinaryInteractionDistance:F1} yalms)."
                    : observation.Message,
            BellGameObjectId = FormatGameObjectId(observation.BellGameObjectId),
            Distance = observation.Available ? observation.Distance : null,
            OrdinaryInteractionDistance = observation.Available ? observation.OrdinaryInteractionDistance : null,
        };
    }

    public string BeginNormalCapture() => BeginNormalCapture(captureCompleteLifecycle: false);

    public string BeginNormalLifecycleCapture() => BeginNormalCapture(captureCompleteLifecycle: true);

    private string BeginNormalCapture(bool captureCompleteLifecycle)
    {
        if (disposed)
            return "The normal bell recorder is unavailable because it has been disposed.";
        if (!configuration.EnableMarketDiagnostics)
            return "Enable Market Diagnostics in settings before arming the normal bell recorder.";
        if (!clientState.IsLoggedIn)
            return "The normal bell recorder requires a logged-in character.";
        if (session is not null)
            return "The remote bell probe is already active.";
        if (yieldProbeSession is not null)
            return "The YieldEventScene2 probe is already active.";
        if (warmSessionProbeSession is not null)
            return "The warm-session retention probe is already active.";
        if (normalCaptureSession is not null)
            return "The normal bell flight recorder is already armed.";
        if (IsAnyRetainerSessionUiOpen())
            return "Close the current retainer interaction before arming the normal bell flight recorder.";
        if (releaseSuppressionWhenRetainerListCloses)
            ReleaseAutoRetainerSuppression();

        if (!AutoRetainerSuppressionLease.TryAcquire(autoRetainer, out var suppression, out var suppressionMessage))
            return suppressionMessage;
        autoRetainerSuppression = suppression;

        var arm = bell.TryArmLoadedBellFlightRecorder(captureCompleteLifecycle);
        if (!arm.Armed)
        {
            ReleaseAutoRetainerSuppression();
            normalCaptureView = normalCaptureView with
            {
                State = "Not armed",
                Message = arm.Message,
                Readiness = arm.Message,
                BellGameObjectId = FormatGameObjectId(arm.BellGameObjectId),
                Distance = arm.BellGameObjectId == 0 ? null : arm.Distance,
                OrdinaryInteractionDistance = arm.BellGameObjectId == 0 ? null : arm.OrdinaryInteractionDistance,
            };
            return arm.Message;
        }

        var now = DateTimeOffset.UtcNow;
        var active = new NormalCaptureSession(
            now,
            now + (captureCompleteLifecycle
                ? NormalLifecycleCaptureWindow
                : NormalCaptureArmWindow),
            clientState.TerritoryType,
            CapturePosition(),
            arm,
            captureCompleteLifecycle);
        CaptureNormalStateTransition(active);
        normalCaptureSession = active;
        normalCaptureView = new(
            true,
            false,
            captureCompleteLifecycle ? "Lifecycle armed" : "Armed",
            "Listening for one ordinary bell interaction. No packet or game state will be altered.",
            captureCompleteLifecycle
                ? $"Interact normally, select one retainer, choose Quit at its command menu, then close the returned RetainerList. {suppressionMessage}"
                : $"Interact with this bell normally, select one retainer, and stop when the retainer command menu appears. {suppressionMessage}",
            FormatGameObjectId(arm.BellGameObjectId),
            arm.Distance,
            arm.OrdinaryInteractionDistance,
            normalCaptureView.LastEvidencePath);
        log.Information(
            "[MarketMafioso] Armed {CaptureMode} bell recorder for bell {BellGameObjectId:X}, event 0x{EventId:X}, territory {TerritoryId}.",
            captureCompleteLifecycle ? "complete-lifecycle" : "normal",
            arm.BellGameObjectId,
            arm.BellEventId,
            active.TerritoryId);
        return captureCompleteLifecycle
            ? "Complete bell-lifecycle recorder armed. Select one retainer, choose Quit, return to the retainer list, then close it."
            : "Normal bell flight recorder armed. Interact with the bell normally, then select one retainer.";
    }

    public string GetNormalCaptureStatus()
    {
        var current = GetNormalCaptureView();
        var evidence = current.LastEvidencePath is null ? string.Empty : $" Evidence: {current.LastEvidencePath}";
        return $"{current.State}: {current.Message}{evidence}";
    }

    public string CancelNormalCapture()
    {
        if (normalCaptureSession is not { } active)
            return "The normal bell flight recorder is not active.";

        CompleteNormalCapture(
            active,
            "Cancelled",
            "The normal bell flight recorder was cancelled by command.",
            IsAddonReady("SelectString"));
        return "Normal bell flight recorder cancelled; any bounded evidence observed so far was written.";
    }

    private void UpdateNormalCapture()
    {
        if (normalCaptureSession is not { } active)
            return;

        CaptureNormalStateTransition(active);
        var now = DateTimeOffset.UtcNow;
        if (clientState.TerritoryType != active.TerritoryId)
        {
            CompleteNormalCapture(active, "TerritoryChanged", "Territory changed before the normal bell capture completed.", false);
            return;
        }

        var transport = bell.ObserveTalkPacketTransport();
        if (transport.State == TalkEventPacketTransportState.Failed)
        {
            CompleteNormalCapture(active, "TransportFailed", transport.Message, false);
            return;
        }

        if (!transport.Sent)
        {
            if (now >= active.DeadlineUtc)
            {
                CompleteNormalCapture(
                    active,
                    "StartTalkTimeout",
                    active.CaptureCompleteLifecycle
                        ? "No matching normal StartTalkEvent appeared within three minutes."
                        : "No matching normal StartTalkEvent appeared within 60 seconds.",
                    false);
                return;
            }

            normalCaptureView = normalCaptureView with
            {
                State = "Armed",
                Message = "Waiting for the ordinary bell interaction.",
            };
            return;
        }

        if (!active.StartTalkObserved)
        {
            active.StartTalkObserved = true;
            normalCaptureView = normalCaptureView with
            {
                State = "Recording",
                Message = $"Captured stock StartTalkEvent opcode 0x{transport.Opcode:X}; recording all bounded zone traffic and client state transitions.",
                Readiness = active.CaptureCompleteLifecycle
                    ? "Select one retainer, choose Quit at its command menu, then close the returned RetainerList."
                    : "Select one retainer from the normal RetainerList and wait for its command menu.",
            };
        }

        var retainerListReady = IsAddonReady("RetainerList");
        var commandMenuReady = IsAddonReady("SelectString");
        if (retainerListReady && active.CommandMenuObservedAtUtc is null)
        {
            active.InitialRetainerListObservedAtUtc ??= now;
        }

        if (commandMenuReady)
        {
            active.CommandMenuObservedAtUtc ??= now;
            if (active.CaptureCompleteLifecycle)
            {
                normalCaptureView = normalCaptureView with
                {
                    State = "Command menu captured",
                    Message = "The retainer command menu appeared; the recorder is preserving the full exit path.",
                    Readiness = "Choose Quit, wait for RetainerList to return, then close RetainerList normally.",
                };
            }
            else
            {
                normalCaptureView = normalCaptureView with
                {
                    State = "Settling",
                    Message = "The retainer command menu appeared. Capturing the final response tail.",
                    Readiness = "Hold here; the recorder will stop automatically.",
                };
                if (now - active.CommandMenuObservedAtUtc.Value >= NormalCaptureSettleWindow)
                {
                    CompleteNormalCapture(
                        active,
                        "Confirmed",
                        "Captured an accepted normal bell session through one retainer selection and command-menu arrival.",
                        true);
                    return;
                }
            }
        }

        if (active.CaptureCompleteLifecycle &&
            active.CommandMenuObservedAtUtc is not null &&
            !commandMenuReady)
        {
            if (!commandMenuReady)
                active.CommandMenuClosedObserved = true;
            if (active.CommandMenuClosedObserved && retainerListReady)
                active.ReturnedRetainerListObservedAtUtc ??= now;

            if (active.ReturnedRetainerListObservedAtUtc is null)
            {
                normalCaptureView = normalCaptureView with
                {
                    State = "Waiting for return",
                    Message = "The command menu closed; waiting for the ordinary RetainerList to return.",
                    Readiness = "Choose Quit if needed. Once RetainerList returns, close it normally.",
                };
            }
            else if (IsAnyRetainerSessionUiOpen())
            {
                normalCaptureView = normalCaptureView with
                {
                    State = "Exit captured",
                    Message = "The command menu returned to RetainerList; waiting for final bell-session closure.",
                    Readiness = "Close RetainerList normally and leave all retainer windows closed.",
                };
            }
            else
            {
                active.SessionClosedObservedAtUtc ??= now;
                normalCaptureView = normalCaptureView with
                {
                    State = "Closure tail",
                    Message = "The full bell session closed; capturing the final server and client-state tail.",
                    Readiness = "Leave all retainer windows closed; the recorder will finish automatically.",
                };
                if (now - active.SessionClosedObservedAtUtc.Value >= NormalLifecycleClosureSettleWindow)
                {
                    CompleteNormalCapture(
                        active,
                        "ConfirmedLifecycle",
                        "Captured the accepted bell session from StartTalk through retainer selection, return, and final closure.",
                        true);
                    return;
                }
            }
        }

        if (now >= active.DeadlineUtc)
        {
            CompleteNormalCapture(
                active,
                "CaptureTimeout",
                active.CaptureCompleteLifecycle
                    ? "The complete bell lifecycle did not reach final closure before the three-minute capture expired."
                    : "The normal StartTalkEvent was captured, but no retainer command menu appeared before the bounded capture expired.",
                active.CommandMenuObservedAtUtc is not null);
        }
    }

    private void CompleteNormalCapture(
        NormalCaptureSession active,
        string verdict,
        string message,
        bool commandMenuObserved)
    {
        CaptureNormalStateTransition(active);
        bell.CancelTalkPacketTransport("The normal bell flight recorder concluded.");
        var transport = bell.ObserveTalkPacketTransport();
        CaptureNormalStateTransition(active);
        normalCaptureSession = null;

        var completedAtUtc = DateTimeOffset.UtcNow;
        var evidence = new NormalSummoningBellCaptureEvidence(
            active.StartedAtUtc,
            completedAtUtc,
            active.TerritoryId,
            active.StartPosition,
            CapturePosition(),
            objectTable.LocalPlayer?.Name.TextValue,
            FormatGameObjectId(active.Arm.BellGameObjectId),
            active.Arm.BellEventId,
            active.Arm.BellEventIdSource,
            active.Arm.Distance,
            active.Arm.OrdinaryInteractionDistance,
            active.CaptureCompleteLifecycle ? "CompleteLifecycle" : "SelectionOnly",
            verdict,
            message,
            commandMenuObserved,
            new NormalBellLifecycleMilestones(
                active.InitialRetainerListObservedAtUtc,
                active.CommandMenuObservedAtUtc,
                active.ReturnedRetainerListObservedAtUtc,
                active.SessionClosedObservedAtUtc),
            active.StateSamples.ToArray(),
            transport);
        var path = WriteNormalCaptureEvidence(evidence);

        if (IsAnyRetainerSessionUiOpen() || condition[ConditionFlag.OccupiedSummoningBell])
            releaseSuppressionWhenRetainerListCloses = autoRetainerSuppression is { Changed: true };
        else
            ReleaseAutoRetainerSuppression();

        normalCaptureView = new(
            false,
            false,
            verdict,
            releaseSuppressionWhenRetainerListCloses
                ? $"{message} AutoRetainer remains suppressed until the retainer session closes."
                : message,
            message,
            FormatGameObjectId(active.Arm.BellGameObjectId),
            active.Arm.Distance,
            active.Arm.OrdinaryInteractionDistance,
            path);

        if (verdict.StartsWith("Confirmed", StringComparison.Ordinal))
            chatGui.Print($"[MMF] Normal bell capture: {message}");
        else
            chatGui.PrintError($"[MMF] Normal bell capture: {message}");
        log.Information(
            "[MarketMafioso] Normal bell flight recorder concluded {Verdict}. Evidence: {EvidencePath}",
            verdict,
            path ?? "(write failed)");
    }

    private void CaptureNormalStateTransition(NormalCaptureSession active)
    {
        if (active.StateSamples.Count >= MaximumNormalStateSamples)
            return;

        var state = CaptureNormalState(active.Arm.BellEventId);
        if (state == active.LastState)
            return;

        active.LastState = state;
        active.StateSamples.Add(new(DateTimeOffset.UtcNow, state));
    }

    private unsafe NormalBellClientState CaptureNormalState(uint bellEventId)
    {
        var eventFramework = EventFramework.Instance();
        var handler = eventFramework == null ? null : eventFramework->GetEventHandlerById(bellEventId);
        var retainerManager = RetainerManager.Instance();
        var agentModule = AgentModule.Instance();
        var retainerListAgent = agentModule == null
            ? null
            : agentModule->GetAgentByInternalId(AgentId.RetainerList);
        var retainerAgent = agentModule == null
            ? null
            : agentModule->GetAgentByInternalId(AgentId.Retainer);
        return new(
            condition[ConditionFlag.OccupiedSummoningBell],
            IsAddonReady("RetainerList"),
            IsAddonReady("SelectString"),
            IsAddonReady("InventoryRetainer"),
            IsAddonReady("InventoryRetainerLarge"),
            IsAgentActive(agentModule, AgentId.RetainerList),
            IsAgentActive(agentModule, AgentId.Retainer),
            eventFramework == null ? 0 : eventFramework->EventState1.EventId.Id,
            eventFramework == null ? 0 : eventFramework->EventState1.ObjectId.Id,
            eventFramework == null ? 0 : eventFramework->EventState1.OccupiedConditionId,
            eventFramework == null ? (byte)0 : eventFramework->EventState1.Flags,
            eventFramework == null ? 0 : eventFramework->EventState2.EventId.Id,
            eventFramework == null ? 0 : eventFramework->EventState2.ObjectId.Id,
            eventFramework == null ? 0 : eventFramework->EventState2.OccupiedConditionId,
            eventFramework == null ? (byte)0 : eventFramework->EventState2.Flags,
            eventFramework == null ? 0 : eventFramework->SceneGameObjectId.Id,
            eventFramework == null ? (short)0 : eventFramework->Scene,
            eventFramework == null ? (ushort)0 : eventFramework->SceneFlags,
            eventFramework == null ? (byte)0 : eventFramework->SceneData.Count,
            FormatPointer(handler),
            handler == null ? 0 : handler->Info.EventId.Id,
            handler == null ? (short)0 : handler->Scene,
            handler == null ? 0ul : (ulong)handler->SceneFlags,
            handler == null || handler->SceneGameObject == null ? 0 : handler->SceneGameObject->EntityId,
            FormatPointer(handler == null ? null : handler->EventSceneModule),
            retainerManager != null && retainerManager->IsReady,
            retainerManager == null ? (byte)0 : retainerManager->MaxRetainerEntitlement,
            retainerManager == null ? 0 : retainerManager->LastSelectedRetainerId,
            retainerManager == null ? 0 : retainerManager->RetainerObjectId,
            condition[ConditionFlag.Occupied],
            condition[ConditionFlag.Occupied30],
            condition[ConditionFlag.OccupiedInEvent],
            condition[ConditionFlag.OccupiedInQuestEvent],
            FormatPointer(handler == null ? null : *(void**)handler),
            FormatPointer(retainerManager),
            FormatPointer(retainerListAgent),
            FormatPointer(retainerListAgent == null ? null : retainerListAgent->OpenerEventInterface),
            FormatPointer(
                retainerListAgent == null || retainerListAgent->OpenerEventInterface == null
                    ? null
                    : *(void**)retainerListAgent->OpenerEventInterface),
            retainerListAgent == null ? 0 : retainerListAgent->AddonId,
            FormatPointer(retainerAgent),
            FormatPointer(retainerAgent == null ? null : retainerAgent->OpenerEventInterface),
            FormatPointer(
                retainerAgent == null || retainerAgent->OpenerEventInterface == null
                    ? null
                    : *(void**)retainerAgent->OpenerEventInterface),
            retainerAgent == null ? 0 : retainerAgent->AddonId);
    }

    private unsafe bool IsAnyRetainerSessionUiOpen() =>
        IsAnyRetainerAddonOpen() ||
        condition[ConditionFlag.OccupiedSummoningBell];

    private unsafe bool IsAnyRetainerAddonOpen() =>
        IsAddonReady("RetainerList") ||
        IsAddonReady("SelectString") ||
        IsAddonReady("InventoryRetainer") ||
        IsAddonReady("InventoryRetainerLarge");

    private unsafe bool IsAddonReady(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(name, 1);
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    private static unsafe bool IsAgentActive(AgentModule* module, AgentId id)
    {
        var agent = module == null ? null : module->GetAgentByInternalId(id);
        return agent != null && agent->IsAgentActive();
    }

    private static unsafe string? FormatPointer(void* value) =>
        value == null ? null : $"0x{(ulong)value:X}";

    private string? WriteNormalCaptureEvidence(NormalSummoningBellCaptureEvidence evidence)
    {
        try
        {
            Directory.CreateDirectory(evidenceDirectory);
            var mode = evidence.CaptureMode == "CompleteLifecycle"
                ? "normal-bell-lifecycle"
                : "normal-bell";
            var path = Path.Combine(
                evidenceDirectory,
                $"{mode}-{evidence.StartedAtUtc:yyyyMMdd-HHmmss-fff}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(evidence, JsonOptions));
            return path;
        }
        catch (Exception ex)
        {
            log.Error(ex, "[MarketMafioso] Unable to write normal bell capture evidence.");
            return null;
        }
    }

    private sealed class NormalCaptureSession
    {
        public NormalCaptureSession(
            DateTimeOffset startedAtUtc,
            DateTimeOffset deadlineUtc,
            uint territoryId,
            ProbePosition? startPosition,
            NormalSummoningBellCaptureArmResult arm,
            bool captureCompleteLifecycle)
        {
            StartedAtUtc = startedAtUtc;
            DeadlineUtc = deadlineUtc;
            TerritoryId = territoryId;
            StartPosition = startPosition;
            Arm = arm;
            CaptureCompleteLifecycle = captureCompleteLifecycle;
        }

        public DateTimeOffset StartedAtUtc { get; }
        public DateTimeOffset DeadlineUtc { get; }
        public uint TerritoryId { get; }
        public ProbePosition? StartPosition { get; }
        public NormalSummoningBellCaptureArmResult Arm { get; }
        public bool CaptureCompleteLifecycle { get; }
        public bool StartTalkObserved { get; set; }
        public DateTimeOffset? InitialRetainerListObservedAtUtc { get; set; }
        public DateTimeOffset? CommandMenuObservedAtUtc { get; set; }
        public bool CommandMenuClosedObserved { get; set; }
        public DateTimeOffset? ReturnedRetainerListObservedAtUtc { get; set; }
        public DateTimeOffset? SessionClosedObservedAtUtc { get; set; }
        public NormalBellClientState? LastState { get; set; }
        public List<NormalBellClientStateSample> StateSamples { get; } = [];
    }

    private sealed record NormalSummoningBellCaptureEvidence(
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        uint TerritoryId,
        ProbePosition? StartPosition,
        ProbePosition? ConclusionPosition,
        string? CharacterName,
        string? BellGameObjectId,
        uint BellEventId,
        string BellEventIdSource,
        float Distance,
        float OrdinaryInteractionDistance,
        string CaptureMode,
        string Verdict,
        string Message,
        bool CommandMenuObserved,
        NormalBellLifecycleMilestones LifecycleMilestones,
        NormalBellClientStateSample[] StateTransitions,
        TalkEventPacketTransportObservation Transport);

    private sealed record NormalBellLifecycleMilestones(
        DateTimeOffset? InitialRetainerListObservedAtUtc,
        DateTimeOffset? CommandMenuObservedAtUtc,
        DateTimeOffset? ReturnedRetainerListObservedAtUtc,
        DateTimeOffset? SessionClosedObservedAtUtc);

    private sealed record NormalBellClientStateSample(
        DateTimeOffset CapturedAtUtc,
        NormalBellClientState State);

    private sealed record NormalBellClientState(
        bool OccupiedSummoningBell,
        bool RetainerListReady,
        bool SelectStringReady,
        bool InventoryRetainerReady,
        bool InventoryRetainerLargeReady,
        bool RetainerListAgentActive,
        bool RetainerAgentActive,
        uint EventState1EventId,
        ulong EventState1ObjectId,
        int EventState1OccupiedConditionId,
        byte EventState1Flags,
        uint EventState2EventId,
        ulong EventState2ObjectId,
        int EventState2OccupiedConditionId,
        byte EventState2Flags,
        ulong SceneGameObjectId,
        short Scene,
        ushort SceneFlags,
        byte SceneDataCount,
        string? BellEventHandlerAddress,
        uint BellEventHandlerEventId,
        short BellEventHandlerScene,
        ulong BellEventHandlerSceneFlags,
        uint BellEventHandlerSceneGameObjectId,
        string? BellEventSceneModuleAddress,
        bool RetainerManagerReady,
        byte MaxRetainerEntitlement,
        ulong LastSelectedRetainerId,
        uint RetainerObjectId,
        bool Occupied,
        bool Occupied30,
        bool OccupiedInEvent,
        bool OccupiedInQuestEvent,
        string? BellEventHandlerVTableAddress,
        string? RetainerManagerAddress,
        string? RetainerListAgentAddress,
        string? RetainerListOpenerEventInterfaceAddress,
        string? RetainerListOpenerEventInterfaceVTableAddress,
        uint RetainerListAgentAddonId,
        string? RetainerAgentAddress,
        string? RetainerOpenerEventInterfaceAddress,
        string? RetainerOpenerEventInterfaceVTableAddress,
        uint RetainerAgentAddonId);
}
