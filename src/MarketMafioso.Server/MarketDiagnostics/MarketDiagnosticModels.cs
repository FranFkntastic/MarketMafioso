namespace MarketMafioso.Server.MarketDiagnostics;

public sealed record OwnedMarketListing
{
    public long Id { get; init; }
    public long AccountId { get; init; }
    public string VersionKey { get; init; } = string.Empty;
    public string ListingKey { get; init; } = string.Empty;
    public string SnapshotId { get; init; } = string.Empty;
    public string? CharacterName { get; init; }
    public string World { get; init; } = string.Empty;
    public ulong RetainerId { get; init; }
    public string RetainerName { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public uint Quantity { get; init; }
    public bool IsHq { get; init; }
    public uint UnitPrice { get; init; }
    public DateTimeOffset? ListedAtUtc { get; init; }
    public DateTimeOffset ListingsObservedAtUtc { get; init; }
    public DateTimeOffset FirstObservedAtUtc { get; init; }
    public DateTimeOffset LastObservedAtUtc { get; init; }
    public DateTimeOffset? LastPubliclySeenAtUtc { get; init; }
    public DateTimeOffset? PubliclyMissingSinceUtc { get; init; }
    public DateTimeOffset? SaleHistoryCheckedAtUtc { get; init; }
}

public sealed record UniversalisListingEvidence
{
    public uint ItemId { get; init; }
    public string ListingId { get; init; } = string.Empty;
    public string RetainerId { get; init; } = string.Empty;
    public string RetainerName { get; init; } = string.Empty;
    public uint UnitPrice { get; init; }
    public uint Quantity { get; init; }
    public bool IsHq { get; init; }
    public DateTimeOffset ReviewedAtUtc { get; init; }
}

public sealed record UniversalisItemEvidence
{
    public uint ItemId { get; init; }
    public DateTimeOffset? UploadedAtUtc { get; init; }
    public uint? MinimumNqPrice { get; init; }
    public uint? MinimumHqPrice { get; init; }
    public IReadOnlyList<UniversalisListingEvidence> Listings { get; init; } = [];
}

public sealed record UniversalisSaleEvidence
{
    public uint ItemId { get; init; }
    public uint UnitPrice { get; init; }
    public uint Quantity { get; init; }
    public bool IsHq { get; init; }
    public string? BuyerName { get; init; }
    public bool OnMannequin { get; init; }
    public DateTimeOffset SoldAtUtc { get; init; }
}

public sealed record MarketSaleHistoryProbe
{
    public required OwnedMarketListing Listing { get; init; }
    public DateTimeOffset EarliestAtUtc { get; init; }
    public DateTimeOffset LatestAtUtc { get; init; }
}

public record RegionMarketCondition
{
    public uint ItemId { get; init; }
    public bool IsHq { get; init; }
    public uint? MinimumListingPrice { get; init; }
    public uint? MinimumListingWorldId { get; init; }
    public double? AverageSalePrice { get; init; }
    public double? DailySaleVelocity { get; init; }
    public uint? RecentPurchasePrice { get; init; }
    public uint? RecentPurchaseWorldId { get; init; }
    public DateTimeOffset? RecentPurchaseAtUtc { get; init; }
    public DateTimeOffset? FreshestWorldUploadAtUtc { get; init; }
}

public sealed record RegionMarketConditionView : RegionMarketCondition
{
    public long Id { get; init; }
    public long AccountId { get; init; }
    public string Region { get; init; } = string.Empty;
    public string? ItemName { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public long? SourceAgeSeconds { get; init; }
}

public sealed record RetainerSaleEventView
{
    public long Id { get; init; }
    public long AccountId { get; init; }
    public long? OwnedListingVersionId { get; init; }
    public string Source { get; init; } = string.Empty;
    public string Confidence { get; init; } = string.Empty;
    public ulong? RetainerId { get; init; }
    public string? RetainerName { get; init; }
    public string? CharacterName { get; init; }
    public string? World { get; init; }
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public uint? Quantity { get; init; }
    public bool? IsHq { get; init; }
    public uint? UnitPrice { get; init; }
    public ulong? TotalGil { get; init; }
    public DateTimeOffset? EventAtUtc { get; init; }
    public DateTimeOffset? EarliestEventAtUtc { get; init; }
    public DateTimeOffset? LatestEventAtUtc { get; init; }
    public int? CandidateCount { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
}

public enum MarketObservationClassification
{
    Clear,
    Undercut,
    UnknownStale,
    UnknownMissing,
}

public sealed record MarketListingEvaluation
{
    public required OwnedMarketListing OwnedListing { get; init; }
    public MarketObservationClassification Classification { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public DateTimeOffset? SourceUploadedAtUtc { get; init; }
    public long? SourceAgeSeconds { get; init; }
    public string SourceFreshness { get; init; } = string.Empty;
    public bool? OwnListingVisible { get; init; }
    public UniversalisListingEvidence? Competitor { get; init; }
    public uint? UndercutDelta { get; init; }
}

public sealed record MarketDiagnosticTransition
{
    public string Type { get; init; } = string.Empty;
    public long AccountId { get; init; }
    public long OwnedListingVersionId { get; init; }
    public string? CharacterName { get; init; }
    public string World { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string RetainerName { get; init; } = string.Empty;
    public uint OwnUnitPrice { get; init; }
    public string? CompetitorRetainerName { get; init; }
    public uint? CompetitorUnitPrice { get; init; }
    public uint? UndercutDelta { get; init; }
    public long? ResponseUpperBoundMs { get; init; }
}

public sealed record MarketDiagnosticEpisodeView
{
    public long Id { get; init; }
    public long AccountId { get; init; }
    public long OwnedListingVersionId { get; init; }
    public string World { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string RetainerName { get; init; } = string.Empty;
    public uint OwnUnitPrice { get; init; }
    public uint CompetitorUnitPrice { get; init; }
    public string? CompetitorRetainerName { get; init; }
    public uint UndercutDelta { get; init; }
    public bool ExactOneGil { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset FirstDetectedAtUtc { get; init; }
    public DateTimeOffset LastSeenAtUtc { get; init; }
    public long ResponseLowerBoundMs { get; init; }
    public long ResponseUpperBoundMs { get; init; }
    public DateTimeOffset? ClearedAtUtc { get; init; }
    public string? CloseReason { get; init; }
}
