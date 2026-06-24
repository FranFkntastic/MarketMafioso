namespace MarketMafioso.Server;

public sealed record InventoryBrowserView
{
    public string? SnapshotId { get; init; }
    public DateTimeOffset? ReceivedAt { get; init; }
    public string? CharacterName { get; init; }
    public string? HomeWorld { get; init; }
    public string Search { get; init; } = string.Empty;
    public IReadOnlyList<InventoryBrowserItemView> Items { get; init; } = [];
    public int TotalQuantity { get; init; }
    public int HqQuantity { get; init; }
    public int OwnerCount { get; init; }
}

public sealed record InventoryBrowserItemView
{
    public uint ItemId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public int TotalQuantity { get; init; }
    public int HqQuantity { get; init; }
    public IReadOnlyList<InventoryBrowserLocationView> Locations { get; init; } = [];
    public int OwnerCount => Locations.Select(x => x.OwnerName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}

public sealed record InventoryBrowserLocationView
{
    public string OwnerName { get; init; } = string.Empty;
    public string BagName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public int HqQuantity { get; init; }
}
