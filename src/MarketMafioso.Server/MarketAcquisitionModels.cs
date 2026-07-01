namespace MarketMafioso.Server;

public sealed record MarketAcquisitionCreateRequest
{
    public int SchemaVersion { get; init; }
    public string IdempotencyKey { get; init; } = string.Empty;
    public string TargetCharacterName { get; init; } = string.Empty;
    public string TargetWorld { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string QuantityMode { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public string HqPolicy { get; init; } = string.Empty;
    public uint MaxUnitPrice { get; init; }
    public uint MaxTotalGil { get; init; }
    public string WorldMode { get; init; } = string.Empty;
    public string SweepScope { get; init; } = "Region";
    public IReadOnlyList<string> SweepDataCenters { get; init; } = [];
    public int ExpiresInSeconds { get; init; } = 90;
}

public sealed record MarketAcquisitionBatchCreateRequest
{
    public int SchemaVersion { get; init; }
    public string IdempotencyKey { get; init; } = string.Empty;
    public string TargetCharacterName { get; init; } = string.Empty;
    public string TargetWorld { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public string WorldMode { get; init; } = string.Empty;
    public string SweepScope { get; init; } = "Region";
    public IReadOnlyList<string> SweepDataCenters { get; init; } = [];
    public int ExpiresInSeconds { get; init; } = 90;
    public IReadOnlyList<MarketAcquisitionBatchLineCreateRequest> Lines { get; init; } = [];
}

public sealed record MarketAcquisitionBatchAppendLinesRequest
{
    public int ExpectedRevision { get; init; }
    public int ExpiresInSeconds { get; init; } = 90;
    public IReadOnlyList<MarketAcquisitionBatchLineCreateRequest> Lines { get; init; } = [];
}

public sealed record MarketAcquisitionBatchLineCreateRequest
{
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string? ItemKind { get; init; }
    public string QuantityMode { get; init; } = string.Empty;
    public uint TargetQuantity { get; init; }
    public uint MaxQuantity { get; init; }
    public string HqPolicy { get; init; } = string.Empty;
    public uint MaxUnitPrice { get; init; }
    public uint GilCap { get; init; }
}

public sealed record MarketAcquisitionBatchLineView
{
    public string LineId { get; init; } = string.Empty;
    public string BatchId { get; init; } = string.Empty;
    public int Ordinal { get; init; }
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string? ItemKind { get; init; }
    public string QuantityMode { get; init; } = string.Empty;
    public uint TargetQuantity { get; init; }
    public uint MaxQuantity { get; init; }
    public string HqPolicy { get; init; } = string.Empty;
    public uint MaxUnitPrice { get; init; }
    public uint GilCap { get; init; }
    public string Status { get; init; } = string.Empty;
    public uint PurchasedQuantity { get; init; }
    public uint SpentGil { get; init; }
    public string? LatestMessage { get; init; }
}

public sealed record MarketAcquisitionClaimRequest
{
    public string CharacterName { get; init; } = string.Empty;
    public string World { get; init; } = string.Empty;
    public string PluginInstanceId { get; init; } = string.Empty;
}

public sealed record MarketAcquisitionClaimTokenRequest
{
    public string ClaimToken { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
}

public sealed record MarketAcquisitionLifecycleRequest
{
    public string ClaimToken { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string? RunnerState { get; init; }
    public string? Message { get; init; }
    public string? Reason { get; init; }
}

public sealed record MarketAcquisitionLineProgressRequest
{
    public string ClaimToken { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string AttemptId { get; init; } = string.Empty;
    public long Sequence { get; init; }
    public string Status { get; init; } = string.Empty;
    public uint PurchasedQuantity { get; init; }
    public uint SpentGil { get; init; }
    public string? Message { get; init; }
    public string? Reason { get; init; }
}

public sealed record MarketAcquisitionPurchaseAuditRequest
{
    public string ClaimToken { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string AttemptId { get; init; } = string.Empty;
    public long Sequence { get; init; }
    public string LineId { get; init; } = string.Empty;
    public string WorldName { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string ListingId { get; init; } = string.Empty;
    public string RetainerName { get; init; } = string.Empty;
    public string RetainerId { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public uint UnitPrice { get; init; }
    public uint TotalGil { get; init; }
    public bool IsHq { get; init; }
    public string Result { get; init; } = string.Empty;
    public string? Message { get; init; }
}

public sealed record MarketAcquisitionPurchaseAuditView
{
    public string AuditId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string LineId { get; init; } = string.Empty;
    public string AttemptId { get; init; } = string.Empty;
    public long Sequence { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string ListingId { get; init; } = string.Empty;
    public string RetainerName { get; init; } = string.Empty;
    public string RetainerId { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public uint UnitPrice { get; init; }
    public uint TotalGil { get; init; }
    public bool IsHq { get; init; }
    public string Result { get; init; } = string.Empty;
    public string? Message { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record MarketAcquisitionRequestView
{
    public string Id { get; init; } = string.Empty;
    public int Revision { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public DateTimeOffset? ClaimedAtUtc { get; init; }
    public DateTimeOffset? ClaimExpiresAtUtc { get; init; }
    public string TargetCharacterName { get; init; } = string.Empty;
    public string TargetWorld { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string QuantityMode { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public string HqPolicy { get; init; } = string.Empty;
    public uint MaxUnitPrice { get; init; }
    public uint MaxTotalGil { get; init; }
    public string WorldMode { get; init; } = string.Empty;
    public string SweepScope { get; init; } = "Region";
    public IReadOnlyList<string> SweepDataCenters { get; init; } = [];
    public string? LatestEventType { get; init; }
    public string? LatestRunnerState { get; init; }
    public string? LatestMessage { get; init; }
    public string? LatestReason { get; init; }
    public DateTimeOffset? LatestEventAtUtc { get; init; }
    public string? LatestAttemptId { get; init; }
    public long? LatestAttemptSequence { get; init; }
    public string? LatestAttemptEventType { get; init; }
    public string? LatestAttemptPhase { get; init; }
    public string? LatestAttemptWorld { get; init; }
    public string? LatestAttemptResult { get; init; }
    public string? LatestAttemptPluginVersion { get; init; }
    public IReadOnlyList<MarketAcquisitionBatchLineView> Lines { get; init; } = [];
}

public sealed record MarketAcquisitionClaimView
{
    public string Id { get; init; } = string.Empty;
    public int Revision { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public DateTimeOffset? ClaimedAtUtc { get; init; }
    public DateTimeOffset? ClaimExpiresAtUtc { get; init; }
    public string TargetCharacterName { get; init; } = string.Empty;
    public string TargetWorld { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string QuantityMode { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public string HqPolicy { get; init; } = string.Empty;
    public uint MaxUnitPrice { get; init; }
    public uint MaxTotalGil { get; init; }
    public string WorldMode { get; init; } = string.Empty;
    public string SweepScope { get; init; } = "Region";
    public IReadOnlyList<string> SweepDataCenters { get; init; } = [];
    public string? LatestEventType { get; init; }
    public string? LatestRunnerState { get; init; }
    public string? LatestMessage { get; init; }
    public string? LatestReason { get; init; }
    public DateTimeOffset? LatestEventAtUtc { get; init; }
    public string? LatestAttemptId { get; init; }
    public long? LatestAttemptSequence { get; init; }
    public string? LatestAttemptEventType { get; init; }
    public string? LatestAttemptPhase { get; init; }
    public string? LatestAttemptWorld { get; init; }
    public string? LatestAttemptResult { get; init; }
    public string? LatestAttemptPluginVersion { get; init; }
    public IReadOnlyList<MarketAcquisitionBatchLineView> Lines { get; init; } = [];
    public string ClaimToken { get; init; } = string.Empty;
}

public sealed record MarketAcquisitionPendingResponse
{
    public IReadOnlyList<MarketAcquisitionRequestView> Requests { get; init; } = [];
}

public sealed record MarketAcquisitionBatchPendingResponse
{
    public IReadOnlyList<MarketAcquisitionRequestView> Batches { get; init; } = [];
}

public sealed record MarketAcquisitionCreateResult(MarketAcquisitionRequestView Request, bool IsReplay);

public static class MarketAcquisitionStatuses
{
    public const string PendingPickup = "PendingPickup";
    public const string Claimed = "Claimed";
    public const string AcceptedInPlugin = "AcceptedInPlugin";
    public const string Running = "Running";
    public const string Complete = "Complete";
    public const string Failed = "Failed";
    public const string Rejected = "Rejected";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";
}

public sealed class MarketAcquisitionIdempotencyConflictException : Exception
{
    public MarketAcquisitionIdempotencyConflictException()
        : base("Idempotency key was already used with a different request body.")
    {
    }
}

public sealed class MarketAcquisitionAttemptSequenceConflictException : Exception
{
    public MarketAcquisitionAttemptSequenceConflictException()
        : base("Attempt event sequence was already used with a different request body.")
    {
    }
}

public sealed class MarketAcquisitionInvalidTransitionException : Exception
{
    public MarketAcquisitionInvalidTransitionException(string status, string targetStatus)
        : base($"Cannot move acquisition request from {status} to {targetStatus}.")
    {
    }
}

public sealed class MarketAcquisitionRevisionConflictException : Exception
{
    public MarketAcquisitionRevisionConflictException(int expectedRevision, int actualRevision)
        : base($"Acquisition request revision changed from {expectedRevision} to {actualRevision}.")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public int ExpectedRevision { get; }

    public int ActualRevision { get; }
}

public sealed class MarketAcquisitionInvalidLineException : Exception
{
    public MarketAcquisitionInvalidLineException(string requestId, string lineId)
        : base($"Line {lineId} does not belong to acquisition request {requestId}.")
    {
    }
}
