using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    private static readonly TimeSpan NativeCallPreloadWindow = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan NativeCallTeardownSettleWindow = TimeSpan.FromSeconds(3);
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
        var noOtherProbe =
            session is null &&
            normalCaptureSession is null &&
            warmSessionProbeSession is null &&
            retainerRpcProbeSession is null;
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

    public string BeginNativeRetainerVerb(NativeRetainerVerb verb)
    {
        var precondition = ValidateYieldProbeStart(requireTemplate: false);
        if (precondition is not null)
            return precondition;

        var observation = bell.ObserveLoadedBell();
        if (verb == NativeRetainerVerb.CallRetainer)
        {
            if (!observation.Available)
                return observation.Message;
            if (observation.OutsideOrdinaryInteractionRange)
            {
                return
                    $"Move inside ordinary bell range for the controlled preload " +
                    $"({observation.Distance:F1}/{observation.OrdinaryInteractionDistance:F1} yalms).";
            }
        }

        if (!AutoRetainerSuppressionLease.TryAcquire(autoRetainer, out var suppression, out var suppressionMessage))
            return suppressionMessage;
        autoRetainerSuppression = suppression;

        var now = DateTimeOffset.UtcNow;
        if (verb == NativeRetainerVerb.CallRetainer)
        {
            var activeCall = new YieldProbeSession(
                YieldEventSceneProbeMode.NativeCallRetainer,
                now,
                now + NativeCallPreloadWindow,
                clientState.TerritoryType,
                CapturePosition(),
                objectTable.LocalPlayer?.Name.TextValue ?? string.Empty,
                observation.BellGameObjectId,
                0,
                "PendingNormalPreload",
                observation.Distance,
                observation.OrdinaryInteractionDistance);
            CaptureYieldStateTransition(activeCall);
            activeCall.NativeCallPreloadTask = RunNativeCallPreloadAsync(
                activeCall,
                activeCall.NativeCallPreloadCancellation.Token);
            yieldProbeSession = activeCall;
            yieldProbeView = new(
                true,
                false,
                false,
                "Native CallRetainer",
                "Preloading roster",
                "Opening the nearby bell normally to capture one verified retainer identity.",
                $"MMF will select the first available retainer, choose Quit, close the returned list, then submit one native CallRetainer verb. {suppressionMessage}",
                FormatGameObjectId(observation.BellGameObjectId),
                null,
                null,
                yieldProbeView.LastEvidencePath);
            return "Native CallRetainer probe started; MMF is running the controlled nearby-bell preload.";
        }

        var submission = bell.TryInvokeNativeRetainerVerb(verb);
        var active = new YieldProbeSession(
            YieldEventSceneProbeMode.NativeSelectRetainer,
            now,
            now + YieldDirectObservationWindow,
            clientState.TerritoryType,
            CapturePosition(),
            objectTable.LocalPlayer?.Name.TextValue ?? string.Empty,
            submission.BellGameObjectId,
            submission.BellEventId,
            submission.BellEventIdSource,
            submission.Distance,
            submission.OrdinaryInteractionDistance);
        CaptureYieldStateTransition(active);
        yieldProbeSession = active;

        if (!submission.Submitted)
        {
            active.TerminalTransport = submission.Transport;
            CompleteYieldProbe(active, "NotSubmitted", submission.Message, false);
            return submission.Message;
        }

        var modeLabel = verb == NativeRetainerVerb.CallRetainer
            ? "Native CallRetainer"
            : "Native SelectRetainer";
        yieldProbeView = new(
            true,
            false,
            false,
            modeLabel,
            "Observing",
            submission.Message,
            $"One signature-resolved native event verb was submitted. No retry or follow-up action will occur. {suppressionMessage}",
            FormatGameObjectId(submission.BellGameObjectId),
            submission.RetainerId == 0 ? null : $"0x{submission.RetainerId:X16}",
            submission.Transport.Opcode == 0 ? null : $"0x{submission.Transport.Opcode:X}",
            yieldProbeView.LastEvidencePath);
        log.Information(
            "[MarketMafioso] Submitted one native {Verb} event verb for bell {BellGameObjectId:X}, event 0x{EventId:X}, handler scene {Scene}, retainer 0x{RetainerId:X16}, territory {TerritoryId}.",
            verb,
            submission.BellGameObjectId,
            submission.BellEventId,
            submission.HandlerScene,
            submission.RetainerId,
            active.TerritoryId);
        return submission.Message;
    }

    private async Task<WarmSessionBootstrapResult> RunNativeCallPreloadAsync(
        YieldProbeSession active,
        CancellationToken cancellationToken)
    {
        var list = await retainerAutomation.EnsureRetainerListAsync(cancellationToken).ConfigureAwait(false);
        RecordNativeCallPreloadStep(active, "Open retainer list", list);
        if (!list.Success)
            return new(false, list.Code, list.Message, null);

        var opened = await retainerAutomation.OpenFirstAvailableRetainerAsync(cancellationToken).ConfigureAwait(false);
        RecordNativeCallPreloadStep(
            active,
            "Select first available retainer",
            new(opened.Success, opened.Code, opened.Message));
        if (!opened.Success || opened.Target is null)
            return new(false, opened.Code, opened.Message, null);

        var quit = await retainerAutomation.CloseRetainerAsync(cancellationToken).ConfigureAwait(false);
        RecordNativeCallPreloadStep(active, "Quit retainer visit", quit);
        if (!quit.Success)
            return new(false, quit.Code, quit.Message, opened.Target);

        var close = await retainerAutomation.CloseRetainerListAsync(cancellationToken).ConfigureAwait(false);
        RecordNativeCallPreloadStep(active, "Close returned retainer list", close);
        return close.Success
            ? new(true, "NativeCallPreloadComplete", "Captured a verified retainer identity and closed the stock bell UI.", opened.Target)
            : new(false, close.Code, close.Message, opened.Target);
    }

    private static void RecordNativeCallPreloadStep(
        YieldProbeSession active,
        string name,
        RetainerAutomationResult result) =>
        active.NativeCallPreloadSteps.Enqueue(
            new(name, result.Success, result.Code, result.Message, DateTimeOffset.UtcNow));

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

        active.NativeCallPreloadCancellation.Cancel();
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

        if (!UpdateNativeCallPreload(active))
            return;

        var transport = bell.ObserveYieldEventSceneProbe();
        if (transport.State == YieldEventSceneProbeState.Failed)
        {
            CompleteYieldProbe(active, "TransportFailed", transport.Message, false);
            return;
        }

        if (transport.Sent && !active.OutboundObserved)
        {
            active.OutboundObserved = true;
            var retainerDescription = transport.RetainerId == 0
                ? string.Empty
                : $" for retainer 0x{transport.RetainerId:X16}";
            yieldProbeView = yieldProbeView with
            {
                State = "Observing",
                Message =
                    $"Sent opcode 0x{transport.Opcode:X}{retainerDescription}; waiting for the matching event response.",
                RetainerId = transport.RetainerId == 0 ? null : $"0x{transport.RetainerId:X16}",
                Opcode = $"0x{transport.Opcode:X}",
            };
        }

        var commandMenuReady = IsAddonReady("SelectString");
        var retainerListReady = IsAddonReady("RetainerList");
        var expectedUiReady = active.Mode == YieldEventSceneProbeMode.NativeSelectRetainer
            ? retainerListReady
            : commandMenuReady;
        if (transport.MatchingEventPlayObserved && expectedUiReady)
        {
            active.SuccessObservedAtUtc ??= DateTimeOffset.UtcNow;
            yieldProbeView = yieldProbeView with
            {
                State = "Settling",
                Message = active.Mode == YieldEventSceneProbeMode.NativeSelectRetainer
                    ? "Matching EventPlay and RetainerList observed."
                    : "Matching EventPlay and retainer command menu observed.",
                Readiness = "Hold here; the recorder will stop automatically.",
            };
            if (DateTimeOffset.UtcNow - active.SuccessObservedAtUtc.Value >= YieldSettleWindow)
            {
                CompleteYieldProbe(
                    active,
                    "Confirmed",
                    active.Mode switch
                    {
                        YieldEventSceneProbeMode.InSessionControl =>
                            "The exact cloned YieldEventScene2 completed inside the accepted bell session.",
                        YieldEventSceneProbeMode.SessionFreeReplay =>
                            "The session-free YieldEventScene2 received scene 2 and opened the retainer command menu.",
                        YieldEventSceneProbeMode.NativeCallRetainer =>
                            "The native CallRetainer verb received EventPlay and opened the retainer command menu.",
                        YieldEventSceneProbeMode.NativeSelectRetainer =>
                            "The native SelectRetainer verb received EventPlay and opened RetainerList.",
                        _ => "The event-yield probe reached its expected UI.",
                    },
                    expectedUiReady);
                return;
            }
        }

        if (DateTimeOffset.UtcNow < active.DeadlineUtc)
            return;

        if (!transport.Sent)
        {
            CompleteYieldProbe(
                active,
                active.Mode == YieldEventSceneProbeMode.InSessionControl
                    ? "ControlPacketNotCaptured"
                    : "NativePacketNotSubmitted",
                active.Mode == YieldEventSceneProbeMode.InSessionControl
                    ? "No matching stock YieldEventScene2 was captured before the control window expired."
                    : "The signature-resolved native event verb produced no outbound packet.",
                expectedUiReady);
            return;
        }

        var verdict = transport.MatchingEventPlayObserved
            ? expectedUiReady
                ? "Confirmed"
                : "MatchingEventPlayWithoutExpectedUi"
            : expectedUiReady
                ? "ExpectedUiWithoutMatchingEventPlay"
                : "NoMatchingEventPlay";
        var message = transport.MatchingEventPlayObserved
            ? "The server returned a matching EventPlay, but the expected retainer UI did not appear."
            : expectedUiReady
                ? "The expected retainer UI appeared without the matching EventPlay hook observation."
                : active.Mode switch
                {
                    YieldEventSceneProbeMode.SessionFreeReplay =>
                        "The one session-free YieldEventScene2 produced no matching scene-2 EventPlay or command menu.",
                    YieldEventSceneProbeMode.NativeCallRetainer =>
                        "The signature-resolved CallRetainer verb produced no matching EventPlay or retainer command menu.",
                    YieldEventSceneProbeMode.NativeSelectRetainer =>
                        "The signature-resolved SelectRetainer verb produced no matching EventPlay or RetainerList.",
                    _ => "The cloned in-session YieldEventScene2 produced no matching scene-2 EventPlay or command menu.",
                };
        CompleteYieldProbe(active, verdict, message, expectedUiReady);
    }

    private bool UpdateNativeCallPreload(YieldProbeSession active)
    {
        if (active.NativeCallPreloadTask is null || active.NativeCallSubmissionStarted)
            return true;

        var steps = active.NativeCallPreloadSteps.ToArray();
        if (steps.Length > active.ObservedNativeCallPreloadStepCount)
        {
            active.ObservedNativeCallPreloadStepCount = steps.Length;
            var last = steps[^1];
            yieldProbeView = yieldProbeView with
            {
                State = last.Success ? "Preloading roster" : "Preload step failed",
                Message = last.Message,
                Readiness = last.Success
                    ? $"Completed {steps.Length}/4 controlled preload actions."
                    : $"Stopped at preload action {steps.Length}/4 ({last.Code}).",
            };
        }

        if (!active.NativeCallPreloadTask.IsCompleted)
        {
            if (DateTimeOffset.UtcNow < active.DeadlineUtc)
                return false;

            active.NativeCallPreloadCancellation.Cancel();
            active.TerminalTransport = YieldEventSceneProbeObservation.Idle with
            {
                State = YieldEventSceneProbeState.Failed,
                Mode = YieldEventSceneProbeMode.NativeCallRetainer,
                Message = "The controlled nearby-bell preload timed out before any native call was submitted.",
            };
            CompleteYieldProbe(
                active,
                "PreloadTimedOut",
                active.TerminalTransport.Message,
                false);
            return false;
        }

        if (active.NativeCallPreloadResult is null)
        {
            try
            {
                active.NativeCallPreloadResult = active.NativeCallPreloadTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                active.NativeCallPreloadResult = new(
                    false,
                    "NativeCallPreloadCancelled",
                    "The controlled nearby-bell preload was cancelled.",
                    null);
            }
            catch (Exception ex)
            {
                log.Error(ex, "[MarketMafioso] Native CallRetainer preload failed unexpectedly.");
                active.NativeCallPreloadResult = new(
                    false,
                    "NativeCallPreloadException",
                    ex.Message,
                    null);
            }
        }

        var preload = active.NativeCallPreloadResult;
        if (!preload.Success || preload.Target is null)
        {
            var message = $"Controlled preload failed ({preload.Code}): {preload.Message}";
            active.TerminalTransport = YieldEventSceneProbeObservation.Idle with
            {
                State = YieldEventSceneProbeState.Failed,
                Mode = YieldEventSceneProbeMode.NativeCallRetainer,
                RetainerId = preload.Target?.RetainerId ?? 0,
                Message = message,
            };
            CompleteYieldProbe(active, "PreloadFailed", message, false);
            return false;
        }

        active.VerifiedRetainerId = preload.Target.RetainerId;
        active.NativeCallPreloadCompletedAtUtc ??= DateTimeOffset.UtcNow;
        if (IsAnyRetainerSessionUiOpen() || condition[ConditionFlag.OccupiedSummoningBell])
        {
            if (DateTimeOffset.UtcNow - active.NativeCallPreloadCompletedAtUtc.Value <
                NativeCallTeardownSettleWindow)
            {
                yieldProbeView = yieldProbeView with
                {
                    State = "Waiting for stock teardown",
                    Message = "The stock retainer UI closed; waiting for its occupied-bell condition to clear.",
                    Readiness = "No native event verb has been submitted yet.",
                    RetainerId = $"0x{active.VerifiedRetainerId:X16}",
                };
                return false;
            }

            var message = "The stock bell session did not finish tearing down before the bounded native call.";
            active.TerminalTransport = YieldEventSceneProbeObservation.Idle with
            {
                State = YieldEventSceneProbeState.Failed,
                Mode = YieldEventSceneProbeMode.NativeCallRetainer,
                RetainerId = active.VerifiedRetainerId,
                Message = message,
            };
            CompleteYieldProbe(active, "StockTeardownTimedOut", message, false);
            return false;
        }

        var submission = bell.TryInvokeNativeRetainerVerb(
            NativeRetainerVerb.CallRetainer,
            active.VerifiedRetainerId);
        active.NativeCallSubmissionStarted = true;
        active.BellGameObjectId = submission.BellGameObjectId;
        active.BellEventId = submission.BellEventId;
        active.BellEventIdSource = submission.BellEventIdSource;
        active.Distance = submission.Distance;
        active.OrdinaryInteractionDistance = submission.OrdinaryInteractionDistance;
        active.DeadlineUtc = DateTimeOffset.UtcNow + YieldDirectObservationWindow;

        if (!submission.Submitted)
        {
            active.TerminalTransport = submission.Transport;
            CompleteYieldProbe(active, "NotSubmitted", submission.Message, false);
            return false;
        }

        yieldProbeView = new(
            true,
            false,
            false,
            "Native CallRetainer",
            "Observing",
            submission.Message,
            "The controlled preload is closed and one signature-resolved native CallRetainer verb was submitted. No retry or follow-up action will occur.",
            FormatGameObjectId(submission.BellGameObjectId),
            $"0x{active.VerifiedRetainerId:X16}",
            submission.Transport.Opcode == 0 ? null : $"0x{submission.Transport.Opcode:X}",
            yieldProbeView.LastEvidencePath);
        log.Information(
            "[MarketMafioso] Completed controlled native-call preload and submitted one CallRetainer event verb for bell {BellGameObjectId:X}, event 0x{EventId:X}, handler scene {Scene}, retainer 0x{RetainerId:X16}.",
            submission.BellGameObjectId,
            submission.BellEventId,
            submission.HandlerScene,
            submission.RetainerId);
        return false;
    }

    private void CompleteYieldProbe(
        YieldProbeSession active,
        string verdict,
        string message,
        bool commandMenuObserved)
    {
        active.NativeCallPreloadCancellation.Cancel();
        CaptureYieldStateTransition(active);
        var transport = active.TerminalTransport ?? bell.ObserveYieldEventSceneProbe();
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
            active.NativeCallPreloadSteps.ToArray(),
            active.StateSamples.ToArray(),
            transport);
        var path = WriteYieldProbeEvidence(evidence);
        active.NativeCallPreloadCancellation.Dispose();

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
            active.Mode switch
            {
                YieldEventSceneProbeMode.InSessionControl => "In-session control",
                YieldEventSceneProbeMode.SessionFreeReplay => "Session-free replay",
                YieldEventSceneProbeMode.NativeCallRetainer => "Native CallRetainer",
                YieldEventSceneProbeMode.NativeSelectRetainer => "Native SelectRetainer",
                _ => active.Mode.ToString(),
            },
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
        if (warmSessionProbeSession is not null)
            return "The warm-session retention probe is already active.";
        if (retainerRpcProbeSession is not null)
            return "The retainer RPC probe is already active.";
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
            var mode = evidence.Mode switch
            {
                nameof(YieldEventSceneProbeMode.InSessionControl) => "yield-control",
                nameof(YieldEventSceneProbeMode.SessionFreeReplay) => "yield-session-free",
                nameof(YieldEventSceneProbeMode.NativeCallRetainer) => "native-call-retainer",
                nameof(YieldEventSceneProbeMode.NativeSelectRetainer) => "native-select-retainer",
                _ => "yield-unknown",
            };
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
        public DateTimeOffset DeadlineUtc { get; set; }
        public uint TerritoryId { get; }
        public ProbePosition? StartPosition { get; }
        public string CharacterName { get; }
        public ulong BellGameObjectId { get; set; }
        public uint BellEventId { get; set; }
        public string BellEventIdSource { get; set; }
        public float? Distance { get; set; }
        public float? OrdinaryInteractionDistance { get; set; }
        public CancellationTokenSource NativeCallPreloadCancellation { get; } = new();
        public ConcurrentQueue<WarmSessionBootstrapStep> NativeCallPreloadSteps { get; } = new();
        public Task<WarmSessionBootstrapResult>? NativeCallPreloadTask { get; set; }
        public WarmSessionBootstrapResult? NativeCallPreloadResult { get; set; }
        public DateTimeOffset? NativeCallPreloadCompletedAtUtc { get; set; }
        public int ObservedNativeCallPreloadStepCount { get; set; }
        public ulong VerifiedRetainerId { get; set; }
        public bool NativeCallSubmissionStarted { get; set; }
        public YieldEventSceneProbeObservation? TerminalTransport { get; set; }
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
        WarmSessionBootstrapStep[] NativeCallPreloadSteps,
        NormalBellClientStateSample[] StateTransitions,
        YieldEventSceneProbeObservation Transport);
}
