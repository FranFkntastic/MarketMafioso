using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    public required bool AcquisitionDiagnosticsOpen { get; init; }
    public required string WorkspaceStatus { get; init; }
    public required bool WorkspaceBusy { get; init; }
    public required string? ClaimedRequestId { get; init; }
    public required string? PreparedPlanStatus { get; init; }
    public required AgentBridgeRouteTruth Route { get; init; }
    public AgentBridgeSquireTruth? Squire { get; init; }
}

public sealed record AgentBridgeSquireTruth
{
    public required bool HasSnapshot { get; init; }
    public required string Status { get; init; }
    public required string? CharacterName { get; init; }
    public required string? HomeWorldId { get; init; }
    public required DateTimeOffset? CapturedAtUtc { get; init; }
    public required bool IsComplete { get; init; }
    public required int UnlockedJobCount { get; init; }
    public required int ValidGearsetCount { get; init; }
    public required int InstanceCount { get; init; }
    public required int CandidateCount { get; init; }
    public required int ProtectedCount { get; init; }
    public required int EvaluationFailureCount { get; init; }
    public required int UnsupportedCount { get; init; }
    public required IReadOnlyList<string> BlockingDiagnostics { get; init; }
    public required IReadOnlyList<string> EvaluationFailureGroups { get; init; }
    public required IReadOnlyList<string> ProtectionGroups { get; init; }
    public required IReadOnlyList<AgentBridgeSquireCandidateTruth> ExecutableCandidates { get; init; }
}

public sealed record AgentBridgeSquireCandidateTruth
{
    public required uint ItemId { get; init; }
    public required string ItemName { get; init; }
    public required string Container { get; init; }
    public required int SlotIndex { get; init; }
    public required uint EquipLevel { get; init; }
    public required uint ItemLevel { get; init; }
    public required string RecommendedDisposition { get; init; }
    public required IReadOnlyList<string> ReasonCodes { get; init; }
    public required IReadOnlyList<string> JobComparisons { get; init; }
    public required string RevalidationCode { get; init; }
    public required bool RevalidationSucceeded { get; init; }
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
