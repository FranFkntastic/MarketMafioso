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

public sealed record MarketAcquisitionMergeSelection(
    MarketAcquisitionRequestView Target,
    MarketAcquisitionRequestView Source);

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
    public string ServiceAccountGroup { get; init; } = "Awaiting account evidence";
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

public record ClientCredentialView
{
    public long Id { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public string KeyPrefix { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? LastUsedAtUtc { get; init; }
    public DateTimeOffset? RevokedAtUtc { get; init; }
}

public sealed record ClientCredentialCreatedView : ClientCredentialView
{
    public string Secret { get; init; } = string.Empty;
}

public sealed record ClientCredentialCreateRequest
{
    public string Label { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
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
