namespace MarketMafioso.Server;

public sealed record MarketAcquisitionAttemptEventRequest
{
    public string ClaimToken { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string PluginInstanceId { get; init; } = string.Empty;
    public string? RunnerState { get; init; }
    public string? Message { get; init; }
    public string? Reason { get; init; }
    public string AttemptId { get; init; } = string.Empty;
    public long EventSequence { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Phase { get; init; } = string.Empty;
    public string? RouteStopId { get; init; }
    public string? WorldName { get; init; }
    public string? PluginVersion { get; init; }
    public DateTimeOffset ClientTimestampUtc { get; init; }
}

public sealed record MarketAcquisitionAttemptEventResult
{
    public MarketAcquisitionRequestView Request { get; init; } = new();
    public string Result { get; init; } = MarketAcquisitionAttemptEventResults.Accepted;
    public string? Reason { get; init; }
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

public static class MarketAcquisitionAttemptEventResults
{
    public const string Accepted = "accepted";
    public const string Replayed = "replayed";
    public const string StaleAttempt = "stale_attempt";
    public const string RequestTerminal = "request_terminal";
}
