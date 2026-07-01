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

    [JsonPropertyName("sweepScope")]
    public string SweepScope { get; init; } = "Region";

    [JsonPropertyName("sweepDataCenters")]
    public List<string> SweepDataCenters { get; init; } = new();

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

    [JsonPropertyName("lines")]
    public List<MarketAcquisitionBatchLineView> Lines { get; init; } = new();
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

public sealed record MarketAcquisitionBatchPendingResponse
{
    [JsonPropertyName("batches")]
    public List<MarketAcquisitionRequestView> Batches { get; init; } = new();
}

public sealed record MarketAcquisitionBatchLineView
{
    [JsonPropertyName("lineId")]
    public string LineId { get; init; } = string.Empty;

    [JsonPropertyName("batchId")]
    public string BatchId { get; init; } = string.Empty;

    [JsonPropertyName("ordinal")]
    public int Ordinal { get; init; }

    [JsonPropertyName("itemId")]
    public uint ItemId { get; init; }

    [JsonPropertyName("itemName")]
    public string? ItemName { get; init; }

    [JsonPropertyName("itemKind")]
    public string? ItemKind { get; init; }

    [JsonPropertyName("quantityMode")]
    public string QuantityMode { get; init; } = string.Empty;

    [JsonPropertyName("targetQuantity")]
    public uint TargetQuantity { get; init; }

    [JsonPropertyName("maxQuantity")]
    public uint MaxQuantity { get; init; }

    [JsonPropertyName("hqPolicy")]
    public string HqPolicy { get; init; } = string.Empty;

    [JsonPropertyName("maxUnitPrice")]
    public uint MaxUnitPrice { get; init; }

    [JsonPropertyName("gilCap")]
    public uint GilCap { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("purchasedQuantity")]
    public uint PurchasedQuantity { get; init; }

    [JsonPropertyName("spentGil")]
    public uint SpentGil { get; init; }

    [JsonPropertyName("latestMessage")]
    public string? LatestMessage { get; init; }
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

public sealed record MarketAcquisitionLineProgressRequest
{
    [JsonPropertyName("claimToken")]
    public string ClaimToken { get; init; } = string.Empty;

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; init; } = string.Empty;

    [JsonPropertyName("attemptId")]
    public string AttemptId { get; init; } = string.Empty;

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("purchasedQuantity")]
    public uint PurchasedQuantity { get; init; }

    [JsonPropertyName("spentGil")]
    public uint SpentGil { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record MarketAcquisitionPurchaseAuditRequest
{
    [JsonPropertyName("claimToken")]
    public string ClaimToken { get; init; } = string.Empty;

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; init; } = string.Empty;

    [JsonPropertyName("attemptId")]
    public string AttemptId { get; init; } = string.Empty;

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("lineId")]
    public string LineId { get; init; } = string.Empty;

    [JsonPropertyName("worldName")]
    public string WorldName { get; init; } = string.Empty;

    [JsonPropertyName("itemId")]
    public uint ItemId { get; init; }

    [JsonPropertyName("itemName")]
    public string? ItemName { get; init; }

    [JsonPropertyName("listingId")]
    public string ListingId { get; init; } = string.Empty;

    [JsonPropertyName("retainerName")]
    public string RetainerName { get; init; } = string.Empty;

    [JsonPropertyName("retainerId")]
    public string RetainerId { get; init; } = string.Empty;

    [JsonPropertyName("quantity")]
    public uint Quantity { get; init; }

    [JsonPropertyName("unitPrice")]
    public uint UnitPrice { get; init; }

    [JsonPropertyName("totalGil")]
    public uint TotalGil { get; init; }

    [JsonPropertyName("isHq")]
    public bool IsHq { get; init; }

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed record MarketAcquisitionPurchaseAuditView
{
    [JsonPropertyName("auditId")]
    public string AuditId { get; init; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("lineId")]
    public string LineId { get; init; } = string.Empty;

    [JsonPropertyName("attemptId")]
    public string AttemptId { get; init; } = string.Empty;

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("worldName")]
    public string WorldName { get; init; } = string.Empty;

    [JsonPropertyName("itemId")]
    public uint ItemId { get; init; }

    [JsonPropertyName("itemName")]
    public string? ItemName { get; init; }

    [JsonPropertyName("listingId")]
    public string ListingId { get; init; } = string.Empty;

    [JsonPropertyName("retainerName")]
    public string RetainerName { get; init; } = string.Empty;

    [JsonPropertyName("retainerId")]
    public string RetainerId { get; init; } = string.Empty;

    [JsonPropertyName("quantity")]
    public uint Quantity { get; init; }

    [JsonPropertyName("unitPrice")]
    public uint UnitPrice { get; init; }

    [JsonPropertyName("totalGil")]
    public uint TotalGil { get; init; }

    [JsonPropertyName("isHq")]
    public bool IsHq { get; init; }

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; init; }
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
