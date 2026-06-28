using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MarketMafioso.MarketAcquisition;

public record MarketAcquisitionRequestView
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("targetCharacterName")]
    public string TargetCharacterName { get; init; } = string.Empty;

    [JsonPropertyName("targetWorld")]
    public string TargetWorld { get; init; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; init; } = string.Empty;

    [JsonPropertyName("itemId")]
    public uint ItemId { get; init; }

    [JsonPropertyName("itemName")]
    public string? ItemName { get; init; }

    [JsonPropertyName("quantityMode")]
    public string QuantityMode { get; init; } = string.Empty;

    [JsonPropertyName("quantity")]
    public uint Quantity { get; init; }

    [JsonPropertyName("hqPolicy")]
    public string HqPolicy { get; init; } = string.Empty;

    [JsonPropertyName("maxUnitPrice")]
    public uint MaxUnitPrice { get; init; }

    [JsonPropertyName("maxTotalGil")]
    public uint MaxTotalGil { get; init; }

    [JsonPropertyName("worldMode")]
    public string WorldMode { get; init; } = string.Empty;

    [JsonPropertyName("latestAttemptId")]
    public string? LatestAttemptId { get; init; }

    [JsonPropertyName("latestAttemptSequence")]
    public long? LatestAttemptSequence { get; init; }

    [JsonPropertyName("latestAttemptEventType")]
    public string? LatestAttemptEventType { get; init; }

    [JsonPropertyName("latestAttemptPhase")]
    public string? LatestAttemptPhase { get; init; }

    [JsonPropertyName("latestAttemptWorld")]
    public string? LatestAttemptWorld { get; init; }

    [JsonPropertyName("latestAttemptResult")]
    public string? LatestAttemptResult { get; init; }

    [JsonPropertyName("latestAttemptPluginVersion")]
    public string? LatestAttemptPluginVersion { get; init; }
}

public sealed record MarketAcquisitionClaimView : MarketAcquisitionRequestView
{
    [JsonPropertyName("claimToken")]
    public string ClaimToken { get; init; } = string.Empty;
}

public sealed record MarketAcquisitionPendingResponse
{
    [JsonPropertyName("requests")]
    public List<MarketAcquisitionRequestView> Requests { get; init; } = new();
}

public sealed record MarketAcquisitionClaimRequest
{
    [JsonPropertyName("characterName")]
    public string CharacterName { get; init; } = string.Empty;

    [JsonPropertyName("world")]
    public string World { get; init; } = string.Empty;

    [JsonPropertyName("pluginInstanceId")]
    public string PluginInstanceId { get; init; } = string.Empty;
}

public sealed record MarketAcquisitionClaimTokenRequest
{
    [JsonPropertyName("claimToken")]
    public string ClaimToken { get; init; } = string.Empty;

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; init; } = string.Empty;
}

public sealed record MarketAcquisitionLifecycleRequest
{
    [JsonPropertyName("claimToken")]
    public string ClaimToken { get; init; } = string.Empty;

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; init; } = string.Empty;

    [JsonPropertyName("runnerState")]
    public string? RunnerState { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record MarketAcquisitionAttemptEventRequest
{
    [JsonPropertyName("claimToken")]
    public string ClaimToken { get; init; } = string.Empty;

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; init; } = string.Empty;

    [JsonPropertyName("pluginInstanceId")]
    public string PluginInstanceId { get; init; } = string.Empty;

    [JsonPropertyName("runnerState")]
    public string? RunnerState { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("attemptId")]
    public string AttemptId { get; init; } = string.Empty;

    [JsonPropertyName("eventSequence")]
    public long EventSequence { get; init; }

    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = string.Empty;

    [JsonPropertyName("phase")]
    public string Phase { get; init; } = string.Empty;

    [JsonPropertyName("routeStopId")]
    public string? RouteStopId { get; init; }

    [JsonPropertyName("worldName")]
    public string? WorldName { get; init; }

    [JsonPropertyName("pluginVersion")]
    public string? PluginVersion { get; init; }

    [JsonPropertyName("clientTimestampUtc")]
    public DateTimeOffset ClientTimestampUtc { get; init; }
}

public sealed record MarketAcquisitionAttemptEventResult
{
    [JsonPropertyName("request")]
    public MarketAcquisitionRequestView Request { get; init; } = new();

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
