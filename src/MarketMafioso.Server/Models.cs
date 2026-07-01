using System.Text.Json.Serialization;

namespace MarketMafioso.Server;

public sealed record InventoryReport
{
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InventoryReportMetadata? Metadata { get; init; }

    [JsonPropertyName("characterName")]
    public string? CharacterName { get; init; }

    [JsonPropertyName("homeWorld")]
    public string? HomeWorld { get; init; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = string.Empty;

    [JsonPropertyName("playerInventory")]
    public List<InventoryBag> PlayerInventory { get; init; } = new();

    [JsonPropertyName("retainers")]
    public List<RetainerReport> Retainers { get; init; } = new();
}

public sealed record InventoryReportMetadata
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

public sealed record InventoryBag
{
    [JsonPropertyName("bagName")]
    public string BagName { get; init; } = string.Empty;

    [JsonPropertyName("items")]
    public List<ItemSlot> Items { get; init; } = new();
}

public sealed record RetainerReport
{
    [JsonPropertyName("retainerName")]
    public string RetainerName { get; init; } = string.Empty;

    [JsonPropertyName("retainerId")]
    public ulong RetainerId { get; init; }

    [JsonPropertyName("lastUpdated")]
    public string LastUpdated { get; init; } = string.Empty;

    [JsonPropertyName("gil")]
    public ulong Gil { get; init; }

    [JsonPropertyName("bags")]
    public List<InventoryBag> Bags { get; init; } = new();

    [JsonPropertyName("marketListings")]
    public List<RetainerMarketListing> MarketListings { get; init; } = new();
}

public sealed record ItemSlot
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

public sealed record RetainerMarketListing
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

    [JsonPropertyName("unitPrice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? UnitPrice { get; init; }

    [JsonPropertyName("listedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ListedAt { get; init; }
}

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

public sealed record DashboardLoginRequest
{
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}
