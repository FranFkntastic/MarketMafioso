namespace MarketMafioso.Server;

public sealed record InventoryBrowserView
{
    public string? SnapshotId { get; init; }
    public DateTimeOffset? ReceivedAt { get; init; }
    public string? CharacterName { get; init; }
    public string? HomeWorld { get; init; }
    public string Search { get; init; } = string.Empty;
    public string Scope { get; init; } = "all";
    public IReadOnlyList<InventoryBrowserItemView> Items { get; init; } = [];
    public IReadOnlyList<InventoryBrowserScopeView> Scopes { get; init; } = [];
    public IReadOnlyList<InventoryBrowserMarketListingView> MarketListings { get; init; } = [];
    public int TotalQuantity { get; init; }
    public int HqQuantity { get; init; }
    public int OwnerCount { get; init; }
    public ulong RetainerGil { get; init; }
}

public sealed record InventoryBrowserItemView
{
    public uint ItemId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? ItemType { get; init; }
    public string? IconUrl { get; init; }
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

public sealed record InventoryBrowserScopeView
{
    public string ScopeKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int StackCount { get; init; }
    public ulong Gil { get; init; }
    public int MarketListingCount { get; init; }
    public string? LastUpdated { get; init; }
}

public sealed record InventoryBrowserMarketListingView
{
    public string OwnerName { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? ItemType { get; init; }
    public string? IconUrl { get; init; }
    public int Quantity { get; init; }
    public int HqQuantity { get; init; }
    public uint? UnitPrice { get; init; }
    public string? ListedAt { get; init; }
}
