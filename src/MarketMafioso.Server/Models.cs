using MarketMafioso.Contracts.Inventory;

namespace MarketMafioso.Server;

public sealed record StoredInventoryReport
{
    public string Id { get; init; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; init; }
    public string? ApiKeyLabel { get; init; }
    public InventoryReport Report { get; init; } = new();
    public ReportSummary Summary { get; init; } = new();
}

public sealed record ReportSummary
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

public sealed record ReceiverStorageSummaryView
{
    public int HistoryRetentionPerCharacter { get; init; }
    public int RawJsonRetentionCount { get; init; }
    public int DiagnosticEventRetentionCount { get; init; }
    public int SnapshotCount { get; init; }
    public int CurrentHeadCount { get; init; }
    public int HistoryCount { get; init; }
    public int RawJsonRetainedCount { get; init; }
    public int RawJsonPrunedCount { get; init; }
    public int DiagnosticEventCount { get; init; }
    public DateTimeOffset? NewestSnapshotReceivedAtUtc { get; init; }
    public DateTimeOffset? OldestSnapshotReceivedAtUtc { get; init; }
    public string AcquisitionSseEndpoint { get; init; } = "api/events/stream";
    public string DiagnosticsSseEndpoint { get; init; } = "api/diagnostics/events/stream";
    public int AcquisitionSseCadenceSeconds { get; init; } = 3;
}

public sealed record DashboardLoginRequest
{
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}
