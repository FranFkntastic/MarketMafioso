using System.Text.Json.Serialization;
using Franthropy.Filtering.Diagnostics;
using Franthropy.Filtering.Documentation;
using Franthropy.Filtering.Completion;

namespace MarketMafioso.Contracts.Inventory;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventoryBrowserMode
{
    Items,
    Stacks,
    Listings,
}

public sealed record InventoryBrowserView
{
    public string? SnapshotId { get; init; }
    public DateTimeOffset? ReceivedAt { get; init; }
    public string? CharacterName { get; init; }
    public string? HomeWorld { get; init; }
    public string Filter { get; init; } = string.Empty;
    public string NormalizedFilter { get; init; } = string.Empty;
    public bool FilterValid { get; init; } = true;
    public IReadOnlyList<FilterDiagnostic> FilterDiagnostics { get; init; } = [];
    public FilterReferenceModel? FilterReference { get; init; }
    public IReadOnlyList<FilterCompletionItem> FilterCompletions { get; init; } = [];
    public InventoryBrowserMode Mode { get; init; } = InventoryBrowserMode.Items;
    public string Scope { get; init; } = "all";
    public IReadOnlyList<InventoryBrowserItemView> Items { get; init; } = [];
    public IReadOnlyList<InventoryBrowserStackView> Stacks { get; init; } = [];
    public IReadOnlyList<InventoryBrowserScopeView> Scopes { get; init; } = [];
    public IReadOnlyList<InventoryBrowserMarketListingView> MarketListings { get; init; } = [];
    public int MatchingRecordCount { get; init; }
    public int TotalQuantity { get; init; }
    public int HqQuantity { get; init; }
    public int OwnerCount { get; init; }
    public int ItemTypeKnownCount { get; init; }
    public int ListingPriceKnownCount { get; init; }
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
    public int OwnerCount { get; init; }
}

public sealed record InventoryBrowserStackView
{
    public string OwnerName { get; init; } = string.Empty;
    public string? OwnerCharacterName { get; init; }
    public string? OwnerHomeWorld { get; init; }
    public string BagName { get; init; } = string.Empty;
    public int? SlotIndex { get; init; }
    public string Location { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? ItemType { get; init; }
    public string? IconUrl { get; init; }
    public int Quantity { get; init; }
    public bool IsHq { get; init; }
    public bool? Equipped { get; init; }
    public decimal? ConditionPercent { get; init; }
}

public sealed record InventoryBrowserLocationView
{
    public string OwnerName { get; init; } = string.Empty;
    public string? OwnerCharacterName { get; init; }
    public string? OwnerHomeWorld { get; init; }
    public string Location { get; init; } = string.Empty;
    public string BagName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public int HqQuantity { get; init; }
}

public sealed record InventoryBrowserScopeView
{
    public string ScopeKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? OwnerCharacterName { get; init; }
    public string? OwnerHomeWorld { get; init; }
    public int StackCount { get; init; }
    public ulong Gil { get; init; }
    public int MarketListingCount { get; init; }
    public string? LastUpdated { get; init; }
}

public sealed record InventoryBrowserMarketListingView
{
    public string OwnerName { get; init; } = string.Empty;
    public string? OwnerCharacterName { get; init; }
    public string? OwnerHomeWorld { get; init; }
    public uint ItemId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? ItemType { get; init; }
    public string? IconUrl { get; init; }
    public int Quantity { get; init; }
    public int HqQuantity { get; init; }
    public decimal? ConditionPercent { get; init; }
    public uint? UnitPrice { get; init; }
    public ulong? TotalPrice { get; init; }
    public string? ListedAt { get; init; }
    public double? EvidenceAgeSeconds { get; init; }
}
