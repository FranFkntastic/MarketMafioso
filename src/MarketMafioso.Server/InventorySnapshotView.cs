namespace MarketMafioso.Server;

public sealed record InventorySnapshotView
{
    public string Id { get; init; } = string.Empty;
    public InventorySnapshotMetadata Metadata { get; init; } = new();
    public DateTimeOffset ReceivedAt { get; init; }
    public string? CharacterName { get; init; }
    public string? HomeWorld { get; init; }
    public string ReportTimestamp { get; init; } = string.Empty;
    public InventoryOwnerView PlayerInventory { get; init; } = new();
    public IReadOnlyList<InventoryOwnerView> Retainers { get; init; } = [];
    public InventorySnapshotTotals Totals { get; init; } = new();
}

public sealed record InventorySnapshotMetadata
{
    public int SchemaVersion { get; init; }
    public string SourcePlugin { get; init; } = "Unknown";
    public string PluginVersion { get; init; } = "Unknown";
    public string GeneratedAtUtc { get; init; } = "Unknown";
}

public sealed record InventoryOwnerView
{
    public string Name { get; init; } = string.Empty;
    public ulong? RetainerId { get; init; }
    public string? OwnerCharacterName { get; init; }
    public string? OwnerHomeWorld { get; init; }
    public string? LastUpdated { get; init; }
    public IReadOnlyList<InventoryBagView> Bags { get; init; } = [];
    public int Stacks { get; init; }
    public int Quantity { get; init; }
    public int HqStacks { get; init; }
}

public sealed record InventoryBagView
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<InventoryItemView> Items { get; init; } = [];
    public int Stacks { get; init; }
    public int Quantity { get; init; }
    public int HqStacks { get; init; }
}

public sealed record InventoryItemView
{
    public uint ItemId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public bool IsHQ { get; init; }
    public float Condition { get; init; }
}

public sealed record InventorySnapshotTotals
{
    public int Stacks { get; init; }
    public int Quantity { get; init; }
    public int HqStacks { get; init; }
    public int PlayerStacks { get; init; }
    public int PlayerQuantity { get; init; }
    public int RetainerStacks { get; init; }
    public int RetainerQuantity { get; init; }
    public int Retainers { get; init; }
}
