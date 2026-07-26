using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Retainers;
using MarketMafioso.Automation.Travel;

namespace MarketMafioso.MarketDiagnostics;

internal sealed record RemoteSummoningBellProbeView(
    bool Active,
    bool CanSubmit,
    string State,
    string Message,
    string Readiness,
    string? BellGameObjectId,
    float? Distance,
    float? OrdinaryInteractionDistance,
    string? LastEvidencePath);

internal sealed partial class RemoteSummoningBellProbe : IDisposable
{
    private const string ProbePhase = "A-loaded-same-zone-out-of-range";
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Configuration configuration;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly ICondition condition;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly DalamudSummoningBellInteractor bell;
    private readonly DalamudRetainerAutomationSession retainerAutomation;
    private readonly VNavmeshIpc vnavmesh;
    private readonly IAutoRetainerIpc autoRetainer;
    private readonly string evidenceDirectory;

    private ProbeSession? session;
    private AutoRetainerSuppressionLease? autoRetainerSuppression;
    private bool releaseSuppressionWhenRetainerListCloses;
    private RemoteSummoningBellProbeView view = new(
        false,
        false,
        "Idle",
        "Phase A is armed for the secondary client only. Load a same-zone bell and move outside its ordinary range; no in-range bootstrap is required.",
        "No loaded bell observation has been taken yet.",
        null,
        null,
        null,
        null);
    private bool disposed;

    public RemoteSummoningBellProbe(
        Configuration configuration,
        IClientState clientState,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IDataManager dataManager,
        IGameInteropProvider interopProvider,
        ISigScanner sigScanner,
        IFramework framework,
        IGameGui gameGui,
        ICondition condition,
        IChatGui chatGui,
        IPluginLog log,
        IDalamudPluginInterface pluginInterface,
        string pluginConfigDirectory)
    {
        this.configuration = configuration;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.framework = framework;
        this.gameGui = gameGui;
        this.condition = condition;
        this.chatGui = chatGui;
        this.log = log;
        bell = new(objectTable, targetManager, dataManager, interopProvider, sigScanner);
        retainerAutomation = new(framework, gameGui, dataManager, log, objectTable, targetManager, sigScanner);
        vnavmesh = new(new DalamudVNavmeshIpcAdapter(pluginInterface, log));
        autoRetainer = new DalamudAutoRetainerIpc(pluginInterface);
        evidenceDirectory = Path.Combine(pluginConfigDirectory, "remote-bell");
        framework.Update += OnFrameworkUpdate;
    }

    public RemoteSummoningBellProbeView GetView()
    {
        if (view.Active)
            return view;

        var observation = bell.ObserveLoadedBell();
        return view with
        {
            CanSubmit =
                configuration.EnableMarketDiagnostics &&
                clientState.IsLoggedIn &&
                observation.Available &&
                observation.OutsideOrdinaryInteractionRange &&
                normalCaptureSession is null &&
                yieldProbeSession is null &&
                warmSessionProbeSession is null &&
                !IsRetainerListReady(),
            Readiness = observation.Message,
            BellGameObjectId = FormatGameObjectId(observation.BellGameObjectId),
            Distance = observation.Available ? observation.Distance : null,
            OrdinaryInteractionDistance = observation.Available ? observation.OrdinaryInteractionDistance : null,
        };
    }

    public string BeginProbe()
    {
        if (disposed)
            return "Remote bell probe is unavailable because it has been disposed.";
        if (!configuration.EnableMarketDiagnostics)
            return "Enable Market Diagnostics in settings before running the remote bell probe.";
        if (!clientState.IsLoggedIn)
            return "Remote bell probe requires a logged-in character.";
        if (normalCaptureSession is not null)
            return "The normal bell flight recorder is already armed.";
        if (yieldProbeSession is not null)
            return "The YieldEventScene2 probe is already active.";
        if (warmSessionProbeSession is not null)
            return "The warm-session retention probe is already active.";
        if (session is not null)
            return "A remote bell probe is already observing its single submitted request.";
        if (IsRetainerListReady())
            return "Close the existing retainer list before running the probe.";
        if (releaseSuppressionWhenRetainerListCloses)
            ReleaseAutoRetainerSuppression();

        if (!AutoRetainerSuppressionLease.TryAcquire(autoRetainer, out var suppression, out var suppressionMessage))
            return suppressionMessage;
        autoRetainerSuppression = suppression;

        var startedAtUtc = DateTimeOffset.UtcNow;
        var territoryId = clientState.TerritoryType;
        var startPosition = CapturePosition();
        var submission = bell.TryOpenLoadedWithScopedHitboxRadius();
        if (!submission.Submitted)
        {
            ReleaseAutoRetainerSuppression();
            var evidence = CreateEvidence(
                startedAtUtc,
                startedAtUtc,
                territoryId,
                startPosition,
                CapturePosition(),
                submission,
                "NotSubmitted",
                false,
                false);
            var path = WriteEvidence(evidence);
            view = new(
                false,
                false,
                "Not submitted",
                submission.Message,
                submission.Message,
                FormatGameObjectId(submission.BellGameObjectId),
                submission.BellGameObjectId == 0 ? null : submission.Distance,
                submission.BellGameObjectId == 0 ? null : submission.OrdinaryInteractionDistance,
                path);
            return submission.Message;
        }

        session = new(
            startedAtUtc,
            startedAtUtc + ObservationWindow,
            territoryId,
            startPosition,
            submission);
        view = new(
            true,
            false,
            "Observing stock interaction",
            "Extended only the loaded bell's hitbox and shadowed its live/default positions to the player, then invoked stock InteractWithObject. Holding those client-only shadows through the bounded response observation.",
            $"The passive observer will not alter or retry the packet. {suppressionMessage}",
            FormatGameObjectId(submission.BellGameObjectId),
            submission.Distance,
            submission.OrdinaryInteractionDistance,
            view.LastEvidencePath);
        log.Information(
            "[MarketMafioso] Remote bell probe invoked transport {Transport} for bell {BellGameObjectId:X} at {Distance:F1} yalms in territory {TerritoryId}; holding radius {OriginalRadius:F1}->{TemporaryRadius:F1} and full live/default position shadows through response observation.",
            submission.Transport,
            submission.BellGameObjectId,
            submission.Distance,
            territoryId,
            submission.OriginalHitboxRadius,
            submission.TemporaryHitboxRadius);
        return submission.Message;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (disposed)
            return;
        if (normalCaptureSession is not null)
        {
            UpdateNormalCapture();
            return;
        }
        if (yieldProbeSession is not null)
        {
            UpdateYieldProbe();
            return;
        }
        if (warmSessionProbeSession is not null)
        {
            UpdateWarmSessionProbe();
            return;
        }
        ValidateYieldTemplateContext();
        if (session is not { } active)
        {
            if (releaseSuppressionWhenRetainerListCloses && !IsAnyRetainerSessionUiOpen())
                ReleaseAutoRetainerSuppression();
            return;
        }

        if (clientState.TerritoryType != active.TerritoryId)
        {
            bell.CancelTalkPacketTransport("Territory changed before the stock StartTalkEvent packet appeared.");
            var cancelled = bell.ObserveTalkPacketTransport();
            active = active with
            {
                Submission = ApplyInboundObservation(
                    active.Submission with
                    {
                        Message = cancelled.Message,
                        PacketsObservedWhileArmed = cancelled.PacketsObservedWhileArmed,
                        SizeEligiblePacketsObserved = cancelled.SizeEligiblePacketsObserved,
                    },
                    cancelled),
            };
            Complete(active, "InconclusiveTerritoryChanged", "Territory changed before the probe concluded.", false);
            return;
        }

        var transport = bell.ObserveTalkPacketTransport();
        var inboundEventPlayWasObserved = active.Submission.InboundEventPlayObserved;
        active = active with
        {
            Submission = ApplyInboundObservation(active.Submission, transport),
        };
        session = active;

        if (!inboundEventPlayWasObserved && active.Submission.InboundEventPlayObserved)
        {
            view = view with
            {
                State = "Inbound event observed",
                Message =
                    $"Observed matching inbound EventPlay scene {active.Submission.InboundScene} " +
                    $"(flags 0x{active.Submission.InboundSceneFlags:X}); waiting to see whether RetainerList opens.",
                Readiness = "The server returned an EventPlay for the exact bell actor/event pair.",
            };
            log.Information(
                "[MarketMafioso] Remote bell probe observed matching inbound EventPlay for bell {BellGameObjectId:X}; event 0x{EventId:X}, scene {Scene}, flags 0x{SceneFlags:X}, scene data count {SceneDataCount}.",
                active.Submission.InboundEventObjectId,
                active.Submission.InboundEventId,
                active.Submission.InboundScene,
                active.Submission.InboundSceneFlags,
                active.Submission.InboundSceneDataCount);
        }

        if (!active.OccupiedSummoningBellObserved &&
            condition[ConditionFlag.OccupiedSummoningBell])
        {
            active = active with { OccupiedSummoningBellObserved = true };
            session = active;
        }

        if (!active.Submission.OutboundPacketObserved)
        {
            if (transport.State == TalkEventPacketTransportState.Failed)
            {
                active = active with
                {
                    Submission = active.Submission with
                    {
                        Message = transport.Message,
                        PacketsObservedWhileArmed = transport.PacketsObservedWhileArmed,
                        SizeEligiblePacketsObserved = transport.SizeEligiblePacketsObserved,
                    },
                };
                Complete(
                    active,
                    "NotSubmitted",
                    $"No outbound request was sent: {transport.Message}",
                    false);
                return;
            }

            if (transport.Sent)
            {
                active = active with
                {
                    Submission = active.Submission with
                    {
                        Message = transport.Message,
                        PacketOpcode = transport.Opcode,
                        BuilderPacketSuppressed = transport.BuilderPacketSuppressed,
                        ConstructedPacket = transport.ConstructedPacket,
                        OutboundPacketObserved = true,
                        PacketsObservedWhileArmed = transport.PacketsObservedWhileArmed,
                        SizeEligiblePacketsObserved = transport.SizeEligiblePacketsObserved,
                    },
                };
                session = active;
                view = view with
                {
                    State = "Observing",
                    Message = $"{transport.Message} Waiting for the ordinary retainer list; no retry will be sent.",
                    Readiness = "One stock StartTalkEvent has been observed and passed through unchanged.",
                };
                log.Information(
                    "[MarketMafioso] Remote bell probe observed one stock StartTalkEvent for bell {BellGameObjectId:X}; opcode 0x{PacketOpcode:X}.",
                    active.Submission.BellGameObjectId,
                    transport.Opcode);
            }
        }

        if (IsRetainerListReady())
        {
            Complete(
                active,
                "Confirmed",
                "Confirmed: the ordinary retainer list opened from one stock out-of-range StartTalkEvent.",
                true);
            return;
        }

        if (DateTimeOffset.UtcNow >= active.DeadlineUtc)
        {
            if (!active.Submission.OutboundPacketObserved)
            {
                bell.CancelTalkPacketTransport("The stock StartTalkEvent packet did not appear within 10 seconds.");
                var cancelled = bell.ObserveTalkPacketTransport();
                active = active with
                {
                    Submission = active.Submission with
                    {
                        Message = cancelled.Message,
                        PacketsObservedWhileArmed = cancelled.PacketsObservedWhileArmed,
                        SizeEligiblePacketsObserved = cancelled.SizeEligiblePacketsObserved,
                    },
                };
                Complete(
                    active,
                    "NotSubmitted",
                    $"No outbound request was sent. {cancelled.Message}",
                    false);
                return;
            }

            Complete(
                active,
                active.Submission.InboundEventPlayObserved
                    ? "InboundEventPlayWithoutRetainerList"
                    : active.Submission.MatchingInboundEventYieldObserved
                        ? "InboundEventYieldWithoutRetainerList"
                    : "NoMatchingInboundEventPlay",
                active.Submission.InboundEventPlayObserved
                    ? "The exact bell EventPlay reached the client, but no retainer list appeared within 10 seconds. No second interaction was sent."
                    : active.Submission.MatchingInboundEventYieldObserved
                        ? "A matching bell EventYield reached the client without an EventPlay or retainer list. No second interaction was sent."
                    : "The stock StartTalkEvent left the client, but no matching bell EventPlay returned within 10 seconds. No second interaction was sent.",
                false);
        }
    }

    private void Complete(ProbeSession active, string verdict, string message, bool retainerListReady)
    {
        session = null;
        active = active with
        {
            Submission = ApplyInboundObservation(
                active.Submission,
                bell.ObserveTalkPacketTransport()),
        };
        bell.CancelTalkPacketTransport("The remote bell probe concluded.");
        var completedAtUtc = DateTimeOffset.UtcNow;
        var evidence = CreateEvidence(
            active.StartedAtUtc,
            completedAtUtc,
            active.TerritoryId,
            active.StartPosition,
            CapturePosition(),
            active.Submission,
            verdict,
            retainerListReady,
            active.OccupiedSummoningBellObserved);
        var path = WriteEvidence(evidence);
        if (verdict == "Confirmed" && autoRetainerSuppression is { Changed: true })
            releaseSuppressionWhenRetainerListCloses = true;
        else
            ReleaseAutoRetainerSuppression();
        view = new(
            false,
            false,
            verdict,
            releaseSuppressionWhenRetainerListCloses
                ? $"{message} AutoRetainer remains suppressed until the retainer list closes."
                : message,
            message,
            FormatGameObjectId(active.Submission.BellGameObjectId),
            active.Submission.Distance,
            active.Submission.OrdinaryInteractionDistance,
            path);

        if (verdict == "Confirmed")
            chatGui.Print($"[MMF] Remote bell probe: {message}");
        else
            chatGui.PrintError($"[MMF] Remote bell probe: {message}");
        log.Information(
            "[MarketMafioso] Remote bell probe concluded {Verdict}. Evidence: {EvidencePath}",
            verdict,
            path ?? "(write failed)");
    }

    private RemoteSummoningBellProbeEvidence CreateEvidence(
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        uint territoryId,
        ProbePosition? startPosition,
        ProbePosition? conclusionPosition,
        RemoteSummoningBellInteractionResult submission,
        string verdict,
        bool retainerListReady,
        bool occupiedSummoningBellObserved) =>
        new(
            startedAtUtc,
            completedAtUtc,
            territoryId,
            startPosition,
            conclusionPosition,
            objectTable.LocalPlayer?.Name.TextValue,
            FormatGameObjectId(submission.BellGameObjectId),
            submission.BellEventId,
            submission.BellEventIdSource,
            submission.Distance,
            submission.OrdinaryInteractionDistance,
            ProbePhase,
            submission.Code,
            submission.Message,
            submission.PacketOpcode,
            submission.BuilderPacketSuppressed,
            submission.ConstructedPacket,
            submission.OutboundPacketObserved,
            submission.InboundEventPlayObserved,
            FormatGameObjectId(submission.InboundEventObjectId),
            submission.InboundEventId,
            submission.InboundScene,
            submission.InboundSceneFlags,
            submission.InboundSceneDataCount,
            submission.InboundSceneData,
            submission.InboundEventPlayCount,
            submission.InboundEventPlaySamples,
            submission.MatchingInboundEventYieldObserved,
            submission.InboundEventYieldCount,
            submission.InboundEventYieldSamples,
            submission.InboundActorControlCount,
            submission.InboundActorControlSamples,
            submission.InboundRawPacketCount,
            submission.InboundRawPacketSamples,
            submission.OriginalHitboxRadius,
            submission.TemporaryHitboxRadius,
            submission.OriginalBellX,
            submission.OriginalBellY,
            submission.OriginalBellZ,
            submission.TemporaryBellX,
            submission.TemporaryBellY,
            submission.TemporaryBellZ,
            submission.OriginalDefaultBellX,
            submission.OriginalDefaultBellY,
            submission.OriginalDefaultBellZ,
            submission.PacketsObservedWhileArmed,
            submission.SizeEligiblePacketsObserved,
            submission.OutboundPacketObserved ? 1 : 0,
            submission.Transport,
            verdict,
            retainerListReady,
            occupiedSummoningBellObserved,
            condition[ConditionFlag.OccupiedSummoningBell]);

    private static RemoteSummoningBellInteractionResult ApplyInboundObservation(
        RemoteSummoningBellInteractionResult submission,
        TalkEventPacketTransportObservation transport) =>
        submission with
        {
            InboundEventPlayObserved = transport.InboundEventPlayObserved,
            InboundEventObjectId = transport.InboundEventObjectId,
            InboundEventId = transport.InboundEventId,
            InboundScene = transport.InboundScene,
            InboundSceneFlags = transport.InboundSceneFlags,
            InboundSceneDataCount = transport.InboundSceneDataCount,
            InboundSceneData = transport.InboundSceneData,
            InboundEventPlayCount = transport.InboundEventPlayCount,
            InboundEventPlaySamples = transport.InboundEventPlaySamples,
            MatchingInboundEventYieldObserved = transport.MatchingInboundEventYieldObserved,
            InboundEventYieldCount = transport.InboundEventYieldCount,
            InboundEventYieldSamples = transport.InboundEventYieldSamples,
            InboundActorControlCount = transport.InboundActorControlCount,
            InboundActorControlSamples = transport.InboundActorControlSamples,
            InboundRawPacketCount = transport.InboundRawPacketCount,
            InboundRawPacketSamples = transport.InboundRawPacketSamples,
        };

    private string? WriteEvidence(RemoteSummoningBellProbeEvidence evidence)
    {
        try
        {
            Directory.CreateDirectory(evidenceDirectory);
            var path = Path.Combine(
                evidenceDirectory,
                $"same-territory-{evidence.StartedAtUtc:yyyyMMdd-HHmmss-fff}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(evidence, JsonOptions));
            return path;
        }
        catch (Exception ex)
        {
            log.Error(ex, "[MarketMafioso] Unable to write remote bell probe evidence.");
            return null;
        }
    }

    private unsafe bool IsRetainerListReady()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("RetainerList", 1);
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    private ProbePosition? CapturePosition()
    {
        var position = objectTable.LocalPlayer?.Position;
        return position is { } value ? new(value.X, value.Y, value.Z) : null;
    }

    private static string? FormatGameObjectId(ulong value) => value == 0 ? null : value.ToString("X");

    private void ReleaseAutoRetainerSuppression()
    {
        const string suppressionSuffix = " AutoRetainer remains suppressed until the retainer session closes.";
        releaseSuppressionWhenRetainerListCloses = false;
        var suppression = autoRetainerSuppression;
        autoRetainerSuppression = null;
        if (suppression is null)
            return;

        suppression.Dispose();
        view = view with { Message = view.Message.Replace(suppressionSuffix, string.Empty, StringComparison.Ordinal) };
        warmSessionProbeView = warmSessionProbeView with
        {
            Message = warmSessionProbeView.Message.Replace(suppressionSuffix, string.Empty, StringComparison.Ordinal),
        };
        if (suppression.RestoreError is not null)
            log.Error("[MarketMafioso] AutoRetainer suppression restoration failed after remote bell probe: {Error}", suppression.RestoreError);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        session = null;
        normalCaptureSession = null;
        yieldProbeSession = null;
        if (warmSessionProbeSession is not null)
        {
            warmSessionProbeSession.BootstrapCancellation.Cancel();
            RestoreLocalBellCondition(warmSessionProbeSession);
            StopOwnedWarmSessionNavigation(warmSessionProbeSession);
            var warm = bell.ObserveWarmSessionRetention();
            if (warm.TeardownSuppressed &&
                !warm.MatchingScene2Observed &&
                !warm.TeardownReleaseSent)
            {
                bell.ReleaseWarmSession();
            }
            warmSessionProbeSession.BootstrapCancellation.Dispose();
        }
        warmSessionProbeSession = null;
        retainerAutomation.CancelActive();
        bell.CancelTalkPacketTransport("The remote bell probe was disposed.");
        bell.CancelYieldEventSceneProbe("The remote bell probe was disposed.");
        bell.StopWarmSessionRetention("The remote bell probe was disposed.");
        ReleaseAutoRetainerSuppression();
        autoRetainer.Dispose();
        bell.Dispose();
        framework.Update -= OnFrameworkUpdate;
    }

    private sealed record ProbeSession(
        DateTimeOffset StartedAtUtc,
        DateTimeOffset DeadlineUtc,
        uint TerritoryId,
        ProbePosition? StartPosition,
        RemoteSummoningBellInteractionResult Submission,
        bool OccupiedSummoningBellObserved = false);

    internal sealed record ProbePosition(float X, float Y, float Z);

    private sealed record RemoteSummoningBellProbeEvidence(
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
        string ProbePhase,
        string SubmissionCode,
        string SubmissionMessage,
        uint PacketOpcode,
        bool BuilderPacketSuppressed,
        bool ConstructedPacket,
        bool OutboundPacketObserved,
        bool InboundEventPlayObserved,
        string? InboundEventObjectId,
        uint InboundEventId,
        short InboundScene,
        ulong InboundSceneFlags,
        byte InboundSceneDataCount,
        uint[]? InboundSceneData,
        int InboundEventPlayCount,
        InboundEventPlaySample[]? InboundEventPlaySamples,
        bool MatchingInboundEventYieldObserved,
        int InboundEventYieldCount,
        InboundEventYieldSample[]? InboundEventYieldSamples,
        int InboundActorControlCount,
        InboundActorControlSample[]? InboundActorControlSamples,
        int InboundRawPacketCount,
        InboundRawPacketSample[]? InboundRawPacketSamples,
        float OriginalHitboxRadius,
        float TemporaryHitboxRadius,
        float OriginalBellX,
        float OriginalBellY,
        float OriginalBellZ,
        float TemporaryBellX,
        float TemporaryBellY,
        float TemporaryBellZ,
        float OriginalDefaultBellX,
        float OriginalDefaultBellY,
        float OriginalDefaultBellZ,
        int PacketsObservedWhileArmed,
        int SizeEligiblePacketsObserved,
        int OutboundRequestCount,
        string Transport,
        string Verdict,
        bool RetainerListReady,
        bool OccupiedSummoningBellObserved,
        bool OccupiedSummoningBell);
}
