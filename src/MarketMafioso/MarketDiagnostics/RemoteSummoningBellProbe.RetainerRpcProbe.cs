using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MarketMafioso.MarketDiagnostics;

internal sealed partial class RemoteSummoningBellProbe
{
    private const string RetainerRpcExpectedClientVersion = "2026.06.18.0000.0000";
    private const long ServerRequestCallbackInterfaceFinalizeRva = 0x00843840;
    private const long ServerRequestCallbackManagerAvailableRva = 0x00843920;
    private const long ServerRequestCallbackManagerGetRva = 0x00843940;
    private const long ServerRequestCallbackManagerRequestRva = 0x00843A60;
    private const long RetainerManagerRequestListRva = 0x011075C0;
    private const long RetainerManagerRequestSingleDataRva = 0x011076F0;
    private static readonly TimeSpan RetainerRpcStageTimeout = TimeSpan.FromSeconds(12);
    private const int MaximumRetainerRpcRosterEntries = 10;

    private readonly ConcurrentQueue<RetainerRpcCallbackObservation> retainerRpcCallbacks = new();
    private RetainerRpcProbeSession? retainerRpcProbeSession;
    private RetainerRpcControlReference? lastRetainerRpcControl;
    private string? lastRetainerRpcEvidencePath;
    private nint retainerRpcCallbackVtable;
    private nint retainerRpcCallbackObject;
    private GCHandle retainerRpcOwnerHandle;
    private bool retainerRpcOwnerHandleAllocated;
    private bool retainerRpcCallbackRegistered;

    public string BeginRetainerRpcControl() =>
        BeginRetainerRpcProbe(RetainerRpcProbeMode.ColdKind4Control);

    public string BeginRetainerRpcBindTest() =>
        BeginRetainerRpcProbe(RetainerRpcProbeMode.Kind2Kind3Kind4);

    public string GetRetainerRpcProbeStatus()
    {
        if (retainerRpcProbeSession is not { } active)
        {
            return lastRetainerRpcEvidencePath is null
                ? "Idle. Run the cold control first."
                : $"Idle. Last evidence: {lastRetainerRpcEvidencePath}";
        }

        return
            $"{active.Mode}, {active.Stage}; waiting until {active.DeadlineUtc:O}. " +
            $"Completed callbacks: {active.Callbacks.Count}.";
    }

    public string CancelRetainerRpcProbe()
    {
        if (retainerRpcProbeSession is not { } active)
            return "No retainer RPC probe is active.";

        CompleteRetainerRpcProbe(
            active,
            "Cancelled",
            "Cancelled by user; any outstanding callback registration was finalized.");
        return "Retainer RPC probe cancelled and callback storage released.";
    }

    private unsafe string BeginRetainerRpcProbe(RetainerRpcProbeMode mode)
    {
        var precondition = ValidateRetainerRpcProbeStart();
        if (precondition is not null)
            return precondition;

        if (!TryInitializeRetainerRpcCallback(out var callbackError))
            return callbackError;

        if (!AutoRetainerSuppressionLease.TryAcquire(
                autoRetainer,
                out var suppression,
                out var suppressionMessage))
        {
            return suppressionMessage;
        }
        autoRetainerSuppression = suppression;

        while (retainerRpcCallbacks.TryDequeue(out _))
        {
        }

        var now = DateTimeOffset.UtcNow;
        var stage = mode == RetainerRpcProbeMode.ColdKind4Control
            ? RetainerRpcProbeStage.AwaitingColdKind4
            : RetainerRpcProbeStage.AwaitingKind2;
        var active = new RetainerRpcProbeSession(
            mode,
            stage,
            now,
            now + RetainerRpcStageTimeout,
            clientState.TerritoryType,
            objectTable.LocalPlayer?.Name.TextValue ?? string.Empty,
            GetCurrentClientVersion(),
            CapturePosition());
        active.Stages.Add(CaptureRetainerRpcStage("Before first request", null));
        retainerRpcProbeSession = active;

        try
        {
            SubmitRetainerRpcRequest(
                mode == RetainerRpcProbeMode.ColdKind4Control ? 4U : 2U,
                0,
                0);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[MarketMafioso] Retainer RPC probe could not submit its first request.");
            CompleteRetainerRpcProbe(
                active,
                "NotSubmitted",
                $"The first native request was not submitted: {ex.Message}");
            return "Retainer RPC request was not submitted; cleanup completed.";
        }

        active.Requests.Add(new(
            now,
            mode == RetainerRpcProbeMode.ColdKind4Control ? 4U : 2U,
            0,
            0));
        active.Stages.Add(CaptureRetainerRpcStage("First request submitted", null));
        return mode == RetainerRpcProbeMode.ColdKind4Control
            ? $"Cold kind-4 control submitted once. {suppressionMessage}"
            : $"Kind 2 submitted once; kind 3 and the post-bind kind 4 will follow only after their preceding callbacks. {suppressionMessage}";
    }

    private string? ValidateRetainerRpcProbeStart()
    {
        if (disposed)
            return "The retainer RPC probe is unavailable because the diagnostic owner was disposed.";
        if (!configuration.EnableMarketDiagnostics)
            return "Enable Market Diagnostics before running this debug-only probe.";
        if (!clientState.IsLoggedIn)
            return "Log in before running the retainer RPC probe.";
        if (retainerRpcProbeSession is not null)
            return "A retainer RPC probe is already active.";
        if (session is not null ||
            normalCaptureSession is not null ||
            yieldProbeSession is not null ||
            warmSessionProbeSession is not null)
        {
            return "Another remote-bell diagnostic is active.";
        }
        if (IsAnyRetainerSessionUiOpen() || condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedSummoningBell])
            return "Close the current bell/retainer session before running the cold RPC probe.";

        var version = GetCurrentClientVersion();
        if (!string.Equals(
                version,
                RetainerRpcExpectedClientVersion,
                StringComparison.Ordinal))
        {
            return
                $"Client build {version} is not the statically verified " +
                $"{RetainerRpcExpectedClientVersion} build; no request was sent.";
        }

        unsafe
        {
            if (RetainerManager.Instance() == null)
                return "RetainerManager is unavailable; no request was sent.";
            if (!IsServerRequestCallbackManagerAvailable())
                return "ServerRequestCallbackManager is not initialized; no request was sent.";
            if (GetServerRequestCallbackManager() == 0)
                return "ServerRequestCallbackManager is unavailable; no request was sent.";
        }

        return null;
    }

    private void UpdateRetainerRpcProbe()
    {
        if (retainerRpcProbeSession is not { } active)
            return;

        if (!clientState.IsLoggedIn)
        {
            CompleteRetainerRpcProbe(active, "CancelledOnLogout", "Logout occurred before the sequence completed.");
            return;
        }
        if (clientState.TerritoryType != active.TerritoryId)
        {
            CompleteRetainerRpcProbe(active, "CancelledOnTerritoryChange", "Territory changed before the sequence completed.");
            return;
        }

        while (retainerRpcCallbacks.TryDequeue(out var callback))
        {
            retainerRpcCallbackRegistered = false;
            active.Callbacks.Add(callback);
            active.Stages.Add(CaptureRetainerRpcStage($"Callback kind {callback.Kind}", callback));
            if (!AdvanceRetainerRpcProbe(active, callback))
                return;
        }

        if (DateTimeOffset.UtcNow >= active.DeadlineUtc)
        {
            CompleteRetainerRpcProbe(
                active,
                "TimedOut",
                $"No callback arrived for {active.Stage} within {RetainerRpcStageTimeout.TotalSeconds:F0} seconds.");
        }
    }

    private bool AdvanceRetainerRpcProbe(
        RetainerRpcProbeSession active,
        RetainerRpcCallbackObservation callback)
    {
        var expectedKind = active.Stage switch
        {
            RetainerRpcProbeStage.AwaitingKind2 => 2U,
            RetainerRpcProbeStage.AwaitingKind3 => 3U,
            RetainerRpcProbeStage.AwaitingColdKind4 or
                RetainerRpcProbeStage.AwaitingPostKind4 => 4U,
            _ => 0U,
        };
        if (callback.Kind != expectedKind)
        {
            CompleteRetainerRpcProbe(
                active,
                "UnexpectedCallback",
                $"Expected callback kind {expectedKind}, received {callback.Kind}; sequence stopped.");
            return false;
        }

        try
        {
            switch (active.Stage)
            {
                case RetainerRpcProbeStage.AwaitingColdKind4:
                    CompleteRetainerRpcProbe(
                        active,
                        "ControlComplete",
                        "Cold kind-4 callback captured; no follow-up request was sent.");
                    return false;

                case RetainerRpcProbeStage.AwaitingKind2:
                    if (!TrySelectFirstRetainer(out var retainerId, out var rosterError))
                    {
                        CompleteRetainerRpcProbe(active, "RosterUnavailable", rosterError);
                        return false;
                    }

                    active.RetainerId = retainerId;
                    active.Stage = RetainerRpcProbeStage.AwaitingKind3;
                    active.DeadlineUtc = DateTimeOffset.UtcNow + RetainerRpcStageTimeout;
                    SubmitRetainerRpcRequest(
                        3,
                        (uint)(retainerId >> 32),
                        (uint)retainerId);
                    active.Requests.Add(new(
                        DateTimeOffset.UtcNow,
                        3,
                        (uint)(retainerId >> 32),
                        (uint)retainerId));
                    active.Stages.Add(CaptureRetainerRpcStage("Kind 3 submitted", null));
                    return true;

                case RetainerRpcProbeStage.AwaitingKind3:
                    active.Stage = RetainerRpcProbeStage.AwaitingPostKind4;
                    active.DeadlineUtc = DateTimeOffset.UtcNow + RetainerRpcStageTimeout;
                    SubmitRetainerRpcRequest(4, 0, 0);
                    active.Requests.Add(new(DateTimeOffset.UtcNow, 4, 0, 0));
                    active.Stages.Add(CaptureRetainerRpcStage("Post-kind-3 kind 4 submitted", null));
                    return true;

                case RetainerRpcProbeStage.AwaitingPostKind4:
                    CompleteRetainerRpcProbe(
                        active,
                        "BindSequenceComplete",
                        DescribeRetainerRpcComparison(active, callback));
                    return false;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "[MarketMafioso] Retainer RPC sequence failed while advancing {Stage}.", active.Stage);
            CompleteRetainerRpcProbe(
                active,
                "SequenceError",
                $"Sequence stopped at {active.Stage}: {ex.Message}");
            return false;
        }

        CompleteRetainerRpcProbe(active, "InvalidStage", $"Unknown sequence stage {active.Stage}.");
        return false;
    }

    private unsafe bool TrySelectFirstRetainer(out ulong retainerId, out string error)
    {
        retainerId = 0;
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady)
        {
            error = "Kind 2 completed without a ready RetainerManager; kind 3 was not sent.";
            return false;
        }

        var count = manager->GetRetainerCount();
        for (uint index = 0; index < count; index++)
        {
            var retainer = manager->GetRetainerBySortedIndex(index);
            if (retainer == null || retainer->RetainerId == 0)
                continue;
            retainerId = retainer->RetainerId;
            error = string.Empty;
            return true;
        }

        error = "Kind 2 completed without a usable roster identity; kind 3 was not sent.";
        return false;
    }

    private unsafe void SubmitRetainerRpcRequest(uint kind, uint argument1, uint argument2)
    {
        if (retainerRpcCallbackRegistered)
            throw new InvalidOperationException("The previous callback registration is still outstanding.");

        *(nint*)retainerRpcCallbackObject = retainerRpcCallbackVtable;
        retainerRpcCallbackRegistered = true;
        try
        {
            var retainerManager = RetainerManager.Instance();
            if (retainerManager == null)
                throw new InvalidOperationException("RetainerManager became unavailable.");

            switch (kind)
            {
                case 2:
                {
                    var requestList = (delegate* unmanaged<nint, nint, void>)
                        ResolveRetainerRpcAddress(RetainerManagerRequestListRva);
                    requestList((nint)retainerManager, retainerRpcCallbackObject);
                    break;
                }
                case 3:
                {
                    var requestSingle = (delegate* unmanaged<nint, nint, ulong, void>)
                        ResolveRetainerRpcAddress(RetainerManagerRequestSingleDataRva);
                    requestSingle(
                        (nint)retainerManager,
                        retainerRpcCallbackObject,
                        ((ulong)argument1 << 32) | argument2);
                    break;
                }
                default:
                {
                    var manager = GetServerRequestCallbackManager();
                    if (manager == 0)
                        throw new InvalidOperationException("ServerRequestCallbackManager became unavailable.");
                    var request = (delegate* unmanaged<nint, nint, uint, uint, uint, void>)
                        ResolveRetainerRpcAddress(ServerRequestCallbackManagerRequestRva);
                    request(manager, retainerRpcCallbackObject, kind, argument1, argument2);
                    break;
                }
            }
        }
        catch
        {
            retainerRpcCallbackRegistered = false;
            throw;
        }
    }

    private unsafe nint GetServerRequestCallbackManager()
    {
        var get = (delegate* unmanaged<nint>)
            ResolveRetainerRpcAddress(ServerRequestCallbackManagerGetRva);
        return get();
    }

    private unsafe bool IsServerRequestCallbackManagerAvailable()
    {
        var available = (delegate* unmanaged<byte>)
            ResolveRetainerRpcAddress(ServerRequestCallbackManagerAvailableRva);
        return available() != 0;
    }

    private unsafe void FinalizeRetainerRpcCallbackRegistration()
    {
        if (!retainerRpcCallbackRegistered || retainerRpcCallbackObject == 0)
            return;

        var finalize = (delegate* unmanaged<nint, void>)
            ResolveRetainerRpcAddress(ServerRequestCallbackInterfaceFinalizeRva);
        finalize(retainerRpcCallbackObject);
        retainerRpcCallbackRegistered = false;
    }

    private unsafe nint ResolveRetainerRpcAddress(long rva) =>
        sigScanner.Module.BaseAddress + checked((int)rva);

    private unsafe bool TryInitializeRetainerRpcCallback(out string error)
    {
        if (retainerRpcCallbackObject != 0)
        {
            error = string.Empty;
            return true;
        }

        try
        {
            retainerRpcOwnerHandle = GCHandle.Alloc(this);
            retainerRpcOwnerHandleAllocated = true;
            retainerRpcCallbackVtable = Marshal.AllocHGlobal(2 * IntPtr.Size);
            retainerRpcCallbackObject = Marshal.AllocHGlobal(2 * IntPtr.Size);
            *(nint*)retainerRpcCallbackVtable =
                (nint)(delegate* unmanaged<nint, void>)&RetainerRpcCallbackDestroy;
            *((nint*)retainerRpcCallbackVtable + 1) =
                (nint)(delegate* unmanaged<nint, uint, nint, void>)&RetainerRpcCallbackReceive;
            *(nint*)retainerRpcCallbackObject = retainerRpcCallbackVtable;
            *((nint*)retainerRpcCallbackObject + 1) =
                GCHandle.ToIntPtr(retainerRpcOwnerHandle);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            FreeRetainerRpcCallbackStorage();
            error = $"Unable to allocate stable native callback storage: {ex.Message}";
            return false;
        }
    }

    [UnmanagedCallersOnly]
    private static void RetainerRpcCallbackDestroy(nint _)
    {
        // Embedded callback storage is owned and released by the probe.
    }

    [UnmanagedCallersOnly]
    private static unsafe void RetainerRpcCallbackReceive(
        nint callback,
        uint kind,
        nint response)
    {
        try
        {
            var handlePointer = *((nint*)callback + 1);
            if (handlePointer == 0)
                return;
            if (GCHandle.FromIntPtr(handlePointer).Target is not RemoteSummoningBellProbe owner)
                return;

            uint? firstWord =
                response == 0 || kind is 2 or 3
                    ? null
                    : *(uint*)response;
            owner.retainerRpcCallbacks.Enqueue(new(
                DateTimeOffset.UtcNow,
                kind,
                FormatPointerValue(callback),
                FormatPointerValue(response),
                firstWord));
        }
        catch
        {
            // An exception must never cross the unmanaged callback boundary.
        }
    }

    private void CompleteRetainerRpcProbe(
        RetainerRpcProbeSession active,
        string verdict,
        string message)
    {
        FinalizeRetainerRpcCallbackRegistration();
        active.Stages.Add(CaptureRetainerRpcStage("Terminal cleanup", null));
        var completedAt = DateTimeOffset.UtcNow;
        var comparison = CreateRetainerRpcComparison(active);
        var evidence = new RetainerRpcProbeEvidence(
            active.StartedAtUtc,
            completedAt,
            active.TerritoryId,
            active.CharacterName,
            active.ClientVersion,
            active.Mode.ToString(),
            active.RetainerId == 0 ? null : $"0x{active.RetainerId:X16}",
            verdict,
            message,
            active.StartPosition,
            CapturePosition(),
            FormatPointerValue(sigScanner.Module.BaseAddress),
            $"0x{ServerRequestCallbackManagerGetRva:X}",
            $"0x{ServerRequestCallbackManagerRequestRva:X}",
            $"0x{RetainerManagerRequestListRva:X}",
            $"0x{RetainerManagerRequestSingleDataRva:X}",
            $"0x{ServerRequestCallbackInterfaceFinalizeRva:X}",
            active.Requests.ToArray(),
            active.Callbacks.ToArray(),
            active.Stages.ToArray(),
            comparison);
        lastRetainerRpcEvidencePath = WriteRetainerRpcEvidence(evidence);

        if (active.Mode == RetainerRpcProbeMode.ColdKind4Control &&
            active.Callbacks.Count == 1 &&
            active.Callbacks[0].Kind == 4)
        {
            lastRetainerRpcControl = new(
                active.TerritoryId,
                active.CharacterName,
                active.ClientVersion,
                active.Callbacks[0],
                lastRetainerRpcEvidencePath);
        }

        retainerRpcProbeSession = null;
        ReleaseAutoRetainerSuppression();
        log.Information(
            "[MarketMafioso] Retainer RPC probe completed: {Verdict}. Evidence: {EvidencePath}. {Message}",
            verdict,
            lastRetainerRpcEvidencePath ?? "(evidence write failed)",
            message);
    }

    private RetainerRpcComparison? CreateRetainerRpcComparison(RetainerRpcProbeSession active)
    {
        if (active.Mode != RetainerRpcProbeMode.Kind2Kind3Kind4)
            return null;

        var post = active.Callbacks.FindLast(static callback => callback.Kind == 4);
        if (lastRetainerRpcControl is not { } control)
        {
            return new(
                false,
                "No compatible in-memory control is available; run the control immediately before the bind test.",
                null,
                post,
                null);
        }

        var compatible =
            control.TerritoryId == active.TerritoryId &&
            string.Equals(control.CharacterName, active.CharacterName, StringComparison.Ordinal) &&
            string.Equals(control.ClientVersion, active.ClientVersion, StringComparison.Ordinal);
        if (!compatible)
        {
            return new(
                false,
                "The prior control belongs to a different character, territory, or client build.",
                control.Callback,
                post,
                control.EvidencePath);
        }

        var same =
            post is not null &&
            (control.Callback.ResponsePointer == "0x0000000000000000") ==
            (post.ResponsePointer == "0x0000000000000000") &&
            control.Callback.FirstUInt32 == post.FirstUInt32;
        return new(
            true,
            same
                ? "Cold and post-kind-3 kind-4 observations match on nullability and first response word."
                : "Cold and post-kind-3 kind-4 observations differ; inspect the exact callback records.",
            control.Callback,
            post,
            control.EvidencePath);
    }

    private string DescribeRetainerRpcComparison(
        RetainerRpcProbeSession active,
        RetainerRpcCallbackObservation post)
    {
        var comparison = CreateRetainerRpcComparison(active);
        return comparison is null
            ? "Post-kind-3 kind-4 callback captured."
            : comparison.Summary;
    }

    private unsafe RetainerRpcStageEvidence CaptureRetainerRpcStage(
        string label,
        RetainerRpcCallbackObservation? callback)
    {
        var manager = RetainerManager.Instance();
        var ids = new List<string>();
        if (manager != null && manager->IsReady)
        {
            var count = manager->GetRetainerCount();
            for (uint index = 0;
                 index < count && ids.Count < MaximumRetainerRpcRosterEntries;
                 index++)
            {
                var retainer = manager->GetRetainerBySortedIndex(index);
                if (retainer != null && retainer->RetainerId != 0)
                    ids.Add($"0x{retainer->RetainerId:X16}");
            }
        }

        return new(
            DateTimeOffset.UtcNow,
            label,
            callback,
            manager != null,
            manager != null && manager->IsReady,
            manager == null ? (byte)0 : manager->MaxRetainerEntitlement,
            manager == null ? 0U : checked((uint)manager->GetRetainerCount()),
            manager == null ? "0x0000000000000000" : $"0x{manager->LastSelectedRetainerId:X16}",
            manager == null ? "0x00000000" : $"0x{manager->RetainerObjectId:X8}",
            ids.ToArray(),
            CaptureNormalState(0));
    }

    private string? WriteRetainerRpcEvidence(RetainerRpcProbeEvidence evidence)
    {
        try
        {
            Directory.CreateDirectory(evidenceDirectory);
            var mode = evidence.Mode == nameof(RetainerRpcProbeMode.ColdKind4Control)
                ? "retainer-rpc-control"
                : "retainer-rpc-bind";
            var path = Path.Combine(
                evidenceDirectory,
                $"{mode}-{evidence.StartedAtUtc:yyyyMMdd-HHmmss-fff}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(evidence, JsonOptions));
            return path;
        }
        catch (Exception ex)
        {
            log.Error(ex, "[MarketMafioso] Unable to write retainer RPC probe evidence.");
            return null;
        }
    }

    private void DisposeRetainerRpcProbe()
    {
        if (retainerRpcProbeSession is { } active)
        {
            FinalizeRetainerRpcCallbackRegistration();
            active.Stages.Add(CaptureRetainerRpcStage("Plugin disposal cleanup", null));
            _ = WriteRetainerRpcEvidence(new(
                active.StartedAtUtc,
                DateTimeOffset.UtcNow,
                active.TerritoryId,
                active.CharacterName,
                active.ClientVersion,
                active.Mode.ToString(),
                active.RetainerId == 0 ? null : $"0x{active.RetainerId:X16}",
                "Disposed",
                "Plugin disposal finalized the outstanding callback registration.",
                active.StartPosition,
                CapturePosition(),
                FormatPointerValue(sigScanner.Module.BaseAddress),
                $"0x{ServerRequestCallbackManagerGetRva:X}",
                $"0x{ServerRequestCallbackManagerRequestRva:X}",
                $"0x{RetainerManagerRequestListRva:X}",
                $"0x{RetainerManagerRequestSingleDataRva:X}",
                $"0x{ServerRequestCallbackInterfaceFinalizeRva:X}",
                active.Requests.ToArray(),
                active.Callbacks.ToArray(),
                active.Stages.ToArray(),
                CreateRetainerRpcComparison(active)));
            retainerRpcProbeSession = null;
        }

        FreeRetainerRpcCallbackStorage();
    }

    private void FreeRetainerRpcCallbackStorage()
    {
        if (retainerRpcCallbackObject != 0)
        {
            Marshal.FreeHGlobal(retainerRpcCallbackObject);
            retainerRpcCallbackObject = 0;
        }
        if (retainerRpcCallbackVtable != 0)
        {
            Marshal.FreeHGlobal(retainerRpcCallbackVtable);
            retainerRpcCallbackVtable = 0;
        }
        if (retainerRpcOwnerHandleAllocated)
        {
            retainerRpcOwnerHandle.Free();
            retainerRpcOwnerHandleAllocated = false;
        }
    }

    private static string GetCurrentClientVersion()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            return "unknown";

        var versionPath = Path.Combine(
            Path.GetDirectoryName(processPath) ?? string.Empty,
            "ffxivgame.ver");
        if (File.Exists(versionPath))
            return File.ReadAllText(versionPath).Trim();

        return FileVersionInfo.GetVersionInfo(processPath).FileVersion ?? "unknown";
    }

    private static string FormatPointerValue(nint pointer) =>
        $"0x{unchecked((ulong)pointer):X16}";

    private enum RetainerRpcProbeMode
    {
        ColdKind4Control,
        Kind2Kind3Kind4,
    }

    private enum RetainerRpcProbeStage
    {
        AwaitingColdKind4,
        AwaitingKind2,
        AwaitingKind3,
        AwaitingPostKind4,
    }

    private sealed class RetainerRpcProbeSession
    {
        public RetainerRpcProbeSession(
            RetainerRpcProbeMode mode,
            RetainerRpcProbeStage stage,
            DateTimeOffset startedAtUtc,
            DateTimeOffset deadlineUtc,
            uint territoryId,
            string characterName,
            string clientVersion,
            ProbePosition? startPosition)
        {
            Mode = mode;
            Stage = stage;
            StartedAtUtc = startedAtUtc;
            DeadlineUtc = deadlineUtc;
            TerritoryId = territoryId;
            CharacterName = characterName;
            ClientVersion = clientVersion;
            StartPosition = startPosition;
        }

        public RetainerRpcProbeMode Mode { get; }
        public RetainerRpcProbeStage Stage { get; set; }
        public DateTimeOffset StartedAtUtc { get; }
        public DateTimeOffset DeadlineUtc { get; set; }
        public uint TerritoryId { get; }
        public string CharacterName { get; }
        public string ClientVersion { get; }
        public ProbePosition? StartPosition { get; }
        public ulong RetainerId { get; set; }
        public List<RetainerRpcRequestObservation> Requests { get; } = [];
        public List<RetainerRpcCallbackObservation> Callbacks { get; } = [];
        public List<RetainerRpcStageEvidence> Stages { get; } = [];
    }

    private sealed record RetainerRpcRequestObservation(
        DateTimeOffset SubmittedAtUtc,
        uint Kind,
        uint Argument1,
        uint Argument2);

    private sealed record RetainerRpcCallbackObservation(
        DateTimeOffset ReceivedAtUtc,
        uint Kind,
        string CallbackPointer,
        string ResponsePointer,
        uint? FirstUInt32);

    private sealed record RetainerRpcStageEvidence(
        DateTimeOffset CapturedAtUtc,
        string Label,
        RetainerRpcCallbackObservation? Callback,
        bool RetainerManagerAvailable,
        bool RetainerManagerIsReady,
        byte MaxRetainerEntitlement,
        uint RetainerCount,
        string LastSelectedRetainerId,
        string RetainerObjectId,
        string[] RosterRetainerIds,
        NormalBellClientState ClientState);

    private sealed record RetainerRpcControlReference(
        uint TerritoryId,
        string CharacterName,
        string ClientVersion,
        RetainerRpcCallbackObservation Callback,
        string? EvidencePath);

    private sealed record RetainerRpcComparison(
        bool CompatibleControl,
        string Summary,
        RetainerRpcCallbackObservation? Control,
        RetainerRpcCallbackObservation? PostKind3,
        string? ControlEvidencePath);

    private sealed record RetainerRpcProbeEvidence(
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        uint TerritoryId,
        string CharacterName,
        string ClientVersion,
        string Mode,
        string? RetainerId,
        string Verdict,
        string Message,
        ProbePosition? StartPosition,
        ProbePosition? ConclusionPosition,
        string ModuleBase,
        string CallbackManagerGetRva,
        string RequestRva,
        string RequestListRva,
        string RequestSingleDataRva,
        string CallbackFinalizerRva,
        RetainerRpcRequestObservation[] Requests,
        RetainerRpcCallbackObservation[] Callbacks,
        RetainerRpcStageEvidence[] Stages,
        RetainerRpcComparison? ControlComparison);
}
