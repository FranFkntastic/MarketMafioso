namespace MarketMafioso.Contracts;

public sealed record MarketDiagnosticWorkbenchView
{
    public IReadOnlyList<MarketDiagnosticListingRow> ActiveListings { get; init; } = [];
    public IReadOnlyList<MarketDiagnosticSaleRow> History { get; init; } = [];
    public DateTimeOffset? CollectorUpdatedAtUtc { get; init; }
    public DateTimeOffset? RegionObservedAtUtc { get; init; }
}

public sealed record MarketDiagnosticListingRow
{
    public long Id { get; init; }
    public long AccountId { get; init; }
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
    public DateTimeOffset FirstObservedAtUtc { get; init; }
    public DateTimeOffset LastObservedAtUtc { get; init; }
    public string Classification { get; init; } = "UnknownMissing";
    public DateTimeOffset? MarketObservedAtUtc { get; init; }
    public DateTimeOffset? SourceUploadedAtUtc { get; init; }
    public long? SourceAgeSeconds { get; init; }
    public string? SourceFreshness { get; init; }
    public bool? OwnListingVisible { get; init; }
    public string? CompetitorRetainerName { get; init; }
    public uint? CompetitorUnitPrice { get; init; }
    public uint? UndercutDelta { get; init; }
    public long? EpisodeId { get; init; }
    public DateTimeOffset? LastClearObservedAtUtc { get; init; }
    public DateTimeOffset? FirstDetectedAtUtc { get; init; }
    public DateTimeOffset? EpisodeLastSeenAtUtc { get; init; }
    public long? ResponseLowerBoundMs { get; init; }
    public long? ResponseUpperBoundMs { get; init; }
}

public sealed record MarketDiagnosticSaleRow
{
    public long Id { get; init; }
    public long? OwnedListingVersionId { get; init; }
    public string Source { get; init; } = string.Empty;
    public string Confidence { get; init; } = string.Empty;
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

public sealed record MarketDiagnosticListingDetailView
{
    public MarketDiagnosticListingRow? Listing { get; init; }
    public IReadOnlyList<MarketDiagnosticTimelineEventView> Timeline { get; init; } = [];
    public MarketDiagnosticRegionContextView? Region { get; init; }
    public MarketDiagnosticCompetitorProfileView? Competitor { get; init; }
}

public sealed record MarketDiagnosticTimelineEventView
{
    public DateTimeOffset OccurredAtUtc { get; init; }
    public string Tone { get; init; } = "Info";
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

public sealed record MarketDiagnosticRegionContextView
{
    public string Region { get; init; } = string.Empty;
    public DateTimeOffset ObservedAtUtc { get; init; }
    public uint? MinimumListingPrice { get; init; }
    public uint? MinimumListingWorldId { get; init; }
    public double? AverageSalePrice { get; init; }
    public double? DailySaleVelocity { get; init; }
}

public sealed record MarketDiagnosticCompetitorProfileView
{
    public string RetainerName { get; init; } = string.Empty;
    public int EpisodeCount { get; init; }
    public int ExactOneGilCount { get; init; }
    public double? AverageResponseUpperBoundSeconds { get; init; }
}
