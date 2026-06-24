using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MarketMafioso;

public record InventoryReport
{
    [JsonPropertyName("metadata")]
    public InventoryReportMetadata Metadata { get; init; } = new();

    [JsonPropertyName("characterName")]
    public string? CharacterName { get; init; }

    [JsonPropertyName("homeWorld")]
    public string? HomeWorld { get; init; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("playerInventory")]
    public List<InventoryBag> PlayerInventory { get; init; } = new();

    [JsonPropertyName("retainers")]
    public List<RetainerReport> Retainers { get; init; } = new();
}

public record InventoryReportMetadata
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("sourcePlugin")]
    public string SourcePlugin { get; init; } = string.Empty;

    [JsonPropertyName("pluginVersion")]
    public string PluginVersion { get; init; } = string.Empty;

    [JsonPropertyName("generatedAtUtc")]
    public string GeneratedAtUtc { get; init; } = string.Empty;
}

public record InventoryBag
{
    [JsonPropertyName("bagName")]
    public string BagName { get; init; } = string.Empty;

    [JsonPropertyName("items")]
    public List<ItemSlot> Items { get; init; } = new();
}

public record RetainerReport
{
    [JsonPropertyName("retainerName")]
    public string RetainerName { get; init; } = string.Empty;

    [JsonPropertyName("retainerId")]
    public ulong RetainerId { get; init; }

    [JsonPropertyName("lastUpdated")]
    public string LastUpdated { get; init; } = string.Empty;

    [JsonPropertyName("bags")]
    public List<InventoryBag> Bags { get; init; } = new();
}

public record ItemSlot
{
    [JsonPropertyName("itemId")]
    public uint ItemId { get; init; }

    [JsonPropertyName("itemName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ItemName { get; init; }

    [JsonPropertyName("itemType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ItemType { get; init; }

    [JsonPropertyName("quantity")]
    public uint Quantity { get; init; }

    [JsonPropertyName("isHQ")]
    public bool IsHQ { get; init; }

    [JsonPropertyName("condition")]
    public float Condition { get; init; }
}
