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
