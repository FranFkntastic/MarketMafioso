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
    public string SemanticFilter { get; init; } = string.Empty;
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
    public ulong? PlayerGil { get; init; }
    public ulong RetainerGil { get; init; }
    public ulong? TotalGil { get; init; }
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
    public InventoryBrowserStowageView? Stowage { get; init; }
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
    public InventoryBrowserStowageView? Stowage { get; init; }
}

public sealed record InventoryBrowserStowageView
{
    public Guid PlanId { get; init; }
    public Guid RuleId { get; init; }
    public string PlanName { get; init; } = string.Empty;
    public int DesiredPlayerQuantity { get; init; }
    public string Quality { get; init; } = string.Empty;
    public string Action { get; init; } = "none";
    public int Quantity { get; init; }
    public int PlayerQuantity { get; init; }
    public IReadOnlyList<string> PreferredDestinations { get; init; } = [];
}

public sealed record QuartermasterStowageReport
{
    public string Schema { get; init; } = "gooseworks-quartermaster-stowage-report/v1";
    public string ProviderInstanceId { get; init; } = string.Empty;
    public long Revision { get; init; }
    public QuartermasterStowageOwner Owner { get; init; } = new();
    public IReadOnlyList<QuartermasterStowagePlanReport> Plans { get; init; } = [];
}

public sealed record QuartermasterStowageOwner
{
    public ulong LocalContentId { get; init; }
    public uint HomeWorldId { get; init; }
}

public sealed record QuartermasterStowagePlanReport
{
    public Guid Id { get; init; }
    public long Revision { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public IReadOnlyList<QuartermasterStowageRuleReport> Rules { get; init; } = [];
}

public sealed record QuartermasterStowageRuleReport
{
    public Guid Id { get; init; }
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public int DesiredPlayerQuantity { get; init; }
    public string Quality { get; init; } = string.Empty;
    public string Action { get; init; } = "none";
    public int Quantity { get; init; }
    public int PlayerQuantity { get; init; }
    public IReadOnlyList<QuartermasterStowageDestinationReport> PreferredDestinations { get; init; } = [];
}

public sealed record QuartermasterStowageDestinationReport
{
    public ulong RetainerId { get; init; }
    public string? RetainerName { get; init; }
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
    public ulong? Gil { get; init; }
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
