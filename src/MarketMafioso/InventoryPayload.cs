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

    [JsonPropertyName("serviceAccountKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceAccountKey { get; init; }

    [JsonPropertyName("playerGil")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? PlayerGil { get; init; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("playerInventory")]
    public List<InventoryBag> PlayerInventory { get; init; } = new();

    [JsonPropertyName("retainers")]
    public List<RetainerReport> Retainers { get; init; } = new();

    [JsonPropertyName("playerStorage")]
    public StorageSourceEvidence PlayerStorage { get; init; } = new();
}

public record StorageSourceEvidence
{
    [JsonPropertyName("requestedSources")]
    public List<string> RequestedSources { get; init; } = new();

    [JsonPropertyName("observedSources")]
    public List<string> ObservedSources { get; init; } = new();
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

    [JsonPropertyName("location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Location { get; init; }

    [JsonPropertyName("observedAtUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObservedAtUtc { get; init; }

    [JsonPropertyName("items")]
    public List<ItemSlot> Items { get; init; } = new();
}

public record RetainerReport
{
    [JsonPropertyName("retainerName")]
    public string RetainerName { get; init; } = string.Empty;

    [JsonPropertyName("retainerId")]
    public ulong RetainerId { get; init; }

    [JsonPropertyName("ownerCharacterName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerCharacterName { get; init; }

    [JsonPropertyName("ownerHomeWorld")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerHomeWorld { get; init; }

    [JsonPropertyName("lastUpdated")]
    public string LastUpdated { get; init; } = string.Empty;

    [JsonPropertyName("gil")]
    public ulong Gil { get; init; }

    [JsonPropertyName("gilObservedAtUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GilObservedAtUtc { get; init; }

    [JsonPropertyName("listingsObservedAtUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ListingsObservedAtUtc { get; init; }

    [JsonPropertyName("bags")]
    public List<InventoryBag> Bags { get; init; } = new();

    [JsonPropertyName("marketListings")]
    public List<RetainerMarketListing> MarketListings { get; init; } = new();

    [JsonPropertyName("storage")]
    public StorageSourceEvidence Storage { get; init; } = new();
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

    [JsonPropertyName("containerKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContainerKey { get; init; }

    [JsonPropertyName("slotIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SlotIndex { get; init; }

    [JsonPropertyName("conditionPercent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? ConditionPercent { get; init; }

    [JsonPropertyName("equipped")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Equipped { get; init; }
}

public record RetainerMarketListing
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

    [JsonPropertyName("containerKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContainerKey { get; init; }

    [JsonPropertyName("slotIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SlotIndex { get; init; }

    [JsonPropertyName("conditionPercent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? ConditionPercent { get; init; }

    [JsonPropertyName("unitPrice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? UnitPrice { get; init; }

    [JsonPropertyName("listedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ListedAt { get; init; }
}
