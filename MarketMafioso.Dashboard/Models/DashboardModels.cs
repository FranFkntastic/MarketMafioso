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

public sealed record MarketAcquisitionRequestView
{
    public string Id { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
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
    public int ExpiresInSeconds { get; init; } = 300;
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
