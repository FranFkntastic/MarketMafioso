namespace MarketMafioso.Server;

public record DiagnosticEventCreate
{
    public DateTimeOffset? OccurredAtUtc { get; init; }
    public string Source { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Severity { get; init; } = "Info";
    public string? Outcome { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
    public long? AccountId { get; init; }
    public long? DashboardUserId { get; init; }
    public string? DashboardSessionId { get; init; }
    public string? PluginInstanceId { get; init; }
    public string? AcquisitionRequestId { get; init; }
    public string? RouteRunId { get; init; }
    public string? RouteStopId { get; init; }
    public string? PurchaseAttemptId { get; init; }
    public string? SnapshotId { get; init; }
    public uint? ItemId { get; init; }
    public string? ItemName { get; init; }
    public string? World { get; init; }
    public string? CharacterName { get; init; }
    public string? HttpMethod { get; init; }
    public string? RoutePattern { get; init; }
    public int? StatusCode { get; init; }
    public long? DurationMs { get; init; }
    public string? ExceptionType { get; init; }
    public string? ExceptionMessage { get; init; }
    public string? PayloadSummaryJson { get; init; }
    public string? PayloadRawJson { get; init; }
    public long? PayloadSizeBytes { get; init; }
    public string? PayloadSha256 { get; init; }
}

public sealed record DiagnosticEventView : DiagnosticEventCreate
{
    public long Id { get; init; }
    public DateTimeOffset ReceivedAtUtc { get; init; }
    public new DateTimeOffset OccurredAtUtc { get; init; }
}
