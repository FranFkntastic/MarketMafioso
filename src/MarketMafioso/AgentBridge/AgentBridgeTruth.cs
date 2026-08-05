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
    public required bool MainWindowCollapseOverrideActive { get; init; }
    public required bool MainWindowPinned { get; init; }
    public required bool AcquisitionDiagnosticsOpen { get; init; }
    public required string WorkspaceStatus { get; init; }
    public required bool WorkspaceBusy { get; init; }
    public required string? ClaimedRequestId { get; init; }
    public required string? PreparedPlanStatus { get; init; }
    public AgentBridgeCraftAppraisalTruth? CraftAppraisal { get; init; }
    public AgentBridgeWorkshopRestockTruth? WorkshopRestock { get; init; }
    public required AgentBridgeTradeQueueTruth TradeQueue { get; init; }
    public required AgentBridgeRemoteMarketTruth RemoteMarket { get; init; }
    public required AgentBridgeRemoteBellProbeTruth RemoteBellProbe { get; init; }
    public required AgentBridgeRouteTruth Route { get; init; }
}

public sealed record AgentBridgeTradeQueueTruth
{
    public required string State { get; init; }
    public required string Message { get; init; }
    public string? RunId { get; init; }
    public required bool IsActive { get; init; }
    public required bool CanResume { get; init; }
    public string? PartnerName { get; init; }
    public required int BatchNumber { get; init; }
    public required int CompletedBatchCount { get; init; }
    public required int InitialUnitCount { get; init; }
    public required int CompletedUnitCount { get; init; }
    public required int RemainingLineCount { get; init; }
    public required int RemainingUnitCount { get; init; }
    public required bool QueueValid { get; init; }
    public required string QueueValidationMessage { get; init; }
    public required int ActionDelayMilliseconds { get; init; }
    public required int TradeRetryMilliseconds { get; init; }
    public required bool AutoAcceptIncomingTrades { get; init; }
    public required bool IsTradeOpen { get; init; }
    public required int OfferedSlotCount { get; init; }
    public required bool CanReceiverReady { get; init; }
    public required bool CanReceiverConfirm { get; init; }
    public required bool CanReceiverCancel { get; init; }
    public AgentBridgeTradePartnerTruth? SelectedPartner { get; init; }
    public IReadOnlyList<AgentBridgeTradePartnerTruth> AvailablePartners { get; init; } = [];
    public IReadOnlyList<AgentBridgeTradeQueueLineTruth> Queue { get; init; } = [];
}

public sealed record AgentBridgeTradePartnerTruth
{
    public required string Name { get; init; }
    public required string HomeWorld { get; init; }
    public required string GameObjectId { get; init; }
}

public sealed record AgentBridgeTradeQueueLineTruth
{
    public required uint ItemId { get; init; }
    public required string ItemName { get; init; }
    public required int Quantity { get; init; }
}

public sealed record AgentBridgeCraftAppraisalTruth
{
    public required bool IsFetching { get; init; }
    public required string Status { get; init; }
    public required bool WorkshopHostEnabled { get; init; }
    public required bool WorkshopHostAvailable { get; init; }
    public uint? SelectedItemId { get; init; }
    public string? SelectedItemName { get; init; }
    public uint? RequestedQuantity { get; init; }
    public string? HqPolicy { get; init; }
    public string? Region { get; init; }
    public required bool HasQuote { get; init; }
    public bool QuoteComplete { get; init; }
    public decimal? QuoteUnitCost { get; init; }
    public string? QuoteSource { get; init; }
    public string? QuoteConfidence { get; init; }
    public int WarningCount { get; init; }
    public string? PlanId { get; init; }
    public required bool CanOpenPlan { get; init; }
}

public sealed record AgentBridgeWorkshopRestockTruth
{
    public required string QueueSignature { get; init; }
    public required bool AutomaticallyBuyVendorMaterials { get; init; }
    public required int MaterialCount { get; init; }
    public required int ShortageLineCount { get; init; }
    public required int OrdinaryGilCatalogLineCount { get; init; }
    public required int AccessibleVendorLineCount { get; init; }
    public required int SelectedVendorLineCount { get; init; }
    public required int RetainerUnits { get; init; }
    public required int VendorUnits { get; init; }
    public required ulong MaximumGil { get; init; }
    public required int StopCount { get; init; }
    public IReadOnlyList<AgentBridgeWorkshopVendorLineTruth> VendorLines { get; init; } = [];
    public string? ActivePhase { get; init; }
    public string? ActiveMessage { get; init; }
    public int VerifiedReceiptCount { get; init; }
    public int VerifiedQuantity { get; init; }
    public ulong VerifiedGil { get; init; }
    public uint? ArmedItemId { get; init; }
}

public sealed record AgentBridgeWorkshopVendorLineTruth
{
    public required uint ItemId { get; init; }
    public required string ItemName { get; init; }
    public required int VendorNeed { get; init; }
    public required bool Selected { get; init; }
    public required int ApprovedQuantity { get; init; }
    public required uint UnitPriceGil { get; init; }
    public required uint NpcId { get; init; }
    public required string NpcName { get; init; }
    public required string AccessState { get; init; }
}

public sealed record AgentBridgeRemoteMarketTruth
{
    public required bool Available { get; init; }
    public required bool ResultVisible { get; init; }
    public bool NativeResultAddonVisible { get; init; }
    public bool NativeAgentActive { get; init; }
    public uint NativeListingCount { get; init; }
    public byte? NativeRequestId { get; init; }
    public required long ViewRevision { get; init; }
    public required int ListingCount { get; init; }
    public required uint? ItemId { get; init; }
    public required bool? HighQuality { get; init; }
    public required uint? CheapestUnitPrice { get; init; }
    public required ulong? CurrentGil { get; init; }
    public required string? MarketContextSource { get; init; }
    public required string? MarketContextSummary { get; init; }
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
    public bool PositionFrameOneShotPrepared { get; init; }
    public bool PositionFrameOneShotCanPrepare { get; init; }
    public bool PositionFrameOneShotCanFire { get; init; }
    public string? PositionFrameOneShotState { get; init; }
    public string? PositionFrameOneShotMessage { get; init; }
    public string? PositionFrameOneShotReadiness { get; init; }
    public DateTimeOffset? PositionFrameOneShotExpiresAtUtc { get; init; }
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
    public bool WarmSessionCanReplayHeld { get; init; }
    public string? WarmSessionMode { get; init; }
    public string? WarmSessionState { get; init; }
    public string? WarmSessionMessage { get; init; }
    public string? WarmSessionReadiness { get; init; }
    public double? WarmSessionHoldSeconds { get; init; }
    public float? WarmSessionDistanceMoved { get; init; }
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
    public int ReportBacklogEntryCount { get; init; }
    public int ReportBacklogRequestCount { get; init; }
    public int ReportBacklogInFlightRequestCount { get; init; }
    public DateTimeOffset? ReportBacklogOldestEnqueuedAtUtc { get; init; }
    public DateTimeOffset? ReportBacklogNextRetryAtUtc { get; init; }
    public string? ReportBacklogLastFailureKind { get; init; }
    public DateTimeOffset? ReportBacklogLastFailureAtUtc { get; init; }
    public int ReportQuarantinedEntryCount { get; init; }
    public string? ReportLastQuarantineStatus { get; init; }
    public DateTimeOffset? ReportLastQuarantineAtUtc { get; init; }
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
