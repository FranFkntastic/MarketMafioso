using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.AgentBridge;

public sealed record AgentBridgeTruth
{
    public required int SchemaVersion { get; init; }
    public required string PluginInstanceId { get; init; }
    public required int ProcessId { get; init; }
    public required string PluginVersion { get; init; }
    public required string CharacterName { get; init; }
    public required string CurrentWorld { get; init; }
    public required string HomeWorld { get; init; }
    public required bool MainWindowOpen { get; init; }
    public required bool MainWindowPinned { get; init; }
    public required bool AcquisitionDiagnosticsOpen { get; init; }
    public required string WorkspaceStatus { get; init; }
    public required bool WorkspaceBusy { get; init; }
    public required string? ClaimedRequestId { get; init; }
    public required string? PreparedPlanStatus { get; init; }
    public required AgentBridgeRemoteBellProbeTruth RemoteBellProbe { get; init; }
    public required AgentBridgeRouteTruth Route { get; init; }
}

public sealed record AgentBridgeRemoteBellProbeTruth
{
    public required bool Active { get; init; }
    public required bool CanSubmit { get; init; }
    public required string State { get; init; }
    public required string Message { get; init; }
    public required string Readiness { get; init; }
    public required string? BellGameObjectId { get; init; }
    public required float? Distance { get; init; }
    public required float? OrdinaryInteractionDistance { get; init; }
    public required string? LastEvidencePath { get; init; }
    public bool NormalCaptureActive { get; init; }
    public bool NormalCaptureCanArm { get; init; }
    public string? NormalCaptureState { get; init; }
    public string? NormalCaptureMessage { get; init; }
    public string? NormalCaptureReadiness { get; init; }
    public string? NormalCaptureLastEvidencePath { get; init; }
    public bool YieldProbeActive { get; init; }
    public bool YieldProbeCanArmControl { get; init; }
    public bool YieldProbeCanReplaySessionFree { get; init; }
    public string? YieldProbeMode { get; init; }
    public string? YieldProbeState { get; init; }
    public string? YieldProbeMessage { get; init; }
    public string? YieldProbeReadiness { get; init; }
    public string? YieldProbeRetainerId { get; init; }
    public string? YieldProbeOpcode { get; init; }
    public string? YieldProbeLastEvidencePath { get; init; }
    public bool WarmSessionActive { get; init; }
    public bool WarmSessionCanArm { get; init; }
    public string? WarmSessionState { get; init; }
    public string? WarmSessionMessage { get; init; }
    public string? WarmSessionReadiness { get; init; }
    public string? WarmSessionRetainerId { get; init; }
    public string? WarmSessionOpcode { get; init; }
    public string? WarmSessionLastEvidencePath { get; init; }
}

public sealed record AgentBridgeRouteTruth
{
    public required string State { get; init; }
    public required string StatusMessage { get; init; }
    public required string VisibleStatus { get; init; }
    public required bool IsActive { get; init; }
    public required bool IsRunning { get; init; }
    public required bool IsPaused { get; init; }
    public required string? ActiveWorld { get; init; }
    public required string? ActiveStopStatus { get; init; }
    public required string? ActiveOperationId { get; init; }
    public required string? ActiveOperationKind { get; init; }
    public required string? ActiveOperationPhase { get; init; }
    public required string? ActiveOperationDisposition { get; init; }
    public required int StopCount { get; init; }
    public required int CompletedOrProbedStopCount { get; init; }
    public string? ExecutionMode { get; init; }
    public string? ArmedExactAcquisitionDryRunScenario { get; init; }
    public bool ExactAcquisitionDryRunFaultEligible { get; init; }
    public bool ExactAcquisitionDryRunFaultInjected { get; init; }
    public string? ExactAcquisitionPhase { get; init; }
    public string? ExactAcquisitionMessage { get; init; }
    public int PersistedExactAcquisitionSunkReceiptCount { get; init; }
    public ulong PersistedExactAcquisitionSunkQuantity { get; init; }
    public ulong PersistedExactAcquisitionSunkGil { get; init; }
    public ulong ActiveExactAcquisitionRemainingQuantity { get; init; }
    public ulong ActiveExactAcquisitionRemainingGil { get; init; }
}

public static class AgentBridgeRouteTruthProjection
{
    public static ulong ResolveActiveExactAcquisitionRemainingGil(MarketAcquisitionRouteEngineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot is not { IsRouteActive: true, ExactAcquisitionExecution: { } execution, ActivePlan: { } plan })
            return 0;

        var lineIds = execution.Lines.Select(line => line.LineId).ToHashSet(StringComparer.Ordinal);
        return plan.WorldBatches
            .SelectMany(batch => batch.ItemSubtasks)
            .Where(subtask => lineIds.Contains(subtask.LineId))
            .SelectMany(subtask => subtask.Listings)
            .Aggregate(0ul, (sum, listing) => checked(sum + listing.TotalGil));
    }
}

public sealed record AgentBridgeProofReceipt
{
    public required int SchemaVersion { get; init; }
    public required long Revision { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required string ProofId { get; init; }
    public required string Challenge { get; init; }
    public required string TruthSha256 { get; init; }
    public required string ProofSha256 { get; init; }
    public required bool PresentedInGame { get; init; }
    public required AgentBridgeTruth Truth { get; init; }
}

public static class AgentBridgeProofFactory
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static AgentBridgeProofReceipt Create(
        AgentBridgeTruth truth,
        long revision,
        string? challenge = null,
        DateTimeOffset? capturedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(truth);
        if (revision < 1)
            throw new ArgumentOutOfRangeException(nameof(revision));

        var canonicalTruth = JsonSerializer.Serialize(truth, CanonicalJsonOptions);
        var truthHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalTruth)));
        var capturedAt = capturedAtUtc ?? DateTimeOffset.UtcNow;
        var proofId = Guid.NewGuid().ToString("N");
        var normalizedChallenge = challenge ?? string.Empty;
        var canonicalProof = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            ProofId = proofId,
            Revision = revision,
            CapturedAtUtc = capturedAt,
            Challenge = normalizedChallenge,
            TruthSha256 = truthHash,
            truth.PluginInstanceId,
            truth.ProcessId,
        }, CanonicalJsonOptions);
        var proofHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalProof)));
        return new AgentBridgeProofReceipt
        {
            SchemaVersion = 1,
            Revision = revision,
            CapturedAtUtc = capturedAt,
            ProofId = proofId,
            Challenge = normalizedChallenge,
            TruthSha256 = truthHash,
            ProofSha256 = proofHash,
            PresentedInGame = false,
            Truth = truth,
        };
    }

    public static string Serialize(AgentBridgeProofReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return JsonSerializer.Serialize(receipt, CanonicalJsonOptions);
    }
}
