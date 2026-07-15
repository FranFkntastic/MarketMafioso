namespace MarketMafioso.Server;

public static class MarketAcquisitionWorkOrderStates
{
    public const string Inbox = "Inbox";
    public const string Working = "Working";
    public const string Recovery = "Recovery";
    public const string Shelved = "Shelved";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public const string Archived = "Archived";
}

public sealed record MarketAcquisitionWorkOrderView
{
    public required string Id { get; init; }
    public required int Revision { get; init; }
    public required string State { get; init; }
    public required string Title { get; init; }
    public required int Priority { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? ShelvedAtUtc { get; init; }
    public DateTimeOffset? ArchivedAtUtc { get; init; }
    public string? ParentWorkOrderId { get; init; }
    public string? MergeSourceWorkOrderId { get; init; }
    public required MarketAcquisitionRequestView Request { get; init; }
}

public sealed record MarketAcquisitionWorkOrderCommand
{
    public int ExpectedRevision { get; init; }
}

public sealed record MarketAcquisitionWorkOrderCloneRequest
{
    public int ExpectedRevision { get; init; }
    public string IdempotencyKey { get; init; } = string.Empty;
    public string? Title { get; init; }
}

public sealed record MarketAcquisitionWorkOrderMergeRequest
{
    public string SourceWorkOrderId { get; init; } = string.Empty;
    public int ExpectedTargetRevision { get; init; }
    public int ExpectedSourceRevision { get; init; }
}

public sealed record MarketAcquisitionWorkOrderMergeConflict
{
    public required string Field { get; init; }
    public required string TargetValue { get; init; }
    public required string SourceValue { get; init; }
    public required string Message { get; init; }
}

public sealed record MarketAcquisitionWorkOrderMergePreview
{
    public required string TargetWorkOrderId { get; init; }
    public required string SourceWorkOrderId { get; init; }
    public bool CanMerge => Conflicts.Count == 0;
    public int ResultLineCount { get; init; }
    public IReadOnlyList<MarketAcquisitionWorkOrderMergeConflict> Conflicts { get; init; } = [];
}

public sealed record MarketAcquisitionLeaseRenewRequest
{
    public string ClaimToken { get; init; } = string.Empty;
    public string PluginInstanceId { get; init; } = string.Empty;
}

public sealed record MarketAcquisitionExecutionLeaseView
{
    public required string WorkOrderId { get; init; }
    public required string PluginInstanceId { get; init; }
    public required DateTimeOffset RenewedAtUtc { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
}

public sealed record MarketAcquisitionWorkOrderRevisionView
{
    public required string WorkOrderId { get; init; }
    public required int Revision { get; init; }
    public required string ChangeKind { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required MarketAcquisitionRequestView Snapshot { get; init; }
}

public sealed record MarketAcquisitionExecutionSnapshotView
{
    public required string SnapshotId { get; init; }
    public required string WorkOrderId { get; init; }
    public required int Revision { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required MarketAcquisitionRequestView Request { get; init; }
}

public sealed record MarketAcquisitionRunReceiptView
{
    public required string ReceiptId { get; init; }
    public required string WorkOrderId { get; init; }
    public required string Outcome { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required uint PurchasedQuantity { get; init; }
    public required ulong SpentGil { get; init; }
    public string? Message { get; init; }
}

public sealed record MarketAcquisitionWorkOrderHistoryView
{
    public required MarketAcquisitionWorkOrderView WorkOrder { get; init; }
    public IReadOnlyList<MarketAcquisitionWorkOrderRevisionView> Revisions { get; init; } = [];
    public IReadOnlyList<MarketAcquisitionExecutionSnapshotView> ExecutionSnapshots { get; init; } = [];
    public IReadOnlyList<MarketAcquisitionRunReceiptView> Receipts { get; init; } = [];
}
