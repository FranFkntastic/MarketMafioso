namespace MarketMafioso.Dashboard.Models;

public sealed record DashboardSessionResponse
{
    public DashboardUser User { get; init; } = new();
    public DateTimeOffset ExpiresAtUtc { get; init; }
}

public sealed record DashboardUser
{
    public long UserId { get; init; }
    public string Username { get; init; } = string.Empty;
}

public sealed record ReceiverHealthView
{
    public bool Ok { get; init; }
    public DateTimeOffset Utc { get; init; }
}

public sealed record DashboardFeatureFlagsView
{
    public bool EnableMarketAcquisition { get; init; }
}

public sealed record ReceiverStorageSummaryView
{
    public int SnapshotRetentionCount { get; init; }
    public int RawJsonRetentionCount { get; init; }
    public int DiagnosticEventRetentionCount { get; init; }
    public int SnapshotCount { get; init; }
    public int RawJsonRetainedCount { get; init; }
    public int RawJsonPrunedCount { get; init; }
    public int DiagnosticEventCount { get; init; }
    public DateTimeOffset? NewestSnapshotReceivedAtUtc { get; init; }
    public DateTimeOffset? OldestSnapshotReceivedAtUtc { get; init; }
    public string AcquisitionSseEndpoint { get; init; } = "api/events/stream";
    public string DiagnosticsSseEndpoint { get; init; } = "api/diagnostics/events/stream";
    public int AcquisitionSseCadenceSeconds { get; init; } = 3;
}

public sealed record MarketAcquisitionRequestView
{
    public string Id { get; init; } = string.Empty;
    public int Revision { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Origin { get; init; } = MarketAcquisitionOrigins.DashboardCreated;
    public string? CreatedByPluginInstanceId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
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

public sealed record MarketAcquisitionRequestTimelineView
{
    public MarketAcquisitionRequestView Request { get; init; } = new();
    public IReadOnlyList<MarketAcquisitionLifecycleEventView> LifecycleEvents { get; init; } = [];
    public IReadOnlyList<MarketAcquisitionAttemptEventView> AttemptEvents { get; init; } = [];
}

public sealed record MarketAcquisitionLifecycleEventView
{
    public string EventType { get; init; } = string.Empty;
    public string ResultStatus { get; init; } = string.Empty;
    public string? RunnerState { get; init; }
    public string? Message { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record MarketAcquisitionAttemptEventView
{
    public string AttemptId { get; init; } = string.Empty;
    public long Sequence { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Phase { get; init; } = string.Empty;
    public string? RouteStopId { get; init; }
    public string? WorldName { get; init; }
    public string Result { get; init; } = string.Empty;
    public string? RunnerState { get; init; }
    public string? Message { get; init; }
    public string? Reason { get; init; }
    public string? PluginVersion { get; init; }
    public DateTimeOffset? ClientTimestampUtc { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record MarketAcquisitionCreateRequest
{
    public int SchemaVersion { get; init; } = 1;
    public string IdempotencyKey { get; init; } = Guid.NewGuid().ToString("N");
    public string TargetCharacterName { get; init; } = string.Empty;
    public string TargetWorld { get; init; } = string.Empty;
    public string Region { get; init; } = "North America";
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string QuantityMode { get; init; } = "TargetQuantity";
    public uint Quantity { get; init; }
    public string HqPolicy { get; init; } = "Either";
    public uint MaxUnitPrice { get; init; }
    public uint MaxTotalGil { get; init; }
    public string WorldMode { get; init; } = "Recommended";
    public string SweepScope { get; init; } = "Region";
    public IReadOnlyList<string> SweepDataCenters { get; init; } = [];
    public int ExpiresInSeconds { get; init; } = 300;
}

public sealed record MarketAcquisitionBatchCreateRequest
{
    public int SchemaVersion { get; init; } = 1;
    public string IdempotencyKey { get; init; } = Guid.NewGuid().ToString("N");
    public string TargetCharacterName { get; init; } = string.Empty;
    public string TargetWorld { get; init; } = string.Empty;
    public string Region { get; init; } = "North America";
    public string WorldMode { get; init; } = "Recommended";
    public string SweepScope { get; init; } = "Region";
    public IReadOnlyList<string> SweepDataCenters { get; init; } = [];
    public int ExpiresInSeconds { get; init; } = 300;
    public IReadOnlyList<MarketAcquisitionBatchLineCreateRequest> Lines { get; init; } = [];
}

public sealed record MarketAcquisitionBatchAppendLinesRequest
{
    public int ExpectedRevision { get; init; }
    public int ExpiresInSeconds { get; init; } = 300;
    public IReadOnlyList<MarketAcquisitionBatchLineCreateRequest> Lines { get; init; } = [];
}

public sealed record MarketAcquisitionBatchLineCreateRequest
{
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string? ItemKind { get; init; }
    public string QuantityMode { get; init; } = "AllBelowThreshold";
    public uint TargetQuantity { get; init; }
    public uint MaxQuantity { get; init; }
    public string HqPolicy { get; init; } = "Either";
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

public static class MarketAcquisitionOrigins
{
    public const string DashboardCreated = "DashboardCreated";
    public const string ClientQuickShop = "ClientQuickShop";
}

public sealed record XivItemSearchResult
{
    public uint ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Type { get; init; }

    public override string ToString() => string.IsNullOrWhiteSpace(Type)
        ? $"{Name} ({ItemId})"
        : $"{Name} ({ItemId}) - {Type}";
}

public sealed record DashboardCharacterOption
{
    public long Id { get; init; }
    public string CharacterName { get; init; } = string.Empty;
    public string? HomeWorld { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
    public string DisplayName { get; init; } = string.Empty;
}

public sealed record InventoryBrowserView
{
    public string? SnapshotId { get; init; }
    public DateTimeOffset? ReceivedAt { get; init; }
    public string? CharacterName { get; init; }
    public string? HomeWorld { get; init; }
    public string Search { get; init; } = string.Empty;
    public string Scope { get; init; } = "all";
    public IReadOnlyList<InventoryBrowserItemView> Items { get; init; } = [];
    public IReadOnlyList<InventoryBrowserScopeView> Scopes { get; init; } = [];
    public IReadOnlyList<InventoryBrowserMarketListingView> MarketListings { get; init; } = [];
    public int TotalQuantity { get; init; }
    public int HqQuantity { get; init; }
    public int OwnerCount { get; init; }
    public ulong RetainerGil { get; init; }
}

public sealed record InventoryBrowserItemView
{
    public uint ItemId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? ItemType { get; init; }
    public string? IconUrl { get; init; }
    public int TotalQuantity { get; init; }
    public int HqQuantity { get; init; }
    public IReadOnlyList<InventoryBrowserLocationView> Locations { get; init; } = [];
    public int OwnerCount { get; init; }
}

public sealed record InventoryBrowserLocationView
{
    public string OwnerName { get; init; } = string.Empty;
    public string? OwnerCharacterName { get; init; }
    public string? OwnerHomeWorld { get; init; }
    public string BagName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public int HqQuantity { get; init; }
}

public sealed record InventoryBrowserScopeView
{
    public string ScopeKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? OwnerCharacterName { get; init; }
    public string? OwnerHomeWorld { get; init; }
    public int StackCount { get; init; }
    public ulong Gil { get; init; }
    public int MarketListingCount { get; init; }
    public string? LastUpdated { get; init; }
}

public sealed record InventoryBrowserMarketListingView
{
    public string OwnerName { get; init; } = string.Empty;
    public string? OwnerCharacterName { get; init; }
    public string? OwnerHomeWorld { get; init; }
    public uint ItemId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? ItemType { get; init; }
    public string? IconUrl { get; init; }
    public int Quantity { get; init; }
    public int HqQuantity { get; init; }
    public uint? UnitPrice { get; init; }
    public string? ListedAt { get; init; }
}

public sealed record ReportSummaryView
{
    public string Id { get; init; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; init; }
    public string? CharacterName { get; init; }
    public string? HomeWorld { get; init; }
    public string ReportTimestamp { get; init; } = string.Empty;
    public int PlayerBagCount { get; init; }
    public int PlayerItemStacks { get; init; }
    public int PlayerItemQuantity { get; init; }
    public int RetainerCount { get; init; }
    public int RetainerItemStacks { get; init; }
    public int RetainerItemQuantity { get; init; }
}

public sealed record DashboardSettingsView
{
    public int SchemaVersion { get; init; } = 1;
    public long? DefaultCharacterId { get; init; }
    public string DefaultRegion { get; init; } = "North America";
    public string DefaultWorldMode { get; init; } = "Recommended";
    public int DefaultPickupExpiresSeconds { get; init; } = 300;
    public DateTimeOffset? UpdatedAtUtc { get; init; }
}

public sealed record DashboardSettingsUpdate
{
    public long? DefaultCharacterId { get; init; }
    public string DefaultRegion { get; init; } = "North America";
    public string DefaultWorldMode { get; init; } = "Recommended";
    public int DefaultPickupExpiresSeconds { get; init; } = 300;
}

public sealed record DiagnosticEventView
{
    public long Id { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; }
    public string Source { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string? Outcome { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
    public string? AcquisitionRequestId { get; init; }
    public string? PayloadSummaryJson { get; init; }
}
